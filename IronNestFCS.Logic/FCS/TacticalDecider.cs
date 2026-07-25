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
        public float Angle;      // 方向角 (0-360)
        public float Distance;   // 距离 km
        public int Priority;     // 0-5
        public bool IsArmored;
        public Vector3 WorldPos;
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
    /// 根据目标特征推荐弹种。只改这里的映射表即可调优。
    /// </summary>
    public static BulletType PickAmmo(TargetInfo t)
    {
        // 高价值（火炮/FDC/工事）→ HCHE 大范围高毁伤
        if (t.Priority >= 3)
            return BulletType.HCHE;
        // 装甲 → AP 穿甲
        if (t.IsArmored)
            return BulletType.AP;
        // 默认 HE
        return BulletType.HE;
    }

    /// <summary>高价值目标自动满装药 (6包)</summary>
    public static bool ShouldUseMaxCharge(TargetInfo t)
    {
        return t.Priority >= 3;
    }

    /// <summary>两个角度之间的最小差值 [0, 180]</summary>
    private static float AngleDelta(float a, float b)
    {
        float d = Mathf.Abs(a - b) % 360f;
        return d > 180f ? 360f - d : d;
    }
}
