using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Rewind.Core;

public sealed record AvailableUpdate(Version Version, string Tag, Uri DownloadUri, string Sha256, long SizeBytes);

public sealed class GitHubUpdateClient(HttpClient http, UpdateConfiguration configuration)
{
    public const long MaximumDownloadBytes = 256L * 1024 * 1024;

    public async Task<AvailableUpdate?> GetLatestAsync(Version currentVersion, CancellationToken token)
    {
        if (!configuration.IsConfigured) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, configuration.LatestReleaseUri);
        request.Headers.UserAgent.ParseAdd("Rewind-Updater/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 1024 * 1024) throw new InvalidDataException("The release response is too large.");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var bytes = new MemoryStream();
        await CopyBoundedAsync(input, bytes, 1024 * 1024, token);
        using var document = JsonDocument.Parse(bytes.ToArray());
        return ParseRelease(document.RootElement, currentVersion, configuration);
    }

    public static AvailableUpdate? ParseRelease(JsonElement release, Version currentVersion, UpdateConfiguration configuration)
    {
        if (!configuration.IsConfigured) throw new InvalidDataException("The update repository is invalid.");
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseVersion(tag, out var version) || version <= currentVersion) return null;
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != configuration.AssetName) continue;
            var uri = new Uri(asset.GetProperty("browser_download_url").GetString()!, UriKind.Absolute);
            if (!configuration.IsDownloadUriValid(uri)) throw new InvalidDataException("The release download is outside the configured repository.");
            var digest = asset.TryGetProperty("digest", out var digestValue) ? digestValue.GetString() : null;
            if (digest == null || !digest.StartsWith("sha256:", StringComparison.Ordinal) || !IsSha256(digest[7..]))
                throw new InvalidDataException("The release asset has no valid GitHub SHA-256 digest.");
            var size = asset.GetProperty("size").GetInt64();
            if (size is < 1024 or > MaximumDownloadBytes) throw new InvalidDataException("The release asset size is outside the allowed limit.");
            return new AvailableUpdate(version, tag, uri, digest[7..].ToLowerInvariant(), size);
        }
        return null;
    }

    public async Task DownloadAsync(AvailableUpdate update, string destination, CancellationToken token, IProgress<long>? progress = null)
    {
        if (!configuration.IsDownloadUriValid(update.DownloadUri) || !IsSha256(update.Sha256)) throw new InvalidDataException("The update is invalid.");
        using var request = new HttpRequestMessage(HttpMethod.Get, update.DownloadUri);
        request.Headers.UserAgent.ParseAdd("Rewind-Updater/1.0");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri != null && (finalUri.Scheme != "https" || !(finalUri.Host == "github.com" || finalUri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("The update redirected outside GitHub.");
        if (response.Content.Headers.ContentLength > MaximumDownloadBytes) throw new InvalidDataException("The update download is too large.");
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
                await CopyBoundedAsync(input, output, Math.Min(update.SizeBytes, MaximumDownloadBytes), token, progress);
            if (new FileInfo(destination).Length != update.SizeBytes || !await HasExpectedHashAsync(destination, update.Sha256, token))
                throw new InvalidDataException("The downloaded update does not match GitHub's size or SHA-256 digest.");
        }
        catch { try { File.Delete(destination); } catch (IOException) { } throw; }
    }

    public static bool TryParseVersion(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var value = tag.StartsWith('v') ? tag[1..] : tag;
        var parts = value.Split('.');
        if (parts.Length is < 3 or > 4 || parts.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit))) return false;
        if (!Version.TryParse(value, out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
        return true;
    }
    public static bool IsSha256(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    public static async Task<bool> HasExpectedHashAsync(string path, string expected, CancellationToken token)
    {
        if (!IsSha256(expected)) return false;
        await using var file = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(file, token);
        return CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(expected));
    }
    private static async Task CopyBoundedAsync(Stream input, Stream output, long maximum, CancellationToken token, IProgress<long>? progress = null)
    {
        var buffer = new byte[128 * 1024];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            total += count;
            if (total > maximum) throw new InvalidDataException("The download exceeded its allowed size.");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
            progress?.Report(total);
        }
    }
}
