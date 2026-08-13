# IronNestFCS.Logic/FCS/ — 域类包

**Generated:** 2026-08-13

## OVERVIEW

火控逻辑的领域组件层：弹道、弹种、采购、炮塔、目标、调度原语。被 FSC.cs（枢纽）与 TacticalRadar.cs 组合使用。多数为纯 C#（无 Il2Cpp 交互）或薄封装游戏控件。

## STRUCTURE

```
FCS/
├── ArtilleryTask.cs        # 任务模型：目标/弹种/装药/进度状态机
├── BallisticCalculator.cs  # 游戏内计算器 UI 绑定（SetDistance/Charge/Direction/Shell）
├── ClusterSolver.cs        # 集群目标解析
├── CoroutineLock.cs        # 协程级互斥锁（bool _held，主线程协作式）
├── GunSystem.cs            # 单管炮：装填/装药/仰角杆/退膛（Left/Right 各一实例）
├── MapTable.cs             # 地图桌：FireMission 绑定、Turret/GetTurretLocal/GetMarkTarget
├── PurchaseDeck.cs         # 采购卡：弹种卡解析、RegisterCard、BuyPowders
├── ShellData.cs            # 20 弹种数据表：BlastRadiusKm/DamageRadiusKm/Cost
├── TacticalDecider.cs      # 纯数据决策：弹种选择/优先级排序
├── TargetLeadSolver.cs     # 提前量解算：ChargeFor/Elevation/LeadPoint/IsMoving
├── TargetRegistry.cs       # 移动集群成员登记/释放（实体屏蔽）
├── ToFTable.cs             # 飞行时间表 FlightTime
├── TriggerConsole.cs       # 扳机：Arm/ReadyToFire/Fire
├── Turret.cs               # 单炮塔：SetDesiredRotation/AngleError（共享方位）
├── AphcheDeck.cs           # APHE 复合弹卡：每局注入 APShellMod（幂等）
└── (GameStateWatcher.cs 已删——死代码清除 a8b3591)
```

## WHERE TO LOOK

| 任务 | 位置 |
|------|------|
| 弹种属性（半径/成本） | `ShellData.cs`（三表 switch on BulletType） |
| 弹道纯数学 | `TargetLeadSolver.cs`（ChargeFor/Elevation/LeadPoint） |
| 游戏内计算器 UI 装饰 | `BallisticCalculator.cs`（SyncCalculatorVisual 调） |
| 采购/买弹 | `PurchaseDeck.cs`（APShellMod→APHE 特判在 TryBind） |
| 炮塔旋转 | `Turret.cs`（单实例，DesiredRotation=-angle） |
| 单管装填 | `GunSystem.cs`（每管独立 elevationLever） |
| 移动目标提前量 | `TargetLeadSolver.IsMoving` + `TargetRegistry` |

## CONVENTIONS

- **GunSystem/TriggerConsole 每管独立实例**，Turret 全局单实例（共享方位）——旋转并行需理解此约束
- 弹种基准：`BulletType` 枚举（GunSystem.cs:10-31，20 种，APHE=2 含复合弹）
- 采购失败信号：`PurchaseDeck.LastBuySucceeded`（bool，BuyShell 后读）
- 协程锁：`CoroutineLock.Acquire()` 必须配 try/finally，`Reset()` 供热重载
- 幂等加卡：AphcheDeck 检查场景已有 `APShellMod` 卡则跳过（不重复注入）
- 补购循环上限 10 次（FSC.cs 临界区 2），防无限循环

## ANTI-PATTERNS

- ❌ 直接用 `fcs.MapTable.Turret`（Player Turret Piece）算方向——会随拖动漂移；用 `GetTurretLocal()`
- ❌ 给 `CoroutineLock` 加超时（历史教训：强制抢锁导致死锁，DEVLOG.md 记录）
- ❌ 新 `MelonCoroutines.Start` 不登记
- ❌ 枚举游戏弹种名硬编码（正式版 20 种卡自动识别，PLCM/PCLM 替换细节在 GunSystem.cs:116）
