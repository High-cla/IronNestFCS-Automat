using System.Collections.Generic;
using IronNestFCS.Logic.FCS;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace IronNestFCS.Logic;

/// <summary>
/// 地图 overlay: 把"打击中"任务(LeftTask/RightTask/InFlight)的意图与动作画到地图上。
/// 参考 KKTIME2024/IronNestFCS-Automat aa17af1 设计(overlay 功能, 后被其 Revert)。
/// 元素(用户确认 2026-08-15: 白字标签已删, 红线粗细减半):
///   毁伤圈(描边+淡填充, 半径=task.BlastRadiusKm 注册表同源)
///   火力线(玩家→落点, 深红)
///   移动目标: 前进路线(白虚线固定长)
/// 1Hz tick, 按任务创建/销毁槽(dict), 对象挂 Draggable Surface 下(地图静态, 仅坐标帧一致)。
/// 只读 FSC 公开 API, 保持 FSC 纯领域逻辑分离。Shutdown 销毁全部对象(热重载安全)。
/// </summary>
public class MapOverlay
{
    // ==== 调参项(构建机实测) ====
    private const float TickInterval = 1f;
    private const float RingWidthWorld = 0.05f;     // 圈描边宽(世界单位)
    private const float RingFillAlpha = 0.1f;       // 圈填充透明度(未击发)
    private const float RingFillAlphaFiring = 0.2f; // 圈填充透明度(在飞)
    private const float PathLengthKm = 1.5f;        // 移动路径固定可见长度(km)
    private const float LineWidthWorld = 0.015f;    // 线宽(世界单位, 用户确认减半)
    private const int CircleSegments = 48;
    private const int DashSegments = 6;             // 移动路径虚线段数

    private static readonly Color RingColor = new(0.75f, 0.15f, 0.15f);   // 深红偏亮(毁伤圈)
    private static readonly Color LineColor = new(0.55f, 0.05f, 0.05f);   // 深红(火力线, 同游戏语义)
    private static readonly Color PathColor = new(0.9f, 0.9f, 0.9f);      // 白(移动路径, 尺规语义)

    private readonly FSC fcs;
    private readonly Transform? mapSurface;
    private readonly List<GameObject> tracked = new();          // 热重载时销毁
    private readonly Dictionary<ArtilleryTask, Slot> slots = new();
    private Texture2D? dashTexture;                             // 虚线材质共享
    private float lastTick;

    public MapOverlay(FSC fcs) {
        this.fcs = fcs;
        mapSurface = GameObject.Find("Draggable Surface")?.transform;
    }

    /// <summary>一个任务对应的渲染槽(按任务创建/销毁)。</summary>
    private sealed class Slot {
        public LineRenderer? ring;
        public GameObject? fill;
        public LineRenderer? fireLine;
        public LineRenderer? path;
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

    /// <summary>热重载/卸载: 销毁全部渲染对象。</summary>
    public void Shutdown() {
        foreach (var go in tracked) { if (go != null) Object.Destroy(go); }
        tracked.Clear();
        slots.Clear();
    }

    // ==== 槽生命周期 ====

    private Slot CreateSlot() => new() {
        ring = MakeLine("OverlayRing"),
        fill = MakeFill(),
        fireLine = MakeLine("OverlayFireLine"),
        path = MakeDashedLine("OverlayPath"),
    };

    private void DestroySlot(Slot s) {
        foreach (var go in new[] { s.ring?.gameObject, s.fill, s.fireLine?.gameObject, s.path?.gameObject }) {
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
        Vector3 player = fcs.MapTable.GetTurretLocal();

        // 毁伤圈: 描边环(半径=注册表同源数据)
        if (s.ring != null && t.BlastRadiusKm > 0f) {
            float rMap = t.BlastRadiusKm / ShellData.KmPerWorldUnit;
            s.ring.positionCount = CircleSegments;
            for (int i = 0; i < CircleSegments; i++) {
                float a = i * 2f * Mathf.PI / CircleSegments;
                s.ring.SetPosition(i, impact + new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * rMap);
            }
        }
        // 填充盘
        if (s.fill != null && t.BlastRadiusKm > 0f) {
            float rMap = t.BlastRadiusKm / ShellData.KmPerWorldUnit;
            s.fill.transform.localPosition = impact;
            s.fill.transform.localScale = new Vector3(rMap * 2f, rMap * 2f, 1f);
            SetFillAlpha(s.fill, t.Fired ? RingFillAlphaFiring : RingFillAlpha);
        }

        // 火力线: 玩家 → 落点
        if (s.fireLine != null) {
            s.fireLine.positionCount = 2;
            s.fireLine.SetPosition(0, player);
            s.fireLine.SetPosition(1, impact);
        }

        // 移动目标: 前进路线(白虚线)
        bool showPath = t.IsMoving && TargetLeadSolver.IsMoving(t.AimVel);
        if (showPath) {
            Vector3 now = fcs.MapTable.WorldToMapLocal(t.AimP0 + t.AimVel * (Time.time - t.AimStartTime));
            float lenMap = PathLengthKm / ShellData.KmPerWorldUnit;
            DrawDashed(s.path, now, now + t.AimVel.normalized * lenMap);
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
        FcsSceneInteractor.SetColor(go, LineColor);   // URP 纯色, 设到 renderer.material
        return lr;
    }

    private LineRenderer MakeDashedLine(string name) {
        var go = NewChild(name);
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = false;
        lr.positionCount = 0;
        lr.startWidth = lr.endWidth = LineWidthWorld;
        lr.loop = false;
        FcsSceneInteractor.SetColor(go, PathColor);
        if (dashTexture == null) dashTexture = MakeDashTexture();
        if (dashTexture != null) {
            lr.textureMode = LineTextureMode.Tile;
            var mat = lr.material;
            if (mat != null) mat.mainTexture = dashTexture;
        }
        return lr;
    }

    /// <summary>半透明红盘(毁伤圈填充)。Quad 1x1 在 XY 平面, 双面渲染防朝向反。</summary>
    private GameObject MakeFill() {
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        if (mapSurface != null) go.transform.SetParent(mapSurface, false);
        var collider = go.GetComponent<Collider>();
        if (collider != null) Object.Destroy(collider);
        var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
        if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f);   // 透明
        if (mat.HasProperty("_Blend")) mat.SetFloat("_Blend", 0f);        // alpha 混合
        if (mat.HasProperty("_Cull")) mat.SetFloat("_Cull", 0f);          // 双面
        mat.color = new Color(RingColor.r, RingColor.g, RingColor.b, RingFillAlpha);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", mat.color);
        var mr = go.GetComponent<MeshRenderer>();
        if (mr != null) mr.material = mat;
        tracked.Add(go);
        return go;
    }

    private GameObject NewChild(string name) {
        var go = new GameObject(name);
        if (mapSurface != null) go.transform.SetParent(mapSurface, false);
        tracked.Add(go);
        return go;
    }

    private static void SetFillAlpha(GameObject fill, float alpha) {
        var mr = fill.GetComponent<MeshRenderer>();
        if (mr == null || mr.material == null) return;
        var c = mr.material.color;
        c.a = alpha;
        mr.material.color = c;
        if (mr.material.HasProperty("_BaseColor"))
            mr.material.SetColor("_BaseColor", c);
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
        var mat = lr.material;
        if (mat != null && mat.mainTexture != null) {
            mat.mainTextureScale = new Vector2(DashSegments, 1f);
            mat.mainTextureOffset = Vector2.zero;
        }
        lr.gameObject.SetActive(true);
    }
}
