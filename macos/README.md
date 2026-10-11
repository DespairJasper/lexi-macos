# Lexi for macOS

当前版本 **3.2.3**：[下载 DMG 与 SHA256](https://github.com/DespairJasper/lexi-macos/releases/tag/v3.2.3)。

3.2.3 修复每日计划学过的词在次日复习时显示内部标识（音标与释义为空）的问题；其余界面与交互沿用3.2.2，保留全部学习记录、个人记忆参数及自动更新。

冰川蓝界面包含学习计划汇总、每日初学 / 复习 / 拼写，以及双语学习统计。日度活动图用色深表示时长；周度、月度与累计以方格柱显示聚合时长，活动图和遗忘曲线从左到右逐渐呈现。

源码位于 [lexi_avalonia](../lexi_avalonia/)，本地 FSRS 助手位于 [native](../native/)，构建与分发脚本位于 [scripts](../scripts/) 和 [packaging](../packaging/)。

用户数据保存在应用包外的 `~/Library/Application Support/Lexi/`。正常退出应用后覆盖安装，沿用词汇、计划、记录、设置及记忆模型；旧版未记录的时长不补算。

[安装、使用、构建与许可](../README.md) · [本次发布说明](../docs/macos/releases/v3.2.3.md)。Windows 与 Android 继续使用各自的源码、数据库和发布版本。

[3.2.1 原生界面预览](../README.md#界面预览) · [英文金句本](../docs/macos/images/quotes-en.png) · [学习按钮布局](../docs/macos/images/study-answer.png)。

3.2.3 只补全复习卡词条内容：到期待复习、未完成复习轮恢复、撤销与改判共用同一解析路径；词身份、FSRS 排期与既有数据不变，学习历史和个人记忆数据完整保留。
