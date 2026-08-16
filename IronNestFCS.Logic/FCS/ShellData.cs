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

    private static readonly HashSet<BulletType> killShells = new();

    /// <summary>注册杀伤弹判定(ShellDefinition.Damage>0; STAR/TEAR/WP Damage=0 非杀伤)。</summary>
    public static void RegisterKillShell(BulletType t, bool kill) {
        if (kill) killShells.Add(t); else killShells.Remove(t);
    }

    /// <summary>是否杀伤弹: 注册表优先; 未注册时排除 STAR/SMK 黑名单(历史行为)。</summary>
    public static bool IsKillShell(BulletType t) => killShells.Count > 0
        ? killShells.Contains(t)
        : t != BulletType.STAR && t != BulletType.SMK;

    /// <summary>毁伤半径(km): 硬编码表(历史维基/实测校准, 2026-08-12; APHE=0.5 用户确认, 10de1a2 原值)。
    /// 集群/覆盖判定用此确定值, 不读场景 ShellDefinition.ImpactRadius(遍历顺序不定会漂移)。</summary>
    public static float BlastRadiusKm(BulletType t) => HardcodedBlastRadiusKm(t);

    /// <summary>硬编码兜底表(历史维基/实测校准, 2026-08-12)。</summary>
    public static float HardcodedBlastRadiusKm(BulletType t) => t switch
    {
        BulletType.AP => 0.08f,
        BulletType.HE => 0.12f,
        BulletType.HCHE => 0.30f,
        BulletType.LE => 0.08f,   // 轻弹, 单点用; 半径与 AP 相同(用户确认)
        BulletType.APHE => 0.5f, // 特殊复合弹(用户确认, 回退 10de1a2 原值)
        _ => 0f
    };

    /// <summary>成本(申请点)。正式版: AP/HE=10, HCHE=18; APHE=5(用户确认)。</summary>
    public static int Cost(BulletType t) => t switch
    {
        BulletType.HCHE => 18,
        BulletType.APHE => 5, // 特殊复合弹(用户确认)
        _ => 10
    };
}
