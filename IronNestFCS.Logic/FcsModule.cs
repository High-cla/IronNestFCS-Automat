using System.Collections.Generic;
using Il2Cpp;
using IronNestFCS.Abstractions;
using IronNestFCS.Logic.FCS;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace IronNestFCS.Logic;

public class FcsModule : IFcsModule
{
    private readonly FSC fcs = new();
    private FcsWindow? window;
    private TacticalRadar? radar;

    private float lastScanTime;
    private float nextSweepTime;
    private float lastCbtPollTime;
    private bool autoMode;          // 全自动模式:true=雷达接管;false=手动(雷达完全休眠)
    private int lastCbtCount = -1;  // 检测 CBT timer 数量变化

    public bool Initialize()
    {
        window = new FcsWindow(fcs);
        radar = new TacticalRadar(fcs);
        fcs.OnGunIdle += OnGunIdle;
        bool bound = fcs.TryBind();
        return bound;
    }

    /// <summary>任一炮管退膛 → 扫描 → 清空队列 → 给每门空闲炮管各派一个目标。扫荡中每 5s 也跑一次，用于在飞窗口到期后恢复派发。</summary>
    private void OnGunIdle()
    {
        if (!autoMode) return;
        if (Time.time < nextSweepTime) return;
        nextSweepTime = Time.time + 0.5f;

        if (radar == null || !fcs.IsBound) return;
        AdjustAllValves(0f);
        radar.Scan();

        fcs.ClearPendingTasks();

        // 用 EntityId 去重（替代旧的 ChildIndex）
        var busyIds = new HashSet<string>();
        if (fcs.LeftTask?.entityId is string l and not "") busyIds.Add(l);
        if (fcs.RightTask?.entityId is string r and not "") busyIds.Add(r);

        // 逐空闲炮管派发（TryDispatch 按 Left→Right 取队首，入队顺序即分配顺序）。
        // 在飞窗口约束：任一炮击发过的目标，InFlightWindow 内不再纳入派发——
        // 800mm 一发成杀，弹未落地就补一发 = 白烧整条射循环。窗口期内两管空等，
        // 由 Update 的周期重扫在窗口到期后恢复派发（含打偏存活的目标）。
        int nextTargetId = 1;
        foreach (var barrel in new[] { LeftRight.Left, LeftRight.Right })
        {
            if (barrel == LeftRight.Left ? fcs.LeftTask != null : fcs.RightTask != null) continue;

            var t = PickTarget(busyIds);
            if (t == null) continue;
            var ti = t.Value;  // TargetInfo 是 struct,Nullable 解包

            busyIds.Add(ti.EntityId);

            var task = new ArtilleryTask
            {
                targetId = nextTargetId++,
                entityId = ti.EntityId,
                angel = ti.Angle,
                distance = ti.Distance,
                position = ti.WorldPos,
                bulletType = TacticalDecider.ChooseShellType(ti),
                useMaxCharge = TacticalDecider.ShouldUseMaxCharge(ti)
            };
            fcs.EnqueueTask(task);
        }
    }

    /// <summary>
    /// 为某炮管挑目标：AliveHostiles 已按优先级排序，取第一个未占用且不在在飞窗口内的。
    /// 全部可选目标都在飞/被占用 → 返回 null，炮管空等；窗口到期后由周期重扫恢复派发。
    /// </summary>
    private TacticalDecider.TargetInfo? PickTarget(HashSet<string> busyIds)
    {
        foreach (var t in radar.AliveHostiles)
        {
            if (busyIds.Contains(t.EntityId)) continue;
            if (fcs.InFlight(t.EntityId)) continue;
            return t;
        }
        return null;
    }

