using System.Windows;
using Rewind.Services;
using Rewind.Core;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Rewind;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        try
        {
            var assemblyName = typeof(App).Assembly.GetName().Name;
            Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"/{assemblyName};component/Resources/Logo.xaml", UriKind.Relative) });
        }
        catch (IOException)
        {
            // Use the fallback mark when the project's logo resource is absent.
        }
    }

    private Mutex? _instance;
    internal static AppPaths Paths { get; private set; } = null!;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string? dataDirectory = null, updateLaunchId = null;
        for (var i = 0; i < e.Args.Length - 1; i++)
        {
            if (e.Args[i] == "--data-dir") dataDirectory = e.Args[++i];
            else if (e.Args[i] == "--update-launch-id") updateLaunchId = e.Args[++i];
        }
        Paths = new AppPaths(dataDirectory);
        if (!e.Args.Contains("--skip-update"))
        {
            var installed = UpdateState.GetInstalledExecutable(Paths.DataDirectory, UpdateConfiguration.Current,
                typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0, 0));
            if (installed != null)
            {
                var start = new ProcessStartInfo(installed) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var argument in e.Args) start.ArgumentList.Add(argument);
                Process.Start(start); Shutdown(); return;
            }
        }
        var instanceKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Paths.DataDirectory.ToUpperInvariant())))[..12];
        _instance = new Mutex(true, @"Local\Rewind-" + Environment.UserName + "-" + instanceKey, out var created);
        if (!created)
        {
            MessageBox.Show("Rewind is already running. Open it from the system tray.", "Rewind", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(); return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            Paths.Log(args.Exception.ToString());
            MessageBox.Show(args.Exception.Message, "Rewind", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        var window = new MainWindow(Paths, e.Args.Contains("--test-source"), e.Args.Contains("--skip-update"));
        MainWindow = window;
        window.Show();
        if (updateLaunchId != null)
        {
            try
            {
                UpdateState.WriteJson(UpdateState.GetAcknowledgementPath(Paths.DataDirectory, updateLaunchId),
                    new UpdateStartupAcknowledgement(Environment.ProcessId, typeof(App).Assembly.GetName().Version!.ToString()));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { Paths.Log("Update startup acknowledgement: " + error.Message); }
        }
    }
    protected override void OnExit(ExitEventArgs e) { _instance?.Dispose(); base.OnExit(e); }
}
