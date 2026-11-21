using System.Buffers.Binary;
using System.Text.Json;
using Rewind.Core;

var passed = 0;
void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS " + name); }
void MustThrow(Action action, string name)
{
    try { action(); } catch (InvalidOperationException) { passed++; Console.WriteLine("PASS " + name); return; }
    throw new Exception("FAIL: " + name);
}
var settings = new AppSettings { BufferSeconds = 999, FramesPerSecond = 240, BitrateMbps = 999, Encoder = "Bogus" }.Validated();
Check(settings.BufferSeconds == 30 && settings.FramesPerSecond == 60 && settings.BitrateMbps == 30 && settings.Encoder == "Auto", "invalid settings are bounded");
Check(DiscordIdentity.IsApplicationIdValid("123456789012345678") && !DiscordIdentity.IsApplicationIdValid("1234") && !DiscordIdentity.IsApplicationIdValid("12345678901234567x") && !DiscordIdentity.IsApplicationIdValid("99999999999999999999"), "shared Discord ID validation");
Check(typeof(AppSettings).GetProperty("DiscordApplicationId") == null && typeof(AppSettings).GetProperty("DiscordAssetKey") == null, "end-user settings contain no Discord identity configuration");
Check(ProjectBranding.Discord.IsConfigured && ProjectBranding.Discord.ApplicationId == "1573632058495078501", "all users receive the embedded Rewind identity");
Check(ProjectBranding.Discord.Details == "Clipping Games" && ProjectBranding.Discord.State == "https://discord.gg/msQgnj3nBD", "shared presence uses the maintainer's requested text");
Check(new AppSettings().HotkeyLabel == "Ctrl + Shift + F9", "default hotkey is readable");
var display = new CaptureDisplay("display", "Display 1", 2, 1, -1920, 0, 1920, 1080, 0x10DE, false);
Check(CaptureArguments.Candidates(new AppSettings(), display).First() == CaptureBackend.Nvidia, "encoder follows the selected GPU");
Check(CaptureArguments.Candidates(new AppSettings { Encoder = "AMD" }, display).SequenceEqual(new[] { CaptureBackend.Amd }), "explicit encoder does not silently fall back");
var gpu = CaptureArguments.Build(new AppSettings(), display, CaptureBackend.Nvidia, [], "C:/test buffer");
Check(gpu.Contains("d3d11va=rewind:2") && gpu.Any(arg => arg.Contains("output_idx=1")) && !gpu.Any(arg => arg.Contains("hwdownload")), "NVENC capture stays on the GPU");
var cpu = CaptureArguments.Build(new AppSettings(), display, CaptureBackend.CompatibilityCpu, [], "C:/buffer");
Check(cpu.Contains("30") && cpu.Contains("-1920") && cpu.Contains("2") && cpu.Any(arg => arg.Contains("1280")) && !cpu.Contains("-init_hw_device"), "CPU fallback is bounded and handles negative monitor offsets");
var audio = CaptureArguments.Build(new AppSettings(), display, CaptureBackend.Nvidia,
    [new AudioInput("pipe1", "f32le", 48000, 2), new AudioInput("pipe2", "s16le", 44100, 1)], "C:/buffer");
Check(audio.Any(arg => arg.Contains("amix=inputs=2")) && audio.Any(arg => arg.Contains("first_pts=0")) && audio.Contains("160k"), "audio is resampled and mixed into AAC");
var root = Path.GetFullPath("test-buffer");
var catalog = SegmentCatalog.Parse("part-000000001.ts,2.0,4.0\npart-000000000.ts,0.0,2.0\n../evil.ts,4,6\npart-000000002.ts,NaN,6\npart-000000003.ts,6,\n", root);
Check(catalog.Count == 2 && catalog[0].Sequence == 0 && catalog[1].Sequence == 1, "manifest parsing rejects traversal, NaN, and partial entries");
var segments = Enumerable.Range(0, 50).Select(i => new Segment(Path.Combine(root, $"part-{i:000000000}.ts"), i, i * 2, i * 2 + 2)).ToArray();
var plan = SegmentCatalog.Plan(segments, 75.5, 30);
Check(plan.Start == 44 && plan.End == 75.5 && plan.Duration == 31.5 && plan.Segments.Count == 16, "clip includes the hotkey moment with at most one extra keyframe interval");
var warm = SegmentCatalog.Plan(segments.Take(3).ToArray(), 5.5, 30);
Check(warm.Start == 0 && warm.Duration == 5.5, "short warm buffers save available history");
MustThrow(() => SegmentCatalog.Plan([], 10, 30), "empty buffer is reported");
MustThrow(() => SegmentCatalog.Plan([segments[0], segments[2]], 6, 30), "buffer gaps cannot silently produce corrupt clips");
Check(SegmentCatalog.PruneBeforeSequence(segments, 30) == 31, "disk retention is bounded to replay plus a small safety margin");
Check(CaptureArguments.ConcatFileLine(Path.Combine(root, "friend's clip.ts")).Contains("friend'\\''s"), "concat paths escape apostrophes");
var export = CaptureArguments.Export("a.ffconcat", "b.mp4", 31.5);
Check(export.Contains("copy") && export.Contains("+faststart") && !export.Contains("libx264"), "clip export does not re-encode");
var frame = RpcFrame.Encode(0, new { v = 1, client_id = "123456789012345678" });
await using var fragmented = new FragmentedStream(frame);
var decoded = await RpcFrame.ReadAsync(fragmented, CancellationToken.None);
using var payload = JsonDocument.Parse(decoded.Payload);
Check(decoded.Opcode == 0 && payload.RootElement.GetProperty("v").GetInt32() == 1, "Discord frames survive fragmented pipe reads");
var pong = RpcFrame.EchoPong([1, 2, 3]);
Check(BinaryPrimitives.ReadInt32LittleEndian(pong) == 4 && pong.AsSpan(8).SequenceEqual(new byte[] { 1, 2, 3 }), "Discord ping is echoed byte for byte");
var malicious = new byte[8]; BinaryPrimitives.WriteInt32LittleEndian(malicious.AsSpan(4), RpcFrame.MaxPayloadBytes + 1);
try { await RpcFrame.ReadAsync(new MemoryStream(malicious), CancellationToken.None); throw new Exception("FAIL: oversized frame accepted"); }
catch (InvalidDataException) { passed++; Console.WriteLine("PASS oversized Discord frame is rejected before allocating"); }
await UpdateChecks.Run(Check);
await CaptureEngineChecks.Run(Check);
Console.WriteLine($"\n{passed} checks passed.");

sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], cancellationToken);
}
