using HarmonyInstance = HarmonyLib.Harmony;
using System.Collections;
using System.Linq;
using System.Reflection;
using Il2Cpp;
using IronNestFCS.Logic.FCS;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace IronNestFCS.Logic;

public enum LeftRight {
    Left,
    Right,
}

/// <summary>
/// 纯火控领域逻辑：查找游戏对象、读取游戏数据、操控游戏内交互（dial 等）。
/// 不含任何 UI / IMGUI / 生命周期框架代码——那些在 <see cref="FcsModule"/> 和 <see cref="FcsWindow"/> 里。
///
/// 重载安全规则：
///  - 不要在这里注册新的 IL2CPP 类型（同一类型进程内只能注册一次）。
///  - 每次实例用独立的 Harmony 实例；Shutdown 时 UnpatchSelf。
///  - 所有对 IL2CPP 对象的引用在 Shutdown 时清空，便于旧 ALC 回收。
/// </summary>
public class FSC
{
    private const string HarmonyId = "com.svr2kos2.ironnestfcs.logic";

    private HarmonyInstance? _harmony;
    
    private FcsSceneInteractor _sceneInteractor;
    private readonly PurchaseDeck _purchaseDeck = new();
    public readonly MapTable MapTable = new MapTable();
    public readonly BallisticCalculator BallisticCalculator = new BallisticCalculator();
    public readonly GunSystem LeftGun = new GunSystem();
    public readonly GunSystem RightGun = new GunSystem();
    public readonly Turret Turret = new Turret();
    public readonly TriggerConsole TriggerConsole = new();
    
    // ===== 任务调度 =====
    // 用户不再指定炮管：任务入队后由调度器派给空闲炮管，炮管打完一发自动拉下一个。
    // 所有读写都在 Unity 主线程（入队来自点击回调，派发/完成来自协程），无并发，无需锁。
    private readonly Queue<ArtilleryTask> _taskQueue = new();

    /// <summary>当前各炮管正在执行的任务；null 表示该炮管空闲。供 UI 显示与调度判断。</summary>
    public ArtilleryTask? LeftTask { get; private set; }
    public ArtilleryTask? RightTask { get; private set; }

    /// <summary>等待派发的任务数（已入队但还没分到炮管）。供 UI 显示。</summary>
    public int PendingCount => _taskQueue.Count;
    public Queue<ArtilleryTask> QueueCan => new Queue<ArtilleryTask>(_taskQueue);

    /// <summary>
    /// 控制台互斥锁：保护弹道计算器、确认开关台、采购台这三组全局唯一的"短操作"硬件。
    /// 临界区都很短（解算 / 确认弹 / 击发前的确认+击发），用完即放。
    /// </summary>
    private readonly CoroutineLock _deskLock = new();

    /// <summary>
    /// 炮塔方向角锁：方向角是全炮塔共享的，且一旦为某任务转到位，必须独占到这一发打出去为止
    /// （中途被另一任务转走就会打偏）。与 <see cref="_deskLock"/> 分开，是为了让本任务能在
    /// 后台早早抢占炮塔、与装填/升仰角重叠，而不挡住另一管炮在 deskLock 上的解算。
    ///
    /// 防死锁：凡同时需要两把锁处，一律"先 turret 后 desk"。本类只有击发段会嵌套两把锁
    /// （此时炮塔已由后台预约持有，再去抢 desk），解算/确认弹只单独用 desk，故无环、不死锁。
    /// </summary>
    private readonly CoroutineLock _turretLock = new();

    // 正在运行的协程句柄。Dispose 时全部停掉，避免热重载后旧 ALC 的协程继续执行导致崩溃。
    private readonly List<object> _runningCoroutines = new();
    public FSC() {
        this._sceneInteractor = new FcsSceneInteractor(this);
    }

    public bool IsBound { get; private set; } = false;

