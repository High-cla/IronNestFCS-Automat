using System.Collections;
using Il2Cpp;
using Il2CppLocalisation;
using Il2CppSleepyNodes;
using MelonLoader;
using Object = UnityEngine.Object;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// APHE 特殊复合弹卡：每局向 Requisition Console 注入一张 APShellMod 卡（复制 APShell + 借 HCHEShell 特效）。
/// 幂等：场景中已存在 APShellMod 卡则跳过。由 FcsModule.Initialize 通过 StartTrackedCoroutine 启动。
/// 属性（用户确认）：ImpactRadius=5, Damage=5, ShellId=APHE, Cost=5。
/// </summary>
public static class AphcheDeck
{
    /// <summary>注入卡的稳定 ID，TryBind/幂等检测都用它。</summary>
    public const string CardId = "APShellMod";

    /// <summary>属性常量（用户确认: 毁伤半径 5km, Damage 5）。</summary>
    public const float ImpactRadius = 5f;
    public const int ShellDamage = 5;

    /// <summary>
    /// 等待 PunchcardRuntime 就绪 → 幂等检查 → 复制 APShell 卡改造为 APHE 并注入。
    /// 素材缺失（找不到 APShell/HCHEShell）时打警告并安全退出。
    /// </summary>
    public static IEnumerator AddCardIfMissing(PurchaseDeck deck)
    {
        // 等 Requisition Console 的卡组加载完成
        while (Object.FindObjectsOfType<PunchcardRuntime>().Count == 0)
            yield return null;

        var objs = Object.FindObjectsOfType<PunchcardRuntime>();
        foreach (var obj in objs) {
            if (obj.CurrentDefinition.ID == CardId) {
                MelonLogger.Msg("[FCS] AphcheDeck: 卡已存在, 跳过注入");
                // 兜底: 确保采购槽位已注册(热重载后 TryBind 可能早于注入)
                deck.RegisterCard(BulletType.APHE, obj.transform);
                yield break;
            }
        }

        PunchcardDefinitionV2? apDef = null;
        PunchcardDefinitionV2? hcheDef = null;
        foreach (var obj in objs) {
            var def = obj.CurrentDefinition;
            if (def.ID == "APShell") apDef = def;
            if (def.ID == "HCHEShell") hcheDef = def;
        }
        if (apDef == null || hcheDef == null) {
            MelonLogger.Warning("[FCS] AphcheDeck: 找不到 APShell/HCHEShell 卡, 跳过注入");
            yield break;
        }

        var newDef = Object.Instantiate<PunchcardDefinitionV2>(apDef);
        newDef.Cost = ShellData.Cost(BulletType.APHE); // 5 (用户确认)

        // 复制节点图; PunchcardGraph 是 NodeGraph 的 Il2Cpp 派生
        newDef.Graph = ((NodeGraph)apDef.Graph).Copy().TryCast<PunchcardGraph>();

        if (newDef.Graph == null) {
            MelonLogger.Error("[FCS] AphcheDeck: PunchcardGraph 复制失败");
            yield break;
        }
        var nodes = ((NodeGraph)newDef.Graph).nodes;
        for (int i = 0; i < nodes.Count; i++) {
            if (!nodes[i].name.Contains("State_Add Shell")) continue;
            var addShell = nodes[i].TryCast<State_AddShell>();
            if (addShell == null) continue;

            // 复制 ShellDefinition 并覆盖为 APHE 属性
            var shelldef = Object.Instantiate<ShellDefinition>(addShell.Shell);
            shelldef.ImpactRadius = ImpactRadius;
            shelldef.Damage = ShellDamage;
            shelldef.ShellId = "APHE";
            addShell.Shell = shelldef;

            // 借 HCHEShell 的爆炸特效预制体
            var hcheNodes = ((NodeGraph)hcheDef.Graph).nodes;
            for (int j = 0; j < hcheNodes.Count; j++) {
                if (hcheNodes[j].name == "State_Add Shell") {
                    var hcheAdd = hcheNodes[j].TryCast<State_AddShell>();
                    if (hcheAdd != null && hcheAdd.Shell != null)
                        shelldef.ImpactEffectPrefab = hcheAdd.Shell.ImpactEffectPrefab;
                    break;
                }
            }
            break; // 只改第一个 State_Add Shell 节点
        }

        newDef.ID = CardId;
        newDef.Title = new TextIdentifier("APHE Shell");
        newDef.Description = new TextIdentifier("Get 1 hardened bunker-piercing high-capacity bursting charge shell.");

        var list = new Il2CppSystem.Collections.Generic.List<PunchcardDefinitionV2>();
        list.Add(newDef);
        Object.FindFirstObjectByType<RequisitionConsoleManager>().AddNewCardsToDeck(list);
        MelonLogger.Msg("[FCS] AphcheDeck: 已注入 APHE 卡 (APShellMod)");

        // 注入后注册采购槽位: TryBind 枚举卡组时卡还不存在, 必须动态补注册
        yield return null; // 等新卡实例化
        foreach (var card in Object.FindObjectsOfType<PunchcardRuntime>()) {
            if (card.CurrentDefinition.ID == CardId) {
                deck.RegisterCard(BulletType.APHE, card.transform);
                break;
            }
        }
    }
}
