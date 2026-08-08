using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Il2Cpp;
using IronNestFCS.Logic.FCS;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic;

/// <summary>
/// 战术雷达：从 FireMission.Entities Dictionary (MapEntity) 扫描敌对单位，
/// 调用 TacticalDecider 决策后自动生成射击任务。
/// 相比旧的 FireMissionRoot.children 方案，Entities 包含全部 40 个目标槽位，
/// 包括动态 spawn 的第二波 FDC/火炮。
/// </summary>
public class TacticalRadar
{
    private const int RoleEnemy = 1;
    private const int RoleAlly = 2;
    private const int RoleTarget = 32;
    private const int RoleArtillery = 128;
    private const int RoleFortification = 65536;
    private const int RoleTank = 262144;
    private const int RoleReference = 33554432;
    private const int RoleAmmo = 8;
    private const int RoleHighValue = 16;

    private readonly FSC fcs;
    private readonly HashSet<string> sweptIds = new();

    // 移动侦测:上次扫描的实体位置快照(EntityId → 位置/时间),相邻扫描差分估速。
    // 正式版出现移动目标(匀速直线为主),先收集真实速度数据再定预测策略。
    private readonly Dictionary<string, (Vector3 pos, float time)> moveSnapshots = new();

    // 缓存的 FireMission 引用（按 F9 重载后失效，Scan 内自动刷新）
    private FireMission? _cachedFm;
    private PropertyInfo? _entitiesProp;
    private RectTransform? _coordinateRoot;   // 网格坐标→世界坐标的桥梁

    public bool AutoPlaceMarkers { get; set; } = true;
    public List<TacticalDecider.TargetInfo> AliveHostiles { get; private set; } = new();
    public int SweptCount => sweptIds.Count;

    public TacticalRadar(FSC fcs) => this.fcs = fcs;

    public bool IsSwept(string entityId) => sweptIds.Contains(entityId);
    public void MarkSwept(string entityId) => sweptIds.Add(entityId);

    public void OnGui()
    {
        var alive = AliveHostiles;
        if (alive == null || alive.Count == 0) return;
        float right = Screen.width - 10f;
        float y = 120f;
        GUI.contentColor = new Color(0.72f, 0.65f, 0.55f);
        GUI.Label(new Rect(right - 140f, y, 140f, 20f), $"Hostiles: {alive.Count}  Swept: {SweptCount}");
    }

    /// <summary>
    /// 从 FireMission.Entities Dictionary 扫描全部 MapEntity，
    /// 按 IsAlive && 敌对 过滤，按优先级+转角排序。
    /// </summary>
    public void Scan()
    {
        AliveHostiles.Clear();

        var (fm, entities) = GetEntitiesDict();
        if (entities == null) return;

        var targets = new List<TacticalDecider.TargetInfo>();

        // 反射调用 Il2Cpp Dictionary 的 GetEnumerator / MoveNext / Current
        var getEnum = entities.GetType().GetMethod("GetEnumerator", BindingFlags.Public | BindingFlags.Instance);
        if (getEnum == null) return;

        var enumerator = getEnum.Invoke(entities, null);
        if (enumerator == null) return;

        var enumType = enumerator.GetType();
        var moveNext = enumType.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.Instance);
        var currentProp = enumType.GetProperty("Current", BindingFlags.Public | BindingFlags.Instance);
        if (moveNext == null || currentProp == null) return;

