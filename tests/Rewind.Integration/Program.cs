using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using Rewind.Core;
using Rewind.Services;

var projectDirectory = new DirectoryInfo(AppContext.BaseDirectory);
while (projectDirectory != null && !File.Exists(Path.Combine(projectDirectory.FullName, "src", "Rewind.props"))) projectDirectory = projectDirectory.Parent;
var projectRoot = projectDirectory?.FullName ?? throw new Exception("Could not find the project directory.");

if (args.Contains("--check-updater"))
{
    await UpdaterChecks.RunAsync(projectRoot);
    return;
}

if (args.Contains("--check-updates"))
{
    await ManualUpdateChecks.RunAsync(projectRoot);
    return;
}

if (args.Contains("--live-discord"))
{
    await using var client = new DiscordRpcService();
    client.StatusChanged += status => Console.WriteLine("Discord: " + status);
    await client.ConfigureAsync(new AppSettings());
    var deadline = Stopwatch.StartNew();
    while (client.Status != "Connected" && deadline.Elapsed.TotalSeconds < 15) await Task.Delay(100);
    if (client.Status != "Connected") throw new Exception("Live Discord activity was not accepted: " + client.Status);
    Console.WriteLine("PASS real Discord accepted Rewind's shared application ID and startup presence.");
    return;
}

if (args.Contains("--check-notifications"))
{
    await ClipNotificationChecks.RunAsync(projectRoot);
    return;
}

if (args.Contains("--render-layout"))
{
    var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var thread = new Thread(() =>
    {
        try
        {
            var application = new Rewind.App(); application.InitializeComponent();
            var previewPaths = new AppPaths(Path.Combine(projectRoot, "artifacts", "layout-data"));
            var window = new Rewind.MainWindow(previewPaths); window.PrepareLayoutPreview();
            if (!window.PreviewLibraryIsEmpty) throw new Exception("The layout preview must start with an empty clip library.");
            foreach (var page in new[] { "Library", "Dashboard", "Capture", "Discord" })
            {
                var output = Path.Combine(projectRoot, "artifacts", "rewind-" + page.ToLowerInvariant() + ".png");
                window.RenderLayoutPreview(page, output);
                Console.WriteLine("Rendered " + output);
            }
            window.RenderLayoutPreview("Library", Path.Combine(projectRoot, "artifacts", "rewind-minimum.png"), 1020, 670);
            window.PrepareUpdatePreview();
            window.RenderLayoutPreview("Library", Path.Combine(projectRoot, "artifacts", "rewind-update.png"));
            window.RenderLayoutPreview("Library", Path.Combine(projectRoot, "artifacts", "rewind-update-minimum.png"), 1020, 670);
            var galleryFixture = Enumerable.Range(0, 5000).Select(index => new SavedClip(
                Path.Combine(previewPaths.DefaultClipDirectory, $"layout-qa-{index}.mp4"), $"Layout QA {index}", DateTime.Now,
                30, 1920, 1080, 1024 * 1024, null)).ToArray();
            var realized = window.PrepareGalleryCheck(galleryFixture);
            if (realized <= 0 || realized > 24) throw new Exception($"Gallery did not virtualize 5,000 test entries: {realized} realized rows.");
            Console.WriteLine($"PASS gallery virtualization: {realized} realized rows for 5,000 in-memory QA entries; no clip files were created.");
            window.FinishLayoutPreview(); application.Shutdown(); rendered.SetResult();
        }
        catch (Exception ex) { rendered.SetException(ex); }
    });
    thread.SetApartmentState(ApartmentState.STA); thread.Start(); await rendered.Task;
    return;
}

