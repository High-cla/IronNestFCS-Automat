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

    /// <summary>毁伤半径(km)。未收录弹种返回 0。</summary>
    public static float BlastRadiusKm(BulletType t) => t switch
    {
        BulletType.AP => 0.14f,
        BulletType.HE => 0.27f,
        BulletType.HCHE => 0.63f,
        _ => 0f
    };

    /// <summary>成本(申请点)。正式版: AP/HE=10, HCHE=18。</summary>
    public static int Cost(BulletType t) => t switch
    {
        BulletType.HCHE => 18,
        _ => 10
    };
}
