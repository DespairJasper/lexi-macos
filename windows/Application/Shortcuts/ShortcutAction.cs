namespace Lexi;

/// <summary>
/// 学习与工作区快捷键动作定义。
/// </summary>
public enum ShortcutAction
{
    /// <summary>忘记 / Forgot (默认: Left，备用: 1)</summary>
    Forgot,

    /// <summary>模糊 / Unsure (默认: Down，备用: 2)</summary>
    Unsure,

    /// <summary>认识 / Known (默认: Right，备用: 3)</summary>
    Known,

    /// <summary>发音朗读 / Speak (默认: Up)</summary>
    Speak,

    /// <summary>主推进动作: 学习推进 / 揭晓释义 / 已评分下一词 (默认: Space, Enter)</summary>
    Primary,

    /// <summary>撤销上一步评价 (默认: Ctrl+Z)</summary>
    Undo,

    /// <summary>收藏词汇 (默认: Ctrl+D)</summary>
    Favorite,

    /// <summary>切换全局专注 (默认: F11)</summary>
    ToggleGlobalFocus,

    /// <summary>退出 / 返回 / 关闭顶层层级 (默认: Escape)</summary>
    Escape
}