    public void Update()
    {
        fcs.Update();

        // 被动扫描:全自动模式下每 5s 周期重扫+派发(在飞窗口到期后恢复派发,或双管全空时补派);
        // 手动模式雷达完全休眠
        if (radar != null && fcs.IsBound && autoMode && Time.time - lastScanTime > 5f)
        {
            lastScanTime = Time.time;
            OnGunIdle();
        }

        // 轮询 CBT 计时器（每 5s），只在发现 timer 时打日志——仅全自动模式
        if (fcs.IsBound && autoMode && Time.time - lastCbtPollTime > 5f)
        {
            lastCbtPollTime = Time.time;
            var (hasTimer, info) = fcs.PollRunningTimers();
            if (hasTimer && lastCbtCount == 0)
            {
                lastCbtCount = 1;
                MelonLogger.Msg($"[FCS] CBT active: {info}");
            }
            else if (!hasTimer && lastCbtCount != 0)
            {
                lastCbtCount = 0;
                MelonLogger.Msg("[FCS] CBT ended");
            }
        }

        // 手柄:Select 键切换全自动/手动模式(等价 Numpad 0)
        var gp = Gamepad.current;
        if (gp != null && gp.selectButton.wasPressedThisFrame && fcs.IsBound)
        {
            ToggleAutoMode();
            return;
        }

        var kb = Keyboard.current;
        if (kb == null || !fcs.IsBound)
            return;

        // Numpad 0: 切换全自动/手动模式
        if (kb.numpad0Key.wasPressedThisFrame || (kb.ctrlKey.isPressed && kb.digit0Key.wasPressedThisFrame))
        {
            ToggleAutoMode();
            return;
        }

        // Numpad 1-4: manual fire targets
        if (kb.numpad1Key.wasPressedThisFrame || (kb.ctrlKey.isPressed && kb.digit1Key.wasPressedThisFrame))
            fcs.FireTarget(1);
        else if (kb.numpad2Key.wasPressedThisFrame || (kb.ctrlKey.isPressed && kb.digit2Key.wasPressedThisFrame))
            fcs.FireTarget(2);
        else if (kb.numpad3Key.wasPressedThisFrame || (kb.ctrlKey.isPressed && kb.digit3Key.wasPressedThisFrame))
            fcs.FireTarget(3);
        else if (kb.numpad4Key.wasPressedThisFrame || (kb.ctrlKey.isPressed && kb.digit4Key.wasPressedThisFrame))
            fcs.FireTarget(4);
    }

    /// <summary>切换全自动/手动模式。切手动时清空自动队列(正在打的不打断,打完自然停)。</summary>
    private void ToggleAutoMode()
    {
        autoMode = !autoMode;
        nextSweepTime = 0;  // 重设时间窗，防止立即触发的首轮被防重入窗口跳过
        if (!autoMode)
        {
            fcs.ClearPendingTasks();   // 手动接管:清掉自动入队的队列
            // 原子化:已在炮管上装填的任务必须发射出去——标记强制自动击发,
            // 否则任务卡在等击发/等炮塔锁,手动任务永远派不进队列。
            if (fcs.LeftTask != null) fcs.LeftTask.forceFire = true;
            if (fcs.RightTask != null) fcs.RightTask.forceFire = true;
            MelonLogger.Msg("[FCS] 手动模式:雷达休眠,手动标点 T1-T4 接管");
        }
        else
        {
            OnGunIdle();
            MelonLogger.Msg("[FCS] 全自动模式:雷达接管");
        }
        if (window != null) window.AutoSweepEnabled = autoMode;
    }

    public void OnGui()
    {
        window?.OnGui();
        radar?.OnGui();
    }

    public void Shutdown()
    {
        fcs.OnGunIdle -= OnGunIdle;
        fcs.Dispose();
        window = null;
        radar = null;
    }

    /// <summary>找到所有蒸汽泄漏点，收紧最近阀门到指定值（0=拧紧, 999=全开）</summary>
    private static void AdjustAllValves(float value)
    {
        var dials = new List<(DialInteractable di, Vector3 pos)>();
        foreach (var go in Object.FindObjectsOfType<GameObject>(true))
        {
            var di = go.GetComponent<DialInteractable>();
            if (di != null) dials.Add((di, go.transform.position));
        }
        int done = 0;
        foreach (var go in Object.FindObjectsOfType<GameObject>(true))
        {
            if (go == null || !go.name.ToLower().Contains("steam leak")) continue;
            DialInteractable? nearest = null;
            float minDist = float.MaxValue;
            foreach (var (di, pos) in dials)
            {
                var d = (pos - go.transform.position).magnitude;
                if (d < minDist) { minDist = d; nearest = di; }
            }
            if (nearest == null) continue;
            nearest.SetDialValue(value);
            done++;
        }
        if (done > 0)
            MelonLogger.Msg($"[FCS] 已自动拧紧 {done} 个阀门");
    }
}
