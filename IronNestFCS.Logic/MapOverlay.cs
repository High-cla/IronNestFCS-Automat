using IronNestFCS.Logic.FCS;
using Il2CppShapes;
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
/// 渲染: 游戏原生矢量库 Shapes(Il2CppShapesRuntime)——Disc(描边环) + Line(线/原生虚线),
///   自带材质与抗锯齿, 无需 URP shader/自定义虚线贴图/圆环手算多边形。
/// 1Hz tick, 按任务创建/销毁槽(dict), 对象挂 Draggable Surface 下(地图静态, 仅坐标帧一致)。
/// 只读 FSC 公开 API, 保持 FSC 纯领域逻辑分离。Shutdown 销毁全部对象(热重载安全)。
/// </summary>
public class MapOverlay
{
    // ==== 调参项(构建机实测) ====
    private const float TickInterval = 1f;
    private const float PathLengthKm = 1.5f;        // 移动路径固定可见长度(km)
    private const float LineWidthWorld = 0.0075f;   // 线宽(世界单位, 用户确认再减半)
    private const int OverlayQueue = 3500;          // 渲染队列: 压过透明航拍照片层(3000+), 深度并列时后画者胜(上游 64a0750)

    private static readonly Color LineColor = new(0.85f, 0.08f, 0.05f);   // 鲜红(火力线/毁伤圈, 深色地图上可见)
    private static readonly Color PathColor = new(0.9f, 0.9f, 0.9f);      // 白(移动路径, 尺规语义)

    private readonly FSC fcs;
    private readonly Transform? mapSurface;
    private readonly List<GameObject> tracked = new();          // 热重载时销毁
    private readonly Dictionary<ArtilleryTask, Slot> slots = new();
    private float lastTick;

    public MapOverlay(FSC fcs) {
        this.fcs = fcs;
        mapSurface = GameObject.Find("Draggable Surface")?.transform;
    }

    /// <summary>一个任务对应的渲染槽(按任务创建/销毁)。</summary>
    private sealed class Slot {
        public Disc? ring;
        public Line? fireLine;
        public Line? path;
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

    /// <summary>热重载/卸载: 销毁全部渲染对象(Shapes 组件材质随 GameObject 销毁)。</summary>
    public void Shutdown() {
        foreach (var go in tracked) { if (go != null) Object.Destroy(go); }
        tracked.Clear();
        slots.Clear();
    }

    // ==== 槽生命周期 ====

    private Slot CreateSlot() => new() {
        ring = MakeRing("OverlayRing", LineColor),
        fireLine = MakeLine("OverlayFireLine", LineColor, dashed: false),
        path = MakeLine("OverlayPath", PathColor, dashed: true),
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

        // 毁伤圈 + 火力线: 仅落点变化时更新(静态几何零更新)
        if (impactChanged) {
            // 毁伤圈: 描边环(Disc Ring, 半径=注册表同源数据), 圆心=落点
            if (s.ring != null) {
                if (t.BlastRadiusKm > 0f) {
                    s.ring.gameObject.SetActive(true);
                    s.ring.transform.localPosition = impact;
                    s.ring.Radius = t.BlastRadiusKm / ShellData.KmPerWorldUnit;
                } else {
                    s.ring.gameObject.SetActive(false);   // 无毁伤半径数据时隐藏
                }
            }
            // 火力线: 玩家 → 落点
            if (s.fireLine != null) {
                s.fireLine.Start = player;
                s.fireLine.End = impact;
            }
        }

        // 移动目标: 前进路线(白虚线, Shapes 原生 dash)
        bool showPath = t.IsMoving && TargetLeadSolver.IsMoving(t.AimVel);
        if (s.path != null) {
            if (showPath) {
                Vector3 now = fcs.MapTable.WorldToMapLocal(t.AimP0 + t.AimVel * (Time.time - t.AimStartTime));
                float lenMap = PathLengthKm / ShellData.KmPerWorldUnit;
                s.path.gameObject.SetActive(true);
                s.path.Start = now;
                s.path.End = now + t.AimVel.normalized * lenMap;
            } else {
                s.path.gameObject.SetActive(false);
            }
        }
    }

    // ==== 渲染对象工厂(Shapes 原生组件) ====

    /// <summary>Shapes.Line 实线/虚线: 父空间(map-local)坐标, 世界单位线宽, queue 3500。</summary>
    private Line MakeLine(string name, Color color, bool dashed) {
        var go = NewChild(name);
        var l = go.AddComponent<Line>();
        l.Geometry = LineGeometry.Flat2D;             // 平铺地图面(而非始终朝相机)
        l.ThicknessSpace = ThicknessSpace.Meters;     // 线宽=世界单位(与旧 LineRenderer 同尺度)
        l.Thickness = LineWidthWorld;
        l.Color = color;
        l.Dashed = dashed;                            // 原生虚线开关(dash 尺寸用默认, 需要时再调)
        l.RenderQueue = OverlayQueue;
        return l;
    }

    /// <summary>Shapes.Disc 描边环(毁伤圈): 圆心=transform 局部位置, 半径/线宽=世界单位。</summary>
    private Disc MakeRing(string name, Color color) {
        var go = NewChild(name);
        var d = go.AddComponent<Disc>();
        d.Type = DiscType.Ring;                       // 仅描边(非填充盘)
        d.RadiusSpace = ThicknessSpace.Meters;
        d.ThicknessSpace = ThicknessSpace.Meters;
        d.Thickness = LineWidthWorld;                 // 圈线宽 = 火力线同宽(用户确认)
        d.Color = color;
        d.RenderQueue = OverlayQueue;
        return d;
    }

    private GameObject NewChild(string name) {
        var go = new GameObject(name);
        if (mapSurface != null) go.transform.SetParent(mapSurface, false);
        tracked.Add(go);
        return go;
    }
}
