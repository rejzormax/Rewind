namespace Rewind.Services;

public sealed record CaptureTools(string Ffmpeg, string? Ffprobe);

public static class FfmpegLocator
{
    public static CaptureTools? Find(string? engineDirectory = null)
    {
        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe") };
        candidates.Add(Path.Combine(engineDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rewind", "engine"), "ffmpeg.exe"));
        var configured = Environment.GetEnvironmentVariable("REWIND_FFMPEG");
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Insert(0, configured);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && directory != null; i++, directory = directory.Parent)
            candidates.Add(Path.Combine(directory.FullName, ".tools", "ffmpeg", "ffmpeg.exe"));
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (!string.IsNullOrWhiteSpace(entry)) candidates.Add(Path.Combine(entry.Trim('"'), "ffmpeg.exe"));
        var executable = candidates.FirstOrDefault(File.Exists);
        if (executable == null) return null;
        var probe = Path.Combine(Path.GetDirectoryName(executable)!, "ffprobe.exe");
        return new CaptureTools(executable, File.Exists(probe) ? probe : null);
    }
}
