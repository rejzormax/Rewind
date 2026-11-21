using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rewind.Core;

internal static class UpdateChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var config = UpdateConfiguration.Current;
        check(config.Repository == "rejzormax/Rewind" && config.IsConfigured, "updater is bound to the Rewind repository");
        check(!UpdateConfiguration.IsRepositoryValid("../other") && !UpdateConfiguration.IsRepositoryValid("https://evil.test/x/y"), "repository paths cannot redirect update requests");
        check(GitHubUpdateClient.TryParseVersion("v1.2.3", out var version) && version == new Version(1, 2, 3, 0) &&
            !GitHubUpdateClient.TryParseVersion("v1.2.3-rc1", out _), "stable release versions are normalized and prerelease labels rejected");
        var current = new Version(0, 4, 0, 0);
        var root = Path.Combine(Path.GetTempPath(), "rewind-update-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var archive = Path.Combine(root, "release.zip");
            CreateZip(archive, [("Rewind/Rewind.exe", "versioned app"), ("Rewind/tools/engine.txt", "engine")]);
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
            { using var padding = zip.CreateEntry("Rewind/test-data.bin").Open(); padding.Write(RandomNumberGenerator.GetBytes(2048)); }
            var bytes = await File.ReadAllBytesAsync(archive);
            var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var payload = Release("v0.4.1", digest, bytes.Length);
            var update = Parse(payload, current, config)!;
            check(update.Version == new Version(0, 4, 1, 0), "new stable release is selected");
            check(Parse(Release("v0.4.0", digest, bytes.Length), current, config) == null &&
                Parse(Release("v0.3.9", digest, bytes.Length), current, config) == null, "same versions and downgrades are ignored");
            check(Parse(Release("v0.4.1", digest, bytes.Length, true), current, config) == null, "draft releases are not installed");
            Reject(() => Parse(Release("v0.4.1", digest, bytes.Length, url: "https://github.com/attacker/Rewind/releases/download/v0.4.1/Rewind-Windows-x64.zip"), current, config), check, "foreign repositories cannot supply an update");
            Reject(() => Parse(Release("v0.4.1", "bad", bytes.Length), current, config), check, "updates without a valid digest are rejected");
            Reject(() => Parse(Release("v0.4.1", digest, GitHubUpdateClient.MaximumDownloadBytes + 1), current, config), check, "oversized release assets are rejected");
            using var http = new HttpClient(new MockHandler(payload, bytes));
            var client = new GitHubUpdateClient(http, config);
            check((await client.GetLatestAsync(current, CancellationToken.None))?.Version == update.Version, "release API response is parsed without credentials");
            var downloaded = Path.Combine(root, "download.zip");
            await client.DownloadAsync(update, downloaded, CancellationToken.None);
            check(await GitHubUpdateClient.HasExpectedHashAsync(downloaded, digest, CancellationToken.None), "download is verified against GitHub's digest");
            var badDownload = Path.Combine(root, "bad-download.zip");
            try { await client.DownloadAsync(update with { Sha256 = new string('0', 64) }, badDownload, CancellationToken.None); throw new Exception("A corrupt download was accepted."); }
            catch (InvalidDataException) { check(!File.Exists(badDownload), "corrupt downloads are removed"); }
            var installation = Path.Combine(root, "install");
            UpdatePackage.Extract(downloaded, installation);
            check(File.ReadAllText(Path.Combine(installation, "Rewind.exe")) == "versioned app" && File.Exists(Path.Combine(installation, "tools", "engine.txt")), "versioned ZIP root is extracted correctly");
            foreach (var unsafePath in new[] { "../escape.txt", "Rewind/../../escape.txt", "C:/escape.txt", "Rewind/file:stream", "Rewind/NUL.txt", "Rewind/dir./file" })
            {
                var unsafeArchive = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
                CreateZip(unsafeArchive, [("Rewind/Rewind.exe", "app"), (unsafePath, "bad")]);
                var target = Path.Combine(root, "unsafe-" + Guid.NewGuid().ToString("N"));
                Reject(() => UpdatePackage.Extract(unsafeArchive, target), check, "unsafe ZIP path rejected: " + unsafePath);
                check(!Directory.Exists(target), "unsafe archive creates no installation files");
            }
            var duplicate = Path.Combine(root, "duplicate.zip");
            CreateZip(duplicate, [("Rewind/Rewind.exe", "app"), ("Rewind/REWIND.EXE", "different")]);
            Reject(() => UpdatePackage.Extract(duplicate, Path.Combine(root, "duplicate-install")), check, "case-insensitive duplicate ZIP entries are rejected");
            var link = Path.Combine(root, "link.zip");
            using (var zip = ZipFile.Open(link, ZipArchiveMode.Create))
            {
                Write(zip.CreateEntry("Rewind/Rewind.exe"), "app");
                var entry = zip.CreateEntry("Rewind/link"); entry.ExternalAttributes = unchecked((int)0xA1FF0000); Write(entry, "elsewhere");
            }
            Reject(() => UpdatePackage.Extract(link, Path.Combine(root, "link-install")), check, "symbolic links are not extracted");
            var dataRoot = Path.Combine(root, "data");
            var installed = Path.Combine(UpdateState.GetVersionsDirectory(dataRoot), "0.4.1.0"); Directory.CreateDirectory(installed);
            File.WriteAllText(Path.Combine(installed, "Rewind.exe"), "app");
            var marker = Path.Combine(UpdateState.GetUpdatesDirectory(dataRoot), "current.json");
            UpdateState.WriteJson(marker, new InstalledUpdate(config.Repository, "0.4.1", "0.4.1.0"));
            check(UpdateState.GetInstalledExecutable(dataRoot, config, current) == Path.Combine(installed, "Rewind.exe"), "portable launcher resolves a newer managed installation");
            UpdateState.WriteJson(marker, new InstalledUpdate(config.Repository, "0.4.1", "../../elsewhere"));
            check(UpdateState.GetInstalledExecutable(dataRoot, config, current) == null, "installation markers cannot escape the managed version directory");
            File.WriteAllText(marker, "{\"Repository\":\"rejzormax/Rewind\",\"Version\":null,\"DirectoryName\":null}");
            check(UpdateState.GetInstalledExecutable(dataRoot, config, current) == null, "an incomplete installation marker cannot crash app startup");
        }
        finally
        {
            var verified = Path.GetFullPath(root);
            if (verified.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(verified).StartsWith("rewind-update-tests-", StringComparison.Ordinal))
                Directory.Delete(verified, true);
        }
    }
    private static string Release(string tag, string digest, long size, bool draft = false, string? url = null) => JsonSerializer.Serialize(new
    {
        tag_name = tag, draft, prerelease = false,
        assets = new[] { new { name = "Rewind-Windows-x64.zip", size, digest = "sha256:" + digest,
            browser_download_url = url ?? $"https://github.com/rejzormax/Rewind/releases/download/{tag}/Rewind-Windows-x64.zip" } }
    });
    private static AvailableUpdate? Parse(string json, Version current, UpdateConfiguration config)
    { using var document = JsonDocument.Parse(json); return GitHubUpdateClient.ParseRelease(document.RootElement, current, config); }
    private static void Reject(Action action, Action<bool, string> check, string name)
    { try { action(); } catch (InvalidDataException) { check(true, name); return; } throw new Exception("Unsafe update accepted: " + name); }
    private static void CreateZip(string path, (string Name, string Value)[] entries)
    { using var zip = ZipFile.Open(path, ZipArchiveMode.Create); foreach (var (name, value) in entries) Write(zip.CreateEntry(name), value); }
    private static void Write(ZipArchiveEntry entry, string text) { using var output = new StreamWriter(entry.Open()); output.Write(text); }
    private sealed class MockHandler(string json, byte[] archive) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { RequestMessage = request, Content = request.RequestUri!.Host == "api.github.com" ? new StringContent(json, Encoding.UTF8, "application/json") : new ByteArrayContent(archive) });
    }
}
