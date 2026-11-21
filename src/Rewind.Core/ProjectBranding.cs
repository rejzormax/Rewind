using System.Globalization;
using System.Text.Json;

namespace Rewind.Core;

public sealed record DiscordIdentity(string ApplicationId, string AssetKey = "", string Details = "Clipping Games", string State = "https://discord.gg/msQgnj3nBD")
{
    public bool IsConfigured => IsApplicationIdValid(ApplicationId);

    public static bool IsApplicationIdValid(string value) => value.Length is >= 17 and <= 20 &&
        value.All(char.IsAsciiDigit) && ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0;
}

/// <summary>The repository's public identity is embedded once and shared by every build user.</summary>
public static class ProjectBranding
{
    public static DiscordIdentity Discord { get; } = LoadIdentity();

    private sealed record Configuration
    {
        public string DiscordApplicationId { get; init; } = "";
        public string DiscordAssetKey { get; init; } = "";
        public string DiscordDetails { get; init; } = "Clipping Games";
        public string DiscordState { get; init; } = "https://discord.gg/msQgnj3nBD";
    }

    private static DiscordIdentity LoadIdentity()
    {
        using var source = typeof(ProjectBranding).Assembly.GetManifestResourceStream("Rewind.Branding.json")
            ?? throw new InvalidOperationException("The project's branding configuration was not embedded.");
        var configuration = JsonSerializer.Deserialize<Configuration>(source, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("The project's branding configuration is invalid.");
        return new DiscordIdentity((configuration.DiscordApplicationId ?? "").Trim(), (configuration.DiscordAssetKey ?? "").Trim(),
            configuration.DiscordDetails, configuration.DiscordState);
    }
}
