当前 Android 预览版本，Android 8.0+，Kotlin + Jetpack Compose 原生应用。

- 与 Windows 1.1.3 起统一 29 列 CSV，兼容旧 CSV/JSON，迁移不等于云同步。
- PROCESS_TEXT 入口“Lexi 查词”，接收传入词自动查询，并保留系统分享入口。
- 例句语境强调目标词的真实用法，默认复习 PDF 仅中文释义，完整档案/CSV 保留双语。
- 保留本地查词、档案、主动 AI、备份恢复和原生 PDF。

**复习仍按第 1、2、4、7、15 天固定日期规则，不是 Windows FSRS-6。**

下载 `Lexi-Android-0.2.2-alpha.apk`。它是已有开发签名预览包，私钥未上传；签名相同才能覆盖原版本，签名不同请先备份/导出并规划迁移，不能把卸载视为保留数据。没有认证荣耀 FreeNote 全部入口、真实付费 AI 或全部 Android / MagicOS 设备。本次归档未重新运行全部移动测试。

## 源码、许可与范围

作者：**Cofran-77 与 DespairJasper**。原创代码与文档采用 MIT；ECDICT、FSRS 和其他依赖保留各自许可。

此节点由保存的交付源码快照整理而来，不伪造原开发日期。公开源码排除个人数据、密钥、签名材料、内部协作日志与第三方 IELTS 教材/录音/讲义。源码 ZIP 只包含本平台与许可说明；GitHub 自动的 Source code ZIP 可能包含该历史提交时的其他平台目录。

macOS 仅预留协作入口，没有源码或安装包。下载文件请与同一 Release 的 SHA256SUMS.txt 核对。

完整介绍、构建和数据迁移说明见 main 分支 README、docs/BUILDING.md、docs/DATA_AND_MIGRATION.md 与 THIRD_PARTY_NOTICES.md。
