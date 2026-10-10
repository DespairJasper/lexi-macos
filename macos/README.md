# Lexi for macOS

当前版本 **3.2.2**：[下载 DMG 与 SHA256](https://github.com/DespairJasper/lexi-macos/releases/tag/v3.2.2)。

3.2.2 修复已确认计划快照与全量备份造成的磁盘增长，自动清理旧快照并回收空闲空间，保留全部学习记录、个人记忆参数及自动更新；沿用3.2.1界面。

冰川蓝界面包含学习计划汇总、每日初学 / 复习 / 拼写，以及双语学习统计。日度活动图用色深表示时长；周度、月度与累计以方格柱显示聚合时长，活动图和遗忘曲线从左到右逐渐呈现。

源码位于 [lexi_avalonia](../lexi_avalonia/)，本地 FSRS 助手位于 [native](../native/)，构建与分发脚本位于 [scripts](../scripts/) 和 [packaging](../packaging/)。

用户数据保存在应用包外的 `~/Library/Application Support/Lexi/`。正常退出应用后覆盖安装，沿用词汇、计划、记录、设置及记忆模型；旧版未记录的时长不补算。

[安装、使用、构建与许可](../README.md) · [本次发布说明](../docs/macos/releases/v3.2.2.md)。Windows 与 Android 继续使用各自的源码、数据库和发布版本。

[3.2.1 原生界面预览](../README.md#界面预览) · [英文金句本](../docs/macos/images/quotes-en.png) · [学习按钮布局](../docs/macos/images/study-answer.png)。
