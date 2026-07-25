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
        public int Priority;     // 5:火炮/FDC, 4:弹药库/高价值, 3:装甲/工事, 2:普通, 1:灰区
        public bool IsArmored;
        public bool IsUnderground;
        public Vector3 WorldPos;
        public int ChildIndex;
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

    /// <summary>高价值目标自动满装药（优先 5 和 4）</summary>
    public static bool ShouldUseMaxCharge(TargetInfo t)
    {
        return t.Priority >= 4;
    }

    /// <summary>两个角度之间的最小差值 [0, 180]</summary>
    private static float AngleDelta(float a, float b)
    {
        float d = Mathf.Abs(a - b) % 360f;
        return d > 180f ? 360f - d : d;
    }
}
