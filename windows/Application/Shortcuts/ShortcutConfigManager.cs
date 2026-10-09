namespace Lexi;

public interface IShortcutConfigManager
{
    ShortcutConfiguration CurrentConfig { get; }
    ShortcutConfigStore Store { get; }

    ShortcutValidationResult Validate(ShortcutConfiguration config);
    ShortcutValidationResult TryUpdateBindings(ShortcutAction action, IEnumerable<string> gestures);
    ShortcutValidationResult TrySetNumberRatings(bool enabled);

    void Save();
    void Load();
    void ResetToDefault();

    IReadOnlyList<string> GetGestures(ShortcutAction action);
    event Action<ShortcutConfiguration>? ConfigChanged;
}

public sealed class ShortcutConfigManager : IShortcutConfigManager
{
    private readonly ShortcutConfigStore _store;
    private ShortcutConfiguration _currentConfig;

    public ShortcutConfigStore Store => _store;
    public ShortcutConfiguration CurrentConfig => _currentConfig;

    public event Action<ShortcutConfiguration>? ConfigChanged;

    public ShortcutConfigManager(ShortcutConfigStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _currentConfig = _store.Load();
    }

    public ShortcutValidationResult Validate(ShortcutConfiguration config)
    {
        return ShortcutValidator.Validate(config);
    }

    public ShortcutValidationResult TryUpdateBindings(ShortcutAction action, IEnumerable<string> gestures)
    {
        var clone = _currentConfig.Clone();
        clone.Bindings[action] = gestures.ToList();
        var validation = Validate(clone);
        if (!validation.IsValid)
            return validation;

        _store.Save(clone);
        _currentConfig = clone;
        ConfigChanged?.Invoke(_currentConfig);
        return ShortcutValidationResult.Success;
    }

    public ShortcutValidationResult TrySetNumberRatings(bool enabled)
    {
        var clone = _currentConfig.Clone();
        clone.EnableNumberRatings = enabled;
        var validation = Validate(clone);
        if (!validation.IsValid)
            return validation;

        _store.Save(clone);
        _currentConfig = clone;
        ConfigChanged?.Invoke(_currentConfig);
        return ShortcutValidationResult.Success;
    }

    public void Save()
    {
        _store.Save(_currentConfig);
    }

    public void Load()
    {
        _currentConfig = _store.Load();
        ConfigChanged?.Invoke(_currentConfig);
    }

    public void ResetToDefault()
    {
        var def = ShortcutConfiguration.CreateDefault();
        _store.Save(def);
        _currentConfig = def;
        ConfigChanged?.Invoke(_currentConfig);
    }

    public IReadOnlyList<string> GetGestures(ShortcutAction action)
    {
        if (_currentConfig.Bindings.TryGetValue(action, out var list) && list != null)
            return list;
        return [];
    }
}
