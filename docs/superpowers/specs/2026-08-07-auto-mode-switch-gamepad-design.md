# 设计:全自动/手动模式开关 + 手柄开关

日期:2026-08-07
来源:粉丝反馈两条——① 自动功能不能关闭,需要在全自动和手动标点 T1-T4 之间自如切换;② 需要纯手柄支持(后收敛为:手柄只需一个全自动开关)。

## 问题

1. 即使关闭扫荡(`sweepActive = false`),被动雷达仍每 5 秒扫描并**无条件覆盖地图标记 1-4**(`TacticalRadar.AutoPlaceMarkers` 恒 true),玩家手动拖放的标记会被冲掉 → "自动功能不能关闭"。
2. mod 全部输入为键盘/鼠标(`Keyboard.current` / `Mouse.current`),无手柄支持。手柄玩家无法切换全自动模式。

## 方案(已确认)

- 单一开关 `autoMode`(由 `sweepActive` 语义升级而来)管全部自动行为。
- 手柄仅映射一个键:全自动开关。不做 T1-T4/弹种/装药的手柄映射。
- 不采用:双开关拆分、TacticalRadar 改造(不需要——FcsModule 挡住 Scan 调用即可)、摇杆模拟鼠标。

## 设计

### 1. 模式开关(FcsModule.cs)

`sweepActive` 改名/升级为 `autoMode`,语义:

| autoMode | 行为 |
|---|---|
| true(全自动) | 现状全部行为:炮退膛扫描填 Top 2、被动 5s 扫描、CBT 轮询、覆盖标记 1-4 |
| false(手动) | **雷达完全休眠**:不调 `radar.Scan()`、不轮询 CBT、不覆盖标记、不自动入队。手动 T1-T4(Numpad 1-4 / 3D 按钮)照常 |

- 切入手动时:`fcs.ClearPendingTasks()` 清掉自动入队的队列;正在执行的任务不打断,打完自然停。
- 切换入口:
  - 键盘:Numpad 0 / Ctrl+0(保留现有)
  - 手柄:`Gamepad.current` 的 Select(⧉)键,`wasPressedThisFrame` 读取
- `FcsWindow.AutoSweepEnabled` 接线(当前为死代码),IMGUI 窗口显示 `[AUTO]` / `[MANUAL]`。
- Update 中被动扫描块与 CBT 轮询块加 `autoMode &&` 条件。

### 2. 手柄映射(FcsModule.cs)

```
Select (⧉)  = 全自动开关(等价 Numpad 0)
```

- 只读 `Gamepad.current`(新 Input System,与 Keyboard 同源,热重载安全,无死区问题)。
- `Gamepad.current == null`(无手柄)时自然跳过,不影响键鼠玩家。

### 3. 文档(README)

按键表更新:手柄 Select = 全自动开关。

## 改动文件

| 文件 | 改动 |
|---|---|
| `IronNestFCS.Logic/FcsModule.cs` | autoMode 语义、手柄块、扫描/CBT 条件、清队列(核心,~20 行) |
| `IronNestFCS.Logic/FcsWindow.cs` | AutoSweepEnabled 接线 + `[AUTO]/[MANUAL]` 显示(约 5 行) |
| `README.md` | 按键表 |

TacticalRadar.cs / FSC.cs 零改动。

## 测试

1. 编译通过。
2. 进游戏手动验证(热重载 F9):
   - 手动模式:拖标记 → Numpad 1-4 / T1-T4 按钮开火;等 >5s 确认标记未被覆盖、敌情栏不刷新
   - Select / Numpad 0 → `[AUTO]`,雷达接管:自动扫描、覆盖标记、清队列填 Top 2
   - 再切回手动 → 队列空、标记不再被冲
   - 无手柄机器:确认 Numpad 0 行为与改动前一致
