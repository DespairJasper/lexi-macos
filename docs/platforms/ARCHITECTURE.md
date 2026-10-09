# 架构与平台边界

Windows 与 Android 是独立工程；此仓库采用 monorepo 便于共同维护文档与版本，不意味着共享所有运行时代码。

## Windows

- `Domain/`：基础档案和复习排期契约。
- `Application/`：学习会话、计划、教材目录模型与档案迁移服务。
- `Infrastructure/`、`Services/`、`Platform/`：SQLite、AI、Windows 密钥/文档/系统集成；部分文件由项目显式链接编译。
- `Features/`、`Controls/`、`Shell/`、`Styles/`：学习、档案、IELTS、设置及视图组件。
- `MainWindow.*`：页面与服务的实际集成；不是另一个独立前端项目。
- `native/fsrs-optimizer/`：Rust 参数训练助手；vendor/fsrs 是保留原许可的第三方库。
- `tests/` 与 `SelfTest/`：领域及交互检查；其中一些 UI 测试需要合法的本地教材资源。

长期复习以 FSRS-6 记忆模型与真实评分记录为依据；计划轮内的强化进度和长期排期不同，不能把完成一次拼写简单等同为固定日期升级。

## Android

Kotlin / Compose 主界面；`app/src/main/java/org/lexi/archive` 包含 ViewModel、SQLite repository、数据迁移、服务与 UI。固定复习日期规则尚未与 Windows FSRS 模型统一。Android PROCESS_TEXT 与分享是系统入口，不申请全局悬浮窗或无障碍监听。

## macOS

由合作作者后续提供本端实现。外部 `DespairJasper/lexi-macos` 可作为参考，但本仓库没有复制该端源码，也不承诺其现有许可证自动变化。

## 资源与数据

离线字典为 MIT 授权 ECDICT 子集。Git 不跟踪用户数据库、个人设置、API Key 或签名材料。第三方专题资料在当前 Windows 分发附件中单独提供，来源与权利声明保留。各端本地数据库有自己的恢复流程；词条迁移经 CSV/兼容 JSON 完成，而非直接共享 SQLite 文件。
