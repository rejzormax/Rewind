using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Rewind.Core;
using Rewind.Services;

internal static class ManualUpdateChecks
{
    public static async Task RunAsync(string projectRoot)
    {
        var root = Path.Combine(projectRoot, "artifacts", "manual-updates-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var passed = 0;
        void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); Console.WriteLine("PASS " + name); passed++; }
        try
        {
            var paths = new AppPaths(Path.Combine(root, "button"));
            File.WriteAllText(paths.SettingsPath, "{\"AutomaticUpdates\":false}");
            Check(!paths.LoadSettings().CheckForUpdates, "previous update preference migrates without restoring automatic installation");
            using var handler = new ReleaseHandler();
            using var http = new HttpClient(handler);
            var idle = 0;
            var launchAttempts = 0;
            ProcessStartInfo? launched = null;
            AppUpdateService? service = null;
            service = new AppUpdateService(paths, () => Volatile.Read(ref idle) == 1, start =>
            {
                launchAttempts++;
                if (launchAttempts == 1) { service!.NotifyIdleChanged(); return false; }
                launched = start; return true;
            }, http, Path.Combine(projectRoot, "dist", "Rewind"));
            await using (service)
            {
                await service.CheckNowAsync();
                Check(service.State.Phase == AppUpdatePhase.Available && handler.Downloads == 0 && !Directory.Exists(Path.Combine(UpdateState.GetUpdatesDirectory(paths.DataDirectory), "downloads")), "checking GitHub only offers the update; it never downloads automatically");
                var firstClick = service.DownloadUpdateAsync();
                var repeatedClick = service.DownloadUpdateAsync();
                Check(ReferenceEquals(firstClick, repeatedClick), "repeated Download update clicks share one operation");
                await WaitFor(() => service.State.Phase == AppUpdatePhase.WaitingForIdle);
                Check(handler.Downloads == 1 && launched == null, "clicked update downloads once and does not interrupt recording");
                Volatile.Write(ref idle, 1); service.NotifyIdleChanged();
                await WaitFor(() => service.State.Phase == AppUpdatePhase.Installing);
                Check(launched != null && launchAttempts == 2, "installer rechecks the idle state before handing off the update");
                var planPath = launched!.ArgumentList[1];
                var plan = UpdateState.ReadJson<UpdatePlan>(planPath);
                Check(plan.Repository == UpdateConfiguration.Current.Repository && plan.ParentProcessId == Environment.ProcessId && File.Exists(plan.ArchivePath), "manual update hands the verified package and current data directory to the real helper");
            }

            paths = new AppPaths(Path.Combine(root, "cancel"));
            using var cancelHandler = new ReleaseHandler();
            using var cancelHttp = new HttpClient(cancelHandler);
            var cancelled = new AppUpdateService(paths, () => false, _ => throw new Exception("Busy capture must not launch the updater."), cancelHttp, Path.Combine(projectRoot, "dist", "Rewind"));
            await cancelled.CheckNowAsync();
            var pending = cancelled.DownloadUpdateAsync();
            await WaitFor(() => cancelled.State.Phase == AppUpdatePhase.WaitingForIdle);
            await cancelled.DisposeAsync(); await pending;
            Check(!Directory.EnumerateFiles(Path.Combine(UpdateState.GetUpdatesDirectory(paths.DataDirectory), "downloads")).Any(), "closing Rewind cleans a downloaded update that was not installed");

            paths = new AppPaths(Path.Combine(root, "retry"));
            using var retryHandler = new ReleaseHandler { CorruptDigest = true };
            using var retryHttp = new HttpClient(retryHandler);
            await using (var retry = new AppUpdateService(paths, () => false, _ => false, retryHttp, Path.Combine(projectRoot, "dist", "Rewind")))
            {
                await retry.CheckNowAsync(); await retry.DownloadUpdateAsync();
                Check(retry.State.Phase == AppUpdatePhase.Failed && !Directory.EnumerateFiles(Path.Combine(UpdateState.GetUpdatesDirectory(paths.DataDirectory), "downloads")).Any(), "failed verification offers a retry and removes the broken download");
                retryHandler.CorruptDigest = false;
                await retry.CheckNowAsync(); var next = retry.DownloadUpdateAsync();
                await WaitFor(() => retry.State.Phase == AppUpdatePhase.WaitingForIdle);
                Check(retryHandler.Downloads == 2, "the user can retry a corrected release without a daily cooldown");
            }
            Console.WriteLine($"{passed} manual update checks passed.");
        }
        finally
        {
            if (UpdatePackage.IsWithin(root, Path.Combine(projectRoot, "artifacts"))) Directory.Delete(root, true);
        }
    }
    private static async Task WaitFor(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(25, timeout.Token);
    }
    private sealed class ReleaseHandler : HttpMessageHandler
    {
        private readonly byte[] _archive = RandomNumberGenerator.GetBytes(2048);
        public int Downloads;
        public bool CorruptDigest;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.Host != "api.github.com")
            {
                Downloads++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(_archive) });
            }
            var payload = JsonSerializer.Serialize(new
            {
                tag_name = "v99.0.0", draft = false, prerelease = false,
                assets = new[] { new { name = "Rewind-Windows-x64.zip", size = _archive.Length,
                    digest = "sha256:" + (CorruptDigest ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(_archive))),
                    browser_download_url = "https://github.com/rejzormax/Rewind/releases/download/v99.0.0/Rewind-Windows-x64.zip" } }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(payload) });
        }
    }
}
