namespace Lexi;
internal static partial class UiCatalog
{
    private static void AddInteractionTranslations()
    {
        var pairs=new Dictionary<string,string>
        {
            ["句子翻译"]="Translate sentence",["翻译句子"]="Translate sentence",["金句本"]="Quotes",
            ["专注"]="Focus",["退出专注"]="Exit focus",["进入 / 退出专注模式 · F11"]="Toggle focus mode · F11",
            ["今日复习"]="Review today",["将所选词安排到今天"]="Schedule selected words for review today",
            ["今日完成 {0}/{1} 词"]="Today {0}/{1} words",["总进度 {0}/{1} · 预计剩余 {2} 天"]="Total {0}/{1} · {2} days left",
            ["↗ 打印"]="↗ Print",["导出打印 A4 清单"]="Export an A4 print sheet",
            ["设置"]="Settings",["← 返回分类"]="← Categories",["外观与界面"]="Appearance",
            ["主题模式、字号大小与界面视觉偏好"]="Theme and visual preferences",
            ["学习与快捷键"]="Study & shortcuts",["学习交互按键绑定、复习队列与评测行为"]="Study actions and keyboard controls",
            ["AI 服务配置"]="AI services",["API 密钥、大模型端点与提示词配置"]="API keys, endpoints and context",
            ["词库备份、导入导出与数据维护"]="Backups, import, export and maintenance",
            ["关于 Lexi"]="About Lexi",["版本信息、开源许可与致谢"]="Version, licenses and credits",
            ["已选"]="Selected",["词"]="words",["天"]="days",["今日完成"]="Completed today",
            ["总进度"]="Total",["预计剩余"]="Remaining",["计划管理"]="Manage plan",["开始学习"]="Start study",["继续学习"]="Continue study",
            ["已完成与已停止的计划"]="Completed and stopped plans",["学习所选"]="Study selected",["更多 ▾"]="More ▾",
            ["在沉浸式视图中学习所选单词"]="Study selected words in focus mode",
            ["基于所选单词创建新的学习计划"]="Create a study plan for selected words",
            ["将单词标记为已熟知，跳过日常复习"]="Mark as mastered and skip daily review",
            ["重置记忆进度并重新安排学习排期"]="Reset memory progress and reschedule",
            ["导出所选"]="Export selected",["导出为文本或表格文件"]="Export vocabulary data",
            ["从词汇档案中永久移除所选单词"]="Remove selected words from vocabulary",
            ["选择专注内容"]="Choose focus content",["继续已有学习，或选择词汇来源。"]="Continue a task or choose a vocabulary source.",
            ["选择词汇档案中的词"]="Choose vocabulary words",["浏览 IELTS 教材"]="Browse IELTS materials",
            ["重新学习将重置所选词的记忆进度与排期。"]="Relearning resets memory progress and the review schedule.",
            ["确认重新学习"]="Confirm relearning",["调整计划"]="Edit plan",["停止计划"]="Stop plan",["恢复计划"]="Resume plan",
            ["查看最近批次"]="View last batch",["每日词数"]="Words per day",["未来批次随机"]="Shuffle future batches",
            ["保存调整"]="Save changes",["请填写名称，每日词数为 1–10000。 "]="Enter a name and a daily target from 1 to 10,000.",
            ["删除计划…"]="Delete plan…",["确认删除此计划"]="Confirm deletion",
            ["Ctrl+D 收藏当前单词；Alt+Space 保留 Windows 系统菜单。学习按键可以在此自定义。"]="Ctrl+D saves the current word. Alt+Space opens the Windows system menu. Customize study shortcuts here."
        };
        foreach(var pair in pairs) English[pair.Key]=pair.Value;
    }
}
