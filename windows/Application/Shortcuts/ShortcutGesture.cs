using System.Text;

namespace Lexi;

[Flags]
public enum ShortcutModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Meta = 8
}

public sealed class ShortcutGesture : IEquatable<ShortcutGesture>
{
    public string Key { get; }
    public ShortcutModifiers Modifiers { get; }

    public ShortcutGesture(string key, ShortcutModifiers modifiers = ShortcutModifiers.None)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("按键名称不能为空。", nameof(key));

        Key = NormalizeKeyName(key);
        Modifiers = modifiers;
    }

    public static string NormalizeKeyName(string key)
    {
        var trimmed = key.Trim();
        if (trimmed.Equals("left", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("←", StringComparison.OrdinalIgnoreCase))
            return "Left";
        if (trimmed.Equals("right", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("→", StringComparison.OrdinalIgnoreCase))
            return "Right";
        if (trimmed.Equals("up", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("↑", StringComparison.OrdinalIgnoreCase))
            return "Up";
        if (trimmed.Equals("down", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("↓", StringComparison.OrdinalIgnoreCase))
            return "Down";
        if (trimmed.Equals("space", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("spacebar", StringComparison.OrdinalIgnoreCase))
            return "Space";
        if (trimmed.Equals("enter", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("return", StringComparison.OrdinalIgnoreCase))
            return "Enter";
        if (trimmed.Equals("esc", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("escape", StringComparison.OrdinalIgnoreCase))
            return "Escape";
        if (trimmed.Equals("1", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("d1", StringComparison.OrdinalIgnoreCase))
            return "D1";
        if (trimmed.Equals("2", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("d2", StringComparison.OrdinalIgnoreCase))
            return "D2";
        if (trimmed.Equals("3", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("d3", StringComparison.OrdinalIgnoreCase))
            return "D3";
        if (trimmed.Equals("numpad1", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("num1", StringComparison.OrdinalIgnoreCase))
            return "NumPad1";
        if (trimmed.Equals("numpad2", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("num2", StringComparison.OrdinalIgnoreCase))
            return "NumPad2";
        if (trimmed.Equals("numpad3", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("num3", StringComparison.OrdinalIgnoreCase))
            return "NumPad3";

        return trimmed.ToUpperInvariant();
    }

    public static bool TryParse(string text, out ShortcutGesture gesture)
    {
        gesture = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split(['+', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        var modifiers = ShortcutModifiers.None;
        string? keyPart = null;

        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Equals("ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("control", StringComparison.OrdinalIgnoreCase))
                modifiers |= ShortcutModifiers.Control;
            else if (part.Equals("alt", StringComparison.OrdinalIgnoreCase))
                modifiers |= ShortcutModifiers.Alt;
            else if (part.Equals("shift", StringComparison.OrdinalIgnoreCase))
                modifiers |= ShortcutModifiers.Shift;
            else if (part.Equals("win", StringComparison.OrdinalIgnoreCase) || part.Equals("meta", StringComparison.OrdinalIgnoreCase) || part.Equals("cmd", StringComparison.OrdinalIgnoreCase))
                modifiers |= ShortcutModifiers.Meta;
            else
            {
                if (keyPart != null) return false; // 多于一个主按键
                keyPart = part;
            }
        }

        if (string.IsNullOrWhiteSpace(keyPart)) return false;
        gesture = new ShortcutGesture(keyPart, modifiers);
        return true;
    }

    public static ShortcutGesture Parse(string text)
    {
        if (!TryParse(text, out var gesture))
            throw new FormatException($"无效的快捷键格式: '{text}'。");
        return gesture;
    }

    public bool Matches(string key, ShortcutModifiers modifiers)
    {
        var norm = NormalizeKeyName(key);
        if (norm != Key)
        {
            // Allow matching D1 with NumPad1 or vice versa if checking number ratings?
            // Strict match here, fallback keys handled specifically in engine.
            return false;
        }
        return Modifiers == modifiers;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        if (Modifiers.HasFlag(ShortcutModifiers.Control)) sb.Append("Ctrl+");
        if (Modifiers.HasFlag(ShortcutModifiers.Alt)) sb.Append("Alt+");
        if (Modifiers.HasFlag(ShortcutModifiers.Shift)) sb.Append("Shift+");
        if (Modifiers.HasFlag(ShortcutModifiers.Meta)) sb.Append("Meta+");
        sb.Append(Key);
        return sb.ToString();
    }

    public bool Equals(ShortcutGesture? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase) && Modifiers == other.Modifiers;
    }

    public override bool Equals(object? obj) => Equals(obj as ShortcutGesture);

    public override int GetHashCode() => HashCode.Combine(Key.ToUpperInvariant(), Modifiers);
}
