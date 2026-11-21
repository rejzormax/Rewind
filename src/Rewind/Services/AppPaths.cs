using System.Text.Json;
using Rewind.Core;

namespace Rewind.Services;

public sealed class AppPaths
{
    public string DataDirectory { get; }
    public string CacheDirectory => Path.Combine(DataDirectory, "buffer");
    public string EngineDirectory => Path.Combine(DataDirectory, "engine");
    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public string LogPath => Path.Combine(DataDirectory, "rewind.log");
    public string DefaultClipDirectory { get; }

    public AppPaths(string? dataDirectory = null)
    {
        var defaultDataDirectory = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rewind"));
        DataDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory ?? defaultDataDirectory));
        DefaultClipDirectory = string.Equals(DataDirectory, defaultDataDirectory, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Rewind")
            : Path.Combine(DataDirectory, "clips");
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(CacheDirectory);
    }

    public AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var source = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(source)?.Validated();
                using var json = JsonDocument.Parse(source);
                if (settings != null && !json.RootElement.TryGetProperty("CheckForUpdates", out _) && json.RootElement.TryGetProperty("AutomaticUpdates", out var legacyUpdates))
                    settings = settings with { CheckForUpdates = legacyUpdates.GetBoolean() };
                if (settings != null) return string.IsNullOrWhiteSpace(settings.ClipDirectory)
                    ? settings with { ClipDirectory = DefaultClipDirectory } : settings;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException) { Log(ex.Message); }
        return new AppSettings { ClipDirectory = DefaultClipDirectory };
    }

    public void SaveSettings(AppSettings settings)
    {
        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings.Validated(), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, SettingsPath, true);
    }

    private readonly object _logLock = new();
    public void Log(string message)
    {
        lock (_logLock)
        {
            try
            {
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 512 * 1024)
                    File.Move(LogPath, LogPath + ".previous", true);
                File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
