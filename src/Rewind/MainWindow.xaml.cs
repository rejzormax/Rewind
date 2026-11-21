using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Rewind.Core;
using Rewind.Services;
using Forms = System.Windows.Forms;
using Brush = System.Windows.Media.Brush;

namespace Rewind;

public partial class MainWindow : Window
{
    private readonly AppPaths _paths;
    private AppSettings _settings;
    private readonly bool _testSource;
    private readonly DiscordRpcService _discord;
    private readonly AppUpdateService _updates;
    private RecorderService? _recorder;
    private GlobalHotkey? _hotkey;
    private Forms.NotifyIcon? _tray;
    private ClipNotificationWindow? _clipNotification;
    private Forms.ToolStripMenuItem? _traySave, _trayCapture;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(7) };
    private IReadOnlyList<SavedClip> _clips = [];
    private IReadOnlyList<CaptureDisplay> _displays = [];
    private bool _loading = true, _quitting, _shutdownInProgress, _shutdownComplete, _busy;
    private volatile bool _windowVisible = true;
    private bool _trayCaptureState;
    private int _libraryRevision;
    private string _currentPage = "Library";
    private readonly Stack<string> _backHistory = new(), _forwardHistory = new();
    private int _galleryColumns = 4;
    private bool _renderingPreview, _importing, _preparingEngine;
    private readonly CancellationTokenSource _engineCancellation = new();
    private Task? _engineTask;
    private CancellationTokenSource? _importCancellation;
    private Task? _importTask;
    private Brush Accent => (Brush)FindResource("Accent");
    private Brush Muted => (Brush)FindResource("Muted");

    public MainWindow(AppPaths paths, bool testSource = false)
    {
        _paths = paths; _settings = paths.LoadSettings(); _testSource = testSource;
        _discord = new DiscordRpcService();
        _updates = new AppUpdateService(paths, CanInstallUpdate, start => Dispatcher.Invoke(() =>
        {
            if (!CanInstallUpdate()) return false;
            if (Process.Start(start) == null) throw new IOException("The updater could not start.");
            _quitting = true; Close(); return true;
        }));
        InitializeComponent();
        var workArea = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, Math.Max(720, workArea.Width - 32));
        MinHeight = Math.Min(MinHeight, Math.Max(520, workArea.Height - 32));
        Width = Math.Min(Width, Math.Max(MinWidth, workArea.Width - 32));
        Height = Math.Min(Height, Math.Max(MinHeight, workArea.Height - 32));
        _discord.StatusChanged += status =>
        {
            _paths.Log("Discord presence: " + status);
            Dispatcher.BeginInvoke(() => DiscordStatusText.Text = status);
        };
        _toastTimer.Tick += (_, _) => { Toast.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };
        _updates.Changed += state => Dispatcher.BeginInvoke(() => ApplyUpdateState(state));
        SizeChanged += (_, _) => UpdateHeaderLayout();
        IsVisibleChanged += (_, _) =>
        {
            _windowVisible = IsVisible && WindowState != WindowState.Minimized;
            if (_windowVisible && !_shutdownInProgress) UpdateUi();
        };
    }

    private bool CanInstallUpdate() => _recorder?.IsRunning != true && _recorder?.IsStarting != true && _recorder?.IsSaving != true &&
        !_busy && !_importing && !_preparingEngine && !_shutdownInProgress;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _displays = DisplayEnumerator.GetDisplays();
            ApplyControls();
            _hotkey = new GlobalHotkey(new WindowInteropHelper(this).Handle);
            _hotkey.Pressed += () => _ = SaveAsync();
            if (!_hotkey.Register(_settings)) ShowToast("Your clip shortcut is in use. Choose another in Capture settings.", true);
            CreateTray();
            _loading = false;
            SelectPage("Library"); UpdateUi();
            await _discord.ConfigureAsync(_settings);
            if (!_testSource) _updates.Start(_settings.CheckForUpdates);
            await ReloadLibraryAsync();
            await EnsureRecorderAsync();
        }
        catch (Exception ex) { _loading = false; Report(ex); }
    }

    internal void PrepareLayoutPreview()
    {
        _renderingPreview = true;
        _displays = DisplayEnumerator.GetDisplays(); ApplyControls();
        var tools = FfmpegLocator.Find();
        if (tools != null) _recorder = new RecorderService(tools, _paths);
        _loading = false; UpdateUi();
    }
    internal void RenderLayoutPreview(string page, string destination, int width = 1440, int height = 900)
    {
        SelectPage(page);
        UpdateHeaderLayout(width);
        var visual = (FrameworkElement)Content;
        visual.Measure(new System.Windows.Size(width, height));
        visual.Arrange(new Rect(0, 0, width, height)); visual.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(destination); encoder.Save(stream);
    }
    internal int PrepareGalleryCheck(IReadOnlyList<SavedClip> clips)
    {
        _clips = clips; FilterLibrary(); SelectPage("Library");
        var visual = (FrameworkElement)Content;
        visual.Measure(new System.Windows.Size(1440, 900)); visual.Arrange(new Rect(0, 0, 1440, 900)); visual.UpdateLayout();
        var realized = Enumerable.Range(0, AllClips.Items.Count).Count(index => AllClips.ItemContainerGenerator.ContainerFromIndex(index) != null);
        return realized;
    }
    internal bool PreviewLibraryIsEmpty => AllClips.Items.Count == 0 && LibraryEmpty.Visibility == Visibility.Visible;
    internal void FinishLayoutPreview()
    {
        _shutdownInProgress = true; _shutdownComplete = true;
        _recorder?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _discord.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _updates.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _engineCancellation.Dispose();
        _toastTimer.Stop();
        _clipNotification?.Dispose();
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Rewind", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        _trayCapture = new Forms.ToolStripMenuItem("Start replay buffer", null, (_, _) => Dispatcher.Invoke(() => _ = ToggleCaptureAsync()));
        _traySave = new Forms.ToolStripMenuItem("Save clip", null, (_, _) => Dispatcher.Invoke(() => _ = SaveAsync())) { Enabled = false };
        menu.Items.Add(_trayCapture); menu.Items.Add(_traySave);
        menu.Items.Add("Open clips folder", null, (_, _) => Dispatcher.Invoke(() => OpenFolder(_settings.ClipDirectory)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit Rewind", null, (_, _) => Dispatcher.Invoke(() => { _quitting = true; Close(); }));
        var icon = System.Drawing.Icon.ExtractAssociatedIcon(Path.Combine(AppContext.BaseDirectory, "Rewind.exe")) ?? System.Drawing.SystemIcons.Application;
        _tray = new Forms.NotifyIcon { Icon = icon, Text = "Rewind · Buffer off", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
    }

    private async Task EnsureRecorderAsync()
    {
        if (_recorder != null || _shutdownInProgress) return;
        if (_engineTask != null) { await _engineTask; return; }
        _engineTask = PrepareRecorderAsync();
        try { await _engineTask; } finally { _engineTask = null; }
    }

    private async Task PrepareRecorderAsync()
    {
        _preparingEngine = true; UpdateUi();
        try
        {
            var tools = FfmpegLocator.Find(_paths.EngineDirectory);
            if (tools == null)
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                var progress = new Progress<string>(message => { if (!_shutdownInProgress) ShowToast(message); });
                await new CaptureEngineInstaller(http).EnsureAsync(_paths.EngineDirectory, _engineCancellation.Token, progress);
                tools = FfmpegLocator.Find(_paths.EngineDirectory) ?? throw new IOException("Recording files could not be prepared.");
            }
            if (_shutdownInProgress) return;
            _recorder = new RecorderService(tools, _paths, _testSource);
            _recorder.Changed += RecorderChanged;
            _recorder.Error += message => Dispatcher.BeginInvoke(() => ShowToast(message, true));
            _recorder.ClipSaved += ClipWasSaved;
        }
        catch (OperationCanceledException) when (_engineCancellation.IsCancellationRequested) { }
        catch (Exception error) { _paths.Log("Recording setup: " + error.Message); ShowToast("Could not download recording files. Check your connection and click Start buffer to retry.", true); }
        finally { _preparingEngine = false; if (!_shutdownInProgress) UpdateUi(); }
    }

    private void ApplyControls()
    {
        var previous = _loading; _loading = true;
        DisplayBox.ItemsSource = _displays;
        DisplayBox.SelectedItem = _displays.FirstOrDefault(d => d.Id == _settings.MonitorId) ??
            _displays.FirstOrDefault(d => d.IsPrimary) ?? _displays.FirstOrDefault();
        SetCombo(BufferBox, _settings.BufferSeconds.ToString());
        SetCombo(FpsBox, _settings.FramesPerSecond.ToString());
        SetCombo(BitrateBox, _settings.BitrateMbps.ToString());
        SetCombo(EncoderBox, _settings.Encoder);
        SystemAudioToggle.IsChecked = _settings.SystemAudio;
        MicrophoneToggle.IsChecked = _settings.Microphone;
        CursorToggle.IsChecked = _settings.CaptureCursor;
        TrayToggle.IsChecked = _settings.MinimizeToTray;
        NotificationsToggle.IsChecked = _settings.ClipNotifications;
        CheckUpdatesToggle.IsChecked = _settings.CheckForUpdates;
        DiscordToggle.IsChecked = _settings.DiscordEnabled;
        DiscordStatusText.Text = _discord.Status;
        DiscordConnectionHint.Text = ProjectBranding.Discord.IsConfigured
            ? "Presence connects automatically whenever the Discord desktop app is running."
            : "Discord presence is unavailable in this development build. The project maintainer must configure its shared identity before release.";
        FolderBox.Text = _settings.ClipDirectory;
        FolderBox.ToolTip = _settings.ClipDirectory;
        HotkeyBox.Text = _settings.HotkeyLabel;
        _loading = previous;
    }

    private static void SetCombo(ComboBox box, string tag)
    {
        box.SelectedItem = box.Items.Cast<ComboBoxItem>().FirstOrDefault(item => item.Tag?.ToString() == tag) ?? box.Items[0];
    }
    private static string ComboTag(ComboBox box, string fallback) => (box.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private void CaptureSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _recorder?.IsRunning == true || _recorder?.IsStarting == true) return;
        _settings = _settings with
        {
            BufferSeconds = int.Parse(ComboTag(BufferBox, "30")), FramesPerSecond = int.Parse(ComboTag(FpsBox, "60")),
            BitrateMbps = int.Parse(ComboTag(BitrateBox, "8")), Encoder = ComboTag(EncoderBox, "Auto"),
            MonitorId = (DisplayBox.SelectedItem as CaptureDisplay)?.Id ?? "",
            SystemAudio = SystemAudioToggle.IsChecked == true, Microphone = MicrophoneToggle.IsChecked == true,
            CaptureCursor = CursorToggle.IsChecked == true
        };
        Persist(); UpdateUi();
    }
    private void GeneralSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings = _settings with { MinimizeToTray = TrayToggle.IsChecked == true, ClipNotifications = NotificationsToggle.IsChecked == true, CheckForUpdates = CheckUpdatesToggle.IsChecked == true };
        if (!_settings.ClipNotifications) _clipNotification?.Dismiss(false);
        _updates.SetEnabled(_settings.CheckForUpdates);
        Persist();
    }
    private void QuickDuration(object sender, RoutedEventArgs e)
    {
        if (_recorder?.IsRunning == true || _busy) return;
        _settings = _settings with { BufferSeconds = int.Parse(((Button)sender).Tag.ToString()!) };
        ApplyControls(); Persist(); UpdateUi();
    }
    private void Persist() { try { _paths.SaveSettings(_settings); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Report(ex); } }

    private void Navigate(object sender, RoutedEventArgs e) => SelectPage(((Button)sender).Tag.ToString()!);
    private void SelectPage(string page, bool remember = true)
    {
        var pages = new Dictionary<string, FrameworkElement> { ["Dashboard"] = DashboardPage, ["Library"] = LibraryPage,
            ["Capture"] = CapturePage, ["Discord"] = DiscordPage };
        if (!pages.ContainsKey(page)) return;
        if (remember && page != _currentPage) { _backHistory.Push(_currentPage); _forwardHistory.Clear(); }
        _currentPage = page;
        foreach (var entry in pages) entry.Value.Visibility = entry.Key == page ? Visibility.Visible : Visibility.Collapsed;
        var buttons = new[] { NavDashboard, NavLibrary, NavCapture, NavDiscord };
        foreach (var button in buttons)
        {
            var selected = button.Tag.ToString() == page;
            button.Background = selected ? (Brush)FindResource("Selected") : Brushes.Transparent;
            button.Foreground = selected ? Accent : Muted;
            if (button.Content is StackPanel panel)
                foreach (var text in panel.Children.OfType<TextBlock>()) text.Foreground = button.Foreground;
        }
        LibraryToolbar.Visibility = page == "Library" ? Visibility.Visible : Visibility.Collapsed;
        BackButton.IsEnabled = _backHistory.Count > 0; ForwardButton.IsEnabled = _forwardHistory.Count > 0;
        if (page == "Library" && !_renderingPreview) _ = ReloadLibraryAsync();
    }
    private void GoBack(object sender, RoutedEventArgs e) { if (_backHistory.TryPop(out var page)) { _forwardHistory.Push(_currentPage); SelectPage(page, false); } }
    private void GoForward(object sender, RoutedEventArgs e) { if (_forwardHistory.TryPop(out var page)) { _backHistory.Push(_currentPage); SelectPage(page, false); } }

    private void RecorderChanged()
    {
        if (_shutdownInProgress) return;
        _updates.NotifyIdleChanged();
        if (_windowVisible || _trayCaptureState != (_recorder?.IsRunning == true))
            Dispatcher.BeginInvoke(UpdateUi);
    }

    private void UpdateUi()
    {
        if (_loading || _shutdownInProgress) return;
        var running = _recorder?.IsRunning == true;
        var starting = _recorder?.IsStarting == true;
        var saving = _recorder?.IsSaving == true;
        var buffered = _recorder?.BufferedSeconds ?? 0;
        BufferTimeText.Text = $"{(int)Math.Floor(buffered)}s";
        BufferLimitText.Text = $"/ {_settings.BufferSeconds} seconds";
        BufferProgress.Maximum = _settings.BufferSeconds; BufferProgress.Value = buffered;
        BufferStateText.Text = starting ? "Finding your encoder…" : running ? (buffered < 2 ? "Warming up…" : "Recording") : "Ready when you are";
        BufferStateText.Foreground = running ? Accent : Muted;
        HeaderStatus.Text = _preparingEngine ? "Preparing Rewind…" : _testSource && running ? "Test source" : starting ? "Starting capture…" : running ? "Recording desktop" : "Capture ready";
        HeaderStatus.Foreground = running ? Accent : Muted; HeaderStatusDot.Fill = running ? (Brush)FindResource("Recording") : Muted;
        HeaderHotkeyText.Text = _settings.HotkeyLabel.Replace(" + ", "+");
        HeaderClipLengthText.Text = $"Clip {(_settings.BufferSeconds >= 60 ? $"{_settings.BufferSeconds / 60}m" : $"{_settings.BufferSeconds}s")}";
        HeaderBufferBadge.Text = running ? "ON" : "OFF";
        HeaderBufferBadge.Foreground = running ? Accent : Muted;
        ArmButton.Content = _preparingEngine ? "Preparing…" : starting ? "Starting…" : running ? "Pause" : "Start buffer";
        ArmButton.IsEnabled = !_preparingEngine && !_busy && !saving;
        ImportButton.IsEnabled = !_preparingEngine && !_importing;
        SaveClipButton.Content = saving ? "Saving…" : "Save clip";
        SaveClipButton.IsEnabled = running && buffered >= 2 && !saving && !_busy;
        ShortcutHint.Text = "Clip from anywhere with " + _settings.HotkeyLabel;
        var plannedFps = running ? _recorder!.EffectiveFps : _settings.Encoder == "CPU" ? Math.Min(30, _settings.FramesPerSecond) : _settings.FramesPerSecond;
        ProfileFpsText.Text = $"{plannedFps} FPS";
        ProfileEncoderText.Text = running ? CaptureArguments.BackendLabel(_recorder!.Backend) :
            _settings.Encoder == "Auto" ? "Auto · GPU first" : _settings.Encoder == "CPU" ? "CPU · 30 FPS maximum" : _settings.Encoder + " · Hardware encoder";
        ProfileEncoderText.ToolTip = ProfileEncoderText.Text;
        var cpu = running && _recorder!.Backend is CaptureBackend.DesktopCpu or CaptureBackend.CompatibilityCpu;
        var display = DisplayBox.SelectedItem as CaptureDisplay;
        var height = display?.Height ?? 1080;
        if ((cpu || !running && _settings.Encoder == "CPU") && display != null)
            height = (int)(height * Math.Min(1280d, display.Width) / display.Width) / 2 * 2;
        if (_testSource && running) height = 360;
        HeaderProfileText.Text = $"{height}p {plannedFps} FPS";
        ProfileBitrateText.Text = $"{_settings.BitrateMbps} Mbps · {(cpu ? "1280 px maximum" : "Native resolution")}";
        DurationButtons.IsEnabled = !running && !starting && !_busy;
        CaptureControls.IsEnabled = !running && !starting && !_busy;
        AudioControls.IsEnabled = !running && !starting && !_busy;
        BrowseFolderButton.IsEnabled = !running && !starting && !_busy;
        CaptureSettingsHint.Text = running ? "Pause the replay buffer to change capture settings." : "Changes apply the next time you start the buffer.";
        foreach (var button in DurationButtons.Children.OfType<Button>())
        {
            var selected = button.Tag.ToString() == _settings.BufferSeconds.ToString();
            button.Background = selected ? Accent : (Brush)FindResource("Selected");
            button.Foreground = selected ? Brushes.Black : (Brush)FindResource("Ink");
        }
        PresenceDetails.Text = ProjectBranding.Discord.Details;
        PresenceState.Text = ProjectBranding.Discord.State;
        UpdateEmptyDescription();
        UpdateTray();
        _updates.NotifyIdleChanged();
    }

    private void ApplyUpdateState(AppUpdateState state)
    {
        if (_shutdownInProgress) return;
        DownloadUpdateButton.Visibility = state.Phase == AppUpdatePhase.Idle ? Visibility.Collapsed : Visibility.Visible;
        DownloadUpdateButton.Content = state.Phase switch
        {
            AppUpdatePhase.Downloading => $"Downloading {state.Percentage ?? 0}%…",
            AppUpdatePhase.WaitingForIdle => "Pause to update",
            AppUpdatePhase.Installing => "Installing…",
            _ => "Download update"
        };
        DownloadUpdateButton.IsEnabled = state.Phase is AppUpdatePhase.Available or AppUpdatePhase.Failed;
        DownloadUpdateButton.ToolTip = state.Error ?? (state.Version == null ? "Update Rewind" : $"Rewind {state.Version.Major}.{state.Version.Minor}.{state.Version.Build} is available");
        if (state.Phase == AppUpdatePhase.Failed && state.Error != null) ShowToast("The update could not finish. You can try Download update again.", true);
        UpdateHeaderLayout();
    }

    private void UpdateHeaderLayout(double? availableWidth = null)
    {
        if (HeaderProfilePanel == null) return;
        var compact = DownloadUpdateButton.Visibility == Visibility.Visible && (availableWidth ?? (ActualWidth > 0 ? ActualWidth : Width)) < 1220;
        HeaderProfilePanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        HeaderReplayPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void DownloadUpdate(object sender, RoutedEventArgs e) => await _updates.DownloadUpdateAsync();

    internal void PrepareUpdatePreview() => ApplyUpdateState(new AppUpdateState(AppUpdatePhase.Available, new Version(0, 5, 1, 0)));

    private void UpdateTray()
    {
        var running = _recorder?.IsRunning == true;
        _trayCaptureState = running;
        if (_tray != null) _tray.Text = running ? $"Rewind · {_settings.BufferSeconds}s replay buffer" : "Rewind · Buffer off";
        if (_traySave != null) { _traySave.Enabled = running && _recorder?.IsSaving != true; _traySave.Text = $"Save clip ({_settings.HotkeyLabel})"; }
        if (_trayCapture != null) { _trayCapture.Text = running ? "Pause replay buffer" : "Start replay buffer"; _trayCapture.Enabled = _recorder != null && !_busy; }
    }

    private async void ToggleCapture(object sender, RoutedEventArgs e) => await ToggleCaptureAsync();
    private async Task ToggleCaptureAsync()
    {
        if (_busy || _shutdownInProgress || _preparingEngine) return;
        if (_recorder == null) await EnsureRecorderAsync();
        if (_recorder == null || _shutdownInProgress) return;
        _busy = true; UpdateUi();
        try
        {
            if (_recorder.IsRunning) { await _recorder.StopAsync(); ShowToast("Replay paused. Your saved clips are in the library."); }
            else
            {
                var display = DisplayBox.SelectedItem as CaptureDisplay ?? throw new IOException("No display is available for capture.");
                await _recorder.StartAsync(_settings, display);
                if (_recorder.Backend is CaptureBackend.DesktopCpu or CaptureBackend.CompatibilityCpu)
                    ShowToast("GPU encoding is unavailable. Recording with the CPU at up to 30 FPS and 1280 px width.");
                else ShowToast("Replay is rolling. Press " + _settings.HotkeyLabel + " to keep a moment.");
            }
        }
        catch (Exception ex) { Report(ex); }
        finally { _busy = false; UpdateUi(); }
    }

    private async void SaveClip(object sender, RoutedEventArgs e) => await SaveAsync();
    private async Task SaveAsync()
    {
        if (_shutdownInProgress) return;
        if (_recorder == null || !_recorder.IsRunning) { ShowToast("Start the replay buffer before saving a clip."); return; }
        if (_recorder.IsSaving) return;
        try { await _recorder.SaveClipAsync(); }
        catch (Exception ex) { Report(ex); }
        finally { UpdateUi(); }
    }
    private void ClipWasSaved(SavedClip clip)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_shutdownInProgress) return;
            if (_settings.ClipNotifications)
            {
                try
                {
                    _clipNotification ??= new ClipNotificationWindow(OpenExternal, _paths.Log);
                    _clipNotification.ShowClip(clip, (DisplayBox.SelectedItem as CaptureDisplay)?.Id ?? _settings.MonitorId);
                }
                catch (Exception error) { _paths.Log("Clip notification: " + error.Message); }
            }
            _ = ReloadLibraryAsync();
        });
    }

    private async Task ReloadLibraryAsync()
    {
        var revision = ++_libraryRevision;
        var directory = _settings.ClipDirectory;
        try
        {
            var clips = await Task.Run(() => ClipLibrary.Load(directory));
            if (_shutdownInProgress || revision != _libraryRevision) return;
            _clips = clips;
            FilterLibrary();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Report(ex); }
    }
    private void FilterLibrary()
    {
        var query = SearchBox.Text.Trim();
        var date = ComboTag(DateFilterBox, "all");
        var cutoff = date switch { "today" => DateTime.Today, "week" => DateTime.Today.AddDays(-6), "month" => DateTime.Today.AddDays(-29), _ => DateTime.MinValue };
        var matching = _clips.Where(c => c.Title.Contains(query, StringComparison.OrdinalIgnoreCase) && c.Created >= cutoff);
        var sort = ComboTag(SortBox, "newest");
        var filtered = (sort switch { "oldest" => matching.OrderBy(c => c.Created), "size" => matching.OrderByDescending(c => c.SizeBytes), _ => matching.OrderByDescending(c => c.Created) }).ToArray();
        AllClips.ItemsSource = filtered.Chunk(_galleryColumns).Select(clips => new ClipRow(clips, _galleryColumns)).ToArray();
        LibraryEmpty.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        LibraryEmptyTitle.Text = _clips.Count == 0 ? "Your clips will appear here" : "No clips match these filters";
        UpdateEmptyDescription();
    }
    private void UpdateEmptyDescription() => LibraryEmptyDescription.Text = _clips.Count > 0
        ? "Try a different search or choose All dates to see your saved clips."
        : $"Start the replay buffer, then press {_settings.HotkeyLabel} to save a clip. You can also import your own MP4 files.";
    private void LibraryFilterChanged(object sender, SelectionChangedEventArgs e) { if (!_loading) FilterLibrary(); }
    private void GallerySizeChanged(object sender, SizeChangedEventArgs e)
    {
        var columns = Math.Clamp((int)((e.NewSize.Width - 16) / 300), 1, 4);
        if (columns == _galleryColumns) return;
        _galleryColumns = columns; if (!_loading) FilterLibrary();
    }
    private void SearchClips(object sender, TextChangedEventArgs e) { if (!_loading) FilterLibrary(); }
    private async void RefreshClips(object sender, RoutedEventArgs e) => await ReloadLibraryAsync();
    private void OpenClip(object sender, RoutedEventArgs e) { if (((Button)sender).Tag is string path) OpenExternal(path); }
    private void RevealClip(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not string path) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or IOException) { Report(ex); }
    }
    private void CopyClipPath(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not string path) return;
        try { Clipboard.SetText(path); ShowToast("Clip file path copied."); }
        catch (System.Runtime.InteropServices.COMException ex) { Report(ex); }
    }
    private async void ImportClips(object sender, RoutedEventArgs e)
    {
        if (_importing || _shutdownInProgress) return;
        var dialog = new OpenFileDialog { Title = "Import your MP4 clips", Filter = "MP4 clips|*.mp4", Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        _importCancellation = new CancellationTokenSource();
        _importTask = ImportPathsAsync(dialog.FileNames, _importCancellation.Token);
        try { await _importTask; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Report(ex); }
        finally { _importCancellation.Dispose(); _importCancellation = null; _importTask = null; }
    }
    private async Task ImportPathsAsync(IReadOnlyList<string> files, CancellationToken token)
    {
        var tools = FfmpegLocator.Find(_paths.EngineDirectory) ?? throw new IOException("Recording files are unavailable. Click Start buffer to finish setup.");
        var originalContent = ImportButton.Content;
        _importing = true; ImportButton.IsEnabled = false; ImportButton.Content = "Importing…";
        try
        {
            var library = new ClipLibrary(tools, _paths);
            foreach (var path in files) await library.ImportAsync(path, _settings.ClipDirectory, token);
            await ReloadLibraryAsync(); ShowToast($"Imported {files.Count} clip{(files.Count == 1 ? "" : "s")}.");
        }
        finally { _importing = false; ImportButton.IsEnabled = true; ImportButton.Content = originalContent; }
    }
    private void OpenClipsFolder(object sender, RoutedEventArgs e) => OpenFolder(_settings.ClipDirectory);
    private void OpenFolder(string directory) { try { Directory.CreateDirectory(directory); OpenExternal(directory); } catch (Exception ex) { Report(ex); } }
    private void OpenExternal(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or IOException) { Report(ex); }
    }
    private async void ChooseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose where Rewind saves clips", InitialDirectory = _settings.ClipDirectory };
        if (dialog.ShowDialog(this) == true)
        {
            _settings = _settings with { ClipDirectory = dialog.FolderName }; ApplyControls(); Persist(); await ReloadLibraryAsync();
        }
    }

    private void CaptureHotkey(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { Keyboard.ClearFocus(); return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        var modifiers = Keyboard.Modifiers;
        uint nativeModifiers = ((modifiers & ModifierKeys.Control) != 0 ? 2u : 0u) |
            ((modifiers & ModifierKeys.Alt) != 0 ? 1u : 0u) | ((modifiers & ModifierKeys.Shift) != 0 ? 4u : 0u) |
            ((modifiers & ModifierKeys.Windows) != 0 ? 8u : 0u);
        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (nativeModifiers == 0 || virtualKey is < 0x30 or > 0x87)
        { ShowToast("Choose a letter, number, or function key with Ctrl, Alt, or Shift."); return; }
        ChangeHotkey(_settings with { HotkeyModifiers = nativeModifiers, HotkeyKey = virtualKey });
    }
    private void ResetHotkey(object sender, RoutedEventArgs e) => ChangeHotkey(_settings with { HotkeyModifiers = 6, HotkeyKey = 0x78 });
    private void ChangeHotkey(AppSettings settings)
    {
        if (_hotkey?.Register(settings) == true) { _settings = settings; Persist(); ApplyControls(); UpdateUi(); ShowToast("Clip shortcut set to " + settings.HotkeyLabel + "."); }
        else { _hotkey?.Register(_settings); ShowToast("That shortcut is already in use. Choose another combination.", true); }
    }

    private async void ToggleDiscord(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try { _settings = _settings with { DiscordEnabled = DiscordToggle.IsChecked == true }; Persist(); UpdateUi(); await _discord.ConfigureAsync(_settings); }
        catch (Exception ex) { Report(ex); }
    }
    private async void ReconnectDiscord(object sender, RoutedEventArgs e)
    {
        try { await _discord.ConfigureAsync(_settings); }
        catch (Exception ex) { Report(ex); }
    }
    private void OpenLog(object sender, RoutedEventArgs e) { if (!File.Exists(_paths.LogPath)) _paths.Log("Diagnostics log opened."); OpenExternal(_paths.LogPath); }
    private void ShowToast(string message, bool error = false)
    {
        ToastText.Text = message;
        Toast.Background = error ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(60, 35, 41)) : (Brush)FindResource("Selected");
        Toast.BorderBrush = error ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(151, 77, 87)) : (Brush)FindResource("Line");
        Toast.Visibility = Visibility.Visible; _toastTimer.Stop(); _toastTimer.Start();
    }
    private void Report(Exception exception) { _paths.Log(exception.ToString()); ShowToast(exception.Message, true); }
    private void DismissToast(object sender, RoutedEventArgs e) { Toast.Visibility = Visibility.Collapsed; _toastTimer.Stop(); }
    private void DragTitleBar(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement element && (element is Button || FindButton(element))) return;
        if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private static bool FindButton(DependencyObject element)
    {
        while (element != null) { if (element is Button) return true; element = VisualTreeHelper.GetParent(element); }
        return false;
    }
    private void MinimizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
    private void QuitRewind(object sender, RoutedEventArgs e) { _quitting = true; Close(); }
    private void OnStateChanged(object? sender, EventArgs e)
    {
        _windowVisible = IsVisible && WindowState != WindowState.Minimized;
        if (WindowState == WindowState.Minimized && _settings.MinimizeToTray && !_shutdownInProgress) Hide();
    }
    private void ShowFromTray() { Show(); WindowState = WindowState.Normal; Activate(); _windowVisible = true; UpdateUi(); }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete) return;
        e.Cancel = true;
        if (!_quitting && _settings.MinimizeToTray) { Hide(); return; }
        if (_shutdownInProgress) return;
        _shutdownInProgress = true; _quitting = true; ArmButton.IsEnabled = false; SaveClipButton.IsEnabled = false;
        _toastTimer.Stop();
        _clipNotification?.Dispose();
        try
        {
            _engineCancellation.Cancel();
            if (_engineTask != null) await _engineTask;
            _importCancellation?.Cancel();
            if (_importTask != null) { try { await _importTask; } catch (OperationCanceledException) { } }
            if (_recorder != null) { _recorder.Changed -= RecorderChanged; _recorder.ClipSaved -= ClipWasSaved; await _recorder.DisposeAsync(); }
            await _discord.DisposeAsync();
            await _updates.DisposeAsync();
        }
        catch (Exception ex) { _paths.Log(ex.ToString()); }
        finally
        {
            _engineCancellation.Dispose();
            _hotkey?.Dispose();
            if (_tray != null) { _tray.Visible = false; _tray.Icon?.Dispose(); _tray.ContextMenuStrip?.Dispose(); _tray.Dispose(); }
            _shutdownComplete = true; Close(); Application.Current.Shutdown();
        }
    }
}
