namespace Lexi;

public sealed record ShortcutValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static ShortcutValidationResult Success => new(true, Array.Empty<string>());
    public static ShortcutValidationResult Failure(params string[] errors) => new(false, errors);
    public static ShortcutValidationResult Failure(IEnumerable<string> errors) => new(false, errors.ToList());
}

public static class ShortcutValidator
{
    private static readonly HashSet<string> DisallowedBareKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Q", "W", "E", "A", "S", "C", "D", "Z", "X", "V", "B", "N", "M",
        "R", "T", "Y", "U", "I", "O", "P", "F", "G", "H", "J", "K", "L",
        "DELETE", "DEL", "BACKSPACE", "TAB"
    };

    public static ShortcutValidationResult Validate(ShortcutConfiguration config)
    {
        if (config is null)
            return ShortcutValidationResult.Failure("配置不能为空。");

        var errors = new List<string>();
        var gestureToAction = new Dictionary<ShortcutGesture, ShortcutAction>();

        foreach (var action in Enum.GetValues<ShortcutAction>())
        {
            if (!config.Bindings.TryGetValue(action, out var gestures) || gestures == null || gestures.Count == 0)
            {
                errors.Add($"动作 '{action}' 缺少按键绑定。");
                continue;
            }

            foreach (var gestureStr in gestures)
            {
                if (!ShortcutGesture.TryParse(gestureStr, out var gesture))
                {
                    errors.Add($"动作 '{action}' 的快捷键 '{gestureStr}' 格式无效。");
                    continue;
                }

                // 校验系统保留键与禁止的裸键
                if (!IsKnownKey(gesture.Key)) errors.Add($"未知按键：{gesture.Key}");
                var reservedError = CheckReserved(gesture);
                if (reservedError != null)
                {
                    errors.Add($"动作 '{action}' 的快捷键 '{gesture}': {reservedError}");
                }

                // 冲突检测
                if (gestureToAction.TryGetValue(gesture, out var existingAction))
                {
                    if (existingAction != action)
                    {
                        errors.Add($"快捷键 '{gesture}' 冲突：同时绑定到了 '{existingAction}' 和 '{action}'。");
                    }
                }
                else
                {
                    gestureToAction[gesture] = action;
                }
            }
        }

        // 校验数字键备用模式冲突
        if(config.Bindings.TryGetValue(ShortcutAction.Escape,out var escape) && (escape.Count!=1||!ShortcutGesture.TryParse(escape[0],out var esc)||esc.Key!="Escape"||esc.Modifiers!=ShortcutModifiers.None))
            errors.Add("Esc 必须保留为返回键。");
        if (config.EnableNumberRatings)
        {
            var numKeys = new[] { "D1", "D2", "D3", "NumPad1", "NumPad2", "NumPad3" };
            foreach (var numKey in numKeys)
            {
                var numGesture = new ShortcutGesture(numKey, ShortcutModifiers.None);
                if (gestureToAction.TryGetValue(numGesture, out var mappedAction))
                {
                    if (mappedAction is not (ShortcutAction.Forgot or ShortcutAction.Unsure or ShortcutAction.Known))
                    {
                        errors.Add($"开启数字备用键时，数字键 '{numKey}' 不能绑定到除评分以外的动作 '{mappedAction}'。");
                    }
                }
            }
        }

        return errors.Count == 0
            ? ShortcutValidationResult.Success
            : ShortcutValidationResult.Failure(errors);
    }

    private static string? CheckReserved(ShortcutGesture gesture)
    {
        if(gesture.Modifiers.HasFlag(ShortcutModifiers.Meta))return "Windows 系统组合键保留。";
        // 1. 禁止 Alt+Space
        if (gesture.Key.Equals("Space", StringComparison.OrdinalIgnoreCase) && gesture.Modifiers == ShortcutModifiers.Alt)
            return "禁止使用 Alt+Space（系统保留与避免干扰全局操作）。";

        // 2. 禁止无修饰符的字母裸键与 Delete 裸键
        if (gesture.Modifiers == ShortcutModifiers.None && DisallowedBareKeys.Contains(gesture.Key))
            return $"禁止使用无修饰符的裸键 '{gesture.Key}'（避免输入框干扰与误触）。";

        // 3. 避免系统热键 Alt+F4, Alt+Tab
        if (gesture.Key.Equals("F4", StringComparison.OrdinalIgnoreCase) && gesture.Modifiers.HasFlag(ShortcutModifiers.Alt))
            return "Alt+F4 为系统保留快捷键。";

        if (gesture.Key.Equals("Tab", StringComparison.OrdinalIgnoreCase) && gesture.Modifiers.HasFlag(ShortcutModifiers.Alt))
            return "Alt+Tab 为系统保留快捷键。";

        return null;
    }
    private static bool IsKnownKey(string key)=>key is "Left" or "Right" or "Up" or "Down" or "Space" or "Enter" or "Escape" or "Home" or "End" or "PageUp" or "PageDown" or "Insert" or "Delete" or "Backspace" or "Tab"
        || key.Length==1&&key[0] is >= 'A' and <= 'Z'
        || key.StartsWith("F")&&int.TryParse(key[1..],out var fn)&&fn>=1&&fn<=24
        || key.StartsWith("D")&&int.TryParse(key[1..],out var digit)&&digit>=0&&digit<=9
        || key.StartsWith("NumPad")&&int.TryParse(key[6..],out var pad)&&pad>=0&&pad<=9;
}
