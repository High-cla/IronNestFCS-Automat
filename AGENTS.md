# PROJECT KNOWLEDGE BASE — IronNestFCS-Automat

**Generated:** 2026-08-13
**Branch:** master

## OVERVIEW

《铁巢：重炮模拟器》的 MelonLoader 全自动火控 Mod（IL2CPP），svr2kos2 FCS 的 deep-fork：扫描全图敌情 → 自动弹道解算/弹种选择/采购装填/瞄准击发。核心玩法：Numpad 0 全自动扫荡循环。无网页/HTTP（用户硬约束）。

## 游戏固有约束（改代码前必读）

### 硬件物理
- 一个炮塔座圈，两管炮共用**同一方位角**；只有**仰角每管独立**。
  两管无法同时指向不同方位 —— 同方位才能齐射，异方位必须串行。
- 全局扳机 `TriggerConsole.Fire()`（AddEnergy）击发**所有已武装炮管**。
  武装/进击发段互斥（持锁者专权），否则异方位齐射必一发偏；
  同方位齐射门（`SalvoBearingToleranceDeg`=0.1°，移动目标限同实体）放行双弹齐射。
- 共享硬件只有两类：炮塔方位（`_turretLock`）、采购台/计算器/确认台（`_deskLock`）。
  装填、仰角杆、回位机构每管独立。
- 炮塔方位**仅击发段抢锁独占**（旋转全程锁外并行）；抢到锁后复检对齐（60s），
  未对齐则失败归还——防止另一管中途转走共享炮塔导致打偏。

### 单炮每发流程
每管炮一发一发的完整流程（`RunTaskRoutine` 遵循）：
**选弹种 → 装弹头 → 装发射药 → 调整仰角 → 发射 → 退膛回位**
- 选弹种：弹仓转目标弹，未解锁沿回退链换（LE/AP→HE→HCHE）。
- 装药决策在装弹完成后定格（装药量决定射程，移动目标加距离余量）。
- 仰角：静止目标一次性到位；移动目标装填后每帧追 aim(t) 直到收敛 + CanFire。
- 发射后退膛，`WaitBackToIdle` 等 ~13s 最小恢复窗口，回位完才算任务结束。

### 运行时
- MelonCoroutines 全部**主线程协作式调度** → 无真并发，bool 锁够用；
  **绝不能用 async/Task.Delay**（continuation 在线程池恢复，跨线程访问 IL2CPP 直接崩溃）。
- 协程被 Stop（热重载）时 finally 照常执行 → 锁必须 try/finally 释放。
- 热重载：Dispose 停全部协程、`UnpatchSelf`、清 IL2CPP 引用；
  同一 IL2CPP 类型进程内只能注册一次。
- `ReleaseTurretOnce` 的 `Released` = "主流程结束占用"（无论是否持锁）；
  后台 `while(!res.Released)` 据此退出。齐射门放宽后任务可无锁走完（非持锁者），
  锁由持锁者 finally 归还，后台兜底不泄漏。

### 射击/弹道
- STAR 照明弹不建击杀契约（不登记注册表）——照明弹的意义是暴露目标，登记反而触发 65s 在飞屏蔽。
- 移动目标：**冻结快照 + 匀速外推**，不连续跟踪；速度由雷达采样历史首尾差分估（4-5s 基线），
  装填期从雷达采纳速度后定格快照。目标停车（速度跌破阈值）退化为静态瞄准当前位置。
- 集群：HE/HCHE 只打软目标，友军禁区；落点 = MEC 圆心。移动集群按实体屏蔽成员（几何半径兜不住车列）。
- 注册表条目 `Fired` 标志决定 65s 过期；`MarkFired` 必须标记任务**全部**条目（不只第一个）。

## STRUCTURE

```
IronNestFCS-Automat/
├── IronNestFCS/              # 宿主 Mod（永不重载）：加载 Logic、监听 F9 热重载、转发生命周期
├── IronNestFCS.Abstractions/ # 契约：仅 IFcsModule 接口，唯一安全跨 ALC 类型
├── IronNestFCS.Logic/        # 火控逻辑（可回收 ALC，F9 卸载重载）
│   ├── FSC.cs                # 枢纽：任务调度/双管协调/击发链（最大文件 ~900 行）
│   ├── FcsModule.cs          # 模块入口：自动/手动模式门控、每帧驱动
│   ├── TacticalRadar.cs      # 敌情扫描、实体枚举、可见性 hack
│   ├── FcsSceneInteractor.cs # 场景 UI 交互、按钮/滑块绑定
│   ├── FcsWindow.cs          # IMGUI 状态面板
│   ├── ClickRaycaster.cs     # 地图点击命中
│   └── FCS/                  # 17 个域类（见子 AGENTS.md）
├── Shared/                   # NullablePolyfill（net6 兼容）
├── docs/                     # 文档
├── build.ps1                 # 构建脚本
└── DEVLOG.md                 # 开发日志
```