if (args.Contains("--check-import"))
{
    var importPaths = new AppPaths(Path.Combine(projectRoot, "artifacts", "import-check"));
    var importTools = FfmpegLocator.Find() ?? throw new Exception("FFmpeg is missing.");
    var sourceDirectory = Path.Combine(projectRoot, "artifacts", "integration-data", "clips");
    var source = Directory.EnumerateFiles(sourceDirectory, "*.mp4").First();
    var importLibrary = new ClipLibrary(importTools with { Ffprobe = null }, importPaths);
    var imported = await importLibrary.ImportAsync(source, importPaths.DefaultClipDirectory);
    var second = await importLibrary.ImportAsync(source, importPaths.DefaultClipDirectory);
    if (imported.Path == second.Path || imported.Width == 0 || imported.Duration <= 0 || !File.Exists(imported.ThumbnailPath))
        throw new Exception("Import failed validation or overwrote an existing clip.");
    if (Directory.EnumerateFiles(importPaths.DefaultClipDirectory, ".pending-*").Any()) throw new Exception("Import left a partial file.");
    if (ClipLibrary.Load(importPaths.DefaultClipDirectory).Count < 2) throw new Exception("Imported clips did not enter the library.");
    Console.WriteLine("PASS import: real MP4 metadata, poster, collision protection, index update and temporary-file cleanup.");
    return;
}