        while ((bool)moveNext.Invoke(enumerator, null)!)
        {
            var kvp = currentProp.GetValue(enumerator);
            if (kvp == null) continue;

            var kvpType = kvp.GetType();
            var keyProp = kvpType.GetProperty("Key", BindingFlags.Public | BindingFlags.Instance);
            var valueProp = kvpType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
            if (keyProp == null || valueProp == null) continue;

            var key = keyProp.GetValue(kvp)?.ToString() ?? "";
            var me = valueProp.GetValue(kvp);
            if (me == null) continue;

            var meType = me.GetType();

            // IsAlive
            var aliveProp = meType.GetProperty("IsAlive", BindingFlags.Public | BindingFlags.Instance);
            if (aliveProp == null) continue;
            if (aliveProp.GetValue(me) is not bool isAlive || !isAlive) continue;

            // Role
            var roleProp = meType.GetProperty("Role", BindingFlags.Public | BindingFlags.Instance);
            if (roleProp == null) continue;
            var roleVal = roleProp.GetValue(me);
            int role = roleVal is int ri ? ri : roleVal is Enum e ? Convert.ToInt32(e) : -1;
            if (role < 0) continue;

            bool isHostile = (role & RoleEnemy) != 0 || (role & RoleTarget) != 0;
            bool isAlly = (role & RoleAlly) != 0;
            bool isReference = (role & RoleReference) != 0;
            if (isReference || (isAlly && !isHostile)) continue;
            if (!isHostile) continue;

            // Icon
            var iconProp = meType.GetProperty("Icon", BindingFlags.Public | BindingFlags.Instance);
            var icon = iconProp?.GetValue(me) is string s ? s : "";

            // Name
            var nameProp = meType.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
            var name = nameProp?.GetValue(me) is string sn ? sn : key;

            // Stars
            var starsProp = meType.GetProperty("Stars", BindingFlags.Public | BindingFlags.Instance);
            int stars = starsProp?.GetValue(me) is int st ? st : 0;

            // Armour
            var armourProp = meType.GetProperty("Armour", BindingFlags.Public | BindingFlags.Instance);
            int armour = armourProp?.GetValue(me) is int ar ? ar : 0;

            // Position (MapEntity.Position 是地图坐标系)
            var posProp = meType.GetProperty("Position", BindingFlags.Public | BindingFlags.Instance);
            var mapPos = posProp?.GetValue(me) is Vector3 mp ? mp : Vector3.zero;

            // ImmuneShells
            var immune = new HashSet<string>();
            var immuneProp = meType.GetProperty("ImmuneShells", BindingFlags.Public | BindingFlags.Instance);
            if (immuneProp != null)
            {
                var iv = immuneProp.GetValue(me);
                if (iv is IEnumerable ie)
                    foreach (var item in ie)
                        if (item != null) immune.Add(item.ToString() ?? "");
            }

            // 地下检测：名字/Icon 关键词
            bool isUnderground = IsUnderground(name, icon);

            // 装甲判断：MapEntity.Armour > 0 或 Role/Icon 匹配
            bool isArmored = armour > 0
                             || (role & RoleFortification) != 0
                             || (role & RoleTank) != 0
                             || (role & RoleAmmo) != 0
                             || (role & RoleHighValue) != 0
                             || icon.IndexOf("ammunition", StringComparison.OrdinalIgnoreCase) >= 0
                             || icon.IndexOf("cache", StringComparison.OrdinalIgnoreCase) >= 0
                             || icon.IndexOf("supply", StringComparison.OrdinalIgnoreCase) >= 0
                             || icon.IndexOf("fire direction", StringComparison.OrdinalIgnoreCase) >= 0;

            // 坐标：优先从 EntityLocation 取；无 Location（未 spawn）用 coordinateRoot 转换
            var locProp = meType.GetProperty("Location", BindingFlags.Public | BindingFlags.Instance);
            var location = locProp?.GetValue(me) as EntityLocation;
            Vector3 worldPos;
            if (location != null)
            {
                worldPos = location.transform.position;
            }
            else if (_coordinateRoot != null)
            {
                worldPos = _coordinateRoot.TransformPoint(mapPos);
            }
            else
            {
                continue;
            }

            // 移动侦测:与上次扫描位置差分 → 速度向量(桌面单位/秒 ×3.8164 = km/s)
            bool isMoving = false;
            Vector3 velocity = Vector3.zero;
            if (moveSnapshots.TryGetValue(key, out var snap) && Time.time - snap.time > 1f)
            {
                float dt = Time.time - snap.time;
                Vector3 disp = worldPos - snap.pos;
                velocity = disp / dt;
                if (velocity.magnitude * 3.8164f > 0.001f)  // 约 1 m/s 阈值
                {
                    isMoving = true;
                    MelonLogger.Msg($"[Radar] MOVING: '{name}' ({key}) " +
                                    $"v={velocity.magnitude * 3.8164f:F3}km/s 位移={disp.magnitude * 3.8164f:F3}km/{dt:F1}s " +
                                    $"from {snap.pos} to {worldPos}");
                }
            }
            moveSnapshots[key] = (worldPos, Time.time);

            targets.Add(new TacticalDecider.TargetInfo
            {
                Name = name,
                EntityId = key,
                Angle = CalcAngle(worldPos),
                Distance = CalcDistance(worldPos),
                Priority = CalcPriority(role, icon, stars),
                IsArmored = isArmored,
                IsUnderground = isUnderground,
                WorldPos = worldPos,
                IsMoving = isMoving,
                Velocity = velocity,
                ChildIndex = 0, // 不再使用，保留兼容
                ImmuneShells = immune
            });
        }

