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

        int added = 0;
        int nextTargetId = 1;
        foreach (var t in radar.AliveHostiles)
        {
            if (added >= 2) break;
            if (busyIds.Contains(t.EntityId)) continue;

            var task = new ArtilleryTask
            {
                targetId = nextTargetId++,
                entityId = t.EntityId,
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

        // 轮询 CBT 计时器（每 5s），只在发现 timer 时打日志
        if (fcs.IsBound && Time.time - lastCbtPollTime > 5f)
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
