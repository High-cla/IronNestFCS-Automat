# IronNestFCS-Automat

[简体中文](#简体中文) | [English](#english)

基于 [svr2kos2](https://github.com/svr2kos2) FCS 的全自动火控 Mod（战术雷达参考 [gxpppp](https://github.com/gxpppp/IronNestFCS) 的实现），为 *[Iron Nest: Heavy Turret Simulator](https://store.steampowered.com/app/4300500/)* 的重型炮塔加入一套**全自动火控系统**：按下 Numpad 0，剩下的交给铁巢！

A deep-fork of [svr2kos2](https://github.com/svr2kos2)'s FCS (tactical radar inspired by [gxpppp](https://github.com/gxpppp/IronNestFCS)) — a **fully automatic Fire Control System** for *[Iron Nest: Heavy Turret Simulator](https://store.steampowered.com/app/4300500/)*: press Numpad 0 and let the mod handle the rest.

---

## 简体中文

### 这是什么

为《铁巢：重炮模拟器》编写的 MelonLoader Mod。本仓库是 svr2kos2 原版 FCS 的分支：开启后 Mod 持续扫描全图敌情，自动完成弹道解算、弹种选择、采购装填、瞄准击发。**支持正式版全部 20 种弹种（采购卡自动识别）**。

### 与上游的区别

本仓库是 [svr2kos2](https://github.com/svr2kos2) 原版 FCS 的 deep-fork（经 [KKTIME2024](https://github.com/KKTIME2024/IronNestFCS-Automat) 演进，并已合并其 `da641f3` 齐射门 / `53bbbf0` 并行确认）。相对原版的关键差异：

| 维度 | svr2kos2 原版 | 本仓库 |
| --- | --- | --- |
| 炮塔旋转 | 后台协程抢 `_turretLock`，**旋转全程独占座圈**，另一管排队等 | **旋转全程锁外并行**——双管同时转方位，仅击发瞬间串行（防双管齐射） |
| 方位驱动权 | 双管异目标时并行驱动共享炮塔（后写者赢，来回反向震荡） | **单主驱动**：先到先得 `_bearingDriver`，另一管让舵只追仰角；击发段持锁者最高优先级；弹打出即放手，不等回位 |
| 同方位齐射 | 无，持锁者专权，一管击发时另一管只能等 | **齐射门 + 会合**：两管目标方位差 ≤0.1°（移动目标限同实体）放行双弹齐射；非持锁者武装后等伙伴也武装才击发（超时 60s 兜底自击） |
| 装填期旋转 | 装填期间不转方位 | **装填期立即转**——静态目标提前到位，触发提前 + 全速旋转（不每帧重写目标干扰速度平滑） |
| 射击基准 | `Player Turret Piece`（可拖动标记，会随拖动漂移） | **铁巢坐标自动更新**——真实炮塔基准（`TurretLocation`）+ **每 5s 刷新自愈**（场景晚就绪/紧急拖动后自动校正） |
| 集群作战 | 无 | **集群解算**：软目标 HE/HCHE 按 MEC 圆心落点，集群优先 APHE；移动集群按实体屏蔽成员；覆盖判定半径 0.95km 实战调校 |
| APHE 复合弹卡 | 无 | **每局自动注入 `APShellMod` 卡**（ImpactRadius=1km / Damage=5 / Cost=5），并入现有 APHE 枚举，幂等跳过已存在 |
| 强制购买 | 受征用点数与限量卡限制 | **与征用点数脱钩**：采购点击前自动注满点数，限量卡 RemainingUses/MaxUses 充至 99，永不缺弹 |
| 击发确认 | 5 个确认台顺序点击（就位→发射窗口长） | **确认与臂杆并行**——臂杆按下期间 0.01s 连点全部确认，就位→发射窗口 ~2.2s→~1.2s |
| 敌人可见 | 依赖视野/探测 | **强制可见**：`VisibilityGroup.alpha→1` + `State` 0x80 清除，扫描不依赖视野 |
| 地图 Overlay | 无 | **毁伤圈可视化**：落点深红描边环（半径=毁伤半径，与判定同源）+ 火力线 + 移动目标白虚线 |
| 爆炸半径 | 静态硬编码 | **硬编码确定值**（APHE=0.95km 用户调校）——集群/覆盖判定不随场景 ShellDefinition 漂移 |
| 死代码 | — | 移除 ~209 行（`GameStateWatcher` 整类等） |

### 核心功能

#### Numpad 0 全自动扫荡（核心玩法）

- 按 **Numpad 0** 启动/停止自动扫荡，开启后无需任何手动操作（可在桌上面板上开启 Auto Fire 自动完成最后的击发动作）：扫描 → 决策 → 打击 → 补弹 → 再扫描，循环不止
- **无小键盘的键盘？按 Ctrl+0** 效果相同（手动打击 Numpad 1-4 也备有 Ctrl+1-4）；**手柄玩家按 `Select` 切换**
- 雷达直接读取游戏 `FireMission.Entities` 目标注册表
- 双炮并行调度：任务自动派给空闲炮管，退膛完成自动接力
- 缺弹自动采购（强制购买，与征用点数脱钩）
- 蒸汽泄漏自动处理：开火后自动检测泄漏，旋紧阀门
- 操作员只需要操作咖啡机，并欣赏唱片机音乐

#### 战术决策

- **弹道自动解算**：自动设定装药、弹种、仰角与方向角
- **智能弹种选择**：装甲/工事/弹药库/地下等硬目标自动打 AP，软目标打 HE，集群目标优先 APHE（便宜 + 高伤），尊重目标 `ImmuneShells` 属性避开无效弹种
- **优先级排序**：6 级目标优先级 — FDC(6) > 火炮(5) > 弹药库/高价值/3★(4) > 装甲/工事/1★(3) > 普通(2) > 其他(1)。同级按综合时间成本排序（距离×2.56 + 角度差×0.30）
- **覆盖判定**：落点 MEC 圆心，毁伤半径硬编码表（APHE=0.95km 用户实战调校），日志实时报告"覆盖 N 个目标 @距离"

#### 手动模式

- Numpad 1-4（或 Ctrl+1-4）对标记目标 T1~T4 手动下达打击任务
- 面板切换弹种（正式版 20 种）、`Auto Fire` 自动击发、`Max Charge` 满装药（全自动/手动均生效）
- **`Numpad 0`（或手柄 `Select`）一键切换全自动/手动**：全自动 = 雷达接管；手动 = 雷达完全休眠，切换时已在装填的任务自动打完（原子化）

### 开发体验

- **F9 热重载**：修改 `IronNestFCS.Logic` 代码 → `dotnet build` → 切回游戏按 F9，无需重启
- **IMGUI 状态面板**：实时显示两管炮当前任务、目标参数与队列情况

### 架构

宿主/逻辑分离设计，核心为热重载服务：

| 项目 | 角色 | 说明 |
| --- | --- | --- |
| `IronNestFCS` | **宿主 Mod** | 稳定加载，永不重载。负责加载 Logic、监听 F9、转发生命周期 |
| `IronNestFCS.Abstractions` | **契约** | 仅含 `IFcsModule` 接口，唯一安全跨 ALC 边界的类型 |
| `IronNestFCS.Logic` | **火控逻辑** | 所有火控代码：弹道解算、任务调度、炮塔操控、战术决策、UI。装入可回收 ALC，F9 卸载重载 |

### 安装（玩家）

1. **MelonLoader**：下载 [MelonLoader.Installer.exe](https://melonwiki.xyz/)，选择游戏 exe 安装（IL2CPP 版）。游戏根目录出现 `MelonLoader/`
2. **解压 Mod**：从 [Releases](https://github.com/High-cla/IronNestFCS-Automat/releases) 下载 `IronNestFCS_vX.X.X.zip`，解压到游戏根目录，将 `Mods/`、`UserData/`、`UserLibs/` 三个文件夹与游戏目录合并
3. **启动验证**：左上角出现 IronNestFCS-Automat 面板即安装成功。若提示 `Dial 未绑定`，按 **F9** 重新绑定；游戏更新后失效则重新解压覆盖

### 构建（开发者）

前置条件：.NET 6 SDK、游戏本体 + MelonLoader（作为编译引用）。

将两个 `.csproj` 中的 `GameDir` 改为你的游戏安装路径：

- `IronNestFCS/IronNestFCS.csproj`
- `IronNestFCS.Logic/IronNestFCS.Logic.csproj`

```xml
<GameDir>你的路径\Iron Nest Heavy Turret Simulator</GameDir>
```

构建：

```bash
dotnet build IronNestFCS.sln
```

输出位置：

| 程序集 | 路径 | 说明 |
| --- | --- | --- |
| `IronNestFCS.dll` | `Mods/` | 宿主 Mod，MelonLoader 自动加载 |
| `IronNestFCS.Logic.dll` | `UserData/IronNestFCS/` | 火控逻辑，宿主反射加载 |
| `IronNestFCS.Abstractions.dll` | `UserLibs/` | 契约，宿主与逻辑共用 |

> `IronNestFCS.Logic.csproj` 的 `OutputPath` 已指向 `$(GameDir)\UserData\IronNestFCS\`，构建即就位，改代码后进游戏按 F9 即生效。

### 使用

1. 启动已安装 MelonLoader 与本 Mod 的游戏。若面板提示 `Dial 未绑定`，按 **F9** 重新绑定
2. 进入包含炮塔与地图桌的关卡，在控制台旁的按钮上选择弹种（默认 HE），按需开启 `Auto Fire` 和 `Max Charge`
3. 按 **Numpad 0**（或 **Ctrl+0** / 手柄 **Select**）切换全自动/手动模式。全自动下 Mod 自动完成解算 → 采购 → 装填 → 瞄准 → 确认 → 击发
4. 手动模式下拖动地图目标标记（T1~T4）到目标位置，按 **Numpad 1-4** 下达任务
5. 左上角面板实时显示两管炮任务进度与队列

**热重载开发**：修改 `IronNestFCS.Logic` 代码后重建项目，切回游戏按 F9 即可加载新逻辑。注意：不要在 Logic 中注册新的 IL2CPP 类型，协程必须登记以便卸载时停止。

### 贡献

欢迎提交 Issue 和 Pull Request。改动火控逻辑时请注意：不要注册新的 IL2CPP 类型、协程必须登记、跨 ALC 只能传递 `IFcsModule`。

### 许可证

MIT © 2025-2026 KK，基于 svr2kos2 的作品（MIT © 2026 svr2kos2），战术雷达参考 gxpppp 的 [IronNestFCS](https://github.com/gxpppp/IronNestFCS)。

### 免责声明

本项目为非官方第三方 Mod，与游戏开发商无关。本 Mod 会自动化操作游戏，若游戏提供线上排行榜，建议游玩前在设置中关闭，以免成绩不被认可或被误判为作弊封禁。仅供学习与单机娱乐使用，使用风险自负。

---

## English

### What is this

A MelonLoader mod for *Iron Nest: Heavy Turret Simulator*. This is a deep-fork of svr2kos2's original FCS: it's a **full-auto sweep** — enable it and the mod continuously scans all targets, solves ballistics, picks shell types, purchases/loads ammunition, aims, confirms, and fires. Unattended combat for hours.

### Differences from upstream

Deep-fork of [svr2kos2](https://github.com/svr2kos2)'s original FCS (evolved via [KKTIME2024](https://github.com/KKTIME2024/IronNestFCS-Automat); its `da641f3` salvo gate / `53bbbf0` parallel-confirm are merged in, adapted to this fork's lock model):

| Aspect | svr2kos2 upstream | This fork |
| --- | --- | --- |
| Turret rotation | Background coroutine grabs `_turretLock` — **rotation holds the shared traverse exclusively**, the other gun queues | **Rotation runs outside the lock** — both guns traverse in parallel; only the firing instant serializes (prevents accidental volley) |
| Bearing ownership | Both guns drive the shared traverse on different targets (last writer wins, oscillation) | **Single master driver** (`_bearingDriver`, first come first served); the other gun only tracks elevation; lock-holder has top priority at firing; bearing released the moment the shell leaves |
| Same-bearing salvo | None — lock-holder fires alone, other gun waits | **Salvo gate + rendezvous**: both guns fire together when target bearings differ ≤0.1° (moving targets: same entity only); the non-lock-holder waits for its partner to arm (60s timeout fallback) |
| Rotation during loading | Traverse not moved while loading | **Rotates immediately during loading** — static targets pre-aim early; full-speed rotation (no per-frame target rewrite disturbing speed smoothing) |
| Aim reference | `Player Turret Piece` (draggable marker, drifts) | **Auto-updated iron-nest coordinates** — real turret base (`TurretLocation`) + **self-healing 5s refresh** (late scene init / emergency drag auto-corrects) |
| Cluster strikes | None | **Cluster solving**: soft targets hit at the MEC center with HE/HCHE, APHE preferred for clusters; moving clusters masked per entity; 0.95km coverage radius (field-tuned) |
| APHE composite shell card | None | **`APShellMod` card auto-injected each match** (ImpactRadius=1km / Damage=5 / Cost=5), merged into the existing APHE enum, idempotent skip |
| Forced purchase | Limited by requisition points & card uses | **Decoupled from requisition points**: points refilled before every purchase click, limited cards charged to 99 uses, never out of shells |
| Fire confirmation | 5 confirmation panels clicked sequentially (long ready-to-fire window) | **Confirmations parallel with arming** — all 5 tapped at 0.01s while the arming lever is held; ready-to-fire window ~2.2s → ~1.2s |
| Enemy visibility | Relies on sighting/detection | **Force-visible**: `VisibilityGroup.alpha→1` + `State` 0x80 cleared, sweep independent of line of sight |
| Map overlay | None | **Blast-radius visualization**: deep-red ring at the impact point (radius = kill radius, same source as the coverage check) + fire line + white dashes for moving targets |
| Blast radius | Static hardcode | **Hardcoded deterministic values** (APHE=0.95km field-tuned) — cluster/coverage checks never drift with scene ShellDefinitions |
| Dead code | — | ~209 lines removed (`GameStateWatcher` class etc.) |

### Core Features

#### Numpad 0 — Full-auto Sweep (the main event)

- Press **Numpad 0** to start/stop the sweep loop. Once active, zero manual input: scan → decide → strike → resupply → scan again, forever
- **No numpad? Press Ctrl+0** instead (manual strikes also have Ctrl+1-4 fallbacks for Numpad 1-4); **gamepad users: press `Select`** to toggle
- The radar reads the game's `FireMission.Entities` target registry directly
- Dual-barrel scheduler: tasks are auto-assigned to idle guns; the next task starts the moment a barrel finishes cycling
- Auto-purchases shells when the magazine runs low (forced purchase, decoupled from requisition points)
- Auto valve tightening: detects steam leaks after firing and tightens the nearest dial

#### Tactical Decisions

- **Auto ballistic solving**: charge, shell type, elevation, and azimuth are set automatically
- **Smart shell selection**: AP for armored/fortification/ammo/underground targets, HE for soft targets, APHE preferred for clusters (cheap + high damage), always respecting the target's `ImmuneShells` to skip ineffective types
- **Priority system**: 6 tiers — FDC(6) > artillery(5) > ammo depot/high-value/3★(4) > armored/fortification/1★(3) > normal(2) > other(1); ties broken by combined cost (distance×2.56 + angle delta×0.30)
- **Coverage check**: impact at MEC center; hardcoded kill-radius table (APHE=0.95km, field-tuned); log reports "covers N targets @distance"

#### Manual Mode

- Numpad 1-4 (or Ctrl+1-4) to manually dispatch strikes against markers T1~T4
- Switch shell types (all 20 full-release types), toggle `Auto Fire`, and `Max Charge` from the console buttons
- **`Numpad 0` (or gamepad `Select`) toggles auto/manual mode**: auto = radar takes over; manual = radar fully dormant, tasks already loading finish atomically

### Developer Experience

- **F9 hot reload**: edit `IronNestFCS.Logic` → `dotnet build` → press F9 in-game. No restart needed
- **IMGUI status panel**: live progress for both guns, target parameters, and queue depth

### Architecture

Host/logic split, built for hot reload:

| Project | Role | Notes |
| --- | --- | --- |
| `IronNestFCS` | **Host mod** | Loaded once, never reloaded. Loads Logic, listens for F9, forwards lifecycle callbacks |
| `IronNestFCS.Abstractions` | **Contract** | Contains only the `IFcsModule` interface — the only type safe across ALC boundaries |
| `IronNestFCS.Logic` | **FCS logic** | All the fire control code: ballistics, scheduling, turret control, tactics, UI. Loaded in a collectible ALC for F9 reload |

### Install (Players)

1. **MelonLoader**: download [MelonLoader.Installer.exe](https://melonwiki.xyz/), point it at the game exe (IL2CPP). A `MelonLoader/` folder appears in the game root
2. **Extract the mod**: download `IronNestFCS_vX.X.X.zip` from [Releases](https://github.com/High-cla/IronNestFCS-Automat/releases) and extract into the game root, merging the three folders (`Mods/`, `UserData/`, `UserLibs/`)
3. **Launch**: the IronNestFCS-Automat panel in the top-left corner means success. If it says `Dial 未绑定`, press **F9** to rebind; re-extract the zip if a game update breaks it

### Build (Developers)

Prerequisites: .NET 6 SDK, the game + MelonLoader (for compile references).

Change `GameDir` in both `.csproj` files to your game install path:

- `IronNestFCS/IronNestFCS.csproj`
- `IronNestFCS.Logic/IronNestFCS.Logic.csproj`

```xml
<GameDir>your\path\Iron Nest Heavy Turret Simulator</GameDir>
```

Build:

```bash
dotnet build IronNestFCS.sln
```

Output:

| Assembly | Path | Notes |
| --- | --- | --- |
| `IronNestFCS.dll` | `Mods/` | Host mod, auto-loaded by MelonLoader |
| `IronNestFCS.Logic.dll` | `UserData/IronNestFCS/` | FCS logic, reflection-loaded by the host |
| `IronNestFCS.Abstractions.dll` | `UserLibs/` | Contract shared by host and logic |

> `IronNestFCS.Logic.csproj`'s `OutputPath` already points to `$(GameDir)\UserData\IronNestFCS\` — build output lands in place, so F9 picks it up instantly.

### Usage

1. Launch the game with MelonLoader + this mod installed. If the panel says `Dial 未绑定` (not bound), press **F9** to rebind
2. Enter a scene with a turret and map table. Pick a shell type at the console buttons (default HE), toggle `Auto Fire` / `Max Charge` as desired
3. Press **Numpad 0** (or **Ctrl+0** / gamepad **Select**) to toggle auto/manual mode. In auto, the mod auto-completes: solve → purchase → load → aim → confirm → fire
4. In manual mode, drag map markers (T1~T4) onto targets and press **Numpad 1-4** to dispatch
5. The top-left panel shows live progress for both guns and the queue

**Dev hot reload**: edit `IronNestFCS.Logic`, rebuild, press F9 in-game. Don't register new IL2CPP types in Logic, and register coroutines so they stop on unload.

### Contributing

Issues and pull requests welcome. When touching FCS logic: no new IL2CPP types, register your coroutines, and pass only `IFcsModule` across ALC boundaries.

### License

MIT © 2025-2026 KK, based on work by svr2kos2 (MIT © 2026 svr2kos2); tactical radar inspired by gxpppp's [IronNestFCS](https://github.com/gxpppp/IronNestFCS).

### Disclaimer

This is an unofficial third-party mod, not affiliated with the game developer. This mod automates gameplay; if the game offers online leaderboards, we recommend disabling them in the settings before playing to avoid unrecognized scores or potential bans. For educational and single-player entertainment use only. Use at your own risk.