        TacticalDecider.SortTargets(targets, fcs.Turret.LastSetAngle);
        AliveHostiles = targets;

        var summary = string.Join(" | ", targets.Select(t =>
            $"({t.Priority}){t.EntityId} {(t.IsUnderground ? "UG" : "")}{(t.IsArmored ? "ARM" : "")}"));
        MelonLogger.Msg($"[Radar] {targets.Count} hostiles: {summary}");

        if (AutoPlaceMarkers)
        {
            for (int i = 1; i <= 4; i++)
            {
                if (i <= targets.Count)
                    fcs.MapTable.SetMarkerWorldPos(i, targets[i - 1].WorldPos);
                else
                    fcs.MapTable.ResetMarker(i);
            }
        }
    }

    /// <summary>获取 FireMission.Entities Dictionary（带缓存，F9 重载自动刷新）</summary>
    private (FireMission? fm, object? dict) GetEntitiesDict()
    {
        try
        {
            if (_cachedFm != null && _entitiesProp != null)
            {
                try { var test = _entitiesProp.GetValue(_cachedFm); if (test != null) return (_cachedFm, test); }
                catch { }
            }

            _cachedFm = null;
            _entitiesProp = null;
            _coordinateRoot = null;

            var fmGo = GameObject.Find("Fire Mission Root");
            if (fmGo == null) return (null, null);
            _cachedFm = fmGo.GetComponent<FireMission>();
            if (_cachedFm == null) return (null, null);

            _entitiesProp = _cachedFm.GetType().GetProperty("Entities", BindingFlags.Public | BindingFlags.Instance);
            if (_entitiesProp == null) return (null, null);

            // 缓存 coordinateRoot：网格坐标 → 世界坐标的桥梁
            var crProp = _cachedFm.GetType().GetProperty("coordinateRoot", BindingFlags.Public | BindingFlags.Instance);
            _coordinateRoot = crProp?.GetValue(_cachedFm) as RectTransform;

            var dict = _entitiesProp.GetValue(_cachedFm);
            return (_cachedFm, dict);
        }
        catch
        {
            return (null, null);
        }
    }

    // ─── 地下检测 ───

    private static bool IsUnderground(string name, string icon)
    {
        var low = name.ToLower();
        var lowIcon = icon.ToLower();
        foreach (var key in new[] {
            "bunker", "underground", "shelter", "bombproof", "pillbox", "dugout",
            "depot", "storage", "magazine", "cache", "armory", "warehouse",
            "subterranean", "tunnel", "cave", "vault", "casemate"
        })
            if (low.Contains(key)) return true;
        foreach (var key in new[] { "underground", "bunker", "bombproof", "subterranean" })
            if (lowIcon.Contains(key)) return true;
        return false;
    }

    // ─── 优先级计算 ───

    private static int CalcPriority(int role, string icon, int stars)
    {
        bool isFdc = icon.ToLower().Contains("fire direction");
        if (isFdc) return 6;

        if ((role & RoleArtillery) != 0) return 5;

        if ((role & RoleAmmo) != 0 || (role & RoleHighValue) != 0) return 4;
        if (stars >= 3) return 4;

        if (stars >= 1) return 3;
        if ((role & RoleFortification) != 0 || (role & RoleTank) != 0) return 3;

        if ((role & RoleEnemy) != 0) return 2;

        return 1;
    }

    // ─── 坐标计算（世界坐标 → 地图坐标系）───

    private float CalcAngle(Vector3 worldPos)
    {
        var mapSurface = GameObject.Find("Draggable Surface")?.transform;
        var turret = fcs.MapTable.Turret;
        if (mapSurface == null || turret == null) return 0f;
        var localPos = mapSurface.InverseTransformPoint(worldPos);
        var target = localPos - turret.localPosition;
        var angle = Vector3.SignedAngle(target, Vector3.up, Vector3.forward);
        if (angle < 0) angle += 360;
        return angle;
    }

    private float CalcDistance(Vector3 worldPos)
    {
        var mapSurface = GameObject.Find("Draggable Surface")?.transform;
        var turret = fcs.MapTable.Turret;
        if (mapSurface == null || turret == null) return 0f;
        var localPos = mapSurface.InverseTransformPoint(worldPos);
        var target = localPos - turret.localPosition;
        return target.magnitude * 3.8164f;
    }
}
