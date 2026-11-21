namespace Rewind.Core;

public sealed record AppSettings
{
    public int BufferSeconds { get; init; } = 30;
    public int FramesPerSecond { get; init; } = 60;
    public int BitrateMbps { get; init; } = 8;
    public string Encoder { get; init; } = "Auto";
    public string MonitorId { get; init; } = "";
    public bool SystemAudio { get; init; } = true;
    public bool Microphone { get; init; }
    public bool CaptureCursor { get; init; } = true;
    public bool MinimizeToTray { get; init; } = true;
    public bool ClipNotifications { get; init; } = true;
    public bool DiscordEnabled { get; init; } = true;
    public bool CheckForUpdates { get; init; } = true;
    public string ClipDirectory { get; init; } = "";
    public uint HotkeyModifiers { get; init; } = 0x0002 | 0x0004;
    public uint HotkeyKey { get; init; } = 0x78;

    public AppSettings Validated() => this with
    {
        BufferSeconds = new[] { 15, 30, 60, 120 }.Contains(BufferSeconds) ? BufferSeconds : 30,
        FramesPerSecond = FramesPerSecond == 30 ? 30 : 60,
        BitrateMbps = Math.Clamp(BitrateMbps, 2, 30),
        Encoder = new[] { "Auto", "NVIDIA", "AMD", "Intel", "CPU" }.Contains(Encoder) ? Encoder : "Auto",
        HotkeyModifiers = HotkeyModifiers & 0xF,
        HotkeyKey = HotkeyKey is >= 0x30 and <= 0x87 ? HotkeyKey : 0x78,
        ClipDirectory = ClipDirectory ?? "",
        MonitorId = MonitorId ?? ""
    };

    public string HotkeyLabel
    {
        get
        {
            var parts = new List<string>();
            if ((HotkeyModifiers & 2) != 0) parts.Add("Ctrl");
            if ((HotkeyModifiers & 1) != 0) parts.Add("Alt");
            if ((HotkeyModifiers & 4) != 0) parts.Add("Shift");
            if ((HotkeyModifiers & 8) != 0) parts.Add("Win");
            parts.Add(HotkeyKey is >= 0x70 and <= 0x87 ? $"F{HotkeyKey - 0x6F}" : ((char)HotkeyKey).ToString());
            return string.Join(" + ", parts);
        }
    }
}
