using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Rewind.Core;

internal static class UpdaterChecks
{
    public static async Task RunAsync(string projectRoot)
    {
        var helper = Path.Combine(projectRoot, "dist", "Rewind", "auto-updater.exe");
        var fixture = Path.Combine(projectRoot, "artifacts", "update-fixture");
        if (!File.Exists(helper) || !File.Exists(Path.Combine(fixture, "Rewind.exe")))
            throw new Exception("Build Rewind and the updater fixture before running installer checks.");
        var current = typeof(UpdateConfiguration).Assembly.GetName().Version!;
        var version = new Version(99, 0, 0, 0);
        var root = Path.Combine(projectRoot, "artifacts", "updater-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL: " + name);
            Console.WriteLine("PASS " + name); passed++;
        }
        var cases = new List<string>();
        try
        {
            var data = PrepareCase("install");
            using (var parent = Start(Path.Combine(fixture, "Rewind.exe"), ["--data-dir", data, "--hold"]))
            {
                await WaitForFile(Path.Combine(data, "parent-ready.txt"));
                var planPath = CreatePlan(data, version, parent.Id, false, false);
                using var installer = Start(helper, ["--plan", planPath, "--data-dir", data]);
                await Task.Delay(600);
                Check(!parent.HasExited && UpdateState.ReadJson<InstalledUpdate>(Marker(data)).Version == current.ToString(), "installer waits for the old app to exit");
                File.WriteAllText(Path.Combine(data, "parent-exit.txt"), "exit");
                await WaitForExit(installer);
                Check(installer.ExitCode == 0, "real updater installs a versioned Windows executable");
                var installed = UpdateState.ReadJson<InstalledUpdate>(Marker(data));
                Check(installed.Version == version.ToString() && File.Exists(Path.Combine(UpdateState.GetVersionsDirectory(data), installed.DirectoryName, "auto-updater.exe")), "app and updater are installed together");
                var started = ReadStartup(data);
                Check(started.Arguments.Contains("--update-launch-id") && !started.Arguments.Contains("--skip-update"), "new process confirms startup with the preserved data directory");
                Check(!File.Exists(planPath) && !Directory.EnumerateFiles(UpdateState.GetUpdatesDirectory(data), "started-*.json").Any(), "successful update removes its plan and startup acknowledgement");
                CheckData(data);
                await StopFixture(data);
            }

            data = PrepareCase("bad-hash");
            var before = File.ReadAllText(Marker(data));
            var failedPlan = CreatePlan(data, version, int.MaxValue, true, false);
            var code = await RunHelper(data, failedPlan);
            Check(code == 1 && File.ReadAllText(Marker(data)) == before, "checksum failure preserves the existing launcher marker");
            await WaitForFile(Path.Combine(data, "fixture-started.json"));
            Check(ReadStartup(data).Arguments.Contains("--skip-update"), "failed install reopens the previous executable without an update loop");
            Check(!Directory.Exists(Path.Combine(UpdateState.GetVersionsDirectory(data), version.ToString())), "checksum failure creates no installed version");
            CheckData(data); await StopFixture(data);

            data = PrepareCase("startup-failure");
            before = File.ReadAllText(Marker(data));
            failedPlan = CreatePlan(data, version, int.MaxValue, false, true);
            code = await RunHelper(data, failedPlan);
            Check(code == 1 && File.ReadAllText(Marker(data)) == before, "an executable that cannot load rolls back the launcher marker");
            Check(!Directory.Exists(Path.Combine(UpdateState.GetVersionsDirectory(data), version.ToString())), "failed startup removes its incomplete version so it can be retried");
            await WaitForFile(Path.Combine(data, "fixture-started.json")); await StopFixture(data);
            File.Delete(Path.Combine(data, "fixture-exit.txt")); File.Delete(Path.Combine(data, "fixture-started.json"));
            code = await RunHelper(data, CreatePlan(data, version, int.MaxValue, false, false));
            Check(code == 0 && UpdateState.ReadJson<InstalledUpdate>(Marker(data)).Version == version.ToString(), "a repaired package can retry the same version successfully");
            Check(!File.Exists(Path.Combine(UpdateState.GetUpdatesDirectory(data), "failed.json")), "successful retry clears the failure cooldown");
            CheckData(data); await StopFixture(data);

            data = PrepareCase("wrong-parent");
            before = File.ReadAllText(Marker(data));
            code = await RunHelper(data, CreatePlan(data, version, Environment.ProcessId, false, false));
            Check(code == 1 && File.ReadAllText(Marker(data)) == before, "parent identity mismatch is rejected without changing the installed version");
            Check(!Directory.EnumerateDirectories(UpdateState.GetVersionsDirectory(data), ".staging-*").Any(), "rejected installer cleans its staging directory");
            CheckData(data);

            data = PrepareCase("wrong-version");
            before = File.ReadAllText(Marker(data));
            code = await RunHelper(data, CreatePlan(data, new Version(99, 0, 1, 0), int.MaxValue, false, false));
            Check(code == 1 && File.ReadAllText(Marker(data)) == before, "release tags must match the actual app and updater versions");
            await WaitForFile(Path.Combine(data, "fixture-started.json")); await StopFixture(data);

            Console.WriteLine($"{passed} updater installation checks passed.");
        }
        finally
        {
            foreach (var data in cases) await StopFixture(data);
            if (UpdatePackage.IsWithin(root, Path.Combine(projectRoot, "artifacts")) && Path.GetFileName(root).StartsWith("updater-check-", StringComparison.Ordinal))
                Directory.Delete(root, true);
        }

