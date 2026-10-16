# Lexi for macOS

当前版本 **3.3.1**：[下载 DMG 与 SHA256](https://github.com/DespairJasper/lexi-macos/releases/tag/v3.3.1)。

3.3.1 新增「一起学习 · 续命局」：和一位好友各完成当日计划即可共同延续连续天数，含双人邀请、逐好友分享开关、预测曲线对照、一周详情与共同天数统计，头像私有存储。考试专题扩展至 IELTS、四级、六级、考研、GMAT、TOEFL、GRE，新增六类 20 本词书。IELTS 的雅思词汇真经、听力 179、阅读同义替换、英美拼写规范、写作 100 句及语法与听力资料继续提供。完整书目、达标与重开规则见 [主 README](../README.md)。保留全部学习记录、个人记忆参数及自动更新。

冰川蓝界面包含学习计划汇总、每日初学 / 复习 / 拼写，以及双语学习统计。日度活动图用色深表示时长；周度、月度与累计以方格柱显示聚合时长，活动图和遗忘曲线从左到右逐渐呈现。

源码位于 [lexi_avalonia](../lexi_avalonia/)，本地 FSRS 助手位于 [native](../native/)，构建与分发脚本位于 [scripts](../scripts/) 和 [packaging](../packaging/)。

用户数据保存在应用包外的 `~/Library/Application Support/Lexi/`。正常退出应用后覆盖安装，沿用词汇、计划、记录、设置及记忆模型；旧版未记录的时长不补算。

[安装、使用、构建与许可](../README.md) · [本次发布说明](../docs/macos/releases/v3.3.1.md)。Windows 与 Android 继续使用各自的源码、数据库和发布版本。

[3.2.1 原生界面预览](../README.md#界面预览) · [英文金句本](../docs/macos/images/quotes-en.png) · [学习按钮布局](../docs/macos/images/study-answer.png)。

3.3.1 的所有社交计算都建立在既有学习记录之上：词身份、计划选词、FSRS 排期与既有数据不变，学习历史和个人记忆数据完整保留；社交数据单独存放于 sidecar，云端只接收当日摘要与 121 点曲线，不接收词条或计划内容。