    /// <summary>查找并绑定游戏对象。返回 false 表示当前场景还没有目标控件。</summary>
    public bool TryBind()
    {
        // 每次重载创建全新的 Harmony 实例，避免与上一版补丁冲突。
        _sceneInteractor = new FcsSceneInteractor(this);
        _sceneInteractor.Initialize();
        _harmony = new HarmonyInstance(HarmonyId);
        _deskLock.Reset();
        _turretLock.Reset();
        IsBound = MapTable.TryBind()
                  && BallisticCalculator.TryBind()
                  && LeftGun.TryBind("Left")
                  && RightGun.TryBind("Right")
                  && _purchaseDeck.TryBind()
                  && Turret.TryBind()
                  && TriggerConsole.TryBind();
        MelonLogger.Msg("[FCS] Initialize: " + (IsBound ? "success" : "failed"));
        // 探针已完成使命，需要时取消注释：
        // if (IsBound) RunEnemyProbe();

        return IsBound;
    }

    /// <summary>
    /// 深探 FireMission 的 Il2Cpp Dictionary 结构：
    ///   .Entities → Dictionary&lt;string, MapEntity&gt; (目标数据库)
    ///   .RunningTimers → Dictionary&lt;string, TimerValue&gt; (CBT 计时器)
    /// Il2Cpp 的泛型字典与 .NET 标准 Dictionary API 不同，需要探查正确的读取方式。
    /// </summary>
    private void RunEnemyProbe()
    {
        try
        {
            MelonLogger.Msg("[FCS] === MapEntity deep probe ===");
            var fm = GameObject.Find("Fire Mission Root")?.GetComponent<FireMission>();
            if (fm == null) { MelonLogger.Msg("[FCS]   no FireMission"); return; }

            var fmType = fm.GetType();
            var entitiesProp = fmType.GetProperty("Entities", BindingFlags.Public | BindingFlags.Instance);
            if (entitiesProp == null) { MelonLogger.Msg("[FCS]   no Entities prop"); return; }
            var entities = entitiesProp.GetValue(fm);
            if (entities == null) { MelonLogger.Msg("[FCS]   Entities == null"); return; }

            // 获取 Count
            var countProp = entities.GetType().GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
            var total = countProp != null ? (int)countProp.GetValue(entities) : -1;
            MelonLogger.Msg($"[FCS] Entities.Count = {total}");

            // 通过 Il2Cpp 泛型 GetEnumerator() 枚举前 5 条
            var getEnum = entities.GetType().GetMethod("GetEnumerator", BindingFlags.Public | BindingFlags.Instance);
            if (getEnum == null) { MelonLogger.Msg("[FCS]   no GetEnumerator"); return; }
            var enumerator = getEnum.Invoke(entities, null);
            if (enumerator == null) { MelonLogger.Msg("[FCS]   GetEnumerator returned null"); return; }

            var enumType = enumerator.GetType();
            var moveNext = enumType.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.Instance);
            var currentProp = enumType.GetProperty("Current", BindingFlags.Public | BindingFlags.Instance);
            if (moveNext == null || currentProp == null) { MelonLogger.Msg("[FCS]   no MoveNext/Current on enumerator"); return; }

            int shown = 0;
            while (shown < 5 && (bool)moveNext.Invoke(enumerator, null)!)
            {
                var kvp = currentProp.GetValue(enumerator);
                if (kvp == null) continue;

                var kvpType = kvp.GetType();
                var keyProp = kvpType.GetProperty("Key", BindingFlags.Public | BindingFlags.Instance);
                var valueProp = kvpType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);

                var key = keyProp?.GetValue(kvp)?.ToString() ?? "?";
                var mapEntity = valueProp?.GetValue(kvp);

                MelonLogger.Msg($"[FCS] --- Entities['{key}'] ---");
                if (mapEntity == null) { MelonLogger.Msg("[FCS]   MapEntity = null"); shown++; continue; }

                var meType = mapEntity.GetType();
                MelonLogger.Msg($"[FCS]   MapEntity type: {meType.FullName}");
                MelonLogger.Msg($"[FCS]   MapEntity.ToString(): {mapEntity}");

                // Dump MapEntity 所有属性
                foreach (var p in meType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (p.Name is "ObjectClass" or "Pointer" or "WasCollected")
                        continue;
                    try
                    {
                        var v = p.GetValue(mapEntity);
                        if (v == null)
                            MelonLogger.Msg($"[FCS]   .{p.Name} ({p.PropertyType.Name}) = null");
                        else
                            MelonLogger.Msg($"[FCS]   .{p.Name} ({p.PropertyType.Name}) = {v}");
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Msg($"[FCS]   .{p.Name} ({p.PropertyType.Name}) → {ex.GetType().Name}: {ex.Message}");
                    }
                }
                shown++;
            }
            MelonLogger.Msg($"[FCS] === Enumerated {shown} MapEntity entries ===");

