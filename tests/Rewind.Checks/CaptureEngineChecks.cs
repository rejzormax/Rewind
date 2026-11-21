using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Rewind.Core;

internal static class CaptureEngineChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "rewind-engine-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bytes = CreateArchive();
            using var handler = new EngineHandler(bytes);
            using var http = new HttpClient(handler);
            var directory = Path.Combine(root, "valid");
            var installer = new CaptureEngineInstaller(http, Convert.ToHexString(SHA256.HashData(bytes)));
            var executable = await installer.EnsureAsync(directory, CancellationToken.None);
            check(CaptureEngineInstaller.IsExecutable(executable) && File.Exists(Path.Combine(directory, "LICENSE")) && File.Exists(Path.Combine(directory, "README.txt")), "recording installer verifies and retains the engine and publisher notices");
            var requests = handler.Requests;
            await installer.EnsureAsync(directory, CancellationToken.None);
            check(handler.Requests == requests, "cached recording engine does not download again");
            check(!Directory.EnumerateFileSystemEntries(directory).Any(path => Path.GetFileName(path).StartsWith('.')), "recording installer removes temporary downloads and staging");

            using var badHttp = new HttpClient(new EngineHandler(bytes));
            var badDirectory = Path.Combine(root, "bad-hash");
            try { await new CaptureEngineInstaller(badHttp, new string('0', 64)).EnsureAsync(badDirectory, CancellationToken.None); throw new Exception("Corrupt recording download was accepted."); }
            catch (InvalidDataException) { check(!File.Exists(Path.Combine(badDirectory, "ffmpeg.exe")) && !Directory.EnumerateFileSystemEntries(badDirectory).Any(), "corrupt recording downloads are rejected and cleaned"); }

            var missingBytes = CreateArchive(includeLicense: false);
            using var missingHttp = new HttpClient(new EngineHandler(missingBytes));
            var missingDirectory = Path.Combine(root, "missing-license");
            try { await new CaptureEngineInstaller(missingHttp, Convert.ToHexString(SHA256.HashData(missingBytes))).EnsureAsync(missingDirectory, CancellationToken.None); throw new Exception("Recording download without its license was accepted."); }
            catch (InvalidDataException) { check(!File.Exists(Path.Combine(missingDirectory, "ffmpeg.exe")), "recording installer requires the publisher's license information"); }

            using var redirectedHttp = new HttpClient(new EngineHandler(bytes, foreignRedirect: true));
            try { await new CaptureEngineInstaller(redirectedHttp).EnsureAsync(Path.Combine(root, "redirect"), CancellationToken.None); throw new Exception("Foreign recording download was accepted."); }
            catch (InvalidDataException) { check(true, "recording downloads cannot redirect to an unrelated host"); }
        }
        finally
        {
            if (UpdatePackage.IsWithin(root, Path.GetTempPath()) && Path.GetFileName(root).StartsWith("rewind-engine-tests-", StringComparison.Ordinal)) Directory.Delete(root, true);
        }
    }
    private static byte[] CreateArchive(bool includeLicense = true)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            using (var engine = archive.CreateEntry("ffmpeg-build/bin/ffmpeg.exe").Open())
            { engine.Write("MZ"u8); engine.Write(RandomNumberGenerator.GetBytes(2048)); }
            if (includeLicense) using (var license = archive.CreateEntry("ffmpeg-build/LICENSE").Open()) license.Write("GPLv3 fixture"u8);
            using var readme = archive.CreateEntry("ffmpeg-build/README.txt").Open(); readme.Write("Source and build fixture"u8);
        }
        return output.ToArray();
    }
    private sealed class EngineHandler(byte[] archive, bool foreignRedirect = false) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = foreignRedirect ? new HttpRequestMessage(HttpMethod.Get, "https://unrelated.test/download") : request,
                Content = new ByteArrayContent(archive)
            };
            return Task.FromResult(response);
        }
    }
}
