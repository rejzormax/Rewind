using System.Reflection;
using Rewind.Core;

string? dataDirectory = null, launchId = null;
for (var index = 0; index < args.Length - 1; index++)
{
    if (args[index] == "--data-dir") dataDirectory = args[++index];
    else if (args[index] == "--update-launch-id") launchId = args[++index];
}
if (dataDirectory == null) return 2;
Directory.CreateDirectory(dataDirectory);
UpdateState.WriteJson(Path.Combine(dataDirectory, "fixture-started.json"), new FixtureStartup(Environment.ProcessId, args));
if (args.Contains("--hold"))
{
    File.WriteAllText(Path.Combine(dataDirectory, "parent-ready.txt"), "ready");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    while (!File.Exists(Path.Combine(dataDirectory, "parent-exit.txt"))) await Task.Delay(50, timeout.Token);
    return 0;
}
if (launchId != null)
    UpdateState.WriteJson(UpdateState.GetAcknowledgementPath(dataDirectory, launchId),
        new UpdateStartupAcknowledgement(Environment.ProcessId, Assembly.GetExecutingAssembly().GetName().Version!.ToString()));
using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
{
    try
    {
        while (!File.Exists(Path.Combine(dataDirectory, "fixture-exit.txt"))) await Task.Delay(50, timeout.Token);
    }
    catch (OperationCanceledException) { }
}
return 0;

public sealed record FixtureStartup(int ProcessId, string[] Arguments);
