using System.Text.Json;

namespace Rewind.Core;

public sealed record UpdatePlan(string Repository, string Version, string ArchivePath, string Sha256, int ParentProcessId, string ParentExecutable);
public sealed record InstalledUpdate(string Repository, string Version, string DirectoryName);
public sealed record UpdateStartupAcknowledgement(int ProcessId, string Version);

public static class UpdateState
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static string GetUpdatesDirectory(string dataDirectory) => Path.Combine(Path.GetFullPath(dataDirectory), "updates");
    public static string GetVersionsDirectory(string dataDirectory) => Path.Combine(GetUpdatesDirectory(dataDirectory), "versions");
    public static string GetAcknowledgementPath(string dataDirectory, string launchId)
    {
        if (launchId.Length != 32 || !launchId.All(char.IsAsciiHexDigit)) throw new InvalidDataException("The update launch identifier is invalid.");
        return Path.Combine(GetUpdatesDirectory(dataDirectory), "started-" + launchId + ".json");
    }
    public static string? GetInstalledExecutable(string dataDirectory, UpdateConfiguration configuration, Version currentVersion)
    {
        try
        {
            var marker = Path.Combine(GetUpdatesDirectory(dataDirectory), "current.json");
            if (!File.Exists(marker) || new FileInfo(marker).Length > 4096) return null;
            var installed = JsonSerializer.Deserialize<InstalledUpdate>(File.ReadAllText(marker), Options);
            if (installed == null || installed.Repository != configuration.Repository ||
                !GitHubUpdateClient.TryParseVersion(installed.Version, out var version) || version <= currentVersion || installed.DirectoryName != version.ToString()) return null;
            var directory = Path.Combine(GetVersionsDirectory(dataDirectory), installed.DirectoryName);
            var path = Path.Combine(directory, "Rewind.exe");
            return UpdatePackage.IsWithin(path, GetVersionsDirectory(dataDirectory)) && File.Exists(path) ? path : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Options));
        File.Move(temporary, path, true);
    }
    public static T ReadJson<T>(string path)
    {
        if (new FileInfo(path).Length > 16384) throw new InvalidDataException("The update state is too large.");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? throw new InvalidDataException("The update state is invalid.");
    }
}
