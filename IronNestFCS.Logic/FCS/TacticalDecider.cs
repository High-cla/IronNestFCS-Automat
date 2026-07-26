using System.Collections.Generic;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// 战术决策：纯数据层，不碰任何 IL2CPP 对象。
/// 输入：目标信息 + 当前炮塔角 → 输出：推荐的弹种、装药量和排序。
/// 流水线完全不动——只在创建 ArtilleryTask 时读这里的返回值。
/// </summary>
public static class TacticalDecider
{
    /// <summary>可直接用于排序的轻量目标快照</summary>
    public struct TargetInfo
    {
        public string Name;
        public float Angle;
        public float Distance;
        public int Priority;     // 6:FDC, 5:火炮, 4:弹药库/高价值, 3:装甲/工事, 2:普通, 1:其他
        public bool IsArmored;
        public bool IsUnderground;
        public Vector3 WorldPos;
        public int ChildIndex;
        /// <summary>目标免疫的弹种 ID 集合（如 {"HE"}），用于自动弹种选择</summary>
        public HashSet<string> ImmuneShells;
    }

    /// <summary>
    /// 排序：优先级降序 → 同优先级按与炮塔朝向的角度差升序。
    /// 减少炮塔来回转动的总时间。
    /// </summary>
    public static void SortTargets(List<TargetInfo> targets, float currentAngle)
    {
        targets.Sort((a, b) =>
        {
            int pc = b.Priority.CompareTo(a.Priority);
            if (pc != 0) return pc;

            float adA = AngleDelta(currentAngle, a.Angle);
            float adB = AngleDelta(currentAngle, b.Angle);
            return adA.CompareTo(adB);
        });
    }

    /// <summary>
    /// 满装药唯一收益是缩短飞行时间，仅在 CBT 竞速时有价值。
    /// 当前无法检测 CBT 状态，统一用最低装药。
    /// 恢复时改为：t.Priority >= 5（仅火炮/FDC 满装抢时间）。
    /// </summary>
    public static bool ShouldUseMaxCharge(TargetInfo t)
    {
        return false;
    }

    /// <summary>
    /// 自动弹种选择，成本优先：AP/HE = 3点，HCHE = 5点。
    /// 装甲/地下 → AP（穿透），其余 → HE（通用）。
    /// HCHE 仅作免疫降级备选。
    /// </summary>
    public static BulletType ChooseShellType(TargetInfo t)
    {
        var immune = t.ImmuneShells ?? new HashSet<string>();

        // 首选：装甲/地下需要穿透，其余 HE 足以解决
        BulletType primary = (t.IsArmored || t.IsUnderground) ? BulletType.AP : BulletType.HE;

        if (!immune.Contains(primary.ToString()))
            return primary;

        // 首选被免疫 → 换另一个低成本弹种
        var fallback = primary == BulletType.AP ? BulletType.HE : BulletType.AP;
        if (!immune.Contains(fallback.ToString()))
            return fallback;

        // 两个都被免疫 → 上 HCHE（贵但可用）
        return BulletType.HCHE;
    }

    /// <summary>两个角度之间的最小差值 [0, 180]</summary>
    private static float AngleDelta(float a, float b)
    {
        float d = Mathf.Abs(a - b) % 360f;
        return d > 180f ? 360f - d : d;
    }
}