## WHERE TO LOOK

| 任务 | 位置 | 备注 |
|------|------|------|
| 任务调度/击发/炮塔锁 | `IronNestFCS.Logic/FSC.cs` | RunTaskRoutine 533-785 |
| 自动/手动模式切换 | `IronNestFCS.Logic/FcsModule.cs` | autoMode 门控 |
| 敌情扫描/实体可见 | `IronNestFCS.Logic/TacticalRadar.cs` | Scan + ForceVisible |
| 弹种/装药数据表 | `IronNestFCS.Logic/FCS/ShellData.cs` | 20 弹种三表 |
| 采购卡 | `IronNestFCS.Logic/FCS/PurchaseDeck.cs` | APShellMod 特判 |
| 弹道解算 | `IronNestFCS.Logic/FCS/BallisticCalculator.cs` + `TargetLeadSolver.cs` | 纯数学走 TargetLeadSolver |
| 热重载入口 | `IronNestFCS/FcsHostMod.cs` + `LogicReloader.cs` | F9 触发 |

## CONVENTIONS

- **不注册新 IL2CPP 类型**：Logic 只反射/操作既有游戏类型，不 `new` 游戏对象组件
- **协程必须登记**：`MelonCoroutines.Start` 的返回值必须进 `_runningCoroutines`（FSC.cs:98 附近）或 `StartTrackedCoroutine`，Dispose 统一 Stop
- **跨 ALC 只传 `IFcsModule`**（IronNestFCS.Abstractions/IFcsModule.cs:9）
- 坐标系统：网页/地图系 = coordinateRoot 反变换；炮塔基准 = **TurretLocation**（真实炮塔，MapTable.GetTurretLocal），Player Turret Piece 仅回退
- 炮塔锁：旋转全程并行（锁外），**仅击发段串行**（防双管齐射）；仰角杆每管独立可并行
- 日志前缀 `[FCS]`；装弹期可转方位、仰角静止（游戏物理）
- 全部 C# 文件：中文注释，telegraphic 风格

## ANTI-PATTERNS（THIS PROJECT）

- ❌ 在 Logic 里注册新 IL2CPP 类型 / 强类型引用未在 Il2CppAssemblies 的类型
- ❌ 未登记的裸 `MelonCoroutines.Start`（热重载会泄漏协程 → "Stop coroutines failed"）
- ❌ 跨 ALC 边界传非 IFcsModule 对象
- ❌ 用 `Player Turret Piece`（可拖动标记）当炮塔基准——会随玩家拖动漂移
- ❌ 把 `.codegraph/`、`verify_*.dll` 等临时产物提交进 git

## COMMANDS

```bash
# 构建（0 错 12-14 警告存量；GameDir 在 csproj 已指向本机 D:\steam）
dotnet build IronNestFCS.sln

# 热重载：build 后切回游戏按 F9（无需重启）
# 部署：Logic.dll 自动输出到游戏目录 UserData/IronNestFCS/
```

## NOTES

- 游戏目录：`D:\steam\steamapps\common\Iron Nest Heavy Turret Simulator`；日志 `MelonLoader/Latest.log`
- F9 热重载窗口查找：`FindWindow('UnityWndClass','Iron Nest: Heavy Turret Simulator')`，进程名 "Iron Nest Heavy Turret Simulator.exe"
- MelonGame 声明**不带冒号**："Iron Nest Heavy Turret Simulator"（带冒号会 incompatible；本仓 FcsHostMod 正确）
- 索引：codebase-memory-mcp 项目名 `D-git-IronNestFCS-Automat`（可能略过期）；codegraph 需项目内 `.codegraph/`（已 .gitignore）
- 游戏 shell 脚本变量（find/xargs/head/tail）被 alias 成 fd/sd/rg——用 fastctx_* 工具替代
