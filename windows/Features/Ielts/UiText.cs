using System;

namespace Lexi.Features.Ielts;

/// <summary>
/// IELTS 专区 UiText 路由契约：
/// 统一映射到 IeltsI18n，所有新增 UI 文字均使用 UiText.T / UiText.Format。
/// </summary>
public static class UiText
{
    public static string Language => Lexi.UiText.Language;
    public static string T(string chinese) => IeltsI18n.T(chinese);
    public static string Text(string chinese) => IeltsI18n.T(chinese);
    public static string Format(FormattableString value) => IeltsI18n.Format(value);
}