        string PrepareCase(string name)
        {
            var data = Path.Combine(root, name); Directory.CreateDirectory(data); cases.Add(data);
            File.WriteAllText(Path.Combine(data, "settings.json"), "{\"fixture\":\"preserve settings\"}");
            Directory.CreateDirectory(Path.Combine(data, "clips")); File.WriteAllText(Path.Combine(data, "clips", "keep.mp4"), "preserve saved clip");
            UpdateState.WriteJson(Marker(data), new InstalledUpdate(UpdateConfiguration.Current.Repository, current.ToString(), current.ToString()));
            return data;
        }
        void CheckData(string data) => Check(File.ReadAllText(Path.Combine(data, "settings.json")) == "{\"fixture\":\"preserve settings\"}" &&
            File.ReadAllText(Path.Combine(data, "clips", "keep.mp4")) == "preserve saved clip", "settings and saved clips remain untouched");
        string CreatePlan(string data, Version target, int parentId, bool badHash, bool missingAssembly)
        {
            var downloads = Path.Combine(UpdateState.GetUpdatesDirectory(data), "downloads"); Directory.CreateDirectory(downloads);
            var archive = Path.Combine(downloads, "release-" + Guid.NewGuid().ToString("N") + ".zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                foreach (var file in Directory.EnumerateFiles(fixture))
                    if (!missingAssembly || Path.GetFileName(file) != "Rewind.dll") zip.CreateEntryFromFile(file, "Rewind/" + Path.GetFileName(file));
                zip.CreateEntryFromFile(Path.Combine(fixture, "Rewind.exe"), "Rewind/auto-updater.exe");
            }
            var hash = badHash ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive))).ToLowerInvariant();
            var plan = Path.Combine(UpdateState.GetUpdatesDirectory(data), "plan-" + Guid.NewGuid().ToString("N") + ".json");
            UpdateState.WriteJson(plan, new UpdatePlan(UpdateConfiguration.Current.Repository, target.ToString(), archive, hash, parentId, Path.Combine(fixture, "Rewind.exe")));
            return plan;
        }
        async Task<int> RunHelper(string data, string plan)
        {
            using var process = Start(helper, ["--plan", plan, "--data-dir", data]); await WaitForExit(process);
            return process.ExitCode;
        }
        async Task StopFixture(string data)
        {
            var path = Path.Combine(data, "fixture-started.json");
            if (!File.Exists(path)) return;
            try
            {
                var info = ReadStartup(data);
                using var process = Process.GetProcessById(info.ProcessId);
                if (process.HasExited || !info.Arguments.Contains(data)) return;
                var executable = process.MainModule?.FileName;
                if (executable == null || !(UpdatePackage.IsWithin(executable, root) || UpdatePackage.IsWithin(executable, fixture))) return;
                File.WriteAllText(Path.Combine(data, "fixture-exit.txt"), "exit");
                File.WriteAllText(Path.Combine(data, "parent-exit.txt"), "exit");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { process.Kill(); await process.WaitForExitAsync(); }
            }
            catch (ArgumentException) { }
        }
    }
    private static string Marker(string data) => Path.Combine(UpdateState.GetUpdatesDirectory(data), "current.json");
    private static Startup ReadStartup(string data) => JsonSerializer.Deserialize<Startup>(File.ReadAllText(Path.Combine(data, "fixture-started.json")))!;
    private static Process Start(string executable, string[] args)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new Exception("Test process could not start.");
    }
    private static async Task WaitForExit(Process process)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(); throw new Exception("The installer did not finish within its timeout."); }
    }
    private static async Task WaitForFile(string file)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!File.Exists(file)) await Task.Delay(50, timeout.Token);
    }
    private sealed record Startup(int ProcessId, string[] Arguments);
}
