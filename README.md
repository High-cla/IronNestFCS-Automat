# IronNestFCS

[Demo Video](https://www.bilibili.com/video/BV1xc7F6WEET/) | [English](#english) | [简体中文](#chinese)

[Iron Nest: Heavy Turret Simulator](https://store.steampowered.com/app/4300500/) 的 [MelonLoader](https://melonwiki.xyz/) Mod，为游戏中的重型炮塔加入一套自动化**火控系统（Fire Control System, FCS）**：在地图上点选目标，Mod 会自动解算弹道、采购/装填炮弹、调整炮塔方向与仰角，并完成确认与击发的全套流程。

A [MelonLoader](https://melonwiki.xyz/) mod for *[Iron Nest: Heavy Turret Simulator](https://store.steampowered.com/app/4300500/)* — an automated **Fire Control System (FCS)** for heavy turrets. Click a target on the map and the mod auto-solves ballistics, purchases/loads shells, sets turret azimuth & elevation, confirms, and fires.

> 基于游戏 Demo 版本开发，使用 IL2CPP + MelonLoader。Built for the Demo version using IL2CPP + MelonLoader.

---

## 功能 / Features

### 核心火控 / Core FCS
- **一键打击 / One-click Strike**：点击地图上的炮兵目标，自动下达完整打击任务。Click a target on the map to dispatch a complete fire mission.
- **双炮管任务调度 / Dual-barrel Scheduler**：任务入队后由调度器自动派给空闲炮管，两管炮并行作业。Tasks queue automatically; idle guns pick up the next task in parallel.
- **自动弹道解算 / Auto Ballistic Solving**：读取目标方向角与距离，自动设定装药、弹种并解算仰角。Reads target azimuth & distance, auto-sets charge, shell type, and solves elevation.
- **多弹种支持 / Multi-shell Support**：AP / HCHE / HE / STAR / SMK，面板切换；弹仓缺弹自动采购。Switch shell types on the panel; auto-purchase when out of stock.
- **自动击发 / Auto Fire**：面板 `Auto Fire` 开关切换手动 / 自动击发。Toggle manual/auto firing via panel switch.

### 战术智能 / Tactical Intelligence (v1.1.0)
- **战术雷达 / Tactical Radar**：从 `FireMission.Entities` 目标注册表扫描全部敌我实体（含未生成的后续波次目标），读取 Role / Icon / Stars / Armour / ImmuneShells / IsAlive 属性进行敌我识别与威胁评估。Auto-scans the `FireMission.Entities` target registry (including future wave spawns), reading Role bitmask, Icon, Stars, Armour, ImmuneShells, and IsAlive for identification and threat assessment.
- **智能弹种选择 / Smart Shell Selection**：装甲/工事/弹药库/地下目标自动切换 AP，软目标使用 HE；结合 ImmuneShells 属性避开无效弹种。AP for armored/fortification/ammo/underground targets, HE for soft targets; respects ImmuneShells to skip ineffective shell types.
- **优先级排序 / Priority System**：6 级目标优先级 — FDC(6) > 火炮(5) > 弹药库/高价值/3★(4) > 装甲/工事/1★(3) > 普通(2) > 其他(1)。同级目标按综合成本排序（距离×2.56 + 角度差×0.30）。6-tier target priority; same-priority targets sorted by combined cost (distance×2.56 + angle delta×0.30).
- **地下目标检测 / Underground Detection**：双重检测 — 名称关键词（bunker/underground/shelter/pillbox/dugout/bombproof/depot/storage/warehouse/magazine/cache/vault/tunnel）+ Entity 属性（IsUnderground/Underground/IsBunker/Bunker）。Dual-path detection: name keywords + Entity property reflection.
- **自动阀门锁紧 / Auto Valve Tightening**：开火后自动检测蒸汽泄漏，定位最近的阀门交互组件并旋紧。Auto-detects steam leaks after firing and tightens the nearest valve dial.
- **扫荡循环 / Sweep Loop**：Numpad 0 开关自动扫荡，开启后持续扫描并派发任务。Toggle auto-sweep with Numpad 0.

### 开发体验 / Developer Experience
- **F9 热重载 / Hot Reload**：修改 `IronNestFCS.Logic` 代码 → `dotnet build` → 切回游戏按 F9，无需重启。Edit code, build, press F9 in-game — no restart needed.
- **IMGUI 状态面板 / Status Panel**：实时显示两管炮当前任务、目标参数与队列情况。Real-time dual-gun task status, target parameters, and queue depth.

### 附赠 / Companion
- **自定义唱片机 / Custom Records**（独立 Mod）：用自定义音频与贴图替换游戏内 RecordDisk。Replace in-game RecordDisk audio/textures with your own.

---

## 架构 / Architecture

四个程序集，核心为**热重载**服务的宿主/逻辑分离设计：

| 项目 / Project | 角色 / Role | 说明 |
| --- | --- | --- |
| `IronNestFCS` | **宿主 Mod** | 稳定加载，永不重载。负责加载 Logic、监听 F9、转发生命周期。Stable host, loaded once. Loads Logic, listens for F9, forwards lifecycle callbacks. |
| `IronNestFCS.Abstractions` | **契约** | 仅含 `IFcsModule` 接口。唯一安全跨 ALC 边界的类型。Single interface type — the only type safe to pass across ALC boundaries. |
| `IronNestFCS.Logic` | **火控逻辑** | 所有火控代码：弹道解算、任务调度、炮塔操控、战术决策、UI。装入可回收 ALC，F9 卸载重载。All FCS logic: ballistics, scheduling, turret control, tactical decisions, UI. Loaded in collectible ALC for hot reload. |
| `IronNestFCS.CustomRecords` | **独立 Mod** | 与火控无关的场景装饰，替换唱片机音轨与贴图。Standalone decor mod for custom audio/textures. |

---

## 构建与安装 / Build & Install

### 前置条件 / Prerequisites
- .NET 6 SDK
- 游戏本体 + [MelonLoader](https://melonwiki.xyz/)（IL2CPP）

### 配置游戏路径 / Configure Game Path

将两个 `.csproj` 中的 `GameDir` 改为你的游戏安装路径：
Change `GameDir` in both files to your game install path:

- `IronNestFCS/IronNestFCS.csproj`
- `IronNestFCS.Logic/IronNestFCS.Logic.csproj`

```xml
<GameDir>你的路径\IRON NEST Heavy Turret Simulator Demo</GameDir>
```

### 构建 / Build

```bash
dotnet build IronNestFCS.sln -c Release
```

输出位置 / Output:
| 程序集 | 路径 | 说明 |
| --- | --- | --- |
| `IronNestFCS.dll` | `Mods/` | 宿主 Mod，MelonLoader 自动加载 |
| `IronNestFCS.Logic.dll` | `UserData/IronNestFCS/` | 火控逻辑，宿主反射加载 |
| `IronNestFCS.Abstractions.dll` | `UserLibs/` | 契约，宿主与逻辑共用 |
| `IronNestFCS.CustomRecords.dll` | `Mods/` | 唱片机 Mod（可选） |

> `IronNestFCS.Logic.csproj` 的 `OutputPath` 已指向 `$(GameDir)\UserData\IronNestFCS\`，构建即就位，改代码后进游戏按 F9 即生效。

### 安装 / Install

**从 Release 下载**（推荐）：解压 `IronNestFCS_vX.X.X.zip` 到游戏根目录，三个 dll 自动归位。
**Download from Releases** (recommended): Extract `IronNestFCS_vX.X.X.zip` to the game root directory.

**手动安装**：将上述三个 dll 放入对应目录。Manual: place the three dlls into their respective directories.

---

## 使用 / Usage

1. 启动已安装 MelonLoader 与本 Mod 的游戏。Launch the game with MelonLoader + this mod installed.
2. 进入包含炮塔与地图桌的关卡。若面板提示 `Dial 未绑定`，按 **F9** 重新绑定。Enter a scene with a turret + map table. Press **F9** to rebind if the panel shows binding errors.
3. 在控制台旁的按钮上选择弹种（默认 HE），按需开启 `Auto Fire` 和 `Max Charge`。Select shell type at the console buttons (default HE), toggle `Auto Fire` and `Max Charge` as needed.
4. 按 **Numpad 0** 开启自动扫荡，或手动拖动地图目标标记（T1~T4）到目标位置后点击右侧按钮下达任务。Press **Numpad 0** to enable auto-sweep, or manually drag map markers (T1~T4) and click the target buttons.
5. Mod 自动完成解算 → 采购 → 装填 → 瞄准 → 确认 → 击发。The mod auto-completes: solve → purchase → load → aim → confirm → fire.
6. 左上角面板实时显示两管炮任务进度与队列。Top-left panel shows real-time progress for both guns.

### 热重载开发 / Dev Hot Reload

修改 `IronNestFCS.Logic` 代码后重建项目，切回游戏按 **F9** 即可加载新逻辑，无需重启。注意：不要在 Logic 中注册新的 IL2CPP 类型，协程必须登记以便卸载时停止。

---

## 贡献 / Contributing

欢迎提交 Issue 和 Pull Request。Welcome!

- Bug 报告 / 功能建议 → [Issues](../../issues)
- 代码改进 → [Pull Requests](../../pulls)
- 改动火控逻辑时请注意：不要注册新的 IL2CPP 类型、协程必须登记、跨 ALC 只能传递 `IFcsModule`。

---

## 许可证 / License

MIT © 2025-2026 KK, based on work by svr2kos2.

---

## 免责声明 / Disclaimer

本项目为非官方第三方 Mod，与游戏开发商无关。仅供学习与单机娱乐使用，使用风险自负。
This is an unofficial third-party mod, not affiliated with the game developer. For educational and single-player entertainment use only. Use at your own risk.
