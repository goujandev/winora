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
    private TaskbarMode selectedMode;
    private bool busyUpdating;
    private bool busyApplying;

    public MainWindow(bool previewOnly = false)
    {
        this.previewOnly = previewOnly;
        InitializeComponent();
        VersionLabel.Text = $"Version {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";
        selectedMode = previewOnly ? TaskbarMode.Transparent : Settings.Load().Mode;
        StartupCheckBox.IsChecked = !previewOnly && Settings.Load().StartWithWindows;
        switch (selectedMode)
        {
            case TaskbarMode.Transparent: TransparentMode.IsChecked = true; break;
            case TaskbarMode.Acrylic: AcrylicMode.IsChecked = true; break;
            default: DefaultMode.IsChecked = true; break;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (previewOnly) return;
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
        StatusLabel.Text = "Applying appearance…";
        try
        {
            var mode = selectedMode;
            var startup = StartupCheckBox.IsChecked == true && mode != TaskbarMode.Default;
            await taskbar.ApplyAsync(mode, startup, new Progress<string>(text => StatusLabel.Text = text));
            Settings.Save(new UserSettings(mode, startup));
            if (mode == TaskbarMode.Default) StartupCheckBox.IsChecked = false;
            StatusLabel.Text = mode == TaskbarMode.Default
                ? "Windows default restored."
                : $"{mode} applied. You can close this window.";
        }
        catch (Exception error)
        {
            StatusLabel.Text = $"Couldn’t apply this appearance: {error.Message}";
        }
        finally { busyApplying = false; ApplyButton.IsEnabled = true; UpdateButton.IsEnabled = !busyUpdating; }
    }

    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        if (busyApplying) return;
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
                UpdateLabel.Text = $"Couldn’t install the update: {error.Message}";
                UpdateButton.IsEnabled = true;
            }
            return;
        }
        await CheckUpdatesAsync();
    }

    private async Task CheckUpdatesAsync()
    {
        if (busyUpdating) return;
        busyUpdating = true;
        UpdateButton.IsEnabled = false;
        UpdateLabel.Text = "Checking for updates…";
        try
        {
            UpdateLabel.Text = await updates.CheckAndDownloadAsync();
            if (updates.PendingUpdate is not null) UpdateButton.Content = "Restart to update";
        }
        catch (Exception error)
        {
            UpdateLabel.Text = $"Couldn’t check for updates. Try again later. {error.Message}";
        }
        finally
        {
            busyUpdating = false;
            UpdateButton.IsEnabled = !busyApplying;
        }
    }
}
