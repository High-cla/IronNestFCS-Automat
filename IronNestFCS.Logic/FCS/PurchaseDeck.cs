using Il2Cpp;
using MelonLoader;
using UnityEngine;
using System.Collections;
using static System.Enum;

namespace IronNestFCS.Logic.FCS;

public class PurchaseDeck {
    private Dictionary<BulletType, Transform> bulletCards = new();
    private Transform? _powderCard;
    private LookAtTarget? _buyButton;


    public bool TryBind() {
        Rescan();
        return true;
    }

    /// <summary>扫描征用台卡片注册表。场景重建/热重载后旧 Transform 引用失效时重新扫描自愈(幂等)。
    /// 与 AphcheDeck 的延迟注入互补: 它补"卡后生成"的 APHE, 本方法补"整台重建"的全部卡。</summary>
    public void Rescan() {
        bulletCards.Clear();
        _powderCard = null;
        var requisitionConsole = GameObject.Find("Requisition Console");
        if (requisitionConsole == null) return;
        var cards = requisitionConsole.GetComponentsInChildren<PunchcardRuntime>();
        foreach (var card in cards) {
            MelonLogger.Msg($"[FCS] PurchaseDeck: Found card {card.CurrentDefinition.ID}");
            // APHE 特殊卡: 由 AphcheDeck 每局注入, ID="APShellMod", 不能靠 Replace 后 TryParse 命中
            if (card.CurrentDefinition.ID == AphcheDeck.CardId) {
                bulletCards[BulletType.APHE] = card.transform;
                continue;
            }
            if (TryParse(
                    card.CurrentDefinition.ID.Replace("SMOKE", "SMK").Replace("Shell", ""),
                    out BulletType type
                )) {
                bulletCards[type] = card.transform;
            }
            else if (card.CurrentDefinition.ID == "PowderCharges") {
                _powderCard = card.transform;
            }
        }
        
        var btn = requisitionConsole.transform.FindChild("Universal Button");
        _buyButton = btn == null ? null : btn.GetComponent<LookAtTarget>();
    }

    /// <summary>AphcheDeck 加卡成功后动态注册该卡，避免错过 TryBind 时机的卡片。</summary>
    public void RegisterCard(BulletType type, Transform card) {
        bulletCards[type] = card;
        MelonLogger.Msg($"[FCS] PurchaseDeck: RegisterCard {type} → {card.name}");
    }
    
    private DialInteractable GetLeftRightDial() {
        var consoleBox = GameObject.Find("Console Box").transform;
        return  consoleBox.GetComponentInChildren<DialInteractable>();
    }

    /// <summary>强制购买: 点击购买前把征用点数注满, 与征用点数脱钩(点数不足时购买按钮点击无效)。
    /// 用游戏自带 SetRequisitionPoints(实测 tampered 防篡改标记不置位), 每次采购都充值到 999999,
    /// 相当于无限点数。空引用安全(找不到账本时静默, 退化为原行为)。</summary>
    private static void EnsureFunded() {
        try {
            MissionStatsTracker.Instance?.SetRequisitionPoints(999999, false);
        }
        catch (Exception ex) {
            MelonLogger.Error($"[FCS] EnsureFunded failed: {ex.Message}");
        }
    }

    public IEnumerator BuyShell(BulletType type, LeftRight leftRight) {
        var card = bulletCards.GetValueOrDefault(type);
        if (card == null) {
            Rescan();   // 场景重建/首绑早于卡生成时自愈, 再试一次
            card = bulletCards.GetValueOrDefault(type);
        }
        if (card == null) {
            MelonLogger.Error($"[FCS] BuyShell: Can't find {type} card");
            yield break;
        }
        var target = new Vector3(6.4814f, -2.4675f, -22.0968f);
        card.position = target;
        card.GetComponent<DraggableItem>().MoveToSlot();
        yield return new WaitForSeconds(0.5f);
        
        switch (leftRight) {
            case LeftRight.Left:
                GetLeftRightDial().SetDialValue(0);
                break;
            case LeftRight.Right:
                GetLeftRightDial().SetDialValue(1);
                break;
        }
        EnsureFunded();   // 强制购买: 点数注满后再点购买, 不受征用点数限制
        yield return FcsSceneInteractor.WaitAndClick(_buyButton);
        yield return new WaitForSeconds(2f);
    }

    public IEnumerator BuyPowders() {
        if (_powderCard == null) {
            Rescan();   // 场景重建/首绑早于卡生成时自愈, 再试一次
        }
        if (_powderCard == null) {
            MelonLogger.Error("[FCS] BuyPowders: Can't find PowderCharges card");
            yield break;
        }
        _powderCard.position = new Vector3(6.4814f, -2.4675f, -22.0968f);
        _powderCard.GetComponent<DraggableItem>().MoveToSlot();
        // 与 BuyShell 一致：等卡牌入槽稳定后再点购买，避免点击早于入槽导致本次采购无效。
        yield return new WaitForSeconds(0.5f);
        EnsureFunded();   // 强制购买: 点数注满后再点购买, 不受征用点数限制
        yield return FcsSceneInteractor.WaitAndClick(_buyButton);
        yield return new WaitForSeconds(2f);
    }
    
}