using System.Diagnostics;
using System.Net.Http;
using Rewind.Core;

namespace Rewind.Services;

public sealed class AppUpdateService(AppPaths paths, Func<bool> canInstall, Func<ProcessStartInfo, bool> launchWhenIdle) : IAsyncDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly SemaphoreSlim _wake = new(0, 1);
    private Task? _worker;
    private volatile bool _enabled;
    public event Action<string>? StatusChanged;

    public void Start(bool enabled)
    {
        _enabled = enabled;
        _worker ??= Task.Run(CheckLoopAsync);
    }
    public void SetEnabled(bool enabled)
    {
        var changed = _enabled != enabled; _enabled = enabled;
        if (enabled && changed) try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    private async Task CheckLoopAsync()
    {
        var token = _cancellation.Token;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            CleanupAbandonedUpdates();
            while (!token.IsCancellationRequested)
            {
                if (_enabled && UpdateConfiguration.Current.IsConfigured)
                {
                    try { await CheckOnceAsync(token); }
                    catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException or TimeoutException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException or System.ComponentModel.Win32Exception)
                    { paths.Log("Update check: " + error.Message); }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { paths.Log("Update check timed out."); }
                }
                await _wake.WaitAsync(TimeSpan.FromHours(6), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task CheckOnceAsync(CancellationToken token)
    {
        var version = typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
        var client = new GitHubUpdateClient(_http, UpdateConfiguration.Current);
        var update = await client.GetLatestAsync(version, token);
        if (update == null) return;
        var updatesRoot = UpdateState.GetUpdatesDirectory(paths.DataDirectory);
        var failed = Path.Combine(updatesRoot, "failed.json");
        if (File.Exists(failed))
        {
            using var info = System.Text.Json.JsonDocument.Parse(File.ReadAllText(failed));
            if (info.RootElement.GetProperty("Version").GetString() == update.Version.ToString() &&
                DateTimeOffset.UtcNow - info.RootElement.GetProperty("FailedAt").GetDateTimeOffset() < TimeSpan.FromDays(1)) return;
        }
        var downloads = Path.Combine(updatesRoot, "downloads"); Directory.CreateDirectory(downloads);
        var archive = Path.Combine(downloads, "release-" + Guid.NewGuid().ToString("N") + ".zip");
        string? helperRoot = null, planPath = null;
        var helperLaunched = false;
        try
        {
        StatusChanged?.Invoke($"Downloading Rewind {update.Version.Major}.{update.Version.Minor}.{update.Version.Build}…");
        await client.DownloadAsync(update, archive, token);
        if (!_enabled || !canInstall()) StatusChanged?.Invoke("Update downloaded. It will install when recording is paused.");
        while (!_enabled || !canInstall())
        {
            await Task.Delay(TimeSpan.FromMinutes(1), token);
        }
        var sourceHelper = Path.Combine(AppContext.BaseDirectory, "auto-updater.exe");
        if (!File.Exists(sourceHelper)) { File.Delete(archive); throw new IOException("This build is missing auto-updater.exe."); }
        helperRoot = Path.Combine(updatesRoot, "helper-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(helperRoot);
        foreach (var file in new[] { "auto-updater.exe", "auto-updater.dll", "auto-updater.runtimeconfig.json", "auto-updater.deps.json", "Rewind.Core.dll" })
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, file))) File.Copy(Path.Combine(AppContext.BaseDirectory, file), Path.Combine(helperRoot, file));
        planPath = Path.Combine(updatesRoot, "plan-" + Guid.NewGuid().ToString("N") + ".json");
        UpdateState.WriteJson(planPath, new UpdatePlan(UpdateConfiguration.Current.Repository, update.Version.ToString(), archive, update.Sha256,
            Environment.ProcessId, Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Rewind.exe")));
        var start = new ProcessStartInfo(Path.Combine(helperRoot, "auto-updater.exe")) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--plan"); start.ArgumentList.Add(planPath);
        start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(paths.DataDirectory);
        while (!_enabled || !(helperLaunched = launchWhenIdle(start))) await Task.Delay(TimeSpan.FromMinutes(1), token);
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        finally
        {
            if (!helperLaunched)
            {
                foreach (var file in new[] { archive, planPath })
                    if (file != null) try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                if (helperRoot != null && UpdatePackage.IsWithin(helperRoot, updatesRoot))
                    try { Directory.Delete(helperRoot, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private void CleanupAbandonedUpdates()
    {
        var root = UpdateState.GetUpdatesDirectory(paths.DataDirectory);
        if (!Directory.Exists(root)) return;
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (var directory in Directory.EnumerateDirectories(root, "helper-*"))
                if (UpdatePackage.IsWithin(directory, root) && Directory.GetCreationTimeUtc(directory) < cutoff)
                    try { Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            foreach (var file in Directory.EnumerateFiles(root, "plan-*.json"))
                if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
            var downloads = Path.Combine(root, "downloads");
            if (Directory.Exists(downloads))
                foreach (var file in Directory.EnumerateFiles(downloads, "release-*.zip"))
                    if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        if (_worker != null) await _worker;
        _http.Dispose(); _cancellation.Dispose(); _wake.Dispose();
    }
}
