using System.Globalization;

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
    public static DiscordIdentity Discord { get; } = new("1573632058495078501");
}
