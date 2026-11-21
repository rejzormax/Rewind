using System.ComponentModel;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Rewind.Services;
using Forms = System.Windows.Forms;
using Rectangle = System.Drawing.Rectangle;

namespace Rewind;

internal sealed partial class ClipNotificationWindow : Window, IDisposable
{
    private readonly Action<string> _openClip, _log;
    private readonly DispatcherTimer _dismissTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private HwndSource? _source;
    private SoundPlayer? _sound;
    private Stream? _soundStream;
    private string? _clipPath;
    private int _presentation;
    private bool _disposed;

    internal ClipNotificationWindow(Action<string> openClip, Action<string> log)
    {
        _openClip = openClip; _log = log;
        InitializeComponent();
        _dismissTimer.Tick += (_, _) => Dismiss();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var style = Environment.Is64BitProcess ? GetWindowLongPtr64(handle, -20) : (nint)GetWindowLong32(handle, -20);
        style |= 0x08000000 | 0x00000080;
        if (Environment.Is64BitProcess) SetWindowLongPtr64(handle, -20, style);
        else SetWindowLong32(handle, -20, (int)style);
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WindowMessage);
    }

    private static nint WindowMessage(nint handle, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0021) { handled = true; return 3; }
        return 0;
    }

    internal void ShowClip(SavedClip clip, string monitorId, bool playSound = true)
    {
        if (_disposed) return;
        ++_presentation;
        _dismissTimer.Stop(); ClearAnimations();
        _clipPath = clip.Path;
        ClipDetailText.Text = $"{clip.DurationLabel} saved · Click to play";
        var screen = Forms.Screen.AllScreens.FirstOrDefault(item => string.Equals(item.DeviceName, monitorId, StringComparison.OrdinalIgnoreCase))
            ?? Forms.Screen.FromHandle(GetForegroundWindow());
        PositionOn(screen.WorkingArea);
        NotificationRoot.Opacity = 1; SlideTransform.X = 0;
        if (SystemParameters.ClientAreaAnimation)
        {
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(240);
            NotificationRoot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
            SlideTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, new DoubleAnimation(32, 0, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
        }
        Show();
        _dismissTimer.Start();
        if (playSound) PlayDing();
    }

    private void PositionOn(Rectangle workArea)
    {
        var handle = new WindowInteropHelper(this).EnsureHandle();
        if (Forms.Screen.FromHandle(handle).WorkingArea != workArea && !SetWindowPos(handle, 0, workArea.Left, workArea.Top, 0, 0, 0x0015))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var scale = Math.Max(96u, GetDpiForWindow(handle)) / 96d;
        var bounds = GetPlacement(workArea, scale);
        Width = bounds.Width / scale; Height = bounds.Height / scale;
        if (!SetWindowPos(handle, -1, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0010)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static Rectangle GetPlacement(Rectangle workArea, double scale)
    {
        var margin = Math.Min((int)Math.Round(20 * scale), Math.Max(0, Math.Min(workArea.Width, workArea.Height) / 4));
        var width = Math.Min((int)Math.Round(388 * scale), workArea.Width - margin * 2);
        var height = Math.Min((int)Math.Round(112 * scale), workArea.Height - margin * 2);
        var top = Math.Clamp(workArea.Top + (int)Math.Round(workArea.Height * 0.18), workArea.Top + margin, workArea.Bottom - height - margin);
        return new Rectangle(workArea.Right - width - margin, top, width, height);
    }

    private void PlayDing()
    {
        try
        {
            if (_sound == null)
            {
                _soundStream = typeof(ClipNotificationWindow).Assembly.GetManifestResourceStream("Rewind.ClipDing")
                    ?? throw new IOException("The clip sound is missing.");
                _sound = new SoundPlayer(_soundStream); _sound.Load();
            }
            _sound.Play();
        }
        catch (Exception error) { _log("Clip sound: " + error.Message); }
    }

    internal void Dismiss(bool animate = true)
    {
        _dismissTimer.Stop();
        if (!animate) _sound?.Stop();
        var presentation = ++_presentation;
        if (!IsVisible) return;
        if (!animate || !SystemParameters.ClientAreaAnimation) { Hide(); ClearAnimations(); return; }
        var duration = TimeSpan.FromMilliseconds(180);
        var fade = new DoubleAnimation(NotificationRoot.Opacity, 0, duration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }, FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) => { if (presentation == _presentation && !_disposed) { Hide(); ClearAnimations(); } };
        NotificationRoot.BeginAnimation(OpacityProperty, fade);
        SlideTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, new DoubleAnimation(SlideTransform.X, 24, duration) { EasingFunction = fade.EasingFunction, FillBehavior = FillBehavior.Stop });
    }

    private void ClearAnimations()
    {
        NotificationRoot.BeginAnimation(OpacityProperty, null);
        SlideTransform.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
    }

    private void OpenClip(object sender, RoutedEventArgs e)
    {
        var path = _clipPath;
        Dismiss();
        if (path != null) _openClip(path);
    }

    private void ClosePopup(object sender, RoutedEventArgs e) => Dismiss();

    public void Dispose()
    {
        if (_disposed) return;
        ReleaseResources(); Close();
    }

    private void ReleaseResources()
    {
        if (_disposed) return;
        _disposed = true; ++_presentation;
        _dismissTimer.Stop(); ClearAnimations();
        if (_source is { IsDisposed: false }) _source.RemoveHook(WindowMessage);
        _sound?.Stop(); _sound?.Dispose(); _soundStream?.Dispose();
    }

    protected override void OnClosed(EventArgs e) { ReleaseResources(); base.OnClosed(e); }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint handle);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint handle, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr64(nint handle, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong32(nint handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr64(nint handle, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong32(nint handle, int index, int value);
}
