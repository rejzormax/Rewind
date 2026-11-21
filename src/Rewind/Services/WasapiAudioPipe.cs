using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Rewind.Core;

namespace Rewind.Services;

public sealed class WasapiAudioPipe : IAsyncDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource<AudioInput> _format = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _worker;
    private readonly bool _loopback;
    private Exception? _failure;
    public Exception? Failure => _failure;
    public Task Connected => _connected.Task;
    public AudioInput Input { get; private set; } = null!;

    private WasapiAudioPipe(bool loopback)
    {
        _loopback = loopback;
        var name = "rewind-audio-" + Guid.NewGuid().ToString("N");
        _pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 64 * 1024, 64 * 1024);
        PipePath = @"\\.\pipe\" + name;
        _worker = Task.Factory.StartNew(Capture, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    private string PipePath { get; }

    public static async Task<WasapiAudioPipe> CreateAsync(bool loopback, CancellationToken token)
    {
        var source = new WasapiAudioPipe(loopback);
        try
        {
            source.Input = await source._format.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            return source;
        }
        catch { await source.DisposeAsync(); throw; }
    }

    private void Capture()
    {
        object? enumeratorObject = null;
        IMMDevice? device = null;
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        nint formatPointer = 0;
        var token = _cancellation.Token;
        using var samplesReady = new EventWaitHandle(false, EventResetMode.AutoReset);
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), true)!;
            enumeratorObject = Activator.CreateInstance(type)!;
            var enumerator = (IMMDeviceEnumerator)enumeratorObject;
            Check(enumerator.GetDefaultAudioEndpoint(_loopback ? 0 : 1, 0, out device));
            var clientId = typeof(IAudioClient).GUID;
            Check(device.Activate(ref clientId, 23, 0, out var clientObject));
            client = (IAudioClient)clientObject;
            Check(client.GetMixFormat(out formatPointer));
            var wave = Marshal.PtrToStructure<WaveFormat>(formatPointer);
            var tag = wave.FormatTag == 0xFFFE ? (ushort)Marshal.ReadInt32(formatPointer, 24) : wave.FormatTag;
            var rawFormat = (tag, wave.BitsPerSample) switch
            {
                (3, 32) => "f32le", (3, 64) => "f64le", (1, 16) => "s16le",
                (1, 24) => "s24le", (1, 32) => "s32le",
                _ => throw new NotSupportedException("This audio device uses an unsupported PCM format.")
            };
            var flags = 0x00040000u | (_loopback ? 0x00020000u : 0u);
            Check(client.Initialize(0, flags, 2_000_000, 0, formatPointer, 0));
            Check(client.SetEventHandle(samplesReady.SafeWaitHandle.DangerousGetHandle()));
            Check(client.GetBufferSize(out var maximumFrames));
            var captureId = typeof(IAudioCaptureClient).GUID;
            Check(client.GetService(ref captureId, out var captureObject));
            capture = (IAudioCaptureClient)captureObject;
            _format.TrySetResult(new AudioInput(PipePath, rawFormat, (int)wave.SampleRate, wave.Channels));
            _pipe.WaitForConnectionAsync(token).GetAwaiter().GetResult();
            var buffer = new byte[Math.Max((int)maximumFrames * wave.BlockAlign, wave.SampleRate / 10 * wave.BlockAlign)];
            var silence = new byte[wave.SampleRate / 50 * wave.BlockAlign];
            long framesWritten = 0;
            Check(client.Start());
            var startedQpc = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            var clock = Stopwatch.StartNew();
            _connected.TrySetResult();
            while (!token.IsCancellationRequested)
            {
                samplesReady.WaitOne(20);
                Check(capture.GetNextPacketSize(out var packetFrames));
                while (packetFrames > 0 && !token.IsCancellationRequested)
                {
                    Check(capture.GetBuffer(out var data, out var frames, out var packetFlags, out _, out var qpc));
                    try
                    {
                        var packetPosition = (packetFlags & 4) == 0
                            ? Math.Max(0L, (long)Math.Round((qpc / 10_000_000d - startedQpc) * wave.SampleRate)) : framesWritten;
                        WriteSilence(packetPosition - framesWritten, silence, wave.BlockAlign, ref framesWritten, token);
                        var skipped = (int)Math.Clamp(framesWritten - packetPosition, 0, frames);
                        var count = ((int)frames - skipped) * wave.BlockAlign;
                        if (count > 0)
                        {
                            if (count > buffer.Length) throw new IOException("Audio packet exceeded the device buffer.");
                            if ((packetFlags & 2) != 0) Array.Clear(buffer, 0, count);
                            else Marshal.Copy(data + skipped * wave.BlockAlign, buffer, 0, count);
                            _pipe.Write(buffer, 0, count);
                            framesWritten += frames - skipped;
                        }
                    }
                    finally { Check(capture.ReleaseBuffer(frames)); }
                    Check(capture.GetNextPacketSize(out packetFrames));
                }
                var requiredFrames = (long)(Math.Max(0, clock.Elapsed.TotalSeconds - 0.05) * wave.SampleRate);
                WriteSilence(requiredFrames - framesWritten, silence, wave.BlockAlign, ref framesWritten, token);
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) _failure = ex;
            _format.TrySetException(new IOException(_loopback
                ? "System audio is unavailable. Disable system audio or choose a working Windows output device."
                : "Microphone capture is unavailable. Check Windows microphone permissions or disable the microphone.", ex));
            _connected.TrySetException(ex);
        }
        finally
        {
            try { client?.Stop(); } catch (COMException) { }
            if (formatPointer != 0) Marshal.FreeCoTaskMem(formatPointer);
            Release(capture); Release(client); Release(device); Release(enumeratorObject);
        }
    }

    private void WriteSilence(long frames, byte[] silence, int blockAlign, ref long written, CancellationToken token)
    {
        frames = Math.Min(frames, silence.Length / blockAlign * 10L);
        while (frames > 0 && !token.IsCancellationRequested)
        {
            var count = (int)Math.Min(frames, silence.Length / blockAlign);
            _pipe.Write(silence, 0, count * blockAlign);
            frames -= count; written += count;
        }
    }
    private static void Check(int result) { Marshal.ThrowExceptionForHR(result); }
    private static void Release(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        _pipe.Dispose();
        await _worker;
        _cancellation.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public ushort FormatTag, Channels;
        public uint SampleRate, AverageBytesPerSecond;
        public ushort BlockAlign, BitsPerSample, ExtraSize;
    }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out nint devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(nint callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(nint callback);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid id, uint context, nint parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        [PreserveSig] int OpenPropertyStore(uint access, out nint properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }
    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int mode, uint flags, long bufferDuration, long period, nint format, nint session);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int mode, nint format, out nint closest);
        [PreserveSig] int GetMixFormat(out nint format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(nint handle);
        [PreserveSig] int GetService(ref Guid id, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }
    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out nint data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
