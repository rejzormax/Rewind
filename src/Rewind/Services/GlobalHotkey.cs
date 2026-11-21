using System.Runtime.InteropServices;
using System.Windows.Interop;
using Rewind.Core;

namespace Rewind.Services;

public sealed class GlobalHotkey : IDisposable
{
    private const int Id = 0x5257;
    private readonly HwndSource _source;
    public event Action? Pressed;
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);

    public GlobalHotkey(nint window)
    {
        _source = HwndSource.FromHwnd(window) ?? throw new InvalidOperationException("Window handle unavailable.");
        _source.AddHook(Hook);
    }
    public bool Register(AppSettings settings)
    {
        UnregisterHotKey(_source.Handle, Id);
        return RegisterHotKey(_source.Handle, Id, settings.HotkeyModifiers | 0x4000, settings.HotkeyKey);
    }
    private nint Hook(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam == Id) { handled = true; Pressed?.Invoke(); }
        return 0;
    }
    public void Dispose() { UnregisterHotKey(_source.Handle, Id); _source.RemoveHook(Hook); }
}
