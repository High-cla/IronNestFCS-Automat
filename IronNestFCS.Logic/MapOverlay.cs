using System.Collections.Generic;
using IronNestFCS.Logic.FCS;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace IronNestFCS.Logic;

/// <summary>
/// 地图 overlay: 把"打击中"任务(LeftTask/RightTask/InFlight)的意图与动作画到地图上。
/// 参考 KKTIME2024/IronNestFCS-Automat aa17af1 设计(overlay 功能, 后被其 Revert)。
/// 元素(用户确认 2026-08-15: 白字标签已删, 红线粗细减半, 填充盘已删):
///   毁伤圈(描边环, 半径=task.BlastRadiusKm 注册表同源)
///   火力线(玩家→落点, 深红)
///   移动目标: 前进路线(白虚线固定长)
/// 1Hz tick, 按任务创建/销毁槽(dict), 对象挂 Draggable Surface 下(地图静态, 仅坐标帧一致)。
/// 只读 FSC 公开 API, 保持 FSC 纯领域逻辑分离。Shutdown 销毁全部对象(热重载安全)。
/// </summary>
public class MapOverlay
{
    // ==== 调参项(构建机实测) ====
    private const float TickInterval = 1f;
    private const float PathLengthKm = 1.5f;        // 移动路径固定可见长度(km)
    private const float LineWidthWorld = 0.0075f;   // 线宽(世界单位, 用户确认再减半)
    private const int CircleSegments = 48;
    private const int DashSegments = 6;             // 移动路径虚线段数
    private const int OverlayQueue = 3500;          // 渲染队列: 压过透明航拍照片层(3000+), 深度并列时后画者胜(上游 64a0750)

    private static readonly Color LineColor = new(0.85f, 0.08f, 0.05f);   // 鲜红(火力线/毁伤圈, 深色地图上可见)
    private static readonly Color PathColor = new(0.9f, 0.9f, 0.9f);      // 白(移动路径, 尺规语义)

    private readonly FSC fcs;
    private readonly Transform? mapSurface;
    private readonly List<GameObject> tracked = new();          // 热重载时销毁
    private readonly Dictionary<ArtilleryTask, Slot> slots = new();
    private Texture2D? dashTexture;                             // 虚线材质共享
    private Material? lineMat;      // 共享实线材质(火力线/毁伤圈): N 实例→1, 材质切换/帧→1(上游 d39193a)
    private Material? pathMat;      // 共享虚线材质(移动路径): 带纹理, 与实线必须分开
    private float lastTick;

    public MapOverlay(FSC fcs) {
        this.fcs = fcs;
        mapSurface = GameObject.Find("Draggable Surface")?.transform;
    }

    /// <summary>一个任务对应的渲染槽(按任务创建/销毁)。</summary>
    private sealed class Slot {
        public LineRenderer? ring;
        public LineRenderer? fireLine;
        public LineRenderer? path;
        public Vector3 lastImpact = new(float.MinValue, float.MinValue, float.MinValue);   // 落点变化检测哨兵
        public Vector3? frozenImpact;   // 击发锁存: 在飞期几何冻结(上游 9fb9455)
    }

    /// <summary>每帧调用, 内部 1Hz 节流。收集活动任务 → 更新/创建槽 → 销毁失效槽。</summary>
    public void Update() {
        if (Time.time - lastTick < TickInterval) return;
        lastTick = Time.time;
        if (mapSurface == null || fcs.MapTable.Turret == null) return;

        var active = new List<ArtilleryTask>(3);
        if (fcs.LeftTask != null) active.Add(fcs.LeftTask);
        if (fcs.RightTask != null && !ReferenceEquals(fcs.RightTask, fcs.LeftTask)) active.Add(fcs.RightTask);
        foreach (var t in fcs.InFlight)
            if (!active.Contains(t)) active.Add(t);

        foreach (var t in active) {
            if (!slots.TryGetValue(t, out var slot)) { slot = CreateSlot(); slots[t] = slot; }
            UpdateSlot(slot, t);
        }

        var stale = new List<ArtilleryTask>();
        foreach (var kv in slots)
            if (!active.Contains(kv.Key)) stale.Add(kv.Key);
        foreach (var t in stale) { DestroySlot(slots[t]); slots.Remove(t); }
    }

    /// <summary>热重载/卸载: 销毁全部渲染对象 + 共享材质。</summary>
    public void Shutdown() {
        foreach (var go in tracked) { if (go != null) Object.Destroy(go); }
        tracked.Clear();
        slots.Clear();
        if (lineMat != null) { Object.Destroy(lineMat); lineMat = null; }   // 共享材质随热重载销毁
        if (pathMat != null) { Object.Destroy(pathMat); pathMat = null; }
        if (dashTexture != null) { Object.Destroy(dashTexture); dashTexture = null; }   // 虚线纹理同样是 Unity Object, 不销毁则每次热重载泄漏
    }

    // ==== 槽生命周期 ====

    private Slot CreateSlot() => new() {
        ring = MakeLine("OverlayRing"),
        fireLine = MakeLine("OverlayFireLine"),
        path = MakeDashedLine("OverlayPath"),
    };

    private void DestroySlot(Slot s) {
        foreach (var go in new[] { s.ring?.gameObject, s.fireLine?.gameObject, s.path?.gameObject }) {
            if (go == null) continue;
            tracked.Remove(go);
            Object.Destroy(go);
        }
    }

    // ==== 每 tick 更新 ====

