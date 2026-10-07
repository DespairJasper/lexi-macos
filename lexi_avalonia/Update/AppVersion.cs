using System.Globalization;
using System.Reflection;

namespace Lexi;

/// <summary>
/// 产品版本的**唯一运行时来源**：程序集版本，由 lexi_avalonia/Lexi.csproj 的 &lt;Version&gt; 生成。
/// 打包版本（Info.plist）、状态栏字面量、DMG 名与 README 文案由
/// scripts/verify_source_integrity.py 断言与之一致，因此本文件不得再引入任何版本常量。
/// </summary>
internal static class AppVersion
{
    /// <summary>四段规范化的当前版本；缺失段补 0，避免 <see cref="Version"/> 把未指定段当作 -1。</summary>
    internal static readonly Version Current = Normalize(typeof(AppVersion).Assembly.GetName().Version);

    /// <summary>用于展示与日志的三段形式，例如 "3.1.2"。</summary>
    internal static string Display => Current.ToString(3);

    internal static Version Normalize(Version? value) => value == null
        ? new Version(0, 0, 0, 0)
        : new Version(Math.Max(value.Major, 0), Math.Max(value.Minor, 0),
                      Math.Max(value.Build, 0), Math.Max(value.Revision, 0));

    /// <summary>
    /// 解析发布标签。接受 "3.1.2" / "v3.1.2" / "V3.1.2" / "3.1" / "3.1.2.0" / "3.1.2-beta.1" / "3.1.2+build"。
    /// 预发布与构建元数据后缀被丢弃（调用方已按 GitHub 的 draft/prerelease 标志过滤，此处只做防御）。
    /// 无法确定时返回 null —— 调用方必须把 null 当作"未知"，不得当作"有新版本"。
    /// </summary>
    internal static Version? Parse(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var text = tag.Trim();
        if (text.Length > 1 && (text[0] == 'v' || text[0] == 'V') && char.IsAsciiDigit(text[1]))
            text = text[1..];
        var cut = text.IndexOfAny(['-', '+']);
        if (cut >= 0) text = text[..cut];
        var parts = text.Split('.');
        if (parts.Length is < 1 or > 4) return null;
        var numbers = new int[4];
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (part.Length == 0 || !part.All(char.IsAsciiDigit)) return null;
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out numbers[index])) return null;
        }
        return new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
    }

    /// <summary>仅当 <paramref name="tag"/> 可解析且严格大于 <see cref="Current"/> 时为 true。</summary>
    internal static bool IsNewer(string? tag) => IsNewer(tag, Current);

    internal static bool IsNewer(string? tag, Version current) => Parse(tag) is { } parsed && parsed > current;
}
