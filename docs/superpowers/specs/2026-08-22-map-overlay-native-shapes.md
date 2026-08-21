# 地图 Overlay 实现机制切换 —— LineRenderer 自绘 → 游戏原生 Shapes

- 日期：2026-08-22
- 状态：已实现（build 0 error / 0 warning；LSP 0 诊断；装机 F9 目检待做）
- 关联：[2026-08-13-map-overlay-visual-design.md](2026-08-13-map-overlay-visual-design.md)——视觉规格不变，本篇只记录**实现机制**对齐其 v2 决议

## 背景

视觉设计 spec 的 v2 定稿（2026-08-16）已决议"全部渲染改用游戏自带 `Il2CppShapes.Line`"，
但当时实际落地走的是上游移植路线：Unity `LineRenderer` + 自造 URP Unlit 共享材质 ×2 +
自绘 dashTexture（Tile 模式虚线）+ 48 段手算圆环多边形。本次按用户指令迁移到原生工具。

## 决策

| 元素 | 旧（自绘） | 新（原生） |
|---|---|---|
| 毁伤圈 | 48 段 loop LineRenderer 多边形 | `Il2CppShapes.Disc`（Type=Ring），圆心=`localPosition`=落点 |
| 火力线 | positionCount=2 + SetPosition | `Il2CppShapes.Line`（Start/End） |
| 移动路径 | dashTexture + LineTextureMode.Tile | `Il2CppShapes.Line`（Dashed=true，原生虚线） |

- 新增 csproj 引用 `Il2CppShapesRuntime.dll`（游戏自带 interop 程序集，Private=false，与其他引用一致）。
- **坐标模型零改动**：Shapes 组件在自身 transform 空间作图 → 照旧挂 `Draggable Surface` 下用 map-local 坐标；
  `ThicknessSpace=Meters` 保持世界单位尺度；`RenderQueue=3500`（压航拍透明层）语义保留；
  颜色/线宽常量原样（LineColor 鲜红 / PathColor 白 / LineWidthWorld=0.0075）。
- 行为保持：1Hz 节流、按任务槽 dict、击发冻结（9fb9455）、落点变化才更新几何、stale 清理、Shutdown 销毁。
- 小改进：`BlastRadiusKm<=0` 时显式隐藏圈（旧代码留空 LineRenderer 渲染空物体）。

## 后果

- 删除自造轮子：URP shader 查找、MakeMat、共享材质 ×2、dashTexture/MakeDashTexture、DrawDashed、48 段循环、
  FcsSceneInteractor.SetColor 回退。MapOverlay.cs 净 -46 行（238→~180）。
- `Shutdown` 不再销毁材质/纹理——Shapes 组件材质随 GameObject.Destroy 释放，热重载泄漏面缩小。
- 更贴合 AGENTS.md"复用既有游戏类型，不自造渲染管线"的项目精神。
- 待 F9 目检项：Flat2D 平铺朝向是否符合地图桌视角、Shapes 默认 dash 密度是否可读、
  Meters 线宽与旧 LineRenderer 的视觉等价性（偏差则调 `LineWidthWorld` / 补 `DashSize/DashSpacing`）。
