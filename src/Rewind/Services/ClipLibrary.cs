using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rewind.Services;

public sealed record SavedClip(string Path, string Title, DateTime Created, double Duration,
    int Width, int Height, long SizeBytes, string? ThumbnailPath)
{
    public string DurationLabel => Duration > 0 ? $"{(int)Duration / 60:00}:{(int)Duration % 60:00}" : "MP4";
    public string DetailLabel => $"{Created:dd MMM · HH:mm}   ·   {SizeBytes / (1024d * 1024d):0.0} MB";
    public string ResolutionLabel => Width > 0 ? $"{Width} × {Height}" : "H.264 video";
    public string DateLabel => Created.Date == DateTime.Today ? "Today" : Created.Date == DateTime.Today.AddDays(-1) ? "Yesterday" : Created.ToString("ddd, d MMM");
    public string SizeLabel => SizeBytes >= 1024L * 1024 * 1024 ? $"{SizeBytes / (1024d * 1024 * 1024):0.0} GB" : $"{SizeBytes / (1024d * 1024):0.0} MB";
    public string LibraryDetailLabel => $"On device · {SizeLabel} · {DateLabel}";
}

public sealed class ClipLibrary(CaptureTools tools, AppPaths paths)
{
    public async Task<SavedClip> ImportAsync(string source, string directory, CancellationToken token = default)
    {
        var inputPath = Path.GetFullPath(source);
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, Path.GetFileName(inputPath));
        if (string.Equals(inputPath, Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            return await InspectAsync(inputPath, token);
        if (File.Exists(destination)) destination = Path.Combine(directory,
            Path.GetFileNameWithoutExtension(inputPath) + "_" + Guid.NewGuid().ToString("N")[..8] + ".mp4");
        var temporary = Path.Combine(directory, ".pending-import-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            await using (var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await input.CopyToAsync(output, 128 * 1024, token);
            File.SetCreationTimeUtc(temporary, File.GetCreationTimeUtc(inputPath));
            var inspected = await InspectAsync(temporary, token);
            if (inspected.Width == 0 || inspected.Duration <= 0) throw new IOException("The selected MP4 does not contain a readable video clip.");
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination);
            string? poster = null;
            if (File.Exists(temporary + ".jpg")) { poster = destination + ".jpg"; File.Move(temporary + ".jpg", poster); }
            var clip = inspected with { Path = destination, Title = Path.GetFileNameWithoutExtension(destination).Replace('_', ' '), ThumbnailPath = poster };
            await File.WriteAllTextAsync(destination + ".rewind.json", JsonSerializer.Serialize(clip), CancellationToken.None);
            return clip;
        }
        finally
        {
            foreach (var path in new[] { temporary, temporary + ".jpg", temporary + ".rewind.json" })
                try { File.Delete(path); } catch (IOException) { }
        }
    }

    public async Task<SavedClip> InspectAsync(string path, CancellationToken token = default)
    {
        double duration = 0;
        int width = 0, height = 0;
        if (tools.Ffprobe != null)
        {
            try
            {
                var result = await ProcessRunner.RunAsync(tools.Ffprobe,
                    ["-v", "error", "-show_entries", "format=duration:stream=width,height", "-of", "json", path], TimeSpan.FromSeconds(10), token);
                if (result.ExitCode == 0)
                {
                    using var json = JsonDocument.Parse(result.Output);
                    if (json.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var time))
                        double.TryParse(time.GetString(), CultureInfo.InvariantCulture, out duration);
                    if (json.RootElement.TryGetProperty("streams", out var streams))
                        foreach (var stream in streams.EnumerateArray())
                            if (stream.TryGetProperty("width", out var w))
                            { width = w.GetInt32(); height = stream.GetProperty("height").GetInt32(); break; }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or TimeoutException) { paths.Log(ex.Message); }
        }
        else
        {
            try
            {
                var result = await ProcessRunner.RunAsync(tools.Ffmpeg, ["-hide_banner", "-i", path], TimeSpan.FromSeconds(10), token);
                var time = Regex.Match(result.Error, @"Duration:\s*(\d+):(\d+):(\d+\.\d+)", RegexOptions.CultureInvariant);
                if (time.Success) duration = int.Parse(time.Groups[1].Value) * 3600 + int.Parse(time.Groups[2].Value) * 60 +
                    double.Parse(time.Groups[3].Value, CultureInfo.InvariantCulture);
                var dimensions = Regex.Match(result.Error, @"Video:[^\r\n]*?\b(\d{2,5})x(\d{2,5})(?=[,\s])", RegexOptions.CultureInvariant);
                if (dimensions.Success) { width = int.Parse(dimensions.Groups[1].Value); height = int.Parse(dimensions.Groups[2].Value); }
            }
            catch (Exception ex) when (ex is IOException or TimeoutException) { paths.Log(ex.Message); }
        }
        var thumbnail = path + ".jpg";
        try
        {
            var result = await ProcessRunner.RunAsync(tools.Ffmpeg,
                ["-hide_banner", "-loglevel", "error", "-y", "-ss", "0.25", "-i", path, "-frames:v", "1",
                 "-vf", "scale=480:-2", "-threads", "1", "-q:v", "4", thumbnail], TimeSpan.FromSeconds(10), token);
            if (result.ExitCode != 0) thumbnail = null;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException) { paths.Log(ex.Message); thumbnail = null; }
        var info = new FileInfo(path);
        var clip = new SavedClip(path, Path.GetFileNameWithoutExtension(path).Replace('_', ' '), info.CreationTime,
            duration, width, height, info.Length, File.Exists(thumbnail) ? thumbnail : null);
        try { await File.WriteAllTextAsync(path + ".rewind.json", JsonSerializer.Serialize(clip), token); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { paths.Log(ex.Message); }
        return clip;
    }

    public static IReadOnlyList<SavedClip> Load(string directory)
    {
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.mp4", SearchOption.TopDirectoryOnly)
            .Where(path => !Path.GetFileName(path).StartsWith(".pending-", StringComparison.Ordinal))
            .Select(path => new FileInfo(path)).OrderByDescending(file => file.CreationTimeUtc).Take(300).Select(file =>
            {
                var thumbnail = File.Exists(file.FullName + ".jpg") ? file.FullName + ".jpg" : null;
                try
                {
                    if (File.Exists(file.FullName + ".rewind.json"))
                    {
                        var stored = JsonSerializer.Deserialize<SavedClip>(File.ReadAllText(file.FullName + ".rewind.json"));
                        if (stored != null) return stored with { Path = file.FullName, SizeBytes = file.Length, ThumbnailPath = thumbnail };
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
                return new SavedClip(file.FullName, Path.GetFileNameWithoutExtension(file.Name).Replace('_', ' '),
                    file.CreationTime, 0, 0, 0, file.Length, thumbnail);
            }).ToArray();
    }
}
