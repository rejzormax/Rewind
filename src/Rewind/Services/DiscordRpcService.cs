using System.IO.Pipes;
using System.Diagnostics;
using System.Text.Json;
using Rewind.Core;

namespace Rewind.Services;

/// <summary>Discord's local IPC transport, with framed reads, ping replies, backoff and graceful activity clearing.</summary>
public sealed class DiscordRpcService : IAsyncDisposable
{
    private readonly string _pipePrefix;
    private readonly DiscordIdentity _identity;
    private readonly long _startedAt;
    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly SemaphoreSlim _configuration = new(1, 1);
    public string Status { get; private set; } = ProjectBranding.Discord.IsConfigured ? "Waiting for Discord" : "Unavailable in this build";
    public event Action<string>? StatusChanged;

    public DiscordRpcService(string pipePrefix = "discord-ipc-", DiscordIdentity? identity = null)
    {
        _pipePrefix = pipePrefix; _identity = identity ?? ProjectBranding.Discord;
        using var process = Process.GetCurrentProcess();
        _startedAt = new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeSeconds();
    }

    public async Task ConfigureAsync(AppSettings settings)
    {
        await _configuration.WaitAsync();
        try
        {
            await StopAsync();
            if (!settings.DiscordEnabled) { SetStatus("Off"); return; }
            if (!_identity.IsConfigured) { SetStatus("Unavailable in this build"); return; }
            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;
            _loop = Task.Run(() => ConnectLoopAsync(token));
        }
        finally { _configuration.Release(); }
    }

    private void SetStatus(string value) { Status = value; StatusChanged?.Invoke(value); }

    private async Task<NamedPipeClientStream?> ConnectAsync(CancellationToken token)
    {
        for (var i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", _pipePrefix + i, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await pipe.ConnectAsync(150, token); return pipe; }
            catch (Exception ex) when (ex is TimeoutException or IOException) { pipe.Dispose(); }
            catch { pipe.Dispose(); throw; }
        }
        return null;
    }

    private async Task ConnectLoopAsync(CancellationToken token)
    {
        var retrySeconds = 5;
        try
        {
            while (!token.IsCancellationRequested)
            {
                SetStatus("Connecting…");
                using var pipe = await ConnectAsync(token);
                if (pipe == null)
                {
                    SetStatus("Waiting for Discord");
                    await Task.Delay(TimeSpan.FromSeconds(retrySeconds), token);
                    retrySeconds = Math.Min(30, retrySeconds * 2);
                    continue;
                }
                using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var activityNonce = Guid.NewGuid().ToString("N");
                var reader = ReadLoopAsync(pipe, ready, accepted, activityNonce, session.Token);
                try
                {
                    await SendAsync(pipe, RpcFrame.Encode(0, new { v = 1, client_id = _identity.ApplicationId }), token);
                    await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                    await SendAsync(pipe, RpcFrame.Encode(1, new { cmd = "SET_ACTIVITY",
                        args = new { pid = Environment.ProcessId, activity = BuildActivity() }, nonce = activityNonce }), token);
                    await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                    SetStatus("Connected"); retrySeconds = 5;
                    // Discord renders elapsed time itself. Read ping/close frames without a polling loop.
                    await reader;
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or JsonException or InvalidDataException)
                { SetStatus("Discord disconnected"); }
                finally
                {
                    await session.CancelAsync();
                    try
                    {
                        using var clearTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
                        if (pipe.IsConnected) await SendAsync(pipe, RpcFrame.Encode(1, new { cmd = "SET_ACTIVITY",
                            args = new { pid = Environment.ProcessId, activity = (object?)null }, nonce = Guid.NewGuid().ToString("N") }), clearTimeout.Token);
                    }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
                    try { await reader; }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or InvalidDataException) { }
                }
                await Task.Delay(TimeSpan.FromSeconds(retrySeconds), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private Dictionary<string, object> BuildActivity()
    {
        var activity = new Dictionary<string, object>
        {
            ["type"] = 0,
            ["details"] = _identity.Details,
            ["state"] = _identity.State,
            ["timestamps"] = new { start = _startedAt }
        };
        if (!string.IsNullOrWhiteSpace(_identity.AssetKey)) activity["assets"] = new { large_image = _identity.AssetKey, large_text = "Rewind · Keep the moment" };
        return activity;
    }

    private async Task ReadLoopAsync(NamedPipeClientStream pipe, TaskCompletionSource ready, TaskCompletionSource accepted,
        string activityNonce, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var frame = await RpcFrame.ReadAsync(pipe, token);
                if (frame.Opcode == 2) throw new IOException("Discord closed the IPC session.");
                if (frame.Opcode == 3) { await SendAsync(pipe, RpcFrame.EchoPong(frame.Payload), token); continue; }
                if (frame.Opcode != 1) continue;
                using var document = JsonDocument.Parse(frame.Payload);
                if (document.RootElement.TryGetProperty("evt", out var evt))
                {
                    if (evt.GetString() == "READY") ready.TrySetResult();
                    if (evt.GetString() == "ERROR")
                        throw new IOException("Discord rejected Rewind's presence.");
                }
                if (document.RootElement.TryGetProperty("cmd", out var command) && command.GetString() == "SET_ACTIVITY" &&
                    document.RootElement.TryGetProperty("nonce", out var nonce) && nonce.GetString() == activityNonce)
                    accepted.TrySetResult();
            }
        }
        catch (Exception ex) { ready.TrySetException(ex); accepted.TrySetException(ex); throw; }
    }

    private async Task SendAsync(NamedPipeClientStream pipe, byte[] frame, CancellationToken token)
    {
        await _writes.WaitAsync(token);
        try { await pipe.WriteAsync(frame, token); await pipe.FlushAsync(token); }
        finally { _writes.Release(); }
    }
    private async Task StopAsync()
    {
        if (_cancellation != null) await _cancellation.CancelAsync();
        if (_loop != null) await _loop;
        _cancellation?.Dispose(); _cancellation = null; _loop = null;
    }
    public async ValueTask DisposeAsync()
    {
        await _configuration.WaitAsync();
        try { await StopAsync(); }
        finally { _configuration.Release(); }
        _writes.Dispose(); _configuration.Dispose();
    }
}
