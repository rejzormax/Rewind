using System.Globalization;

namespace Rewind.Core;

public sealed record CaptureDisplay(string Id, string Name, int AdapterIndex, int OutputIndex,
    int Left, int Top, int Width, int Height, uint VendorId, bool IsPrimary)
{
    public string Label => $"{Name} · {Width} × {Height}{(IsPrimary ? " · Primary" : "")}";
    public override string ToString() => Label;
}

public enum CaptureBackend { Nvidia, Amd, Intel, DesktopCpu, CompatibilityCpu, TestPattern }

public sealed record AudioInput(string PipePath, string Format, int SampleRate, int Channels);

public static class CaptureArguments
{
    public const int SegmentSeconds = 2;

    public static int EffectiveFps(CaptureBackend backend, AppSettings settings) =>
        backend is CaptureBackend.DesktopCpu or CaptureBackend.CompatibilityCpu or CaptureBackend.TestPattern
            ? Math.Min(30, settings.FramesPerSecond) : settings.FramesPerSecond;

    public static string BackendLabel(CaptureBackend backend) => backend switch
    {
        CaptureBackend.Nvidia => "NVIDIA NVENC",
        CaptureBackend.Amd => "AMD AMF",
        CaptureBackend.Intel => "Intel Quick Sync",
        CaptureBackend.DesktopCpu => "CPU · Desktop Duplication",
        CaptureBackend.CompatibilityCpu => "CPU · Compatibility capture",
        _ => "Synthetic test source"
    };

    public static IReadOnlyList<CaptureBackend> Candidates(AppSettings settings, CaptureDisplay display)
    {
        if (settings.Encoder == "CPU") return [CaptureBackend.DesktopCpu, CaptureBackend.CompatibilityCpu];
        if (settings.Encoder == "NVIDIA") return [CaptureBackend.Nvidia];
        if (settings.Encoder == "AMD") return [CaptureBackend.Amd];
        if (settings.Encoder == "Intel") return [CaptureBackend.Intel];
        var preferred = display.VendorId switch
        {
            0x10DE => CaptureBackend.Nvidia,
            0x1002 or 0x1022 => CaptureBackend.Amd,
            0x8086 => CaptureBackend.Intel,
            _ => (CaptureBackend?)null
        };
        return preferred.HasValue
            ? [preferred.Value, CaptureBackend.DesktopCpu, CaptureBackend.CompatibilityCpu]
            : [CaptureBackend.DesktopCpu, CaptureBackend.CompatibilityCpu];
    }