if (args.Contains("--native-audio"))
{
    var nativePaths = new AppPaths(Path.Combine(projectRoot, "artifacts", "native-audio-data"));
    var nativeTools = FfmpegLocator.Find() ?? throw new Exception("FFmpeg is missing.");
    var nativeSettings = new AppSettings { BufferSeconds = 15, FramesPerSecond = 30, SystemAudio = true,
        Microphone = false, ClipDirectory = nativePaths.DefaultClipDirectory };
    await using var nativeRecorder = new RecorderService(nativeTools, nativePaths, true);
    await nativeRecorder.StartAsync(nativeSettings, new CaptureDisplay("test", "Synthetic", 0, 0, 0, 0, 640, 360, 0, true));
    await Task.Delay(4500);
    var nativeClip = await nativeRecorder.SaveClipAsync();
    await nativeRecorder.StopAsync();
    var probe = await ProcessRunner.RunAsync(nativeTools.Ffprobe!,
        ["-v", "error", "-show_entries", "stream=codec_name,codec_type,duration,sample_rate", "-of", "json", nativeClip.Path], TimeSpan.FromSeconds(10));
    using var data = JsonDocument.Parse(probe.Output);
    var streams = data.RootElement.GetProperty("streams").EnumerateArray().ToArray();
    var video = streams.First(s => s.GetProperty("codec_type").GetString() == "video");
    var audio = streams.First(s => s.GetProperty("codec_type").GetString() == "audio");
    if (audio.GetProperty("codec_name").GetString() != "aac" || audio.GetProperty("sample_rate").GetString() != "48000")
        throw new Exception("Native loopback did not produce 48 kHz AAC.");
    var videoDuration = double.Parse(video.GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
    var audioDuration = double.Parse(audio.GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
    if (Math.Abs(videoDuration - audioDuration) > 0.2) throw new Exception("Native audio/video durations diverged.");
    Console.WriteLine($"PASS native WASAPI loopback -> AAC; video {videoDuration:0.000}s, audio {audioDuration:0.000}s.");
    return;
}

if (args.Contains("--probe-backends"))
{
    var probeTools = FfmpegLocator.Find() ?? throw new Exception("FFmpeg is missing.");
    var displays = DisplayEnumerator.GetDisplays();
    var probedDisplay = displays.FirstOrDefault(d => d.IsPrimary) ?? displays.First();
    CaptureBackend? preferredBackend = null;
    Console.WriteLine($"Selected {probedDisplay.Label}; adapter {probedDisplay.AdapterIndex}, output {probedDisplay.OutputIndex}, vendor {probedDisplay.VendorId:X4}.");
    foreach (var backend in CaptureArguments.Candidates(new AppSettings(), probedDisplay))
    {
        try
        {
            var result = await ProcessRunner.RunAsync(probeTools.Ffmpeg,
                CaptureArguments.Build(new AppSettings(), probedDisplay, backend, [], projectRoot, true), TimeSpan.FromSeconds(8));
            Console.WriteLine($"{(result.ExitCode == 0 ? "PASS" : "UNAVAILABLE")} {CaptureArguments.BackendLabel(backend)}");
            if (result.ExitCode == 0 && preferredBackend == null) preferredBackend = backend;
            if (result.ExitCode != 0) Console.WriteLine(result.Error.Trim());
        }
        catch (TimeoutException ex) { Console.WriteLine("UNAVAILABLE " + CaptureArguments.BackendLabel(backend) + ": " + ex.Message); }
    }
    if (preferredBackend.HasValue)
    {
        await using var loopback = await WasapiAudioPipe.CreateAsync(true, CancellationToken.None);
        var pipeline = CaptureArguments.Build(new AppSettings(), probedDisplay, preferredBackend.Value, [loopback.Input], projectRoot);
        var outputOptions = pipeline.IndexOf("-thread_queue_size");
        pipeline.RemoveRange(outputOptions, pipeline.Count - outputOptions);
        pipeline.AddRange(["-t", "2", "-f", "null", "-"]);
        var avResult = await ProcessRunner.RunAsync(probeTools.Ffmpeg, pipeline, TimeSpan.FromSeconds(10));
        if (avResult.ExitCode != 0) throw new Exception("Combined desktop/audio pipeline failed: " + avResult.Error);
        Console.WriteLine("PASS combined " + CaptureArguments.BackendLabel(preferredBackend.Value) + " desktop capture and native WASAPI audio (null output; no footage saved).");
    }
    return;
}

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS " + name); passed++;
}

Console.WriteLine("Testing Discord against a local fake IPC server (no Discord account involved)...");
var prefix = "rewind-check-" + Guid.NewGuid().ToString("N") + "-";
using var rpcTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
await using var server = new NamedPipeServerStream(prefix + "0", PipeDirection.InOut, 1,
    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
var pingReceived = false;
var activityReceived = false;
var cleared = false;
var rpcServer = Task.Run(async () =>
{
    await server.WaitForConnectionAsync(rpcTimeout.Token);
    var handshake = await RpcFrame.ReadAsync(server, rpcTimeout.Token);
    using (var data = JsonDocument.Parse(handshake.Payload))
        Check(handshake.Opcode == 0 && data.RootElement.GetProperty("client_id").GetString() == "123456789012345678", "Discord handshake uses the configured application ID");
    var ready = RpcFrame.Encode(1, new { cmd = "DISPATCH", evt = "READY", data = new { } });
    for (var offset = 0; offset < ready.Length; offset += 3)
        await server.WriteAsync(ready.AsMemory(offset, Math.Min(3, ready.Length - offset)), rpcTimeout.Token);
    var ping = RpcFrame.Encode(3, new { probe = "hello" });
    await server.WriteAsync(ping, rpcTimeout.Token);
    while (!cleared)
    {
        var message = await RpcFrame.ReadAsync(server, rpcTimeout.Token);
        if (message.Opcode == 4)
        {
            Check(message.Payload.AsSpan().SequenceEqual(ping.AsSpan(8)), "Discord IPC ping receives an exact pong");
            pingReceived = true;
        }
        else if (message.Opcode == 1)
        {
            using var data = JsonDocument.Parse(message.Payload);
            if (data.RootElement.GetProperty("cmd").GetString() != "SET_ACTIVITY") continue;
            var activity = data.RootElement.GetProperty("args").GetProperty("activity");
            if (activity.ValueKind == JsonValueKind.Null) { cleared = true; break; }
            Check(activity.GetProperty("details").GetString() == "Clipping Games" && activity.GetProperty("state").GetString() == "https://discord.gg/msQgnj3nBD", "Discord receives the project's details and state immediately");
            var started = activity.GetProperty("timestamps").GetProperty("start").GetInt64();
            using var currentProcess = Process.GetCurrentProcess();
            Check(started == new DateTimeOffset(currentProcess.StartTime.ToUniversalTime()).ToUnixTimeSeconds(), "Discord elapsed time starts at process launch");
            await server.WriteAsync(RpcFrame.Encode(1, new { cmd = "SET_ACTIVITY", nonce = data.RootElement.GetProperty("nonce").GetString(), evt = (object?)null, data = (object?)null }), rpcTimeout.Token);
            activityReceived = true;
        }
    }
});
await using (var discord = new DiscordRpcService(prefix, new DiscordIdentity("123456789012345678")))
{
    await discord.ConfigureAsync(new AppSettings());
    var wait = Stopwatch.StartNew();
    while (!(pingReceived && activityReceived && discord.Status == "Connected") && wait.Elapsed.TotalSeconds < 8) await Task.Delay(50);
    Check(pingReceived && activityReceived && discord.Status == "Connected", "Discord client connects and publishes activity");
}
await rpcServer;
Check(cleared, "Discord activity is cleared on shutdown");

Console.WriteLine("\nTesting the real FFmpeg replay pipeline with a synthetic source...");
var paths = new AppPaths(Path.Combine(projectRoot, "artifacts", "integration-data"));
var tools = FfmpegLocator.Find() ?? throw new Exception("FFmpeg is missing. Run scripts/setup.ps1 first.");
var settings = new AppSettings { BufferSeconds = 15, FramesPerSecond = 30, SystemAudio = false,
    Microphone = false, Encoder = "CPU", ClipDirectory = paths.DefaultClipDirectory };
var display = new CaptureDisplay("test", "Synthetic", 0, 0, 0, 0, 640, 360, 0, true);
await using var recorder = new RecorderService(tools, paths, true);
var errors = new List<string>();
recorder.Error += message => { lock (errors) errors.Add(message); };
await recorder.StartAsync(settings, display);
Check(recorder.IsRunning, "real capture process starts");
var clock = Stopwatch.StartNew();
await Task.Delay(7000);
var firstSave = recorder.SaveClipAsync();
try { await recorder.SaveClipAsync(); throw new Exception("FAIL: duplicate clip export was accepted"); }
catch (InvalidOperationException) { Check(true, "concurrent clip hotkeys are coalesced"); }
var first = await firstSave;
Check(File.Exists(first.Path) && first.SizeBytes > 1024 && first.Duration >= 5 && first.Duration < 12,
    "a partially filled buffer produces a playable MP4");
Check(recorder.IsRunning, "capture continues during clip export");
var leanLibrary = new ClipLibrary(tools with { Ffprobe = null }, paths);
var leanClip = await leanLibrary.InspectAsync(first.Path);
Check(leanClip.Duration >= 5 && leanClip.Width == 640 && leanClip.Height == 360 && File.Exists(leanClip.ThumbnailPath),
    "portable build probes metadata and thumbnails without a separate ffprobe binary");
if (tools.Ffprobe != null)
{
    var probe = await ProcessRunner.RunAsync(tools.Ffprobe,
        ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name,width,height", "-of", "json", first.Path], TimeSpan.FromSeconds(10));
    using var data = JsonDocument.Parse(probe.Output);
    var stream = data.RootElement.GetProperty("streams")[0];
    Check(probe.ExitCode == 0 && stream.GetProperty("codec_name").GetString() == "h264" &&
        stream.GetProperty("width").GetInt32() == 640 && stream.GetProperty("height").GetInt32() == 360, "saved clip contains valid H.264 at the source resolution");
}
var decoded = await ProcessRunner.RunAsync(tools.Ffmpeg,
    ["-hide_banner", "-v", "error", "-i", first.Path, "-f", "null", "-"], TimeSpan.FromSeconds(15));
Check(decoded.ExitCode == 0 && string.IsNullOrWhiteSpace(decoded.Error), "all exported video frames decode without errors");
var remaining = 31 - clock.Elapsed.TotalSeconds;
if (remaining > 0) await Task.Delay(TimeSpan.FromSeconds(remaining));
var parts = Directory.EnumerateFiles(paths.CacheDirectory, "part-*.ts", SearchOption.AllDirectories).ToArray();
Check(parts.Length <= 15 && recorder.BufferedSeconds <= 15 && recorder.BufferedSeconds >= 12,
    $"disk ring remains bounded after two buffer lengths ({parts.Length} segments)");
var full = await recorder.SaveClipAsync();
Check(full.Duration >= 14 && full.Duration <= 18, "a full replay contains the requested history within a keyframe interval");
await recorder.StopAsync();
Check(!recorder.IsRunning && !Directory.EnumerateFiles(paths.CacheDirectory, "*.ts", SearchOption.AllDirectories).Any(), "stop terminates capture and removes temporary buffer segments");
Check(File.Exists(first.Path) && File.Exists(full.Path) && errors.Count == 0, "saved clips survive cleanup with no capture errors");
Console.WriteLine($"\n{passed} integration checks passed. Test clips: {paths.DefaultClipDirectory}");
