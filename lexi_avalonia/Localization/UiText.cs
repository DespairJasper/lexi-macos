using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace Lexi;

/// <summary>UI-only translation. Vocabulary, notes and AI content never pass through this catalog.</summary>
internal static class UiText
{
    internal static string Language { get; private set; } = "zh-CN";
    private static readonly Dictionary<string, (string Chinese, string English)> Rendered = new(StringComparer.Ordinal);
    internal static string Normalize(string? language) => language == "en" ? "en" : "zh-CN";
    internal static string Text(string chinese)
    {
        var english = UiCatalog.English.GetValueOrDefault(chinese, chinese);
        Remember(chinese, english);
        return Language == "en" ? english : chinese;
    }
    internal static string Format(FormattableString value)
    {
        var chinese = value.ToString(CultureInfo.CurrentCulture);
        var english = string.Format(CultureInfo.CurrentCulture, UiCatalog.English.GetValueOrDefault(value.Format, value.Format), value.GetArguments());
        Remember(chinese, english);
        return Language == "en" ? english : chinese;
    }
    private static void Remember(string chinese, string english)
    {
        Rendered[chinese] = (chinese, english);
        Rendered[english] = (chinese, english);
    }
    internal static string Redisplay(string? rendered)
    {
        if (rendered == null) return "";
        if (Rendered.TryGetValue(rendered, out var pair)) return Language == "en" ? pair.English : pair.Chinese;
        return Text(rendered);
    }
    internal static void Apply(string? language)
    {
        Language = Normalize(language);
        if (Avalonia.Application.Current is not { } app) return;
        foreach (var entry in UiCatalog.Resources) app.Resources[entry.Key] = Text(entry.Value);
        // Native menu items are refreshed explicitly because their lifetime differs from visual controls.
        var icons = TrayIcon.GetIcons(app);
        if (icons is { Count: > 0 })
        {
            icons[0].ToolTipText = "lexi";
            if (icons[0].Menu is { } menu)
            {
                if (menu.Items[0] is NativeMenuItem toggle) toggle.Header = Text("显示 / 隐藏主窗口");
                if (menu.Items[^1] is NativeMenuItem quit) quit.Header = Text("退出 lexi");
            }
        }
    }
    internal static string Value(object? value, string? kind = null)
    {
        if (kind == "encounters") return Format($"遇见 {value} 次 · AI 辅助内容可在编辑档案中修改");
        if (kind == "created") return Format($"录入于 {value}");
        var text = value?.ToString() ?? "";
        // Legacy stage is only a persistence projection, never a five-step learning promise.
        // Only domain-generated stage/status fields use this converter; user meaning/notes do not.
        var stage = Regex.Match(text, "^阶段 (\\d+) / 5$");
        if (stage.Success) return Text("待复习");
        stage = Regex.Match(text, "^第 (\\d+) 阶段 \\(共 5 阶段\\)$");
        if (stage.Success) return Text("待复习");
        if (text == "全部完成") return Text("已暂停复习");
        return Redisplay(text);
    }
}

public sealed class UiValueConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => UiText.Value(value, parameter?.ToString());
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
