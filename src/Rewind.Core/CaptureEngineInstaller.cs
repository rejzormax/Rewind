using System.IO.Compression;
using System.Net.Http;

namespace Rewind.Core;

public sealed class CaptureEngineInstaller(HttpClient http, string? expectedChecksum = null)
{
    public static readonly Uri DownloadUri = new("https://github.com/GyanD/codexffmpeg/releases/download/9.1/ffmpeg-9.1-essentials_build.zip");
    public const string PublisherChecksum = "b9bbbb3acece000cbeec994f856649354e52c773807841e78908374b24d658fd";
    private const long MaximumDownloadBytes = 256L * 1024 * 1024;
    private const long MaximumExecutableBytes = 220L * 1024 * 1024;

    public async Task<string> EnsureAsync(string engineDirectory, CancellationToken cancellationToken, IProgress<string>? progress = null)
    {
        var root = Path.GetFullPath(engineDirectory);
        var executable = Path.Combine(root, "ffmpeg.exe");
        if (IsExecutable(executable)) return executable;
        Directory.CreateDirectory(root);
        var archivePath = Path.Combine(root, ".download-" + Guid.NewGuid().ToString("N") + ".zip");
        var staging = Path.Combine(root, ".install-" + Guid.NewGuid().ToString("N"));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(12));
        var token = deadline.Token;
        try
        {
            progress?.Report("Downloading recording files…");
            var checksum = expectedChecksum ?? PublisherChecksum;
            if (!GitHubUpdateClient.IsSha256(checksum)) throw new InvalidDataException("The recording files have no valid publisher checksum.");
            using var response = await GetAsync(DownloadUri, token);
            if (response.Content.Headers.ContentLength > MaximumDownloadBytes) throw new InvalidDataException("The recording download is too large.");
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
                await CopyBoundedAsync(input, output, MaximumDownloadBytes, token);
            progress?.Report("Checking recording files…");
            if (!await GitHubUpdateClient.HasExpectedHashAsync(archivePath, checksum!, token)) throw new InvalidDataException("The recording download checksum does not match its publisher.");
            using var archive = ZipFile.OpenRead(archivePath);
            if (archive.Entries.Count > 2000) throw new InvalidDataException("The recording archive has too many entries.");
            var candidates = archive.Entries.Where(entry => entry.FullName.Replace('\\', '/').EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (candidates.Length != 1) throw new InvalidDataException("The recording archive has no unique engine executable.");
            Directory.CreateDirectory(staging);
            foreach (var name in new[] { "ffmpeg.exe", "LICENSE", "README.txt" })
            {
                var entry = name == "ffmpeg.exe" ? candidates[0] : archive.Entries.SingleOrDefault(item => Path.GetFileName(item.FullName.Replace('\\', '/')) == name);
                if (entry == null) throw new InvalidDataException("The recording archive is missing its executable or license information.");
                if (entry.Length > MaximumExecutableBytes || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new InvalidDataException("The recording archive contains an oversized file or link.");
                await using var input = entry.Open();
                await using var output = new FileStream(Path.Combine(staging, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await CopyBoundedAsync(input, output, entry.Length, token);
                if (output.Length != entry.Length) throw new InvalidDataException("The recording archive is incomplete.");
            }
            if (!IsExecutable(Path.Combine(staging, "ffmpeg.exe"))) throw new InvalidDataException("The recording executable is invalid.");
            token.ThrowIfCancellationRequested();
            foreach (var name in new[] { "LICENSE", "README.txt" }) File.Move(Path.Combine(staging, name), Path.Combine(root, name), true);
            if (!IsExecutable(executable)) File.Move(Path.Combine(staging, "ffmpeg.exe"), executable, true);
            progress?.Report("Recording files are ready.");
            return executable;
        }
        finally
        {
            try { File.Delete(archivePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (Directory.Exists(staging) && UpdatePackage.IsWithin(staging, root))
                try { Directory.Delete(staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Rewind-Setup/1.0");
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        try
        {
            response.EnsureSuccessStatusCode();
            var finalUri = response.RequestMessage?.RequestUri;
            if (finalUri != null && (finalUri.Scheme != "https" || !finalUri.IsDefaultPort || !string.IsNullOrEmpty(finalUri.UserInfo) ||
                !(finalUri.Host is "www.gyan.dev" or "gyan.dev" or "github.com" || finalUri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase))))
                throw new InvalidDataException("The recording download redirected outside its publisher.");
            return response;
        }
        catch { response.Dispose(); throw; }
    }
    public static bool IsExecutable(string path)
    {
        try { using var stream = File.OpenRead(path); return stream.Length > 1024 && stream.ReadByte() == 'M' && stream.ReadByte() == 'Z'; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    private static async Task CopyBoundedAsync(Stream input, Stream output, long maximum, CancellationToken token)
    {
        var buffer = new byte[128 * 1024];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            total += count;
            if (total > maximum) throw new InvalidDataException("The recording file exceeded its size limit.");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
    }
}
