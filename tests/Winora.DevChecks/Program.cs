using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Winora;

internal static class DevChecks
{
    [STAThread]
    public static int Main()
    {
        var failures = 0;
        void Check(bool value, string name) { Console.WriteLine($"{(value ? "PASS" : "FAIL")} {name}"); if (!value) failures++; }
        if (!AppBuild.IsDevelopment) throw new InvalidOperationException("Refusing to exercise a production build.");
        Check(AppBuild.Name == "Winora Dev" && AppBuild.InstanceMutex != @"Local\Winora.Settings", "Dev identity and instance mutex are distinct");
        Check(!AppBuild.AllowTilingEffects, "Normal Dev does not opt in to managing desktop windows");
        Check(Settings.DirectoryPath.EndsWith("WinoraDev") && !Settings.DirectoryPath.EndsWith("\\Winora"), "Dev data directory is isolated");
        var productionPreferences = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Winora", "preferences.json");
        byte[]? ReadProductionPreferences() => File.Exists(productionPreferences) ? File.ReadAllBytes(productionPreferences) : null;
        var before = ReadProductionPreferences();
        var startupBefore = RegistrySnapshot(@"Software\Microsoft\Windows\CurrentVersion\Run");
        var trayPreferenceBefore = RegistrySnapshot(@"Software\Winora\TrayIcons\Preference");
        var windowsThemeBefore = RegistrySnapshot(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        using var trayRoot = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
        var trayBefore = trayRoot?.GetSubKeyNames().ToDictionary(name => name, name => { using var key = trayRoot.OpenSubKey(name); return key?.GetValue("IsPromoted"); });
        var preferenceBefore = File.Exists(Settings.FilePath) ? File.ReadAllBytes(Settings.FilePath) : null;
        var configPath = Path.Combine(TaskbarService.EngineDirectory, "settings.json");
        var configBefore = File.Exists(configPath) ? File.ReadAllBytes(configPath) : null;
        var tilingConfigPath = Path.Combine(Settings.DirectoryPath, "tiling", "config.yaml");
        var tilingConfigBefore = File.Exists(tilingConfigPath) ? File.ReadAllBytes(tilingConfigPath) : null;
        var existingGlazeConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".glzr", "glazewm", "config.yaml");
        var existingGlazeConfigBefore = File.Exists(existingGlazeConfig) ? File.ReadAllBytes(existingGlazeConfig) : null;
        try
        {
            foreach (var mode in Enum.GetValues<TaskbarMode>()) new TaskbarService().ApplyAsync(mode).GetAwaiter().GetResult();
            StartupService.Synchronize(true);
            StartupService.Synchronize(false);
            StartupService.Remove();
            var startupCalls = 0;
            var startupFailures = FeatureStartup.RestoreAsync(new UserSettings(Mode: TaskbarMode.Acrylic, AlwaysShowTrayIcons: true, TilingEnabled: true),
                _ => { startupCalls++; return Task.CompletedTask; }, () => { startupCalls++; return Task.CompletedTask; },
                _ => { startupCalls++; return Task.CompletedTask; }).GetAwaiter().GetResult();
            Check(startupCalls == 0 && startupFailures.Count == 0, "Dev startup never restores production feature engines");
            TaskbarService.StopAsync().GetAwaiter().GetResult();
            new TrayIconService().EnableAsync().GetAwaiter().GetResult();
            TrayIconService.DisableAsync().GetAwaiter().GetResult();
            var tiling = new TilingService();
            var tilingEnginePresent = File.Exists(TilingService.EngineExecutable);
            var tilingEngineWriteTime = tilingEnginePresent ? File.GetLastWriteTimeUtc(TilingService.EngineExecutable) : (DateTime?)null;
            var tilingProcessesBefore = GlazeProcessIds();
            tiling.EnableAsync(12).GetAwaiter().GetResult();
            tiling.SetGapAsync(20).GetAwaiter().GetResult();
            tiling.TogglePauseAsync().GetAwaiter().GetResult();
            tiling.DisableAsync().GetAwaiter().GetResult();
            Check(File.Exists(tilingConfigPath) && File.ReadAllText(tilingConfigPath) == TilingConfiguration.Build(20)
                && !tiling.IsRunning,
                "Normal Dev can preview and stage gap changes without running a tiling engine");
            Check(File.Exists(TilingService.EngineExecutable) == tilingEnginePresent
                && (!tilingEnginePresent || File.GetLastWriteTimeUtc(TilingService.EngineExecutable) == tilingEngineWriteTime)
                && tilingProcessesBefore.SequenceEqual(GlazeProcessIds()),
                "Normal Dev neither downloads the tiling engine nor starts or stops any GlazeWM process");
            var devPreferences = new UserSettings(Mode: TaskbarMode.Acrylic, AlwaysShowTrayIcons: true,
                StartWinoraWithWindows: false, TilingEnabled: true, TilingGap: 12);
            Settings.Save(devPreferences);
            Check(Settings.Load() == devPreferences, "Dev preferences persist independently");
            var startupPreferences = File.ReadAllBytes(Settings.FilePath);
            var startupConfig = File.ReadAllBytes(configPath);
            var errorPath = Path.Combine(Settings.DirectoryPath, "errors.log");
            var startupErrors = File.Exists(errorPath) ? File.ReadAllBytes(errorPath) : null;
            var startupInfo = new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(typeof(AppBuild).Assembly.Location)!, "Winora.Dev.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startupInfo.ArgumentList.Add("--startup");
            using (var startupProcess = Process.Start(startupInfo) ?? throw new InvalidOperationException("Could not launch Dev startup check."))
            {
                var exited = startupProcess.WaitForExit(5000);
                if (!exited) { startupProcess.Kill(entireProcessTree: true); startupProcess.WaitForExit(); }
                Check(exited && startupProcess.ExitCode == 0
                    && startupProcess.StandardOutput.ReadToEnd().Length == 0 && startupProcess.StandardError.ReadToEnd().Length == 0
                    && startupPreferences.SequenceEqual(File.ReadAllBytes(Settings.FilePath))
                    && startupConfig.SequenceEqual(File.ReadAllBytes(configPath))
                    && JsonSerializer.Serialize(startupErrors) == JsonSerializer.Serialize(File.Exists(errorPath) ? File.ReadAllBytes(errorPath) : null),
                    "The Dev startup entrypoint exits quietly without changing preferences, engine data or error logs");
            }
            Check(!new TaskbarService().IsRunning, "Dev never claims a production taskbar engine");
            var previousSource = Environment.GetEnvironmentVariable("WINORA_UPDATE_SOURCE");
            try
            {
                Environment.SetEnvironmentVariable("WINORA_UPDATE_SOURCE", "https://invalid.example/dev-must-not-connect");
                var updates = new UpdateService();
                Check(!updates.IsConfigured && !updates.IsInstalled && updates.CheckAndDownloadAsync().GetAwaiter().GetResult().Contains("disabled"), "Dev ignores update sources and disables updates");
            }
            finally { Environment.SetEnvironmentVariable("WINORA_UPDATE_SOURCE", previousSource); }
            Check(JsonSerializer.Serialize(before) == JsonSerializer.Serialize(ReadProductionPreferences()), "Production preferences unchanged");
            Check(startupBefore == RegistrySnapshot(@"Software\Microsoft\Windows\CurrentVersion\Run"), "Windows startup entries unchanged");
            Check(trayPreferenceBefore == RegistrySnapshot(@"Software\Winora\TrayIcons\Preference"), "Production tray-helper preference unchanged");
            var trayAfter = trayRoot?.GetSubKeyNames().ToDictionary(name => name, name => { using var key = trayRoot.OpenSubKey(name); return key?.GetValue("IsPromoted"); });
            Check(JsonSerializer.Serialize(trayBefore) == JsonSerializer.Serialize(trayAfter), "Windows tray visibility unchanged");
            var app = new App(); app.InitializeComponent();
            var window = new MainWindow(previewOnly: true);
            Check(window.Title == "Winora Dev" && ((System.Windows.FrameworkElement)window.FindName("DevBadge")).Visibility == System.Windows.Visibility.Visible, "Dev window is visibly labelled");
            var previewPreferences = File.ReadAllBytes(Settings.FilePath);
            ((RadioButton)window.FindName("DefaultMode")).IsChecked = true;
            ((RadioButton)window.FindName("AcrylicMode")).IsChecked = true;
            ((CheckBox)window.FindName("StartupCheckBox")).IsChecked = true;
            ((CheckBox)window.FindName("StartupCheckBox")).IsChecked = false;
            ((CheckBox)window.FindName("TrayIconsCheckBox")).IsChecked = true;
            ((CheckBox)window.FindName("DarkModeCheckBox")).IsChecked = true;
            ((CheckBox)window.FindName("TilingEnabledCheckBox")).IsChecked = true;
            ((Slider)window.FindName("TilingGapSlider")).Value = 24;
            ((CheckBox)window.FindName("FullscreenRoutingCheckBox")).IsChecked = false;
            ((CheckBox)window.FindName("FullscreenRoutingCheckBox")).IsChecked = true;
            Check(previewPreferences.SequenceEqual(File.ReadAllBytes(Settings.FilePath)), "Offscreen preview interactions never save preferences");

            var liveWindow = new MainWindow();
            liveWindow.Dispatcher.BeginInvoke(new Action(() =>
            {
                ((RadioButton)liveWindow.FindName("DefaultMode")).IsChecked = true;
                ((RadioButton)liveWindow.FindName("TransparentMode")).IsChecked = true;
                ((RadioButton)liveWindow.FindName("AcrylicMode")).IsChecked = true;
            }));
            var immediateSaved = PumpUntil(() => Settings.Load().Mode == TaskbarMode.Acrylic && !Settings.Load().StartWinoraWithWindows
                && ((TextBlock)liveWindow.FindName("StatusLabel")).Text == "Preview saved");
            Check(immediateSaved, "Clicking a finish immediately saves the latest dev preference without Apply");
            liveWindow.Dispatcher.BeginInvoke(new Action(() => ((CheckBox)liveWindow.FindName("StartupCheckBox")).IsChecked = true));
            Check(PumpUntil(() => Settings.Load().StartWinoraWithWindows) && Settings.Load().Mode == TaskbarMode.Acrylic,
                "App startup changes save immediately without changing the finish");
            liveWindow.Dispatcher.BeginInvoke(new Action(() => ((CheckBox)liveWindow.FindName("TrayIconsCheckBox")).IsChecked = false));
            Check(PumpUntil(() => !Settings.Load().AlwaysShowTrayIcons)
                && ((FrameworkElement)liveWindow.FindName("TrayPreviewOverflow")).Visibility == Visibility.Visible
                && ((FrameworkElement)liveWindow.FindName("TrayPreviewApps")).Visibility == Visibility.Collapsed,
                "Tray preference and functional preview update immediately");
            ((RadioButton)liveWindow.FindName("TilingNavigation")).IsChecked = true;
            Check(((FrameworkElement)liveWindow.FindName("TilingPage")).Visibility == Visibility.Visible
                && ((FrameworkElement)liveWindow.FindName("TaskbarPage")).Visibility == Visibility.Collapsed
                && ((FrameworkElement)liveWindow.FindName("SettingsPage")).Visibility == Visibility.Collapsed,
                "Tiling has a separate sidebar section without displacing app settings or taskbar controls");
            var tilingToggle = (CheckBox)liveWindow.FindName("TilingEnabledCheckBox");
            var tilingGap = (Slider)liveWindow.FindName("TilingGapSlider");
            Check(tilingToggle.IsChecked == true && tilingGap.Value == 12,
                "Opening the tiling section restores its saved enablement and gap");
            liveWindow.Dispatcher.BeginInvoke(new Action(() => tilingToggle.IsChecked = false));
            Check(PumpUntil(() => !Settings.Load().TilingEnabled)
                && Settings.Load().Mode == TaskbarMode.Acrylic && Settings.Load().StartWinoraWithWindows,
                "Disabling tiling saves immediately and preserves taskbar and global startup choices");
            var tilingRetry = (Button)liveWindow.FindName("RetryTilingButton");
            var tilingStatus = (TextBlock)liveWindow.FindName("TilingStatusLabel");
            var temporaryPreferences = Settings.FilePath + ".tmp";
            var temporaryPreferencesBefore = File.Exists(temporaryPreferences) ? File.ReadAllBytes(temporaryPreferences) : null;
            try
            {
                // Fail only persistence after the preview service succeeds. The
                // retry must retain the requested ON state through UI rollback.
                using (var lockedPreferences = new FileStream(temporaryPreferences, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None))
                {
                    liveWindow.Dispatcher.BeginInvoke(new Action(() => tilingToggle.IsChecked = true));
                    Check(PumpUntil(() => tilingRetry.Visibility == Visibility.Visible && tilingRetry.IsEnabled
                        && tilingToggle.IsEnabled && tilingToggle.IsChecked == false
                        && !Settings.Load().TilingEnabled && tilingStatus.Text.Contains("Couldn’t change tiling")),
                        "A failed tiling save rolls the toggle back and exposes a usable Retry action");
                }
                liveWindow.Dispatcher.BeginInvoke(new Action(() =>
                    tilingRetry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, tilingRetry))));
                Check(PumpUntil(() => tilingToggle.IsChecked == true && tilingToggle.IsEnabled
                    && Settings.Load().TilingEnabled && tilingRetry.Visibility == Visibility.Collapsed),
                    "Retry reapplies the failed enable request instead of the saved disabled state");
            }
            finally { Restore(temporaryPreferences, temporaryPreferencesBefore); }
            var tilingPause = (Button)liveWindow.FindName("TilingPauseButton");
            var tilingRetile = (Button)liveWindow.FindName("TilingRetileButton");
            liveWindow.Dispatcher.BeginInvoke(new Action(() =>
                tilingPause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, tilingPause))));
            Check(PumpUntil(() => Equals(tilingPause.Content, "Resume") && tilingPause.IsEnabled)
                && System.Windows.Automation.AutomationProperties.GetName(tilingPause) == "Resume automatic tiling"
                && !tilingRetile.IsEnabled,
                "Pausing exposes an accessible Resume action and disables Retile until resumed");
            liveWindow.Dispatcher.BeginInvoke(new Action(() =>
                tilingPause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, tilingPause))));
            Check(PumpUntil(() => Equals(tilingPause.Content, "Pause") && tilingPause.IsEnabled)
                && System.Windows.Automation.AutomationProperties.GetName(tilingPause) == "Pause automatic tiling"
                && tilingRetile.IsEnabled,
                "Resuming restores both the accessible Pause action and Retile");
            var gapBefore = ((Border)liveWindow.FindName("TilingPreviewPrimary")).Margin.Right;
            var existingPreferences = Settings.Load();
            liveWindow.Dispatcher.BeginInvoke(new Action(() => tilingGap.Value = 24));
            Check(PumpUntil(() => Settings.Load().TilingGap == 24)
                && ((Border)liveWindow.FindName("TilingPreviewPrimary")).Margin.Right > gapBefore
                && Settings.Load() == (existingPreferences with { TilingGap = 24 }),
                "Changing the tiling gap updates both the preview spacing and only its saved setting");
            Check(!new TilingService().IsRunning,
                "Normal Dev tiling interactions never start a real window-management engine");
            var fullscreenRouting = (CheckBox)liveWindow.FindName("FullscreenRoutingCheckBox");
            Check(fullscreenRouting.IsChecked == true && Settings.Load().MoveTilesForFullscreenGames,
                "Moving tiles away from fullscreen games is enabled by default");
            var beforeFullscreenRouting = Settings.Load();
            liveWindow.Dispatcher.BeginInvoke(new Action(() => fullscreenRouting.IsChecked = false));
            Check(PumpUntil(() => !Settings.Load().MoveTilesForFullscreenGames && fullscreenRouting.IsEnabled)
                && Settings.Load() == (beforeFullscreenRouting with { MoveTilesForFullscreenGames = false }),
                "Disabling fullscreen-game routing saves only its isolated Dev preference");
            liveWindow.Dispatcher.BeginInvoke(new Action(() => fullscreenRouting.IsChecked = true));
            Check(PumpUntil(() => Settings.Load().MoveTilesForFullscreenGames && fullscreenRouting.IsEnabled)
                && Settings.Load() == beforeFullscreenRouting && !new TilingService().IsRunning,
                "Fullscreen-game routing can be restored without starting an engine or changing other preferences");
            var routingTemporaryBefore = File.Exists(temporaryPreferences) ? File.ReadAllBytes(temporaryPreferences) : null;
            try
            {
                using (var lockedPreferences = new FileStream(temporaryPreferences, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None))
                {
                    liveWindow.Dispatcher.BeginInvoke(new Action(() => fullscreenRouting.IsChecked = false));
                    Check(PumpUntil(() => tilingRetry.Visibility == Visibility.Visible && tilingRetry.IsEnabled
                        && fullscreenRouting.IsEnabled && fullscreenRouting.IsChecked == true
                        && Settings.Load().MoveTilesForFullscreenGames && tilingStatus.Text.Contains("fullscreen routing")),
                        "A failed fullscreen-routing save restores its toggle and exposes Retry");
                }
                liveWindow.Dispatcher.BeginInvoke(new Action(() =>
                    tilingRetry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, tilingRetry))));
                Check(PumpUntil(() => fullscreenRouting.IsChecked == false && fullscreenRouting.IsEnabled
                    && !Settings.Load().MoveTilesForFullscreenGames && tilingRetry.Visibility == Visibility.Collapsed)
                    && Settings.Load() == (beforeFullscreenRouting with { MoveTilesForFullscreenGames = false }),
                    "Fullscreen-routing Retry reapplies the failed OFF request while preserving other settings");
            }
            finally { Restore(temporaryPreferences, routingTemporaryBefore); }
            liveWindow.Dispatcher.BeginInvoke(new Action(() => fullscreenRouting.IsChecked = true));
            if (!PumpUntil(() => Settings.Load().MoveTilesForFullscreenGames && fullscreenRouting.IsEnabled))
                throw new InvalidOperationException("Could not restore the fullscreen-routing preference after the retry check.");
            ((RadioButton)liveWindow.FindName("SettingsNavigation")).IsChecked = true;
            Check(((FrameworkElement)liveWindow.FindName("SettingsPage")).Visibility == Visibility.Visible
                && ((FrameworkElement)liveWindow.FindName("TaskbarPage")).Visibility == Visibility.Collapsed
                && ((FrameworkElement)liveWindow.FindName("TilingPage")).Visibility == Visibility.Collapsed,
                "Settings navigation opens the app settings view separately from Taskbar");
            var startupToggle = (CheckBox)liveWindow.FindName("StartupCheckBox");
            Check(IsWithin(startupToggle, (DependencyObject)liveWindow.FindName("SettingsPage"))
                && !IsWithin(startupToggle, (DependencyObject)liveWindow.FindName("TaskbarPage")),
                "The single startup toggle belongs to app Settings rather than Taskbar");
            liveWindow.Dispatcher.BeginInvoke(new Action(() => ((RadioButton)liveWindow.FindName("DefaultMode")).IsChecked = true));
            Check(PumpUntil(() => Settings.Load().Mode == TaskbarMode.Default) && startupToggle.IsEnabled
                && startupToggle.IsChecked == true && Settings.Load().StartWinoraWithWindows,
                "App startup remains enabled and available with the Windows taskbar selected");
            liveWindow.Dispatcher.BeginInvoke(new Action(() => startupToggle.IsChecked = false));
            Check(PumpUntil(() => !Settings.Load().StartWinoraWithWindows) && Settings.Load().Mode == TaskbarMode.Default,
                "App startup can be disabled independently of every feature");
            var taskbarPreferences = Settings.Load();
            var lightInk = ((SolidColorBrush)app.FindResource("Ink")).Color;
            var branding = app.FindResource("WinoraMark");
            var buttonStyle = app.FindResource(typeof(Button));
            liveWindow.Dispatcher.BeginInvoke(new Action(() => ((CheckBox)liveWindow.FindName("DarkModeCheckBox")).IsChecked = true));
            Check(PumpUntil(() => Settings.Load().DarkMode && AppTheme.CurrentIsDark)
                && ((SolidColorBrush)app.FindResource("Ink")).Color != lightInk
                && Settings.Load() == (taskbarPreferences with { DarkMode = true }),
                "Dark mode changes the app palette immediately and saves only the app theme preference");
            Check(ReferenceEquals(app.FindResource("WinoraMark"), branding)
                && ReferenceEquals(app.FindResource(typeof(Button)), buttonStyle),
                "Theme changes preserve the shared branding and control styles");
            var darkInk = ((SolidColorBrush)app.FindResource("Ink")).Color;
            Check(((SolidColorBrush)((TextBlock)liveWindow.FindName("AppNameLabel")).Foreground).Color == darkInk
                && ((SolidColorBrush)((CheckBox)liveWindow.FindName("DarkModeCheckBox")).Foreground).Color == darkInk,
                "Existing header text and settings control update to the dark palette without reopening");
            AppTheme.Apply(false);
            var reopenedWindow = new MainWindow();
            Check(((CheckBox)reopenedWindow.FindName("DarkModeCheckBox")).IsChecked == true
                && AppTheme.CurrentIsDark && ((SolidColorBrush)app.FindResource("Ink")).Color == darkInk,
                "Opening the app restores the saved dark theme and toggle state");
            Check(windowsThemeBefore == RegistrySnapshot(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"),
                "Changing the app theme leaves the Windows theme unchanged");
            ((RadioButton)liveWindow.FindName("TaskbarNavigation")).IsChecked = true;
            Check(((FrameworkElement)liveWindow.FindName("TaskbarPage")).Visibility == Visibility.Visible
                && ((FrameworkElement)liveWindow.FindName("SettingsPage")).Visibility == Visibility.Collapsed
                && ((FrameworkElement)liveWindow.FindName("TilingPage")).Visibility == Visibility.Collapsed,
                "Taskbar navigation returns to the existing controls");
            Check(JsonSerializer.Serialize(before) == JsonSerializer.Serialize(ReadProductionPreferences())
                && startupBefore == RegistrySnapshot(@"Software\Microsoft\Windows\CurrentVersion\Run")
                && trayPreferenceBefore == RegistrySnapshot(@"Software\Winora\TrayIcons\Preference")
                && JsonSerializer.Serialize(existingGlazeConfigBefore) == JsonSerializer.Serialize(File.Exists(existingGlazeConfig) ? File.ReadAllBytes(existingGlazeConfig) : null),
                "Immediate dev UI changes leave production data, Windows preferences and existing GlazeWM configuration unchanged");
        }
        finally
        {
            Restore(Settings.FilePath, preferenceBefore);
            Restore(configPath, configBefore);
            Restore(tilingConfigPath, tilingConfigBefore);
        }
        return failures == 0 ? 0 : 1;
    }
    private static string RegistrySnapshot(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        return JsonSerializer.Serialize(key?.GetValueNames().Order().ToDictionary(name => name, name => key.GetValue(name)));
    }
    private static int[] GlazeProcessIds()
    {
        var ids = new List<int>();
        foreach (var process in Process.GetProcessesByName("glazewm"))
            using (process) ids.Add(process.Id);
        return ids.Order().ToArray();
    }
    private static bool IsWithin(DependencyObject child, DependencyObject ancestor)
    {
        for (DependencyObject? item = child; item is not null; item = LogicalTreeHelper.GetParent(item))
            if (ReferenceEquals(item, ancestor)) return true;
        return false;
    }
    private static void Restore(string path, byte[]? content)
    {
        if (content is null) { if (File.Exists(path)) File.Delete(path); }
        else File.WriteAllBytes(path, content);
    }
    private static bool PumpUntil(Func<bool> condition)
    {
        if (condition()) return true;
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => { if (condition() || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        return condition();
    }
}
