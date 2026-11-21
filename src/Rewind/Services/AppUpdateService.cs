using System.Diagnostics;
using System.Net.Http;
using Rewind.Core;

namespace Rewind.Services;

public enum AppUpdatePhase { Idle, Available, Downloading, WaitingForIdle, Installing, Failed }
public sealed record AppUpdateState(AppUpdatePhase Phase, Version? Version = null, int? Percentage = null, string? Error = null);

public sealed class AppUpdateService : IAsyncDisposable
{
    private readonly AppPaths _paths;
    private readonly Func<bool> _canInstall;
    private readonly Func<ProcessStartInfo, bool> _launchWhenIdle;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _helperDirectory;
    private readonly TimeSpan _startupDelay;
    private readonly SemaphoreSlim _wake = new(0, 1), _idleWake = new(0, 1);
    private readonly object _gate = new();
    private Task? _worker, _download;
    private volatile bool _enabled;
    private int _checking;
    private AvailableUpdate? _available;
    public AppUpdateState State { get; private set; } = new(AppUpdatePhase.Idle);
    public event Action<AppUpdateState>? Changed;

    public AppUpdateService(AppPaths paths, Func<bool> canInstall, Func<ProcessStartInfo, bool> launchWhenIdle,
        HttpClient? http = null, string? helperDirectory = null, TimeSpan? startupDelay = null)
    {
        _paths = paths; _canInstall = canInstall; _launchWhenIdle = launchWhenIdle;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) }; _ownsHttp = http == null;
        _helperDirectory = helperDirectory ?? AppContext.BaseDirectory;
        _startupDelay = startupDelay ?? TimeSpan.FromSeconds(10);
    }

    public void Start(bool enabled)
    {
        _enabled = enabled;
        _worker ??= Task.Run(CheckLoopAsync);
    }
    public void SetEnabled(bool enabled)
    {
        var changed = _enabled != enabled; _enabled = enabled;
        if (enabled && changed) Signal(_wake);
    }
    public void NotifyIdleChanged() => Signal(_idleWake);
    private static void Signal(SemaphoreSlim signal) { try { signal.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { } }

    private async Task CheckLoopAsync()
    {
        var token = _cancellation.Token;
        try
        {
            await Task.Delay(_startupDelay, token);
            CleanupAbandonedUpdates();
            while (!token.IsCancellationRequested)
            {
                if (_enabled) await CheckNowAsync();
                await _wake.WaitAsync(TimeSpan.FromHours(6), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public async Task CheckNowAsync()
    {
        if (Interlocked.Exchange(ref _checking, 1) != 0) return;
        try
        {
            lock (_gate) if (_download is { IsCompleted: false }) return;
            var version = typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
            var client = new GitHubUpdateClient(_http, UpdateConfiguration.Current);
            var update = await client.GetLatestAsync(version, _cancellation.Token);
            lock (_gate)
            {
                if (_download is { IsCompleted: false }) return;
                _available = update;
            }
            Publish(update == null ? new(AppUpdatePhase.Idle) : new(AppUpdatePhase.Available, update.Version));
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException or
            System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException or OperationCanceledException)
        { _paths.Log("Update check: " + error.Message); }
        finally { Interlocked.Exchange(ref _checking, 0); }
    }

    public Task DownloadUpdateAsync()
    {
        lock (_gate)
        {
            if (_download is { IsCompleted: false }) return _download;
            if (_available == null || _cancellation.IsCancellationRequested) return Task.CompletedTask;
            var selected = _available;
            _download = Task.Run(() => DownloadAndInstallAsync(selected));
            return _download;
        }
    }

    private async Task DownloadAndInstallAsync(AvailableUpdate update)
    {
        var token = _cancellation.Token;
        var updatesRoot = UpdateState.GetUpdatesDirectory(_paths.DataDirectory);
        var downloads = Path.Combine(updatesRoot, "downloads");
        var archive = Path.Combine(downloads, "release-" + Guid.NewGuid().ToString("N") + ".zip");
        string? helperRoot = null, planPath = null;
        var helperLaunched = false;
        try
        {
            Directory.CreateDirectory(downloads);
            Publish(new(AppUpdatePhase.Downloading, update.Version, 0));
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(TimeSpan.FromMinutes(15));
                var percentage = -1;
                var progress = new DownloadProgress(bytes =>
                {
                    var next = (int)Math.Clamp(bytes * 100 / update.SizeBytes, 0, 100);
                    if (next == percentage) return;
                    percentage = next; Publish(new(AppUpdatePhase.Downloading, update.Version, next));
                });
                await new GitHubUpdateClient(_http, UpdateConfiguration.Current).DownloadAsync(update, archive, deadline.Token, progress);
            }
            Publish(new(AppUpdatePhase.WaitingForIdle, update.Version, 100));
            while (!_canInstall()) await _idleWake.WaitAsync(TimeSpan.FromSeconds(5), token);
            var sourceHelper = Path.Combine(_helperDirectory, "auto-updater.exe");
            if (!File.Exists(sourceHelper)) throw new IOException("The update helper is missing. Reinstall Rewind using Setup.");
            helperRoot = Path.Combine(updatesRoot, "helper-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(helperRoot);
            foreach (var file in new[] { "auto-updater.exe", "auto-updater.dll", "auto-updater.runtimeconfig.json", "auto-updater.deps.json", "Rewind.Core.dll" })
                if (File.Exists(Path.Combine(_helperDirectory, file))) File.Copy(Path.Combine(_helperDirectory, file), Path.Combine(helperRoot, file));
            planPath = Path.Combine(updatesRoot, "plan-" + Guid.NewGuid().ToString("N") + ".json");
            UpdateState.WriteJson(planPath, new UpdatePlan(UpdateConfiguration.Current.Repository, update.Version.ToString(), archive, update.Sha256,
                Environment.ProcessId, Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Rewind.exe")));
            var start = new ProcessStartInfo(Path.Combine(helperRoot, "auto-updater.exe")) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--plan"); start.ArgumentList.Add(planPath);
            start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(_paths.DataDirectory);
            while (!_canInstall() || !(helperLaunched = _launchWhenIdle(start)))
                await _idleWake.WaitAsync(TimeSpan.FromSeconds(5), token);
            Publish(new(AppUpdatePhase.Installing, update.Version, 100));
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            _paths.Log("Update download: " + error.Message);
            Publish(new(AppUpdatePhase.Failed, update.Version, Error: error.Message));
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

    private void Publish(AppUpdateState state)
    {
        lock (_gate) State = state;
        Changed?.Invoke(state);
    }

    private void CleanupAbandonedUpdates()
    {
        var root = UpdateState.GetUpdatesDirectory(_paths.DataDirectory);
        if (!Directory.Exists(root)) return;
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-1);
            foreach (var directory in Directory.EnumerateDirectories(root, "helper-*"))
                if (Guid.TryParseExact(Path.GetFileName(directory)[7..], "N", out _) && UpdatePackage.IsWithin(directory, root) && Directory.GetCreationTimeUtc(directory) < cutoff)
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
        Task? download; lock (_gate) download = _download;
        if (download != null) await download;
        if (_ownsHttp) _http.Dispose();
        _cancellation.Dispose(); _wake.Dispose(); _idleWake.Dispose();
    }

    private sealed class DownloadProgress(Action<long> report) : IProgress<long> { public void Report(long value) => report(value); }
}
