using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// 弹道射表: 每档装药(1-6)在 3-20km 各射程的飞行时间(秒), 数据来自实测 ToF.txt。
/// 用于面板"预计落弹": 解算后显示估计值, 开火后倒计时, 归零=估计落地。
/// 线性插值; 低于 3km 或超过该档最远射程时夹到边界值。
/// </summary>
public static class ToFTable
{
    private const float MinRangeKm = 3f;
    private const float MaxRangeKm = 20f;

    // [装药档-1][射程=3km+i]; 远端缺数据用该档最远射程的值填充(夹紧)
    private static readonly float[][] FlightSeconds =
    {
        new[] { 14f, 19f, 23f, 23f, 23f, 23f, 23f, 23f, 23f, 23f, 23f, 23f, 23f, 23f, 23f, 23f, 23f, 23f }, // 1 最远 5km
        new[] { 11f, 15f, 19f, 23f, 26.5f, 30f, 34f, 38f, 38f, 38f, 38f, 38f, 38f, 38f, 38f, 38f, 38f, 38f }, // 2 最远 10km
        new[] { 7f, 10f, 13f, 15f, 18f, 20f, 22.5f, 26f, 28f, 31f, 34f, 36.5f, 39f, 39f, 39f, 39f, 39f, 39f }, // 3 最远 15km
        new[] { 5f, 7f, 9f, 11f, 13f, 15f, 17f, 18.5f, 20f, 22f, 24f, 26f, 28f, 30f, 32f, 34f, 35.5f, 37f }, // 4 最远 20km
        new[] { 4f, 5.5f, 7f, 9f, 11f, 12.5f, 13.5f, 15f, 16f, 18f, 20f, 21f, 23f, 24f, 26f, 27f, 29f, 30f }, // 5
        new[] { 4f, 5f, 7f, 8f, 10f, 11f, 12f, 14f, 15f, 17f, 18f, 20f, 21f, 22f, 24f, 26f, 27f, 28f }, // 6
    };

    /// <summary>按射程+装药档估计飞行时间(秒)。charge=1..6, 越界夹紧。</summary>
    public static float FlightTime(float distanceKm, int charge)
    {
        charge = Mathf.Clamp(charge, 1, 6) - 1;
        var row = FlightSeconds[charge];
        float d = Mathf.Clamp(distanceKm, MinRangeKm, MaxRangeKm);
        float idx = d - MinRangeKm;
        int lo = (int)idx;
        if (lo >= row.Length - 1) return row[row.Length - 1];
        float frac = idx - lo;
        return row[lo] + (row[lo + 1] - row[lo]) * frac;
    }
}
