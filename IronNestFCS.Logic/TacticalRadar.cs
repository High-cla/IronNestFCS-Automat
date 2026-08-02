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
/// 战术雷达：从 FireMissionRoot 扫描敌对单位，调用 TacticalDecider 决策后自动生成射击任务。
/// 纯读 + 委托调用，不碰装填流水线。
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
    private const int RoleAmmo = 8;           // 弹药库
    private const int RoleHighValue = 16;     // 高价值

    // 地下/地堡单位常见名字关键词和 Icon
    private static readonly string[] UndergroundNameKeys = { "bunker", "underground", "shelter", "bombproof" };

    private readonly FSC fcs;
    private readonly HashSet<int> sweptIndices = new();
    private static bool entityPropsDumped;  // 一次性 dump Entity 所有属性名

    public bool AutoPlaceMarkers { get; set; } = true;
    public List<TacticalDecider.TargetInfo> AliveHostiles { get; private set; } = new();
    public int SweptCount => sweptIndices.Count;

    public TacticalRadar(FSC fcs) => this.fcs = fcs;

    public bool IsSwept(int childIndex) => sweptIndices.Contains(childIndex);
    public void MarkSwept(int childIndex) => sweptIndices.Add(childIndex);

    public void OnGui()
    {
        var alive = AliveHostiles;
        if (alive == null || alive.Count == 0) return;
        float right = Screen.width - 10f;
        float y = 120f;
        GUI.contentColor = new Color(0.72f, 0.65f, 0.55f);
        GUI.Label(new Rect(right - 140f, y, 140f, 20f), $"Hostiles: {alive.Count}  Swept: {SweptCount}");
    }

    /// <summary>扫描 FireMissionRoot 下的所有单位，更新存活敌对列表并按优先级+转角排序。</summary>
    public void Scan()
    {
        AliveHostiles.Clear();

        var fireMissionRoot = GameObject.Find("Fire Mission Root")?.transform;
        if (fireMissionRoot == null) return;

        var targets = new List<TacticalDecider.TargetInfo>();

        for (int i = 0; i < fireMissionRoot.childCount; i++)
        {
            var child = fireMissionRoot.GetChild(i);
            var loc = child.GetComponent<EntityLocation>();
            if (loc == null) continue;
            if (!IsAlive(loc, child)) continue;
            if (!TryReadRole(loc, out int role, out string icon, out int stars, out var immuneShells, out bool entityUnderground)) continue;

            bool isHostile = (role & RoleEnemy) != 0 || (role & RoleTarget) != 0;
            bool isAlly = (role & RoleAlly) != 0;
            bool isReference = (role & RoleReference) != 0;

            if (isReference || (isAlly && !isHostile)) continue;
            if (!isHostile) continue;

            int priority = CalcPriority(role, icon, stars);
            bool isArmored = (role & RoleFortification) != 0 || (role & RoleTank) != 0
                             || (role & RoleAmmo) != 0       // 弹药库
                             || (role & RoleHighValue) != 0  // 补给/高价值仓库
                             || icon.IndexOf("ammunition", StringComparison.OrdinalIgnoreCase) >= 0
                             || icon.IndexOf("cache", StringComparison.OrdinalIgnoreCase) >= 0
                             || icon.IndexOf("supply", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isUnderground = entityUnderground || IsUnderground(child.name, icon);

            targets.Add(new TacticalDecider.TargetInfo
            {
                Name = child.name,
                Angle = CalcAngle(child.position),
                Distance = CalcDistance(child.position),
                Priority = priority,
                IsArmored = isArmored,
                IsUnderground = isUnderground,
                WorldPos = child.position,
                ChildIndex = i,
                ImmuneShells = immuneShells
            });
        }

        TacticalDecider.SortTargets(targets, fcs.Turret.LastSetAngle);
        AliveHostiles = targets;

        // 每次扫描只打印一行汇总
        var summary = string.Join(" | ", targets.Select(t =>
            $"({t.Priority}){t.Name} {(t.IsUnderground ? "UG" : "")}{(t.IsArmored ? "ARM" : "")}"));
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

    // ─── 存活判断 ───

    private static bool IsAlive(EntityLocation loc, Transform t)
    {
        if (!t.gameObject.activeInHierarchy) return false;
        try
        {
            var enabledProp = loc.GetType().GetProperty("enabled",
                BindingFlags.Public | BindingFlags.Instance);
            if (enabledProp != null)
            {
                var val = enabledProp.GetValue(loc);
                if (val is bool b && !b) return false;
            }
        }
        catch { }
        return true;
    }

    // ─── 角色读取 ───

    private static bool TryReadRole(EntityLocation loc, out int role, out string icon, out int stars,
        out HashSet<string> immuneShells, out bool isUnderground)
    {
        role = -1; icon = ""; stars = 0; immuneShells = new HashSet<string>(); isUnderground = false;
        try
        {
            var entityProp = loc.GetType().GetProperty("Entity",
                BindingFlags.Public | BindingFlags.Instance);
            if (entityProp == null) return false;
            var entity = entityProp.GetValue(loc);
            if (entity == null) return false;

            var entType = entity.GetType();

            // 一次性 dump Entity 全属性（找地下标记的数据源）
            if (!entityPropsDumped)
            {
                entityPropsDumped = true;
                MelonLogger.Msg($"[Radar] Entity properties for '{loc.name}':");
                foreach (var p in entType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    try { MelonLogger.Msg($"[Radar]   .{p.Name} ({p.PropertyType.Name}) = {p.GetValue(entity)}"); }
                    catch { MelonLogger.Msg($"[Radar]   .{p.Name} ({p.PropertyType.Name}) = <error>"); }
                }
            }

            var roleProp = entType.GetProperty("Role",
                BindingFlags.Public | BindingFlags.Instance);
            if (roleProp != null)
            {
                var v = roleProp.GetValue(entity);
                if (v is int i) role = i;
                else if (v is Enum e) role = Convert.ToInt32(e);
            }

            var iconProp = entType.GetProperty("Icon",
                BindingFlags.Public | BindingFlags.Instance);
            if (iconProp != null)
            {
                var v = iconProp.GetValue(entity);
                if (v is string s) icon = s;
            }

            var starsProp = entType.GetProperty("Stars",
                BindingFlags.Public | BindingFlags.Instance);
            if (starsProp != null)
            {
                var v = starsProp.GetValue(entity);
                if (v is int si) stars = si;
            }

            // 读 Entity 自带的地下标记（FDC 等目标有独立的地下/地堡 tag）
            foreach (var propName in new[] { "IsUnderground", "Underground", "IsBunker", "Bunker" })
            {
                var ugProp = entType.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                if (ugProp == null) continue;
                var val = ugProp.GetValue(entity);
                if (val is bool b && b) { isUnderground = true; break; }
                if (val is int i && i != 0) { isUnderground = true; break; }
                if (val is string s && !string.IsNullOrEmpty(s)) { isUnderground = true; break; }
            }

            // 读取 ImmuneShells：可能是 string[]、Il2Cpp 数组或 IEnumerable
            var immuneProp = entType.GetProperty("ImmuneShells",
                BindingFlags.Public | BindingFlags.Instance);
            if (immuneProp != null)
            {
                var val = immuneProp.GetValue(entity);
                if (val is IEnumerable enumerable)
                {
                    foreach (var item in enumerable)
                    {
                        if (item != null)
                            immuneShells.Add(item.ToString() ?? "");
                    }
                }
            }

            return role >= 0;
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[Radar] TryReadRole failed for {loc.name}: {ex.Message}");
            return false;
        }
    }

    // ─── 优先级计算 ───

    private static bool IsUnderground(string name, string icon)
    {
        var low = name.ToLower();
        var lowIcon = icon.ToLower();

        // 名字关键词（地下/地堡/仓库/弹药库）
        foreach (var key in new[] {
            "bunker", "underground", "shelter", "bombproof", "pillbox", "dugout",
            "depot", "storage", "magazine", "cache", "armory", "warehouse",
            "subterranean", "tunnel", "cave", "vault", "casemate"
        })
            if (low.Contains(key)) return true;

        // Icon 里的标签（游戏可能用 underground / bunker / fortification 标记）
        foreach (var key in new[] { "underground", "bunker", "bombproof", "subterranean" })
            if (lowIcon.Contains(key)) return true;

        return false;
    }

    private static int CalcPriority(int role, string icon, int stars)
    {
        // 6: FDC（最高优先——暂停 CBT）
        bool isFdc = icon.ToLower().Contains("fire direction");
        if (isFdc) return 6;

        // 5: 火炮
        if ((role & RoleArtillery) != 0) return 5;

        // 4: 弹药库/高价值/3星以上
        if ((role & RoleAmmo) != 0 || (role & RoleHighValue) != 0) return 4;
        if (stars >= 3) return 4;

        // 3: 装甲/工事/1星以上
        if (stars >= 1) return 3;
        if ((role & RoleFortification) != 0 || (role & RoleTank) != 0) return 3;

        // 2: 普通敌人
        if ((role & RoleEnemy) != 0) return 2;

        return 1;
    }

    // ─── 坐标 / 角度 / 距离 ───

    private float CalcAngle(Vector3 worldPos)
    {
        var mapSurface = GameObject.Find("Draggable Surface")?.transform;
        var turret = fcs.MapTable.turret;
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
        var turret = fcs.MapTable.turret;
        if (mapSurface == null || turret == null) return 0f;

        var localPos = mapSurface.InverseTransformPoint(worldPos);
        var target = localPos - turret.localPosition;
        return target.magnitude * 3.8164f;
    }
}
