using System.Reflection;
using System.Diagnostics;
using System.Windows.Navigation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using System.Runtime.InteropServices;

namespace Winora;

public partial class MainWindow : Window
{
    private readonly bool previewOnly;
    private readonly TaskbarService taskbar = new();
    private readonly UpdateService updates = new();
    private readonly TrayIconService trayIcons = new();
    private readonly System.Windows.Threading.DispatcherTimer trayHealthTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly LatestTaskbarChange changes;
    private TaskbarPreference? retryPreference;
    private bool busyUpdating;
    private bool restartingUpdate;
    private bool busyTray;
    private bool initializing = true;
    private bool synchronizing;
    private int updateStatusGeneration;
    private int activityGeneration;
    private int finishStatusGeneration;
    private int trayStatusGeneration;
    private bool CanChangeTrayPreference => TrayIconService.IsSupported || TrayIconsCheckBox.IsChecked == true;

    public MainWindow(bool previewOnly = false)
    {
        this.previewOnly = previewOnly;
        InitializeComponent();
        if (!previewOnly)
        {
            Width = Math.Max(MinWidth, Math.Min(Width, SystemParameters.WorkArea.Width - 24));
            Height = Math.Max(MinHeight, Math.Min(Height, SystemParameters.WorkArea.Height - 24));
        }
        Title = AppBuild.Name;
        AppNameLabel.Text = AppBuild.Name;
        DevBadge.Visibility = AppBuild.IsDevelopment ? Visibility.Visible : Visibility.Collapsed;
        VersionLabel.Text = $"v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";
        var settings = previewOnly ? new UserSettings(TaskbarMode.Transparent) : Settings.Load();
        changes = new LatestTaskbarChange(new(settings.Mode, settings.StartWithWindows), ApplyTaskbarAsync,
            preference => Settings.Save(Settings.Load() with { Mode = preference.Mode, StartWithWindows = preference.StartWithWindows }));
        changes.Applying += _ =>
        {
            StatusLabel.Tag = null;
            ++activityGeneration;
            ++finishStatusGeneration;
            ActivityLabel.Text = AppBuild.IsDevelopment ? "Saving preview…" : "Changing finish…";
            ActivityLabel.Visibility = Visibility.Visible;
            StatusLabel.Text = "";
        };
        changes.Applied += preference =>
        {
            if (preference != changes.Requested) return;
            StatusLabel.Text = AppBuild.IsDevelopment ? "Preview saved" : preference.Mode == TaskbarMode.Default ? "Windows default" : $"{preference.Mode} active";
            _ = ClearFinishStatusAsync(++finishStatusGeneration);
        };
        changes.Failed += failure =>
        {
            Trace.WriteLine(failure.Error);
            if (failure.RestoreError is not null) Trace.WriteLine(failure.RestoreError);
            if (failure.Superseded) return;
            retryPreference = failure.Attempted;
            ++finishStatusGeneration;
            SelectPreference(changes.Committed);
            StatusLabel.Tag = "Error";
            StatusLabel.Text = failure.RestoreError is null ? $"Couldn’t change finish: {failure.Error.Message}" : $"Couldn’t change or restore the finish: {failure.Error.Message}";
            RetryButton.Visibility = Visibility.Visible;
        };
        changes.StateChanged += () =>
        {
            if (!changes.IsBusy) ActivityLabel.Visibility = Visibility.Collapsed;
            RefreshInteractionState();
        };
        SelectPreference(changes.Committed);
        TrayIconsCheckBox.IsChecked = settings.AlwaysShowTrayIcons;
        UpdateTrayPreview();
        initializing = false;
        RefreshInteractionState();
        trayHealthTimer.Tick += (_, _) =>
        {
            if (!busyTray && !AppBuild.IsDevelopment && Settings.Load().AlwaysShowTrayIcons) ShowTrayStatus(TrayIconService.GetStatus());
        };
        Closed += (_, _) => trayHealthTimer.Stop();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var light = 0;
        _ = DwmSetWindowAttribute(handle, 20, ref light, sizeof(int));
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
    private void OnWorkspaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < 820;
        SidebarColumn.Width = new GridLength(compact ? 60 : 164);
        SidebarLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        TaskbarNavigation.HorizontalContentAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        TaskbarNavigation.Padding = new Thickness(compact ? 6 : 12, 9, compact ? 6 : 12, 9);
        TaskbarWorkspace.Margin = new Thickness(compact ? 20 : 28, 14, compact ? 20 : 28, 12);
    }
    private void OnMinimize(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void OnClose(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        WindowSurface.Margin = WindowState == WindowState.Maximized ? new Thickness(8) : new Thickness(0);
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, MaximizeButton.ToolTip.ToString());
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (previewOnly) return;
        if (AppBuild.IsDevelopment)
        {
            StatusLabel.Text = "";
            TrayIconsCheckBox.ToolTip = "Preview only. Windows tray visibility is unchanged.";
            UpdateButton.Content = "Local build";
            RefreshInteractionState();
            return;
        }
        if (!TrayIconService.IsSupported) ShowTrayStatus("Tray automation is unavailable on this Windows configuration.");
        else if (Settings.Load().AlwaysShowTrayIcons)
        {
            busyTray = true;
            RefreshInteractionState();
            try { await trayIcons.EnableAsync(); ShowTrayStatus(TrayIconService.GetStatus()); }
            catch (Exception error) { ShowTrayStatus($"Couldn’t resume tray automation: {error.Message}"); }
            finally { busyTray = false; RefreshInteractionState(); }
        }
        trayHealthTimer.Start();
        if (changes.Committed.Mode != TaskbarMode.Default && !taskbar.IsRunning)
            await changes.RequestAsync(changes.Committed, force: true);
        else
        {
            StatusLabel.Text = changes.Committed.Mode == TaskbarMode.Default ? "" : $"{changes.Committed.Mode} active";
            _ = ClearFinishStatusAsync(++finishStatusGeneration);
        }
        await CheckUpdatesAsync();
    }

    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private async void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } || PreviewBar is null) return;
        var mode = Enum.Parse<TaskbarMode>(tag);
        RenderPreview(mode);
        if (initializing || synchronizing) return;
        if (previewOnly)
        {
            StartupCheckBox.IsEnabled = mode != TaskbarMode.Default;
            if (mode == TaskbarMode.Default) StartupCheckBox.IsChecked = false;
            return;
        }
        if (mode == TaskbarMode.Default)
        {
            synchronizing = true;
            StartupCheckBox.IsChecked = false;
            synchronizing = false;
        }
        await RequestPreferenceAsync(new(mode, StartupCheckBox.IsChecked == true));
    }
    private async void OnStartupChanged(object sender, RoutedEventArgs e)
    {
        if (initializing || synchronizing || previewOnly) return;
        await RequestPreferenceAsync(new(changes.Requested.Mode, StartupCheckBox.IsChecked == true));
    }
    private async Task RequestPreferenceAsync(TaskbarPreference preference, bool force = false)
    {
        retryPreference = null;
        ++finishStatusGeneration;
        RetryButton.Visibility = Visibility.Collapsed;
        var pending = changes.RequestAsync(preference, force);
        RefreshInteractionState();
        await pending;
    }
    private async void OnRetry(object sender, RoutedEventArgs e)
    {
        if (retryPreference is not { } preference || changes.IsBusy || busyTray) return;
        SelectPreference(preference);
        await RequestPreferenceAsync(preference, force: true);
    }
    private Task ApplyTaskbarAsync(TaskbarPreference preference)
    {
        var generation = activityGeneration;
        return taskbar.ApplyAsync(preference.Mode, preference.StartWithWindows,
            new Progress<string>(text =>
            {
                if (generation == activityGeneration && changes.IsBusy)
                    ActivityLabel.Text = text.Replace("appearance", "finish", StringComparison.OrdinalIgnoreCase);
            }));
    }
    private void SelectPreference(TaskbarPreference preference)
    {
        synchronizing = true;
        try
        {
            DefaultMode.IsChecked = preference.Mode == TaskbarMode.Default;
            TransparentMode.IsChecked = preference.Mode == TaskbarMode.Transparent;
            AcrylicMode.IsChecked = preference.Mode == TaskbarMode.Acrylic;
            StartupCheckBox.IsChecked = preference.StartWithWindows;
            RenderPreview(preference.Mode);
        }
        finally { synchronizing = false; }
    }
    public void RenderPreview(TaskbarMode mode)
    {
        var color = mode switch
        {
            TaskbarMode.Transparent => Color.FromArgb(0, 229, 237, 241),
            TaskbarMode.Acrylic => Color.FromArgb(110, 236, 248, 247),
            _ => Color.FromArgb(240, 229, 237, 241)
        };
        if (PreviewBar.Background is not SolidColorBrush { IsFrozen: false } brush)
        {
            brush = new SolidColorBrush(color);
            PreviewBar.Background = brush;
        }
        if (!previewOnly && !initializing && SystemParameters.ClientAreaAnimation)
            brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(color, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        else { brush.BeginAnimation(SolidColorBrush.ColorProperty, null); brush.Color = color; }
        AcrylicBackdrop.Visibility = mode == TaskbarMode.Acrylic ? Visibility.Visible : Visibility.Collapsed;
        PreviewModeLabel.Text = mode.ToString();
    }
    private void UpdateTrayPreview()
    {
        var enabled = TrayIconsCheckBox.IsChecked == true;
        if (FindName("TrayPreviewApps") is FrameworkElement apps) apps.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (FindName("TrayPreviewOverflow") is FrameworkElement overflow) overflow.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
    }
    private void RefreshInteractionState()
    {
        DefaultMode.IsEnabled = TransparentMode.IsEnabled = AcrylicMode.IsEnabled = !busyTray && !restartingUpdate;
        StartupCheckBox.IsEnabled = !busyTray && !restartingUpdate && changes.Requested.Mode != TaskbarMode.Default;
        TrayIconsCheckBox.IsEnabled = CanChangeTrayPreference && !busyTray && !changes.IsBusy && !restartingUpdate;
        UpdateButton.IsEnabled = !AppBuild.IsDevelopment && !busyUpdating && !busyTray && !changes.IsBusy;
        RetryButton.IsEnabled = !busyTray && !changes.IsBusy && !restartingUpdate;
    }
    private void ShowTrayStatus(string text, bool transient = false)
    {
        var generation = ++trayStatusGeneration;
        TrayStatusLabel.Text = text;
        TrayStatusLabel.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        if (transient) _ = ClearTrayStatusAsync(generation);
    }
    private async void OnTrayPreferenceChanged(object sender, RoutedEventArgs e)
    {
        if (TrayIconsCheckBox is null) return;
        UpdateTrayPreview();
        if (initializing || synchronizing || previewOnly || busyTray) return;
        busyTray = true;
        RefreshInteractionState();
        var enabled = TrayIconsCheckBox.IsChecked == true;
        var previous = Settings.Load().AlwaysShowTrayIcons;
        ShowTrayStatus(enabled ? "Enabling…" : "Stopping…");
        try
        {
            if (enabled) await trayIcons.EnableAsync();
            else await TrayIconService.DisableAsync();
            Settings.Save(Settings.Load() with { AlwaysShowTrayIcons = enabled });
            ShowTrayStatus(AppBuild.IsDevelopment ? "Preview saved" : enabled ? "" : "Automation off; current icons stay visible", transient: true);
        }
        catch (Exception error)
        {
            Exception? restorationError = null;
            try { if (previous) await trayIcons.EnableAsync(); else await TrayIconService.DisableAsync(); }
            catch (Exception restoreError) { restorationError = restoreError; Trace.WriteLine(restoreError); }
            TrayIconsCheckBox.IsChecked = previous;
            UpdateTrayPreview();
            ShowTrayStatus(restorationError is null ? $"Couldn’t change tray automation: {error.Message}" : $"Couldn’t change or restore tray automation: {error.Message}");
        }
        finally { busyTray = false; RefreshInteractionState(); }
    }
    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        if (changes.IsBusy || busyTray || AppBuild.IsDevelopment) return;
        if (updates.PendingUpdate is not null)
        {
            busyUpdating = true;
            restartingUpdate = true;
            RefreshInteractionState();
            try { await taskbar.PrepareForUpdateAsync(); updates.ApplyAndRestart(); }
            catch (Exception error) { Trace.WriteLine(error); ShowUpdateStatus("Couldn’t update. Retry."); }
            finally { busyUpdating = false; restartingUpdate = false; RefreshInteractionState(); }
            return;
        }
        await CheckUpdatesAsync();
    }
    private async Task CheckUpdatesAsync()
    {
        if (busyUpdating || busyTray || AppBuild.IsDevelopment) return;
        busyUpdating = true;
        RefreshInteractionState();
        ++updateStatusGeneration;
        UpdateLabel.Visibility = Visibility.Collapsed;
        UpdateButton.Content = "Checking…";
        try
        {
            await updates.CheckAndDownloadAsync();
            if (updates.PendingUpdate is not null)
            {
                UpdateButton.Content = "Restart to update";
                ShowUpdateStatus("Update ready");
            }
            else
            {
                UpdateButton.Content = "Check for updates";
                ShowUpdateStatus(updates.IsInstalled ? "Up to date" : "Install to update");
                _ = ClearUpdateStatusAsync(updateStatusGeneration);
            }
        }
        catch (Exception error)
        {
            Trace.WriteLine(error);
            UpdateButton.Content = "Check for updates";
            ShowUpdateStatus("Couldn’t check. Retry.");
            _ = ClearUpdateStatusAsync(updateStatusGeneration);
        }
        finally { busyUpdating = false; RefreshInteractionState(); }
    }
    private void ShowUpdateStatus(string text)
    {
        UpdateLabel.Text = text;
        UpdateLabel.Visibility = Visibility.Visible;
    }
    private async Task ClearUpdateStatusAsync(int generation)
    {
        await Task.Delay(5000);
        if (generation == updateStatusGeneration && updates.PendingUpdate is null) UpdateLabel.Visibility = Visibility.Collapsed;
    }
    private async Task ClearFinishStatusAsync(int generation)
    {
        await Task.Delay(2500);
        if (generation == finishStatusGeneration && !changes.IsBusy && retryPreference is null) StatusLabel.Text = "";
    }
    private async Task ClearTrayStatusAsync(int generation)
    {
        await Task.Delay(2500);
        if (generation == trayStatusGeneration && !busyTray) ShowTrayStatus("");
    }
}
