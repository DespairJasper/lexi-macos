# Lexi for Windows

Windows 10 / 11 x64 桌面版，当前应用版本 1.2.4。使用 C#、Avalonia 11.3.12 与 .NET 8，作者 Cofran-77、DespairJasper。

## 功能

- 离线 ECDICT 词典、个人词汇档案、来源和标签、批量管理。
- FSRS-6 自适应复习、今日重逢、每日计划与辅助拼写。
- 全局专注学习、正文选择与复制、可配置快捷键、朗读。
- 可选 AI 内容生成、句子翻译和金句本。
- CSV / JSON 词条迁移、打印、本端备份与恢复。
- 专题词汇、听力、语法与写作工作区，安装包附带当前配套资料。

1.2.4 修复小浮框磨砂材质遮挡新界面的问题，磨砂限于浮框内部，保持主页面清晰。此后的发布许可修订不改变应用功能版本。

## 安装与数据

从 [Releases](https://github.com/Cofran-77/lexi/releases) 下载 Windows 安装程序，包含 .NET 运行环境与 FSRS 助手。安装包未签名；Windows 可能提示未知发布者。

词库默认保存在 `%LOCALAPPDATA%\Lexi`。更新前备份并从托盘完全退出。安装更新与卸载不主动删除该数据目录；备份仍应由用户保存。安装包包含离线词典及当前专题资料，查词、个人档案和专题练习可直接使用。第三方资料的来源与限制见独立声明。

## 学习快捷键

← 忘记、↓ 模糊、→ 认识、↑ 朗读；Space / Enter 执行主要动作，Ctrl+Z 撤销，F11 专注，Esc 关闭当前界面。评分在揭晓后生效，输入框、菜单与文本选择按上下文处理。全局查词、翻译、收藏默认 Alt+D / Alt+T / Alt+S，可在设置修改。

## 构建

```powershell
cargo build --manifest-path native/fsrs-optimizer/Cargo.toml --release --locked
dotnet run --project Lexi.csproj
```

安装包构建和定向验证见 [构建指南](../docs/platforms/BUILDING.md)。缺少第三方教材时，依赖该资料的历史 UI 检查不能作为公开包的测试结论。

## 许可与资源

当前分发采用本目录 PolyForm Noncommercial 1.0.0；此前 MIT 版本的既有授权保留，见 [许可政策](../docs/platforms/LICENSE-POLICY.md)。第三方许可独立保留在 Notices/ 和 FSRS vendor 目录。教材接入见 [资源指南](../docs/platforms/RESOURCES.md)。

## 源码目录

- UI/MainWindow：主窗口各功能的界面交互代码。
- Features、Controls、Shell、Styles：功能组件、控件、窗口外壳与样式。
- Domain、Application、Infrastructure、Services、Platform：数据模型、业务逻辑、存储与平台服务。
- Assets、Notices：资源及第三方声明。
- tests、SelfTest：领域测试与界面自检。
- native、packaging：FSRS 助手及安装包工具。

根目录保留启动入口、App/MainWindow 界面文件和 Lexi.csproj。

## 来源

本端源码复制自 [Cofran-77/lexi](https://github.com/Cofran-77/lexi)，基线提交 68becc1。作者 Cofran-77、DespairJasper。安装包和更新记录由来源仓库提供。本目录许可见 LICENSE，第三方声明见 docs/platforms。
