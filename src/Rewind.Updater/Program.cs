using System.Diagnostics;
using Rewind.Core;

string? planPath = null, dataDirectory = null;
for (var index = 0; index < args.Length - 1; index++)
{
    if (args[index] == "--plan") planPath = args[++index];
    else if (args[index] == "--data-dir") dataDirectory = args[++index];
}
dataDirectory = Path.GetFullPath(dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rewind"));
if (args.Contains("--prepare-engine"))
{
    try
    {
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        await new CaptureEngineInstaller(http).EnsureAsync(Path.Combine(dataDirectory, "engine"), CancellationToken.None);
    }
    catch (Exception error)
    {
        Directory.CreateDirectory(dataDirectory);
        File.AppendAllText(Path.Combine(dataDirectory, "setup.log"), $"{DateTimeOffset.Now:O} {error.Message}{Environment.NewLine}");
        Environment.ExitCode = 1;
    }
    return;
}
var updatesRoot = UpdateState.GetUpdatesDirectory(dataDirectory);
var versionsRoot = UpdateState.GetVersionsDirectory(dataDirectory);
var markerPath = Path.Combine(updatesRoot, "current.json");
UpdatePlan? plan = null;
Process? launchedApp = null;
string? staging = null, installedDirectory = null, acknowledgementPath = null;
var committed = false;
var markerTouched = false;
string? previousMarker = null;
try
{
    if (planPath == null || !UpdatePackage.IsWithin(planPath, updatesRoot)) throw new InvalidDataException("The update plan is outside Rewind's update directory.");
    plan = UpdateState.ReadJson<UpdatePlan>(planPath);
    var current = typeof(UpdateConfiguration).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
    if (plan.Repository != UpdateConfiguration.Current.Repository || !GitHubUpdateClient.TryParseVersion(plan.Version, out var version) || version <= current ||
        !UpdatePackage.IsWithin(plan.ArchivePath, Path.Combine(updatesRoot, "downloads")) || !GitHubUpdateClient.IsSha256(plan.Sha256))
        throw new InvalidDataException("The update plan is invalid or would downgrade Rewind.");
    if (File.Exists(markerPath))
    {
        var installed = UpdateState.ReadJson<InstalledUpdate>(markerPath);
        previousMarker = File.ReadAllText(markerPath);
        if (installed.Repository == plan.Repository && GitHubUpdateClient.TryParseVersion(installed.Version, out var installedVersion) && installedVersion >= version)
            throw new InvalidDataException("This or a newer version of Rewind is already installed.");
    }
    if (!await GitHubUpdateClient.HasExpectedHashAsync(plan.ArchivePath, plan.Sha256, CancellationToken.None)) throw new InvalidDataException("The update archive checksum does not match.");
    Directory.CreateDirectory(versionsRoot);
    staging = Path.Combine(versionsRoot, ".staging-" + Guid.NewGuid().ToString("N"));
    UpdatePackage.Extract(plan.ArchivePath, staging);
    foreach (var name in new[] { "Rewind.exe", "auto-updater.exe" })
    {
        var executable = Path.Combine(staging, name);
        if (!File.Exists(executable) || !GitHubUpdateClient.TryParseVersion(FileVersionInfo.GetVersionInfo(executable).FileVersion ?? "", out var actualVersion) || actualVersion != version)
            throw new InvalidDataException("The application and updater versions must match the release tag.");
    }
    try
    {
        using var parent = Process.GetProcessById(plan.ParentProcessId);
        if (!parent.HasExited)
        {
            if (!string.Equals(parent.MainModule?.FileName, plan.ParentExecutable, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The update parent process does not match Rewind.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await parent.WaitForExitAsync(timeout.Token);
        }
    }
    catch (ArgumentException) { }
    var finalDirectory = Path.Combine(versionsRoot, version.ToString());
    if (Directory.Exists(finalDirectory)) throw new IOException("This update version is already installed.");
    Directory.Move(staging, finalDirectory); staging = null; installedDirectory = finalDirectory;
    markerTouched = true;
    UpdateState.WriteJson(markerPath, new InstalledUpdate(plan.Repository, version.ToString(), version.ToString()));
    var launchId = Guid.NewGuid().ToString("N");
    acknowledgementPath = UpdateState.GetAcknowledgementPath(dataDirectory, launchId);
    var launch = new ProcessStartInfo(Path.Combine(finalDirectory, "Rewind.exe")) { UseShellExecute = false, CreateNoWindow = true };
    launch.ArgumentList.Add("--data-dir"); launch.ArgumentList.Add(dataDirectory);
    launch.ArgumentList.Add("--update-launch-id"); launch.ArgumentList.Add(launchId);
    launchedApp = Process.Start(launch) ?? throw new IOException("The updated app could not start.");
    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
    {
        while (!File.Exists(acknowledgementPath))
        {
            if (launchedApp.HasExited) throw new IOException("The updated app exited before startup completed.");
            await Task.Delay(100, timeout.Token);
        }
    }
    var acknowledgement = UpdateState.ReadJson<UpdateStartupAcknowledgement>(acknowledgementPath);
    if (acknowledgement.ProcessId != launchedApp.Id || !GitHubUpdateClient.TryParseVersion(acknowledgement.Version, out var startedVersion) || startedVersion != version)
        throw new IOException("The updated app did not confirm its version.");
    committed = true;
    foreach (var cleanup in new[] { plan.ArchivePath, planPath, acknowledgementPath }) TryDeleteFile(cleanup);
    TryDeleteFile(Path.Combine(updatesRoot, "failed.json"));
    Log($"Installed Rewind {version}.");
}
catch (Exception error)
{
    Log("Update failed: " + error.Message);
    if (plan != null && !committed)
    {
        if (launchedApp != null)
        {
            try
            {
                if (!launchedApp.HasExited)
                {
                    launchedApp.CloseMainWindow();
                    if (!launchedApp.WaitForExit(3000)) { launchedApp.Kill(); launchedApp.WaitForExit(3000); }
                }
            }
            catch (Exception stopError) { Log("Could not close the failed update: " + stopError.Message); }
        }
        if (markerTouched)
        {
            try
            {
                if (previousMarker != null) File.WriteAllText(markerPath, previousMarker);
                else TryDeleteFile(markerPath);
            }
            catch (Exception restoreError) { Log("Could not restore the previous version marker: " + restoreError.Message); }
        }
        try { UpdateState.WriteJson(Path.Combine(updatesRoot, "failed.json"), new { plan.Version, FailedAt = DateTimeOffset.UtcNow }); }
        catch (Exception stateError) { Log("Could not record update failure: " + stateError.Message); }
        try
        {
            var parentAlive = false;
            try { using var parent = Process.GetProcessById(plan.ParentProcessId); parentAlive = !parent.HasExited; } catch (ArgumentException) { }
            if (!parentAlive && File.Exists(plan.ParentExecutable) && Path.GetFileName(plan.ParentExecutable).Equals("Rewind.exe", StringComparison.OrdinalIgnoreCase))
            {
                var start = new ProcessStartInfo(plan.ParentExecutable) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("--skip-update");
                start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(dataDirectory);
                Process.Start(start)?.Dispose();
            }
        }
        catch (Exception fallback) { Log("Could not reopen the previous app: " + fallback.Message); }
    }
    Environment.ExitCode = committed ? 0 : 1;
}
finally
{
    launchedApp?.Dispose();
    if (acknowledgementPath != null) TryDeleteFile(acknowledgementPath);
    if (staging != null) TryDeleteDirectory(staging, true);
    if (!committed && installedDirectory != null) TryDeleteDirectory(installedDirectory, false);
}

void Log(string message)
{
    try { Directory.CreateDirectory(dataDirectory); File.AppendAllText(Path.Combine(dataDirectory, "updater.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"); }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
}
void TryDeleteFile(string path)
{
    try { File.Delete(path); }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
}
void TryDeleteDirectory(string path, bool isStaging)
{
    if (!UpdatePackage.IsWithin(path, versionsRoot) || isStaging && !Path.GetFileName(path).StartsWith(".staging-", StringComparison.Ordinal)) return;
    try { Directory.Delete(path, true); }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
}
