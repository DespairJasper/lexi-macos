using System.Text.Json.Serialization;

namespace Lexi;

public sealed class ShortcutConfiguration
{
    public Dictionary<ShortcutAction, List<string>> Bindings { get; set; } = [];

    public bool EnableNumberRatings { get; set; } = true;

    public static ShortcutConfiguration CreateDefault()
    {
        return new ShortcutConfiguration
        {
            EnableNumberRatings = true,
            Bindings = new Dictionary<ShortcutAction, List<string>>
            {
                [ShortcutAction.Forgot] = ["Left"],
                [ShortcutAction.Unsure] = ["Down"],
                [ShortcutAction.Known] = ["Right"],
                [ShortcutAction.Speak] = ["Up"],
                [ShortcutAction.Primary] = ["Space", "Enter"],
                [ShortcutAction.Undo] = ["Ctrl+Z"],
                [ShortcutAction.Favorite] = ["Ctrl+D"],
                [ShortcutAction.ToggleGlobalFocus] = ["F11"],
                [ShortcutAction.Escape] = ["Escape"]
            }
        };
    }

    public ShortcutConfiguration Clone()
    {
        var copy = new ShortcutConfiguration
        {
            EnableNumberRatings = EnableNumberRatings,
            Bindings = []
        };
        foreach (var (action, list) in Bindings)
        {
            copy.Bindings[action] = new List<string>(list);
        }
        return copy;
    }

    public IReadOnlyList<ShortcutGesture> GetGestures(ShortcutAction action)
    {
        if (!Bindings.TryGetValue(action, out var list) || list == null)
            return [];

        var results = new List<ShortcutGesture>();
        foreach (var item in list)
        {
            if (ShortcutGesture.TryParse(item, out var gesture))
                results.Add(gesture);
        }
        return results;
    }
}