    private void UpdateSlot(Slot s, ArtilleryTask t) {
        // 落点: 静态=目标位置; 移动=提前点(与瞄准同公式)
        Vector3 impactWorld = t.position;
        if (t.IsMoving && TargetLeadSolver.IsMoving(t.AimVel)) {
            var tof = ToFTable.FlightTime(t.distance, t.LoadedCharge);
            impactWorld = TargetLeadSolver.LeadPoint(t.AimP0, t.AimVel, Time.time - t.AimStartTime, tof);
        }
        Vector3 impact = fcs.MapTable.WorldToMapLocal(impactWorld);
        // 击发锁存(上游 9fb9455): 弹已出膛, 在飞期落点冻结不再外推; 未击发清锁存(瞄准期正常更新)。
        if (t.Fired) {
            s.frozenImpact ??= impact;
            impact = s.frozenImpact.Value;
        } else {
            s.frozenImpact = null;
        }
        bool impactChanged = (impact - s.lastImpact).sqrMagnitude > 1e-10f;
        if (impactChanged) s.lastImpact = impact;

        Vector3 player = fcs.MapTable.GetTurretLocal();

        // 毁伤圈 + 火力线: 仅落点变化时重建(静态几何零更新)
        if (impactChanged) {
            // 毁伤圈: 描边环(半径=注册表同源数据)
            if (s.ring != null && t.BlastRadiusKm > 0f) {
                float rMap = t.BlastRadiusKm / ShellData.KmPerWorldUnit;
                s.ring.loop = true;                          // 闭合描边
                s.ring.startWidth = s.ring.endWidth = LineWidthWorld;   // 圈线宽 = 火力线同宽(用户确认, 不再自适应)
                s.ring.positionCount = CircleSegments;
                for (int i = 0; i < CircleSegments; i++) {
                    float a = i * 2f * Mathf.PI / CircleSegments;
                    s.ring.SetPosition(i, impact + new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * rMap);
                }
            }
            // 火力线: 玩家 → 落点
            if (s.fireLine != null) {
                s.fireLine.positionCount = 2;
                s.fireLine.SetPosition(0, player);
                s.fireLine.SetPosition(1, impact);
            }
        }

        // 移动目标: 前进路线(白虚线)
        bool showPath = t.IsMoving && TargetLeadSolver.IsMoving(t.AimVel);
        if (showPath) {
            Vector3 now = fcs.MapTable.WorldToMapLocal(t.AimP0 + t.AimVel * (Time.time - t.AimStartTime));
            float lenMap = PathLengthKm / ShellData.KmPerWorldUnit;
            if (s.path != null) DrawDashed(s.path, now, now + t.AimVel.normalized * lenMap);
        } else {
            if (s.path != null) s.path.gameObject.SetActive(false);
        }
    }

    // ==== 渲染对象工厂 ====

    private LineRenderer MakeLine(string name) {
        var go = NewChild(name);
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = false;              // 位置=父空间(map-local)
        lr.positionCount = 0;
        lr.startWidth = lr.endWidth = LineWidthWorld;
        lr.loop = false;
        if (lineMat == null) lineMat = MakeMat(LineColor);
        if (lineMat != null) lr.material = lineMat;   // 直接共享赋值(不走 getter 克隆)
        else FcsSceneInteractor.SetColor(go, LineColor);   // 无 URP shader 时退回旧行为
        return lr;
    }

    private LineRenderer MakeDashedLine(string name) {
        var go = NewChild(name);
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = false;
        lr.positionCount = 0;
        lr.startWidth = lr.endWidth = LineWidthWorld;
        lr.loop = false;
        if (pathMat == null) {
            if (dashTexture == null) dashTexture = MakeDashTexture();
            pathMat = MakeMat(PathColor);
            if (pathMat != null && dashTexture != null) {
                pathMat.mainTexture = dashTexture;
                pathMat.mainTextureScale = new Vector2(DashSegments, 1f);   // 缩放定格在共享材质, 不再逐帧写
            }
        }
        if (pathMat != null) {
            lr.material = pathMat;
            if (pathMat.mainTexture != null) lr.textureMode = LineTextureMode.Tile;
        } else {
            FcsSceneInteractor.SetColor(go, PathColor);   // 无 URP shader 时退回旧行为
        }
        return lr;
    }

    /// <summary>URP Unlit 共享材质工厂: 纯色一次定格 + queue 3500。返回 null 表示找不到 shader(调用方回退)。</summary>
    private static Material? MakeMat(Color color) {
        var shader = Shader.Find("Universal Render Pipeline/Unlit")
                     ?? Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) {
            MelonLogger.Warning("[FCS] Can't find URP shader. Overlay falls back to per-object material.");
            return null;
        }
        var mat = new Material(shader);
        mat.color = color;
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        mat.renderQueue = OverlayQueue;
        return mat;
    }

    private GameObject NewChild(string name) {
        var go = new GameObject(name);
        if (mapSurface != null) go.transform.SetParent(mapSurface, false);
        tracked.Add(go);
        return go;
    }

    /// <summary>白/透明条纹虚线贴图(LineRenderer Tile 模式用)。</summary>
    private static Texture2D MakeDashTexture() {
        var tex = new Texture2D(8, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Repeat };
        for (int x = 0; x < 8; x++) {
            bool on = (x / 2) % 2 == 0;   // 4 像素亮 4 像素暗
            tex.SetPixel(x, 0, on ? new Color(1f, 1f, 1f, 1f) : new Color(1f, 1f, 1f, 0f));
        }
        tex.Apply();
        return tex;
    }

    /// <summary>两点间画虚线(纹理 Tile 模式, 段数由材质纹理缩放控制)。</summary>
    private static void DrawDashed(LineRenderer lr, Vector3 a, Vector3 b) {
        if (lr == null) return;
        lr.positionCount = 2;
        lr.SetPosition(0, a);
        lr.SetPosition(1, b);
        lr.gameObject.SetActive(true);   // 纹理缩放已定格在共享 pathMat, 不再逐帧访问材质
    }
}
