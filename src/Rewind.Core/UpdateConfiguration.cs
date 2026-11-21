using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rewind.Core;

public sealed record UpdateConfiguration(string Repository, string AssetName)
{
    public static UpdateConfiguration Current { get; } = Load();
    public bool IsConfigured => IsRepositoryValid(Repository) && AssetName == "Rewind-Windows-x64.zip";
    public Uri LatestReleaseUri => new($"https://api.github.com/repos/{Repository}/releases/latest");
    public static bool IsRepositoryValid(string value) => Regex.IsMatch(value,
        @"\A[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9][A-Za-z0-9_.-]{0,99}\z", RegexOptions.CultureInvariant);

    public bool IsDownloadUriValid(Uri uri) => uri.Scheme == "https" && uri.Host == "github.com" && uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) && uri.AbsolutePath.StartsWith($"/{Repository}/releases/download/", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.EndsWith("/" + AssetName, StringComparison.Ordinal) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    private static UpdateConfiguration Load()
    {
        using var resource = typeof(UpdateConfiguration).Assembly.GetManifestResourceStream("Rewind.Updates.json")
            ?? throw new InvalidOperationException("The release configuration is missing.");
        return JsonSerializer.Deserialize<UpdateConfiguration>(resource, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("The release configuration is invalid.");
    }
}
