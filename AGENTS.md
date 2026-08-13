# PROJECT KNOWLEDGE BASE — IronNestFCS-Automat

**Generated:** 2026-08-13
**Branch:** master

## OVERVIEW

《铁巢：重炮模拟器》的 MelonLoader 全自动火控 Mod（IL2CPP），svr2kos2 FCS 的 deep-fork：扫描全图敌情 → 自动弹道解算/弹种选择/采购装填/瞄准击发。核心玩法：Numpad 0 全自动扫荡循环。无网页/HTTP（用户硬约束）。

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
