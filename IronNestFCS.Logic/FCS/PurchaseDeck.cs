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

    /// <summary>解除限量卡(ATMC 等 MaxUses 有限)一次性限制: 台上卡充值到 99 次防耗尽移除;
    /// 已被游戏移除的卡从定义表(AllDefinitions)重新生成(AddNewCardsToDeck)。限量弹无限使用。</summary>
    private static void UnlimitCards() {
        try {
            var mgr = RequisitionConsoleManager.Instance;
            if (mgr == null) return;
            var onDeck = new HashSet<string>();
            foreach (var card in mgr.GetAllCards()) {
                var d = card.CurrentDefinition;
                if (d == null) continue;
                onDeck.Add(d.ID);
                if (d.MaxUses > 0 && d.RemainingUses <= 1) {
                    d.RemainingUses = 99;
                    d.MaxUses = 99;
                    MelonLogger.Msg($"[FCS] 限量卡 {d.ID} 已解除限制 (RemainingUses=99)");
                }
            }
            if (mgr.AllDefinitions == null) return;
            var missing = new Il2CppSystem.Collections.Generic.List<PunchcardDefinitionV2>();
            foreach (var kv in mgr.AllDefinitions) {
                var d = kv.Value;
                if (d == null || d.MaxUses <= 0 || onDeck.Contains(d.ID)) continue;
                d.RemainingUses = 99;
                d.MaxUses = 99;
                missing.Add(d);
            }
            if (missing.Count > 0) {
                mgr.AddNewCardsToDeck(missing);
                var ids = "";
                foreach (var d in missing) ids += d.ID + ",";
                MelonLogger.Msg($"[FCS] 限量卡重新生成: {ids.TrimEnd(',')}");
            }
        }
        catch (Exception ex) {
            MelonLogger.Error($"[FCS] UnlimitCards failed: {ex.Message}");
        }
    }

    public IEnumerator BuyShell(BulletType type, LeftRight leftRight) {
        EnsureFunded();    // 强制购买: 点数注满
        UnlimitCards();    // 限量卡充值/补卡(用尽消失的卡先重新生成, Rescan 才能发现)
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
        EnsureFunded();    // 强制购买: 点数注满
        UnlimitCards();    // 限量卡充值/补卡
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