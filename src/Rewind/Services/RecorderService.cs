using System.Diagnostics;
using System.Globalization;
using Rewind.Core;

namespace Rewind.Services;

public sealed class RecorderService : IAsyncDisposable
{
    private readonly CaptureTools _tools;
    private readonly AppPaths _paths;
    private readonly ClipLibrary _library;
    private readonly bool _testSource;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _export = new(1, 1);
    private readonly object _catalogLock = new();
    private readonly HashSet<string> _pinned = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<WasapiAudioPipe> _audio = [];
    private Process? _process;
    private Task<string>? _errorReader;
    private Task? _progressReader, _monitor;
    private CancellationTokenSource? _cancellation;
    private IReadOnlyList<Segment> _segments = [];
    private AppSettings _settings = new();
    private string? _sessionDirectory;
    private double _outputSeconds;
    private long _progressTimestamp;
    private volatile bool _running, _saving, _starting, _stopping;
    public bool IsRunning => _running;
    public bool IsSaving => _saving;
    public bool IsStarting => _starting;
    public CaptureBackend Backend { get; private set; }
    public int EffectiveFps => CaptureArguments.EffectiveFps(Backend, _settings);
    public long BufferBytes { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public event Action? Changed;
    public event Action<string>? Error;
    public event Action<SavedClip>? ClipSaved;
    public double BufferedSeconds
    {
        get { lock (_catalogLock) return _segments.Count == 0 ? 0 : Math.Min(_settings.BufferSeconds, _segments[^1].End - _segments[0].Start); }
    }
    public double CurrentMediaTime => Math.Max(0, Volatile.Read(ref _outputSeconds) +
        (_progressTimestamp == 0 ? 0 : Math.Min(1, (Stopwatch.GetTimestamp() - Interlocked.Read(ref _progressTimestamp)) / (double)Stopwatch.Frequency)));

    public (long, TimeSpan) GetUsage()
    {
        try { _process?.Refresh(); return _process is { HasExited: false } ? (_process.WorkingSet64, _process.TotalProcessorTime) : (0, TimeSpan.Zero); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return (0, TimeSpan.Zero); }
    }

    public RecorderService(CaptureTools tools, AppPaths paths, bool testSource = false)
    {
        _tools = tools; _paths = paths; _testSource = testSource;
        _library = new ClipLibrary(tools, paths);
        foreach (var directory in Directory.EnumerateDirectories(paths.CacheDirectory, "session-*"))
            TryDeleteSession(directory);
    }

    public async Task StartAsync(AppSettings settings, CaptureDisplay display, CancellationToken token = default)
    {
        await _lifecycle.WaitAsync(token);
        try
        {
            if (_running) return;
            _settings = settings.Validated();
            _starting = true; _stopping = false; Changed?.Invoke();
            _sessionDirectory = Path.Combine(_paths.CacheDirectory, "session-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_sessionDirectory);
            _cancellation = new CancellationTokenSource();
            var candidates = _testSource ? new[] { CaptureBackend.TestPattern } : CaptureArguments.Candidates(_settings, display);
            var found = false;
            var failures = new List<string>();
            foreach (var backend in candidates)
            {
                try
                {
                    var probe = await ProcessRunner.RunAsync(_tools.Ffmpeg,
                        CaptureArguments.Build(_settings, display, backend, [], _sessionDirectory, true), TimeSpan.FromSeconds(8), token);
                    if (probe.ExitCode == 0) { Backend = backend; found = true; break; }
                    failures.Add(CaptureArguments.BackendLabel(backend) + ": " + probe.Error);
                }
                catch (TimeoutException ex) { failures.Add(ex.Message); }
            }
            if (!found)
            {
                _paths.Log(string.Join(Environment.NewLine, failures));
                throw new IOException("No capture backend could start. Try the CPU encoder, use a borderless game window, and check the diagnostics log.");
            }
            if (_settings.SystemAudio) _audio.Add(await WasapiAudioPipe.CreateAsync(true, token));
            if (_settings.Microphone) _audio.Add(await WasapiAudioPipe.CreateAsync(false, token));
            _outputSeconds = 0; _progressTimestamp = 0;
            _process = ProcessRunner.Start(_tools.Ffmpeg,
                CaptureArguments.Build(_settings, display, Backend, _audio.Select(a => a.Input).ToArray(), _sessionDirectory));
            _errorReader = ProcessRunner.ReadBoundedAsync(_process.StandardError, 32 * 1024);
            _progressReader = Task.Run(() => ReadProgressAsync(_process.StandardOutput));
            var deadline = Stopwatch.StartNew();
            while (_outputSeconds <= 0 && deadline.Elapsed < TimeSpan.FromSeconds(12))
            {
                if (_process.HasExited) throw new IOException("The capture engine stopped: " + await _errorReader);
                if (_audio.Any(a => a.Failure != null)) throw new IOException("An audio device could not start. Check its Windows permissions.");
                await Task.Delay(100, token);
            }
            if (_outputSeconds <= 0) throw new TimeoutException("Capture did not produce frames. Try another display or disable audio temporarily.");
            _running = true; _starting = false;
            StartedAt = DateTimeOffset.Now;
            _monitor = Task.Run(() => MonitorAsync(_cancellation.Token));
            _paths.Log($"Capture started: {CaptureArguments.BackendLabel(Backend)}, {EffectiveFps} FPS, display {display.Id}.");
            Changed?.Invoke();
        }
        catch { await StopCoreAsync(); throw; }
        finally { _starting = false; _lifecycle.Release(); Changed?.Invoke(); }
    }

    private async Task ReadProgressAsync(StreamReader reader)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds) && microseconds >= 0)
            { Volatile.Write(ref _outputSeconds, microseconds / 1_000_000d); Interlocked.Exchange(ref _progressTimestamp, Stopwatch.GetTimestamp()); }
    }

    private async Task MonitorAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(token))
            {
                if (_process == null || _process.HasExited)
                    throw new IOException("Capture stopped unexpectedly. " + (_errorReader == null ? "" : await _errorReader));
                if (_audio.Any(a => a.Failure != null))
                    throw new IOException("An audio device disconnected. Check Windows audio settings and restart capture.");
                RefreshCatalog(); Prune(); Changed?.Invoke();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_stopping) return;
            _paths.Log(ex.ToString()); Error?.Invoke(ex.Message);
            _ = Task.Run(StopAsync);
        }
    }

    private void RefreshCatalog()
    {
        if (_sessionDirectory == null) return;
        var path = Path.Combine(_sessionDirectory, "segments.csv");
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file);
            var parsed = SegmentCatalog.Parse(reader.ReadToEnd(), _sessionDirectory).Where(s => File.Exists(s.Path)).ToArray();
            if (parsed.Length == 0) return;
            lock (_catalogLock) _segments = parsed;
        }
        catch (IOException) { }
    }

    private void Prune()
    {
        if (_sessionDirectory == null) return;
        lock (_catalogLock)
        {
            var threshold = SegmentCatalog.PruneBeforeSequence(_segments, _settings.BufferSeconds);
            long bytes = 0;
            foreach (var path in Directory.EnumerateFiles(_sessionDirectory, "part-*.ts"))
            {
                try
                {
                    if (SegmentCatalog.TryGetSequence(Path.GetFileName(path), out var index) && index < threshold && !_pinned.Contains(path)) File.Delete(path);
                    else bytes += new FileInfo(path).Length;
                }
                catch (IOException) { }
            }
            _segments = _segments.Where(s => s.Sequence >= threshold || _pinned.Contains(s.Path)).ToArray();
            BufferBytes = bytes;
        }
    }

    public async Task<SavedClip> SaveClipAsync(CancellationToken token = default)
    {
        var target = CurrentMediaTime;
        if (!_running) throw new InvalidOperationException("Start the replay buffer before saving a clip.");
        if (!await _export.WaitAsync(0, token)) throw new InvalidOperationException("A clip is already being saved.");
        ClipPlan? plan = null;
        string? manifest = null, temporary = null;
        try
        {
            _saving = true; Changed?.Invoke();
            var wait = Stopwatch.StartNew();
            // Let the segment containing the hotkey press finish, without pausing capture.
            while (wait.Elapsed < TimeSpan.FromSeconds(6) && _running)
            {
                RefreshCatalog();
                lock (_catalogLock) if (_segments.Count > 0 && _segments[^1].End >= target) break;
                await Task.Delay(100, token);
            }
            lock (_catalogLock)
            {
                plan = SegmentCatalog.Plan(_segments, target, _settings.BufferSeconds);
                foreach (var segment in plan.Segments) _pinned.Add(segment.Path);
            }
            manifest = Path.Combine(_sessionDirectory!, "export-" + Guid.NewGuid().ToString("N") + ".ffconcat");
            await File.WriteAllLinesAsync(manifest, new[] { "ffconcat version 1.0" }
                .Concat(plan.Segments.Select(s => CaptureArguments.ConcatFileLine(s.Path))), token);
            Directory.CreateDirectory(_settings.ClipDirectory);
            var name = $"Rewind_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{Guid.NewGuid().ToString("N")[..4]}.mp4";
            var destination = Path.Combine(_settings.ClipDirectory, name);
            temporary = Path.Combine(_settings.ClipDirectory, ".pending-" + Guid.NewGuid().ToString("N") + ".mp4");
            var result = await ProcessRunner.RunAsync(_tools.Ffmpeg, CaptureArguments.Export(manifest, temporary, plan.Duration), TimeSpan.FromSeconds(45), token);
            if (result.ExitCode != 0 || !File.Exists(temporary) || new FileInfo(temporary).Length < 512)
                throw new IOException("The clip could not be exported. " + result.Error);
            File.Move(temporary, destination);
            temporary = null;
            var clip = await _library.InspectAsync(destination, token);
            _paths.Log($"Clip saved: {name}, {clip.Duration:0.00}s, {clip.SizeBytes} bytes.");
            ClipSaved?.Invoke(clip);
            return clip;
        }
        finally
        {
            lock (_catalogLock) if (plan != null) foreach (var segment in plan.Segments) _pinned.Remove(segment.Path);
            if (manifest != null) TryDeleteFile(manifest);
            if (temporary != null) TryDeleteFile(temporary);
            _saving = false; _export.Release(); Changed?.Invoke();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            await _export.WaitAsync();
            try { await StopCoreAsync(); }
            finally { _export.Release(); }
        }
        finally { _lifecycle.Release(); }
    }

    private async Task StopCoreAsync()
    {
        _stopping = true;
        if (_cancellation != null) await _cancellation.CancelAsync();
        if (_process != null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    await _process.StandardInput.WriteLineAsync("q");
                    await _process.StandardInput.FlushAsync();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await _process.WaitForExitAsync(timeout.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException) { ProcessRunner.Kill(_process); }
            await _process.WaitForExitAsync();
        }
        foreach (var audio in _audio) await audio.DisposeAsync();
        _audio.Clear();
        if (_progressReader != null) await _progressReader;
        if (_errorReader != null) { var error = await _errorReader; if (!string.IsNullOrWhiteSpace(error)) _paths.Log(error); }
        if (_monitor != null) await _monitor;
        _process?.Dispose(); _process = null;
        _cancellation?.Dispose(); _cancellation = null;
        _progressReader = null; _errorReader = null; _monitor = null;
        _running = false; _starting = false; StartedAt = null;
        if (_sessionDirectory != null) TryDeleteSession(_sessionDirectory);
        _sessionDirectory = null;
        lock (_catalogLock) _segments = [];
        BufferBytes = 0; _outputSeconds = 0; _progressTimestamp = 0;
        Changed?.Invoke();
    }

    private void TryDeleteSession(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        var root = Path.GetFullPath(_paths.CacheDirectory) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(fullPath).StartsWith("session-", StringComparison.Ordinal)) return;
        try { if (Directory.Exists(fullPath)) Directory.Delete(fullPath, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private static void TryDeleteFile(string path) { try { File.Delete(path); } catch (IOException) { } }
    public async ValueTask DisposeAsync() { await StopAsync(); _lifecycle.Dispose(); _export.Dispose(); }
}
