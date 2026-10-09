using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lexi;

public sealed class ShortcutConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _filePath;

    public string FilePath => _filePath;

    public ShortcutConfigStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("配置路径不能为空。", nameof(filePath));
        _filePath = Path.GetFullPath(filePath);
    }

    public static ShortcutConfigStore ForDataDirectory(string dataDirectory, string fileName = "study-shortcuts.json")
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("数据目录不能为空。", nameof(dataDirectory));
        return new ShortcutConfigStore(Path.Combine(dataDirectory, fileName));
    }

    public ShortcutConfiguration Load()
    {
        if (!File.Exists(_filePath))
        {
            var def = ShortcutConfiguration.CreateDefault();
            return def;
        }

        ShortcutConfiguration config;
        try
        {
            var text = File.ReadAllText(_filePath);
            config = JsonSerializer.Deserialize<ShortcutConfiguration>(text, JsonOptions)
                ?? throw new InvalidDataException("快捷键配置文件内容为空。");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"快捷键 JSON 格式无效: {ex.Message}", ex);
        }

        var validation = ShortcutValidator.Validate(config);
        if (!validation.IsValid)
        {
            throw new InvalidDataException($"快捷键配置校验失败: {string.Join("; ", validation.Errors)}");
        }

        return config;
    }

    public void Save(ShortcutConfiguration config)
    {
        if (config is null)
            throw new ArgumentNullException(nameof(config));

        var validation = ShortcutValidator.Validate(config);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException($"快捷键配置校验失败，无法保存: {string.Join("; ", validation.Errors)}");
        }

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temporary = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(temporary, json);
            File.Move(temporary, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); } catch { /* ignore */ }
            }
        }
    }
}
