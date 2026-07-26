using System.Collections.Generic;
using IronNestFCS.Abstractions;
using IronNestFCS.Logic.FCS;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;

namespace IronNestFCS.Logic;

public class FcsModule : IFcsModule
{
    private readonly FSC fcs = new();
    private FcsWindow? window;
    private TacticalRadar? radar;

    private float lastScanTime;
    private float nextSweepTime;
    private float lastCbtPollTime;
    private bool sweepActive;
    private int lastCbtCount = -1;  // 检测 CBT timer 数量变化

    public bool Initialize()
    {
        window = new FcsWindow(fcs);
        radar = new TacticalRadar(fcs);
        fcs.OnGunIdle += OnGunIdle;
        bool bound = fcs.TryBind();
        return bound;
    }

    /// <summary>任一炮管退膛 → 扫描 → 清空队列 → 填入最新 Top 2</summary>
    private void OnGunIdle()
    {
        if (!sweepActive) return;
        // 两门炮可能在同帧退膛，只触发一次
        if (Time.time < nextSweepTime) return;
        nextSweepTime = Time.time + 0.5f;

        if (radar == null || !fcs.IsBound) return;
        radar.Scan();

        // 清空等待队列（不碰正在执行的任务）
        fcs.ClearPendingTasks();

        // 另一门炮正在打的目标编号，不要分配重复任务
        var busyTargets = new HashSet<int>();
        if (fcs.LeftTask != null) busyTargets.Add(fcs.LeftTask.targetId);
        if (fcs.RightTask != null) busyTargets.Add(fcs.RightTask.targetId);

        int added = 0;
        foreach (var t in radar.AliveHostiles)
        {
            if (added >= 2) break;
            // 不重复分配另一门炮正在打的目标
            if (busyTargets.Contains(t.ChildIndex + 1)) continue;

            var task = new ArtilleryTask
            {
                targetId = t.ChildIndex + 1,
                angel = t.Angle,
                distance = t.Distance,
                position = t.WorldPos,
                bulletType = TacticalDecider.ChooseShellType(t),
                useMaxCharge = TacticalDecider.ShouldUseMaxCharge(t)
            };

            if (t.Priority >= 4)
                fcs.EnqueueTaskFront(task);
            else
                fcs.EnqueueTask(task);

            added++;
        }
    }

    public void Update()
    {
        fcs.Update();

        // 被动扫描（只显示敌情）
        if (radar != null && fcs.IsBound && Time.time - lastScanTime > 5f)
        {
            radar.Scan();
            lastScanTime = Time.time;
        }

        // 轮询 CBT 计时器（每 5s），数量变化时打日志
        if (fcs.IsBound && Time.time - lastCbtPollTime > 5f)
        {
            lastCbtPollTime = Time.time;
            var (hasTimer, info) = fcs.PollRunningTimers();
            var currentCount = hasTimer ? 1 : 0;
            if (currentCount != lastCbtCount)
            {
                lastCbtCount = currentCount;
                MelonLogger.Msg($"[FCS] CBT {(hasTimer ? $"found: {info}" : $"poll: {info}")}");
            }
        }

        var kb = Keyboard.current;
        if (kb == null || !fcs.IsBound)
            return;

        // Numpad 0: 启动/停止扫荡循环
        if (kb.numpad0Key.wasPressedThisFrame || (kb.ctrlKey.isPressed && kb.digit0Key.wasPressedThisFrame))
        {
            sweepActive = !sweepActive;
            nextSweepTime = 0;  // 重设时间窗，防止立即触发的首轮被防重入窗口跳过
            if (sweepActive) OnGunIdle();
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
}
