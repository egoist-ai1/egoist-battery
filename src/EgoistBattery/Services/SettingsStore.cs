using System.Text.Json;

namespace EgoistBattery.Services;

internal sealed class Settings
{
    public int RefreshSeconds { get; set; } = 15;
    public int LowThreshold { get; set; } = 20;
    public bool Notifications { get; set; } = true;
    public bool EcoMode { get; set; } = true;
    public List<string> HiddenTrayDevices { get; set; } = [];
}

internal sealed class SettingsStore(string directory)
{
    public string DirectoryPath { get; } = directory;
    public string? LoadWarning { get; private set; }
    public Settings Load()
    {
        try
        {
            var path = Path.Combine(DirectoryPath, "settings.json");
            var settings = File.Exists(path) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new() : new();
            settings.RefreshSeconds = Math.Clamp(settings.RefreshSeconds, 5, 120);
            settings.LowThreshold = Math.Clamp(settings.LowThreshold, 5, 50);
            settings.HiddenTrayDevices ??= [];
            return settings;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { LoadWarning = "Не удалось прочитать настройки. Использованы стандартные значения."; return new(); }
    }

    public void Save(Settings settings) => WriteJson("settings.json", settings);
    public void WriteJson(string filename, object data)
    {
        Directory.CreateDirectory(DirectoryPath);
        var target = Path.Combine(DirectoryPath, filename);
        var temporary = target + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, target, true);
    }
    public void Log(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var path = Path.Combine(DirectoryPath, "errors.log");
            if (File.Exists(path) && new FileInfo(path).Length > 256_000) File.Move(path, path + ".previous", true);
            // Записываем только тип и стек нашего приложения, без произвольных данных устройств.
            var reason = exception is System.Windows.Markup.XamlParseException ? exception.GetBaseException().Message : exception.GetType().Name;
            File.AppendAllText(path, $"{DateTimeOffset.Now:o} {reason}\n{exception.StackTrace}\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