    public static List<string> Build(AppSettings settings, CaptureDisplay display, CaptureBackend backend,
        IReadOnlyList<AudioInput> audio, string sessionDirectory, bool probe = false)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "warning", "-y", "-filter_threads", "2", "-filter_complex_threads", "2" };
        var fps = EffectiveFps(backend, settings);
        var mouse = settings.CaptureCursor ? 1 : 0;
        var hardwareSource = backend is not CaptureBackend.TestPattern and not CaptureBackend.CompatibilityCpu;
        var videoGraph = "";
        if (backend == CaptureBackend.TestPattern)
            args.AddRange(["-re", "-f", "lavfi", "-i", $"testsrc2=size=640x360:rate={fps}"]);
        else if (backend == CaptureBackend.CompatibilityCpu)
            args.AddRange(["-f", "gdigrab", "-framerate", fps.ToString(),
                "-draw_mouse", mouse.ToString(), "-offset_x", display.Left.ToString(CultureInfo.InvariantCulture),
                "-offset_y", display.Top.ToString(CultureInfo.InvariantCulture), "-video_size", $"{display.Width}x{display.Height}", "-i", "desktop"]);
        else
        {
            args.AddRange(["-init_hw_device", $"d3d11va=rewind:{display.AdapterIndex}", "-filter_hw_device", "rewind"]);
            // A global graph receives filter_hw_device, unlike an independent lavfi input graph.
            videoGraph = $"ddagrab=output_idx={display.OutputIndex}:framerate={fps}:draw_mouse={mouse}:dup_frames=1";
            if (backend == CaptureBackend.Intel) videoGraph += ",hwmap=derive_device=qsv,scale_qsv=format=nv12";
            if (backend == CaptureBackend.DesktopCpu) videoGraph +=
                $",hwdownload,format=bgra,scale=w={Math.Min(1280, display.Width) / 2 * 2}:h=-2:flags=fast_bilinear,format=yuv420p";
            videoGraph += "[capture]";
        }

        if (!probe)
            foreach (var input in audio)
                args.AddRange(["-probesize", "32", "-analyzeduration", "0", "-f", input.Format, "-ar", input.SampleRate.ToString(),
                    "-ac", input.Channels.ToString(), "-i", input.PipePath]);

        args.AddRange(["-map", hardwareSource ? "[capture]" : "0:v:0"]);
        if (!probe && audio.Count > 0)
        {
            var inputOffset = hardwareSource ? 0 : 1;
            var filters = string.Join(";", audio.Select((_, i) =>
                $"[{i + inputOffset}:a]aresample=48000:async=1:first_pts=0,aformat=sample_fmts=fltp:channel_layouts=stereo[a{i}]"));
            var mix = audio.Count == 1 ? "[a0]anull[mix]" :
                $"{string.Concat(audio.Select((_, i) => $"[a{i}]"))}amix=inputs={audio.Count}:duration=longest:dropout_transition=0:normalize=1[mix]";
            args.AddRange(["-filter_complex", $"{(hardwareSource ? videoGraph + ";" : "")}{filters};{mix}", "-map", "[mix]", "-c:a", "aac", "-b:a", "160k", "-ar", "48000"]);
        }
        else
        {
            args.Add("-an");
            if (hardwareSource) args.AddRange(["-filter_complex", videoGraph]);
        }

        var bitrate = $"{settings.BitrateMbps}M";
        switch (backend)
        {
            case CaptureBackend.Nvidia:
                args.AddRange(["-c:v", "h264_nvenc", "-preset", "p1", "-tune", "ll", "-rc", "cbr", "-b:v", bitrate,
                    "-maxrate", bitrate, "-bufsize", $"{settings.BitrateMbps * 2}M", "-forced-idr", "1"]);
                break;
            case CaptureBackend.Amd:
                args.AddRange(["-c:v", "h264_amf", "-usage", "ultralowlatency", "-quality", "speed", "-rc", "cbr",
                    "-b:v", bitrate, "-maxrate", bitrate, "-bufsize", $"{settings.BitrateMbps * 2}M", "-forced_idr", "1"]);
                break;
            case CaptureBackend.Intel:
                args.AddRange(["-c:v", "h264_qsv",
                    "-preset", "veryfast", "-look_ahead", "0", "-async_depth", "2", "-b:v", bitrate, "-forced_idr", "1"]);
                break;
            default:
                var scale = $"scale=w={Math.Min(1280, display.Width) / 2 * 2}:h=-2:flags=fast_bilinear,format=yuv420p";
                if (backend == CaptureBackend.TestPattern) scale = "format=yuv420p";
                if (!hardwareSource) args.AddRange(["-vf", scale]);
                args.AddRange(["-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency",
                    "-threads", "2", "-crf", "24", "-maxrate", bitrate, "-bufsize", $"{settings.BitrateMbps * 2}M",
                    "-sc_threshold", "0"]);
                break;
        }
        args.AddRange(["-g", (fps * SegmentSeconds).ToString(), "-bf", "0"]);
        if (probe)
            args.AddRange(["-frames:v", "3", "-f", "null", "-"]);
        else
            args.AddRange(["-thread_queue_size", "8", "-max_muxing_queue_size", "64", "-muxing_queue_data_threshold", "4194304",
                "-force_key_frames", "expr:gte(t,n_forced*2)", "-progress", "pipe:1", "-stats_period", "0.5",
                "-f", "segment", "-segment_time", "2", "-reset_timestamps", "1", "-segment_format", "mpegts",
                "-segment_list", Path.Combine(sessionDirectory, "segments.csv"), "-segment_list_type", "csv",
                "-segment_list_size", ((int)Math.Ceiling(settings.BufferSeconds / 2d) + 8).ToString(),
                "-segment_list_flags", "+live", Path.Combine(sessionDirectory, "part-%09d.ts")]);
        return args;
    }

    public static List<string> Export(string manifest, string destination, double duration) =>
        ["-hide_banner", "-loglevel", "error", "-y", "-f", "concat", "-safe", "0", "-i", manifest,
         "-t", duration.ToString("0.000", CultureInfo.InvariantCulture), "-map", "0:v:0", "-map", "0:a?",
         "-c", "copy", "-movflags", "+faststart", "-avoid_negative_ts", "make_zero", destination];

    public static string ConcatFileLine(string path) => "file '" + Path.GetFullPath(path).Replace('\\', '/').Replace("'", "'\\''") + "'";
}
