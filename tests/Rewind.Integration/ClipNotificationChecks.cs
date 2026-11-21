using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Rewind;
using Rewind.Services;

internal static class ClipNotificationChecks
{
    private static int _passed;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        ++_passed; Console.WriteLine("PASS " + name);
    }

    internal static async Task RunAsync(string projectRoot)
    {
        using (var stream = typeof(Rewind.App).Assembly.GetManifestResourceStream("Rewind.ClipDing") ?? throw new Exception("Clip sound was not embedded."))
        using (var reader = new BinaryReader(stream, Encoding.ASCII))
        {
            var riff = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var length = reader.ReadInt32();
            var format = Encoding.ASCII.GetString(reader.ReadBytes(8));
            var formatLength = reader.ReadInt32();
            var encoding = reader.ReadInt16(); var channels = reader.ReadInt16();
            var sampleRate = reader.ReadInt32(); reader.ReadInt32(); reader.ReadInt16();
            var bits = reader.ReadInt16(); var chunk = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var dataLength = reader.ReadInt32();
            Check(riff == "RIFF" && format == "WAVEfmt " && chunk == "data" && formatLength == 16 && length == stream.Length - 8 && dataLength == stream.Length - 44,
                "release embeds a complete WAV notification sound");
            Check(encoding == 1 && channels == 1 && bits == 16 && sampleRate == 22050 && dataLength / (sampleRate * 2d) is > 0.1 and < 0.8 && stream.Length < 25000,
                "ding is short, uncompressed PCM and smaller than 25 KB");
            var samples = Enumerable.Range(0, dataLength / 2).Select(_ => reader.ReadInt16()).ToArray();
            Check(samples.Max(value => Math.Abs((int)value)) is > 2000 and < 16000 && samples.TakeLast(20).All(value => Math.Abs((int)value) < 128),
                "ding has audible headroom and fades to silence without a sharp cut");
        }
        foreach (var (area, scale) in new[]
        {
            (new Rectangle(0, 0, 1920, 1040), 1d),
            (new Rectangle(-2560, 120, 2560, 1400), 1.5),
            (new Rectangle(0, -2160, 3840, 2120), 2d),
            (new Rectangle(-800, 0, 800, 560), 2d)
        })
        {
            var bounds = ClipNotificationWindow.GetPlacement(area, scale);
            Check(area.Contains(bounds) && bounds.Left > area.Left && bounds.Top > area.Top + 40 && bounds.Right < area.Right && bounds.Bottom < area.Bottom,
                $"popup fits the right side of a {area.Width} px display at {scale * 100:0}% scaling");
        }

        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var document = XDocument.Load(Path.Combine(projectRoot, "src", "Rewind", "App.xaml"));
            document.Root!.Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"))!.Remove();
            foreach (var source in document.Descendants().Where(element => element.Name.LocalName == "ResourceDictionary").Attributes("Source"))
                source.Value = "pack://application:,,,/Rewind;component/" + source.Value;
            var application = (System.Windows.Application)XamlReader.Parse(document.ToString());
            if (File.Exists(Path.Combine(projectRoot, "src", "Rewind", "Resources", "Logo.xaml")))
                application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Rewind;component/Resources/Logo.xaml", UriKind.Relative) });
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await CheckWindowAsync(projectRoot);
                    Console.WriteLine($"{_passed} clip notification checks passed.");
                    completed.SetResult();
                }
                catch (Exception error) { completed.SetException(error); }
                finally
                {
                    application.Shutdown();
                    if (!Dispatcher.CurrentDispatcher.HasShutdownStarted) Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completed.Task;
    }

    private static async Task CheckWindowAsync(string projectRoot)
    {
        var opened = new List<string>(); var errors = new List<string>();
        using var window = new ClipNotificationWindow(opened.Add, errors.Add);
        var firstClip = new SavedClip(Path.Combine(projectRoot, "artifacts", "first-notification.mp4"), "First clip", DateTime.Now, 30, 1920, 1080, 1000, null);
        var secondClip = firstClip with { Path = Path.Combine(projectRoot, "artifacts", "second-notification.mp4"), Duration = 15 };
        var foreground = GetForegroundWindow();
        window.ShowClip(firstClip, "");
        var handle = new WindowInteropHelper(window).Handle;
        Check(window.IsVisible && !window.ShowInTaskbar && window.Owner == null && GetForegroundWindow() == foreground,
            "desktop popup appears independently of the main window without taking focus");
        Check(SendMessage(handle, 0x0021, 0, 0) == 3, "clicks on the popup do not activate its window");
        await Task.Delay(350);
        var visual = (FrameworkElement)window.Content;
        visual.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.Width * 2), (int)Math.Ceiling(window.Height * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var imagePath = Path.Combine(projectRoot, "artifacts", "rewind-clip-notification.png");
        using (var output = File.Create(imagePath)) encoder.Save(output);
        Console.WriteLine("Rendered " + imagePath);

        window.Dismiss();
        await Task.Delay(50);
        var refreshedForeground = GetForegroundWindow();
        window.ShowClip(secondClip, "", false);
        var retainedFocus = GetForegroundWindow() == refreshedForeground;
        await Task.Delay(300);
        Check(window.IsVisible && new WindowInteropHelper(window).Handle == handle && retainedFocus,
            "a clip saved during dismissal refreshes the same popup and retains focus");
        window.PlayClipButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(opened.SequenceEqual(new[] { secondClip.Path }), "clicking a refreshed popup opens the latest saved clip");
        await Task.Delay(250);
        Check(!window.IsVisible, "clicking the clip dismisses the popup smoothly");

        window.ShowClip(firstClip, "", false);
        await Task.Delay(4400);
        Check(!window.IsVisible, "popup automatically disappears after four seconds");
        window.ShowClip(secondClip, "", false); window.Dismiss(false);
        Check(!window.IsVisible, "turning notifications off dismisses the current popup immediately");
        window.ShowClip(firstClip, "", false); window.Close();
        Check(!IsWindow(handle) && errors.Count == 0, "closing releases the notification window and sound without errors");
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint SendMessage(nint handle, int message, nint wParam, nint lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint handle);
}
