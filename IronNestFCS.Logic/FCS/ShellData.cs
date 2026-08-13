namespace IronNestFCS.Logic.FCS;

/// <summary>
/// 弹种数据（维基静态表）: 毁伤半径(km) + 成本(申请点)。正式版价格。
/// 常用对敌弹仅 AP/HE/HCHE 三个。纯数据, 无 IL2CPP 引用。
/// </summary>
public static class ShellData
{
    /// <summary>地图比例: km / 世界单位（与 CalcDistance 一致）</summary>
    public const float KmPerWorldUnit = 3.8164f;

    /// <summary>km → 世界单位（毁伤半径等比较距离前转换）</summary>
    public static float KmToWorld(float km) => km / KmPerWorldUnit;

    /// <summary>
    /// 致死半径(km)——实测校准(2026-08-12):
    ///   HE 0.24km 4发不死(集群成员连续4轮存活)、HCHE 0.60km 不死 → 维基"毁伤半径"是满伤包络, 非致死半径。
    ///   单点(≈0km)一发必死。取值取下界一半, 留余量; 集群只包"真炸得死"的目标。
    ///   注: 0.27/0.63 原值会让集群成员活着, 同一落点被反复重打(实测 4 发 HE 空耗)。
    /// </summary>
    public static float BlastRadiusKm(BulletType t) => t switch
    {
        BulletType.AP => 0.08f,
        BulletType.HE => 0.12f,
        BulletType.HCHE => 0.30f,
        BulletType.LE => 0.08f,   // 轻弹, 单点用; 半径与 AP 相同(用户确认)
        BulletType.APHCHE => 0.5f, // 特殊复合弹(用户确认)
        _ => 0f
    };

    /// <summary>杀伤包络(维基毁伤半径): 包络内即可能被杀伤——友军禁区基数, 集群覆盖不用它(致死半径更小)</summary>
    public static float DamageRadiusKm(BulletType t) => t switch
    {
        BulletType.AP => 0.14f,
        BulletType.HE => 0.27f,
        BulletType.HCHE => 0.63f,
        BulletType.LE => 0.14f,   // 与 AP 相同(用户确认)
        BulletType.APHCHE => 1.0f, // 特殊复合弹(用户确认)
        _ => 0f
    };

    /// <summary>友军禁区半径 = 杀伤包络 + 20% 余量(不赌包络边缘)</summary>
    public static float FriendlySafeRadiusKm(BulletType t) => DamageRadiusKm(t) * 1.2f;

    /// <summary>成本(申请点)。正式版: AP/HE=10, HCHE=18; APHCHE=5(用户确认)。</summary>
    public static int Cost(BulletType t) => t switch
    {
        BulletType.HCHE => 18,
        BulletType.APHCHE => 5, // 特殊复合弹(用户确认)
        _ => 10
    };
}
