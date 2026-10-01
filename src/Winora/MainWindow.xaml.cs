using System.Reflection;
using System.Diagnostics;
using System.Windows.Navigation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Interop;
using System.Runtime.InteropServices;

namespace Winora;

public partial class MainWindow : Window
{
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var enabled = 1;
        _ = DwmSetWindowAttribute(handle, 20 /* immersive dark mode */, ref enabled, sizeof(int));
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

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
    private readonly bool previewOnly;
    private readonly TaskbarService taskbar = new();
    private readonly UpdateService updates = new();
    private readonly TrayIconService trayIcons = new();
    private readonly System.Windows.Threading.DispatcherTimer trayHealthTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private TaskbarMode selectedMode;
    private bool busyUpdating;
    private bool busyApplying;
    private bool busyTray;
    private bool initializing = true;
    private int updateStatusGeneration;
    private bool CanChangeTrayPreference => TrayIconService.IsSupported || TrayIconsCheckBox.IsChecked == true;

    public MainWindow(bool previewOnly = false)
    {
        this.previewOnly = previewOnly;
        InitializeComponent();
        Title = AppBuild.Name;
        AppNameLabel.Text = AppBuild.Name;
        DevBadge.Visibility = AppBuild.IsDevelopment ? Visibility.Visible : Visibility.Collapsed;
        VersionLabel.Text = $"v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";
        selectedMode = previewOnly ? TaskbarMode.Transparent : Settings.Load().Mode;
        StartupCheckBox.IsChecked = !previewOnly && Settings.Load().StartWithWindows;
        TrayIconsCheckBox.IsChecked = !previewOnly && Settings.Load().AlwaysShowTrayIcons;
        switch (selectedMode)
        {
            case TaskbarMode.Transparent: TransparentMode.IsChecked = true; break;
            case TaskbarMode.Acrylic: AcrylicMode.IsChecked = true; break;
            default: DefaultMode.IsChecked = true; break;
        }
        initializing = false;
        trayHealthTimer.Tick += (_, _) =>
        {
            if (!busyTray && Settings.Load().AlwaysShowTrayIcons) ShowTrayStatus(TrayIconService.GetStatus());
        };
        Closed += (_, _) => trayHealthTimer.Stop();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (previewOnly) return;
        if (AppBuild.IsDevelopment)
        {
            StatusLabel.Text = "Dev preview. Apply saves dev preferences only.";
            TrayIconsCheckBox.ToolTip = "Dev preview: saves this preference without changing Windows or starting a helper.";
            UpdateButton.Content = "Local development";
            UpdateButton.IsEnabled = false;
            return;
        }
        TrayIconsCheckBox.IsEnabled = CanChangeTrayPreference;
        if (!TrayIconService.IsSupported) ShowTrayStatus("Tray automation is unavailable on this Windows configuration.");
        else if (Settings.Load().AlwaysShowTrayIcons)
        {
            busyTray = true;
            TrayIconsCheckBox.IsEnabled = false;
            ApplyButton.IsEnabled = false;
            UpdateButton.IsEnabled = false;
            try { await trayIcons.EnableAsync(); ShowTrayStatus(TrayIconService.GetStatus()); }
            catch (Exception error) { ShowTrayStatus($"Tray automation could not resume: {error.Message}"); }
            finally { busyTray = false; TrayIconsCheckBox.IsEnabled = CanChangeTrayPreference; ApplyButton.IsEnabled = true; UpdateButton.IsEnabled = true; }
        }
        trayHealthTimer.Start();
        StatusLabel.Text = taskbar.DescribeStatus();
        if (Settings.Load().Mode != TaskbarMode.Default && !taskbar.IsRunning)
        {
            busyApplying = true;
            ApplyButton.IsEnabled = false;
            try { await taskbar.ApplyAsync(Settings.Load().Mode, Settings.Load().StartWithWindows); StatusLabel.Text = "Your saved appearance is active."; }
            catch (Exception error) { StatusLabel.Text = $"Couldn’t restore your appearance: {error.Message}"; }
            finally { busyApplying = false; ApplyButton.IsEnabled = true; }
        }
        await CheckUpdatesAsync();
    }

    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } || PreviewBar is null) return;
        selectedMode = Enum.Parse<TaskbarMode>(tag);
        var color = selectedMode switch
        {
            TaskbarMode.Transparent => Color.FromArgb(0, 32, 39, 55),
            TaskbarMode.Acrylic => Color.FromArgb(112, 32, 39, 55),
            _ => Color.FromArgb(238, 32, 32, 32)
        };
        PreviewBar.Background = new SolidColorBrush(color);
        AcrylicBackdrop.Visibility = selectedMode == TaskbarMode.Acrylic ? Visibility.Visible : Visibility.Collapsed;
        PreviewModeLabel.Text = selectedMode.ToString();
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        ApplyButton.IsEnabled = false;
        UpdateButton.IsEnabled = false;
        busyApplying = true;
        TrayIconsCheckBox.IsEnabled = false;
        StatusLabel.Text = "Applying appearance…";
        try
        {
            var mode = selectedMode;
            var startup = StartupCheckBox.IsChecked == true && mode != TaskbarMode.Default;
            await taskbar.ApplyAsync(mode, startup, new Progress<string>(text => StatusLabel.Text = text));
            Settings.Save(Settings.Load() with { Mode = mode, StartWithWindows = startup });
            if (mode == TaskbarMode.Default) StartupCheckBox.IsChecked = false;
            StatusLabel.Text = AppBuild.IsDevelopment ? "Dev preferences saved. Windows is unchanged." : mode == TaskbarMode.Default
                ? "Windows default restored."
                : $"{mode} applied. You can close this window.";
        }
        catch (Exception error)
        {
            StatusLabel.Text = $"Couldn’t apply this appearance: {error.Message}";
        }
        finally { busyApplying = false; ApplyButton.IsEnabled = !busyTray; UpdateButton.IsEnabled = !AppBuild.IsDevelopment && !busyUpdating && !busyTray; TrayIconsCheckBox.IsEnabled = CanChangeTrayPreference && !busyTray; }
    }

    private void ShowTrayStatus(string text)
    {
        TrayStatusLabel.Text = text;
        TrayStatusLabel.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnTrayPreferenceChanged(object sender, RoutedEventArgs e)
    {
        if (initializing || previewOnly || busyTray) return;
        busyTray = true;
        TrayIconsCheckBox.IsEnabled = false;
        ApplyButton.IsEnabled = false;
        UpdateButton.IsEnabled = false;
        var enabled = TrayIconsCheckBox.IsChecked == true;
        var previous = Settings.Load().AlwaysShowTrayIcons;
        ShowTrayStatus(enabled ? "Enabling…" : "Stopping…");
        try
        {
            if (enabled) await trayIcons.EnableAsync();
            else await TrayIconService.DisableAsync();
            Settings.Save(Settings.Load() with { AlwaysShowTrayIcons = enabled });
            ShowTrayStatus(AppBuild.IsDevelopment ? "Dev preference saved. Windows tray visibility is unchanged." : enabled ? "" : "Automation off. Icon visibility can be changed in Windows Settings.");
        }
        catch (Exception error)
        {
            // Restore the previous preference if persistence or activation failed.
            try { if (previous) await trayIcons.EnableAsync(); else await TrayIconService.DisableAsync(); }
            catch (Exception restoreError) { System.Diagnostics.Trace.WriteLine(restoreError); }
            TrayIconsCheckBox.IsChecked = previous;
            ShowTrayStatus($"Could not change tray automation: {error.Message}");
        }
        finally
        {
            busyTray = false;
            TrayIconsCheckBox.IsEnabled = CanChangeTrayPreference && !busyApplying;
            ApplyButton.IsEnabled = !busyApplying;
            UpdateButton.IsEnabled = !AppBuild.IsDevelopment && !busyUpdating && !busyApplying;
        }
    }

    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        if (busyApplying || busyTray) return;
        if (updates.PendingUpdate is not null)
        {
            UpdateButton.IsEnabled = false;
            try
            {
                // Shut the engine down gracefully before Velopack replaces its files.
                await taskbar.PrepareForUpdateAsync();
                updates.ApplyAndRestart();
            }
            catch (Exception error)
            {
                System.Diagnostics.Trace.WriteLine(error);
                ShowUpdateStatus("Couldn't update. Retry.");
                UpdateButton.IsEnabled = true;
            }
            return;
        }
        await CheckUpdatesAsync();
    }

    private async Task CheckUpdatesAsync()
    {
        if (busyUpdating || busyTray) return;
        busyUpdating = true;
        UpdateButton.IsEnabled = false;
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
            System.Diagnostics.Trace.WriteLine(error);
            UpdateButton.Content = "Check for updates";
            ShowUpdateStatus("Couldn't check. Retry.");
            _ = ClearUpdateStatusAsync(updateStatusGeneration);
        }
        finally
        {
            busyUpdating = false;
            UpdateButton.IsEnabled = !busyApplying && !busyTray;
        }
    }

    private void ShowUpdateStatus(string text)
    {
        UpdateLabel.Text = text;
        UpdateLabel.Visibility = Visibility.Visible;
    }
    private async Task ClearUpdateStatusAsync(int generation)
    {
        await Task.Delay(5000);
        if (generation == updateStatusGeneration && updates.PendingUpdate is null)
            UpdateLabel.Visibility = Visibility.Collapsed;
    }
}