            // Dump coordinateRoot: 这是游戏网格坐标 → 地图桌面的映射桥梁
            DumpCoordinateRoot(fm);

            DumpTimerValue(fm);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[FCS] MapEntity probe failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private static void DumpCoordinateRoot(FireMission fm)
    {
        try
        {
            var crProp = fm.GetType().GetProperty("coordinateRoot", BindingFlags.Public | BindingFlags.Instance);
            if (crProp == null) { MelonLogger.Msg("[FCS] --- coordinateRoot: no property ---"); return; }
            var cr = crProp.GetValue(fm) as RectTransform;
            if (cr == null) { MelonLogger.Msg("[FCS] --- coordinateRoot: null ---"); return; }

            MelonLogger.Msg("[FCS] --- coordinateRoot RectTransform ---");
            MelonLogger.Msg($"[FCS]   rect = {cr.rect}");
            MelonLogger.Msg($"[FCS]   anchorMin = {cr.anchorMin}, anchorMax = {cr.anchorMax}");
            MelonLogger.Msg($"[FCS]   pivot = {cr.pivot}");
            MelonLogger.Msg($"[FCS]   sizeDelta = {cr.sizeDelta}");
            MelonLogger.Msg($"[FCS]   localPosition = {cr.localPosition}");
            MelonLogger.Msg($"[FCS]   localScale = {cr.localScale}");
            MelonLogger.Msg($"[FCS]   anchoredPosition = {cr.anchoredPosition}");
            MelonLogger.Msg($"[FCS]   lossyScale = {cr.lossyScale}");
            // 也 dump 父 Transform（可能是 Draggable Surface 或 Map 相关）
            var parent = cr.parent;
            if (parent != null)
                MelonLogger.Msg($"[FCS]   parent = '{parent.name}', parent.localScale = {parent.localScale}, parent.localPosition = {parent.localPosition}");

            // 尝试把 MapEntity.Position 通过 coordinateRoot 转换
            var entitiesProp = fm.GetType().GetProperty("Entities", BindingFlags.Public | BindingFlags.Instance);
            if (entitiesProp != null)
            {
                var entities = entitiesProp.GetValue(fm);
                if (entities != null)
                {
                    var getEnum = entities.GetType().GetMethod("GetEnumerator", BindingFlags.Public | BindingFlags.Instance);
                    if (getEnum != null)
                    {
                        var enumerator = getEnum.Invoke(entities, null);
                        if (enumerator != null)
                        {
                            var moveNext = enumerator.GetType().GetMethod("MoveNext", BindingFlags.Public | BindingFlags.Instance);
                            var currentProp = enumerator.GetType().GetProperty("Current", BindingFlags.Public | BindingFlags.Instance);
                            if (moveNext != null && currentProp != null && (bool)moveNext.Invoke(enumerator, null)!)
                            {
                                var kvp = currentProp.GetValue(enumerator);
                                var valueProp = kvp?.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                                var me = valueProp?.GetValue(kvp);
                                var posProp = me?.GetType().GetProperty("Position", BindingFlags.Public | BindingFlags.Instance);
                                if (posProp != null)
                                {
                                    var mapPos = (Vector3)posProp.GetValue(me)!;
                                    // 试 RectTransformUtility 转换
                                    var worldFromRect = cr.TransformPoint(mapPos);
                                    MelonLogger.Msg($"[FCS]   Test: MapEntity.Pos={mapPos} → coordRoot.TransformPoint={worldFromRect}");
                                    // 试 Draggable Surface TransformPoint
                                    var ds = GameObject.Find("Draggable Surface")?.transform;
                                    if (ds != null)
                                        MelonLogger.Msg($"[FCS]   Test: MapEntity.Pos={mapPos} → DragSurface.TransformPoint={ds.TransformPoint(mapPos)}");
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex) { MelonLogger.Warning($"[FCS] coordinateRoot dump: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static void DumpTimerValue(FireMission fm)
    {
        try
        {
            var timersProp = fm.GetType().GetProperty("RunningTimers", BindingFlags.Public | BindingFlags.Instance);
            if (timersProp == null) return;
            var timers = timersProp.GetValue(fm);
            if (timers == null) { MelonLogger.Msg("[FCS] --- TimerValue: no timers (empty dictionary) ---"); return; }

            // 尝试从 Values 获取元素
            var valuesProp = timers.GetType().GetProperty("Values", BindingFlags.Public | BindingFlags.Instance);
            if (valuesProp == null) return;
            var values = valuesProp.GetValue(timers);
            if (values == null) return;

            var getEnum = values.GetType().GetMethod("GetEnumerator", BindingFlags.Public | BindingFlags.Instance);
            if (getEnum == null) return;
            var enumerator = getEnum.Invoke(values, null);
            if (enumerator == null) return;

            var moveNext = enumerator.GetType().GetMethod("MoveNext", BindingFlags.Public | BindingFlags.Instance);
            var currentProp = enumerator.GetType().GetProperty("Current", BindingFlags.Public | BindingFlags.Instance);
            if (moveNext == null || currentProp == null) return;

            if ((bool)moveNext.Invoke(enumerator, null)!)
            {
                var tv = currentProp.GetValue(enumerator);
                if (tv != null)
                {
                    MelonLogger.Msg($"[FCS] --- TimerValue: {tv.GetType().FullName} ---");
                    foreach (var p in tv.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (p.Name is "ObjectClass" or "Pointer" or "WasCollected") continue;
                        try { MelonLogger.Msg($"[FCS]   .{p.Name} ({p.PropertyType.Name}) = {p.GetValue(tv)}"); }
                        catch (Exception ex) { MelonLogger.Msg($"[FCS]   .{p.Name} → {ex.GetType().Name}: {ex.Message}"); }
                    }
                }
            }
            else MelonLogger.Msg("[FCS] --- TimerValue: enumerator empty ---");
        }
        catch (Exception ex) { MelonLogger.Warning($"[FCS] TimerValue dump: {ex.GetType().Name}: {ex.Message}"); }
    }

    public (bool hasTimer, string keys) PollRunningTimers()
    {
        try
        {
            var fm = GameObject.Find("Fire Mission Root")?.GetComponent<FireMission>();
            if (fm == null) return (false, "no FireMission");

            var timersProp = fm.GetType().GetProperty("RunningTimers",
                BindingFlags.Public | BindingFlags.Instance);
            if (timersProp == null) return (false, "no RunningTimers prop");

            var timers = timersProp.GetValue(fm);
            if (timers == null) return (false, "RunningTimers == null");

            // 读 Count 属性
            var countProp = timers.GetType().GetProperty("Count",
                BindingFlags.Public | BindingFlags.Instance);
            if (countProp == null) return (false, "no Count prop");

            var count = (int)countProp.GetValue(timers);
            if (count == 0) return (false, "0 timers");

            // 读 Keys，拼接键名
            var keysProp = timers.GetType().GetProperty("Keys",
                BindingFlags.Public | BindingFlags.Instance);
            if (keysProp == null) return (true, $"count={count}, no Keys prop");

            var keys = keysProp.GetValue(timers);
            if (keys is not IEnumerable keyEnum) return (true, $"count={count}, keys not IEnumerable");

            var keyList = new List<string>();
            foreach (var k in keyEnum) keyList.Add(k.ToString() ?? "?");
            return (true, $"count={count}, keys=[{string.Join(", ", keyList)}]");
        }
        catch (Exception ex)
        {
            return (false, $"error: {ex.Message}");
        }
    }

    public void Update() {
        _sceneInteractor.Update();
    }

    /// <summary>键盘快捷键触发射击目标（小键盘 1-4）</summary>
    public void FireTarget(int targetId) {
        _sceneInteractor.FireTarget(targetId);
    }

    /// <summary>释放：撤销补丁、清空 IL2CPP 引用。</summary>
    public void Dispose()
    {
        // 停掉所有未完成的协程，否则热重载后旧 ALC 的协程仍会被 Unity 驱动 → 崩溃。
        foreach (var handle in _runningCoroutines) {
            try { MelonCoroutines.Stop(handle); }
            catch (Exception ex) { MelonLogger.Error($"[FCS] Stop coroutines failed: {ex}"); }
        }
        _runningCoroutines.Clear();

        // 清空调度状态，避免热重载后残留任务/槽位影响新一轮绑定。
        _taskQueue.Clear();
        LeftTask = null;
        RightTask = null;

        _sceneInteractor.ShutDown();
        try { _harmony?.UnpatchSelf(); }
        catch (Exception ex) { MelonLogger.Error($"[FCS] UnpatchSelf failed: {ex}"); }
        _harmony = null;
    }

    public IEnumerator ExposeAllEntities() {
        while (true) {
            foreach (var m in MapTable.GetAllFireMissionEntities()) {
                m.GetComponent<Image>().enabled = true;
            }

            yield return new WaitForSeconds(1f);
        }
    }

    /// <summary>
    /// 把任务加入调度队列。用户不指定炮管——调度器自动派给空闲炮管。
    /// 入队后立即尝试派发；若两管炮都忙，任务留在队列里，等某管炮打完自动拉取。
    /// 必须在主线程调用（点击回调即是）。
    /// </summary>
    public void EnqueueTask(ArtilleryTask task) {
        task.progress = Progress.Pending;
        _taskQueue.Enqueue(task);
        TryDispatch();
    }

    /// <summary>插队：任务放到队列最前面，用于高优先级目标</summary>
    public void EnqueueTaskFront(ArtilleryTask task) {
        task.progress = Progress.Pending;
        var existing = _taskQueue.ToArray();
        _taskQueue.Clear();
        _taskQueue.Enqueue(task);
        foreach (var t in existing) _taskQueue.Enqueue(t);
        TryDispatch();
    }

    /// <summary>把队首任务派给空闲炮管，直到没有空闲炮管或队列空。</summary>
    private void TryDispatch() {
        while (_taskQueue.Count > 0) {
            LeftRight slot;
            if (LeftTask == null) slot = LeftRight.Left;
            else if (RightTask == null) slot = LeftRight.Right;
            else break; // 两管炮都忙

            var task = _taskQueue.Dequeue();
            if (slot == LeftRight.Left) LeftTask = task;
            else RightTask = task;
            StartTaskRoutine(slot, task);
        }
    }

    /// <summary>
    /// 启动一个火控任务协程。用 MelonCoroutines 跑协程实现延时——
    /// 协程由 Unity 在主线程分帧驱动，yield 期间不阻塞、恢复后仍在主线程，
    /// 因此可安全访问 IL2CPP 对象。绝不能用 async/Task.Delay：其 continuation
    /// 会在线程池线程恢复，跨线程访问 IL2CPP 运行时会导致进程崩溃且无日志。
    /// </summary>
    private void StartTaskRoutine(LeftRight leftRight, ArtilleryTask task) {
        var handle = MelonCoroutines.Start(RunTaskRoutine(leftRight, task));
        _runningCoroutines.Add(handle);
    }

    /// <summary>任一炮管退膛时触发回调（WaitBackToIdle 完成后）。供雷达层刷新队列。</summary>
    public event Action? OnGunIdle;

    /// <summary>清空待处理队列（不碰正在执行的任务）。</summary>
    public void ClearPendingTasks() {
        _taskQueue.Clear();
    }

    /// <summary>炮管打完一发后释放槽位并尝试拉取队列里的下一个任务。</summary>
    private void ReleaseSlot(LeftRight leftRight) {
        if (leftRight == LeftRight.Left) LeftTask = null;
        else RightTask = null;
        TryDispatch();
    }

    private IEnumerator RunTaskRoutine(LeftRight leftRight, ArtilleryTask task) {
        var gunSys = leftRight == LeftRight.Left ? LeftGun : RightGun;

        // ===== 炮塔预约：任务一开始就在后台抢方向角并转向 =====
        // 方向旋转和装填/升仰角互不冲突。后台协程阻塞式抢炮塔锁（"一旦释放就立即获取"），
        // 一拿到就开始转向，与本任务接下来的整个装填+升仰角段重叠。等到击发前只需确认它转好，
        // 而不必等仰角转完再从头抢炮塔、再转向。方向角必须独占到这一发打出去为止，
        // 故锁一直持有到击发完成（WaitFire 后由 ReleaseOnce 归还）。
        var turret = new TurretReservation();
        // 独立的 fire-and-forget 协程，必须登记以便 Dispose 时一并 Stop，
        // 否则热重载后旧 ALC 的它仍被 Unity 驱动 → 崩溃。
        _runningCoroutines.Add(MelonCoroutines.Start(ReserveTurretAndRotate(task, turret)));

        var powderCount = task.useMaxCharge ? 6 : _sceneInteractor.maxCharge ? 6 : BallisticCalculator.MinimumCharge(task.distance);

        // ===== 临界区 1：解算 =====
        // 弹道计算器 / 确认台 / 采购台都是全局唯一硬件，必须串行。算完仰角即放，
        // 让另一管炮能立刻进来算它自己的弹道，与本管炮接下来的长装填段重叠。
        float elevation = 0f;
        bool viable = true;
        bool slotReleased = false;
        yield return _deskLock.Acquire();
        try {
            task.progress = Progress.Calculating;
            yield return BallisticCalculator.SetDistance(task.distance);
            yield return BallisticCalculator.SetDirection(task.angel);
            yield return BallisticCalculator.SetCharge(powderCount);
            yield return BallisticCalculator.SetShellType(task.bulletType);
            yield return BallisticCalculator.Calculate();
            elevation = BallisticCalculator.GetElevation();

            // 装药不足则补购。单次采购未必补满（且偶发点击早于卡牌入槽而失败），
            // 故循环购买直到够本次发射所需，避免"装药不足但非 0"时直接推进、卡住后续装填。
            // 加购买次数上限兜底：采购始终无效时不至于无限循环（每次约 2.5s）。
            var powderPurchaseAttempts = 0;
            while (gunSys.RemainingCharges() < powderCount) {
                yield return _purchaseDeck.BuyPowders();
                if (++powderPurchaseAttempts >= 10) {
                    MelonLogger.Error(
                        $"[FCS] {leftRight} 炮管：购买装药 {powderPurchaseAttempts} 次后仍不足 " +
                        $"{powderCount}（当前 {gunSys.RemainingCharges()}），停止补购。");
                    // 补购失败＝任务不可行，与弹种缺失的处理一致
                    task.progress = Progress.Failed;
                    viable = false;
                    break;
                }
            }

            if (viable) {
                task.progress = Progress.SelectingBullet;
                // 弹仓里没有目标弹种则采购（采购台也是共享硬件，放在锁内）。
                if (!gunSys.HaveBulletInCylinder(task.bulletType)) {
                    if (!gunSys.HaveEmptyShellInCylinder()) {
                        task.progress = Progress.Failed;
                        viable = false;
                    }
                    else {
                        yield return _purchaseDeck.BuyShell(task.bulletType, leftRight);
                    }
                }
            }
        }
        finally {
            _deskLock.Release();
        }

        try {
            if (!viable) {
                // 任务不可行：取消炮塔预约并归还（后台若尚未抢到，会在抢到后自行归还），
                // 并释放炮管槽位让队列里的下一个任务能用这管炮。
                turret.Canceled = true;
                ReleaseTurretOnce(turret);
                ReleaseSlot(leftRight);
                slotReleased = true;
                yield break;
            }

            // ===== 锁外：装填（每管炮独立，最耗时段，可与另一管炮全程并行）=====
            task.progress = Progress.LoadingBullet;
            yield return gunSys.LoadBullet(task.bulletType);
            // LoadBullet 只是点推弹杆按钮——炮弹从挂架到炮膛需要机械时间，
            // 不能立刻读 BulletInChamber()，否则会误判为装填失败。
            {
                var chamberTimeout = 0;
                while (gunSys.BulletInChamber() == null && chamberTimeout < 30) {
                    yield return new WaitForSeconds(0.5f);
                    chamberTimeout++;
                }
                if (gunSys.BulletInChamber() != task.bulletType.ToString()) {
                    MelonLogger.Error($"[FCS] {leftRight} 炮管：装填后弹种不匹配，" +
                                      $"期望 {task.bulletType}，实际 {gunSys.BulletInChamber() ?? "null"}");
                    task.progress = Progress.Failed;
                    yield break;
                }
            }

            task.progress = Progress.LoadingPowder;
            yield return gunSys.LoadPowder(powderCount);
            task.progress = Progress.WaitLoading;
            var loadTimeout = 0;
            while (!gunSys.CanFire()) {
                yield return new WaitForSeconds(1f);
                if (++loadTimeout >= 120) { // 2 分钟超时
                    MelonLogger.Error($"[FCS] {leftRight} 炮管：等待装填完成超时");
                    task.progress = Progress.Failed;
                    yield break;
                }
            }

            // ===== 锁外：升仰角（每管炮独立，最耗时段之一）=====
            // 仰角杆是本管炮专属，不碰共享硬件；此时后台多半已把方向角转好。
            task.progress = Progress.Aiming;
            yield return gunSys.SetElevation(elevation);

            // ===== 临界区 2：击发 =====
            // 此处不再现抢炮塔——炮塔早已由后台预约持有。只等它转到位（通常已就绪，瞬间通过），
            // 然后确认+击发。炮塔锁一直由本任务持有，直到击发完成才归还。
            task.progress = Progress.WaitingForFire;
            // ponytail: 炮塔按固定角速度旋转，必然到达，无需超时。
            // 超时 = 制造失败 = OnGunIdle 重新派任务 = 炮塔还在转又被抢 = 死循环。
            while (!turret.Ready) {
                yield return null;
            }
            try {
                yield return TriggerConsole.ConfirmTask();
                yield return TriggerConsole.ConfirmBullet();
                yield return TriggerConsole.ConfirmRotation();
                yield return TriggerConsole.ConfirmElevation();
                yield return TriggerConsole.ReadyToFire();
                yield return TriggerConsole.Arm(leftRight);
                if (_sceneInteractor.AutoFire) {
                    TriggerConsole.Fire();
                }
                yield return gunSys.WaitFire();
            }
            finally {
                ReleaseTurretOnce(turret);
            }

            // ===== 锁外：回位（仰角回 0，每管炮独立，最耗时段之一）=====
            task.progress = Progress.BackToIdle;
            yield return gunSys.WaitBackToIdle();
            task.progress = Progress.Finished;
            _sceneInteractor.TaskFinished(task);
            ReleaseSlot(leftRight);
            slotReleased = true;
        }
        finally {
            // 防泄漏：协程崩了或 yield break 没走到 ReleaseSlot 时兜底释放
            if (!slotReleased) {
                if (leftRight == LeftRight.Left) LeftTask = null;
                else RightTask = null;
                TryDispatch();
            }
            // 确保炮塔锁归还（已归还的 ReleaseTurretOnce 是幂等的）
            ReleaseTurretOnce(turret);
            // 通知雷达：有一门炮退膛完毕，可以刷新队列了
            try { OnGunIdle?.Invoke(); } catch { }
        }
    }

    /// <summary>
    /// 炮塔预约状态。三个标志全在主线程协作式调度下读写，无真正并发。
    /// 生命周期：后台 <see cref="ReserveTurretAndRotate"/> 抢锁→转向→置 Ready；
    /// 主流程击发后 / 任务放弃时 ReleaseTurretOnce 归还。Released 保证恰好归还一次。
    /// </summary>
    private sealed class TurretReservation {
        public bool Acquired;  // 已拿到炮塔锁
        public bool Ready;     // 已转到目标方向角
        public bool Canceled;  // 主流程已放弃本次预约
        public bool Released;  // 锁已归还（防重复 Release）
    }

    /// <summary>
    /// 后台预约炮塔并转向。阻塞式抢锁实现"一旦炮塔释放就立即获取"。
    /// 抢到后若发现已被取消则立即归还、不空转；否则转到目标方向并置 Ready，
    /// 此后炮塔由主流程在击发完成时归还（若转向期间被取消则在此自行归还）。
    /// </summary>
    private IEnumerator ReserveTurretAndRotate(ArtilleryTask task, TurretReservation res) {
        yield return _turretLock.Acquire();
        res.Acquired = true;
        if (res.Canceled) {
            ReleaseTurretOnce(res);
            yield break;
        }
        yield return Turret.SetRotation(task.angel);
        res.Ready = true;
        // 转向期间主流程可能已放弃（如解算失败）——此时主流程不会再击发，由这里归还。
        if (res.Canceled) {
            ReleaseTurretOnce(res);
        }
    }

    /// <summary>归还炮塔锁，保证恰好一次。仅在确实持有（Acquired）且未归还时执行。</summary>
    private void ReleaseTurretOnce(TurretReservation res) {
        if (res.Acquired && !res.Released) {
            res.Released = true;
            _turretLock.Release();
        }
    }
}
