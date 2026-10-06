# Lexi 0.2.1 · 界面与词卡

`VisualTheme.axaml` 是完整可用的 Avalonia 11 全局样式，包含浅色/暗色资源字典、字体、半透明卡片、渐变边缘、阴影、按钮模板、焦点辉光及胶囊样式。在 `App.axaml` 的 FluentTheme 后引入。

## 词卡结构

完整生产布局见 `MainWindow.axaml` 的 `LookupResultCard`，不是独立演示页。以下为实际结构的精简片段（完整命名控件和事件在主窗口）：

```xml
<Border Name="LookupResultCard" Classes="card">
  <StackPanel Spacing="14">
    <TextBlock Name="ResultWordText" FontSize="32" TextWrapping="Wrap"/>
    <WrapPanel Name="ResultPosPanel"/>
    <TextBlock Name="ResultTranslationText" FontSize="15" TextWrapping="Wrap"/>
    <WrapPanel>
      <CheckBox Name="AiOptExamples" Classes="capsule" Content="例句 (1–3 句)"/>
      <CheckBox Name="AiOptSynonyms" Classes="capsule" Content="同义词 (3–5 个)"/>
      <CheckBox Name="AiOptAntonyms" Classes="capsule" Content="反义词 (2–4 个)"/>
      <Button Name="AiGenerateBtn" Classes="secondary" Content="生成"/>
    </WrapPanel>
    <Button Name="AiDrawerToggleBtn" Classes="ghost" Content="展开扩展 ↓"/>
    <Border Name="AiDrawerSlot" Classes="drawer-slot"
            Height="0" Opacity="0" IsVisible="False" ClipToBounds="True">
      <Border Name="AiResultBox" Padding="16,14">
        <StackPanel Spacing="12">
          <StackPanel Name="AiResultExamplesContainer" Spacing="6"/>
          <WrapPanel Name="AiResultSynonymsPanel"/>
          <WrapPanel Name="AiResultAntonymsPanel"/>
        </StackPanel>
      </Border>
    </Border>
  </StackPanel>
</Border>
```

`MainWindow.axaml.cs` 的 `RenderExpansion` 将例句绘制为独立 `example-card`，英文 15px / 24px 行高、中文 13px / 21px 行高；所有前景色使用动态主题样式，切换系统主题不会保留错误颜色。

同反义词由 `micro-capsule` Button 呈现，Click 只调用 `PerformLookupAsync`，不会调用 AI。`RenderPartOfSpeech` 从已有词典信息提取词性，不生成假词性。

`OpenDrawerAsync` / `CloseDrawerAsync`：260ms 展开、200ms 收起，CubicEaseOut；先测量内容高度，完成后恢复自动高度以适应换行。版本号取消旧动画，快速连击以最后一次请求为准。收起不丢弃本次结果，重新展开无需再次调用 API；换词会清理旧扩展。

按钮按下 80ms 缩至 0.96，恢复 200ms 使用轻微回弹曲线；微胶囊悬浮上移 1px、按下下沉并缩至 0.94。没有持续动画。搜索清除按钮在有内容时淡入并进入键盘焦点序列，无内容时不可点击且不可聚焦。

生词本每行通过 `WordItem.IsExpanded` 独立展开，复选框仍只负责批量选择。展开状态仅属于当前界面，不改数据库和复习进度。

## 材质与验收边界

窗口依次请求 Mica / AcrylicBlur / Blur / None，核心内容只有一个裁剪圆角根容器；具体系统材质由 Windows 版本和透明效果设置决定。关闭到托盘、Alt+D、非置顶、单实例仍沿用原实现。默认软件渲染，未承诺内存低于 15 MB。

`--visual-test` 必须配置隔离目录 `LEXI_DATA_DIR`：验证真实渲染按钮、抽屉连击、独立展开及主题更新，并生成截图。测试使用固定示例，不发送付费 AI 请求。`--ui-smoke` 验证基础流程，执行前退出占用 Alt+D 的 Lexi 实例。

发布：.NET 8 SDK 执行 `dotnet publish -c Release -r win-x64 --self-contained true -o publish`；使用 PowerShell 7 执行 `packaging/build-installer.ps1 -Nsis <makensis.exe完整路径>`。可用 `-Dotnet` 指定 SDK 程序路径。目标机器无需安装 .NET 或 WebView2。
