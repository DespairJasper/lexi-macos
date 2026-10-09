# Lexi 1.2.0 · 样式与窗口系统设计

`VisualTheme.axaml` 是符合 Windows 桌面规范与 macOS 核心产品契约的完整 Avalonia 11 全局样式系统。在 `App.axaml` 的 FluentTheme 之后引入。

## 1. 按钮层次与设计契约 (Spec 第12节)

遵循现代 Windows 桌面交互规范，彻底杜绝“每个次要按钮都是浅蓝底看起来像已选中”的问题：

| 按钮类型 / 类名 | 默认态 | 悬停态 (PointerOver) | 按下态 (Pressed) | 焦点态 (FocusVisible) | 语义与规则 |
|---|---|---|---|---|---|
| **默认 / 次要按钮** (`Button`, `Button.secondary`) | 中性白/深蓝灰表面 (`CardBrush`), 中性细边 (`LineBrush`), 中性墨色字 (`InkBrush`) | 轻微中性提亮 (`TintBrush`), 边框微调 (`MutedBorderBrush`) | 微深按压表面 (`PressedTintBrush`), `scale(0.97)` | 细蓝色对焦环 (`PrimaryGreen`, 1px), 柔和辉光 | 普通操作与次级动作，**绝不带有强调蓝色底** |
| **任务主动作** (`Button.primary`) | 实心蓝底 (`PrimaryGreen`: #245FAD / #93C5F8), 高对比文字 (`OnPrimaryBrush`) | 饱和蓝悬停 (`PrimaryGreenHover`) | 深蓝按下 (`PrimaryGreenPressed`), `scale(0.97)` | 聚焦微光环 (`FocusGlowShadow`), 保留实心底 | 当前任务区域**唯一**最重要的下一步，每个区域最多一个 |
| **幽灵次动作** (`Button.ghost`) | 透明背景, 次要文字 (`MutedBrush`), 无边框 | 浅中性底 (`TintBrush`), 主文字色 (`InkBrush`) | 按压底 (`PressedTintBrush`), `scale(0.97)` | 细对焦环 | 辅助操作、折叠展开、取消动作 |
| **危险 / 删除** (`Button.danger`) | 中性表面, 红色警示字 (`DangerBrush`), 红细边 (`DangerBorder`) | 实心危险红 (`DangerBrush`), 白字 | 深红按下 (`DangerPressedBrush`), 白字 | 红色聚焦环 | 不可逆删除与销毁动作 |
| **选中筛选 / 切换** (`Button.filter-active`, `Button.selected`, `ToggleButton:checked`) | 低饱和冰蓝底 (`SelectionBrush`), 细天蓝边框 (`FilterSelectedBorderBrush`), 粗体蓝字 (`PrimaryGreen`) | 保持浅蓝底微深 (`FilterSelectedHoverBrush`), 强调蓝字 | 浅蓝按压底 (`FilterSelectedPressedBrush`), `scale(0.97)` | 聚焦环 | 持久选中状态，与普通按钮及主动作样式**明确物理分离** |
| **禁用态** (`:disabled`) | `Opacity: 0.45`, `Cursor: Arrow`, 中性边与字 | 无反馈 | 无反馈 | 无焦点 | 不可执行状态 |

## 2. 窗口标题栏与控件 (Spec 12.4)

- **紧凑 Windows 标尺**：`Button.window-control` 采用无边框、无大圆角、矩形贴靠标题栏设计 (46×32 DIP)。
- **关闭按钮中性规范**：`Button.window-control.close` **默认完全中性透明，无红块**；仅在鼠标悬停时显现系统危险红 (`#E81123`)、按下时深红 (`#C42B1C`)，图标居中矢量 Path 变为纯白。
- **快捷卡唯一关闭入口**：`QuickCardWindow` 使用 `ExtendClientAreaChromeHints.NoChrome` 消除双重标题栏，右上角单套紧凑 Windows 关闭按钮，内容区不再重复放置关闭按钮。

## 3. 浅深主题与无障碍支持

- **双主题完整映射**：Light (冷白/深蓝灰) 与 Dark (深蓝夜空/冰蓝) 均具备完备的状态画刷字典。
- **无障碍降级**：
  - `HighContrast` 模式：完整映射到黑白高对比度前背景。
  - `reduce-motion` 模式：消除所有 `Transitions` 动画，按下不再发生位移缩放。
  - `OpaqueMaterial` 模式：透明度与材质安全回退为纯色不透明表面。