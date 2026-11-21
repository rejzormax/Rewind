using System.Diagnostics;
using System.Text;

namespace Rewind.Services;

public sealed record ProcessResult(int ExitCode, string Output, string Error);

public static class ProcessRunner
{
    public static Process Start(string executable, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in args) info.ArgumentList.Add(argument);
        var process = new Process { StartInfo = info };
        if (!process.Start()) throw new IOException("Could not start the capture engine.");
        ChildProcessJob.Attach(process.Handle);
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        return process;
    }

    public static async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> args,
        TimeSpan timeout, CancellationToken token = default)
    {
        using var process = Start(executable, args);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancellation.CancelAfter(timeout);
        var outputTask = ReadBoundedAsync(process.StandardOutput, 128 * 1024);
        var errorTask = ReadBoundedAsync(process.StandardError, 32 * 1024);
        try { await process.WaitForExitAsync(cancellation.Token); }
        catch (OperationCanceledException)
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            if (token.IsCancellationRequested) throw;
            throw new TimeoutException("The capture engine took too long to respond.");
        }
        return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
    }

    public static async Task<string> ReadBoundedAsync(StreamReader reader, int maximumCharacters)
    {
        var result = new StringBuilder();
        var buffer = new char[2048];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            result.Append(buffer, 0, read);
            if (result.Length > maximumCharacters) result.Remove(0, result.Length - maximumCharacters);
        }
        return result.ToString();
    }

    public static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
