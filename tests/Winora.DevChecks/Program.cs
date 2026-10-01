using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
        Check(Settings.DirectoryPath.EndsWith("WinoraDev") && !Settings.DirectoryPath.EndsWith("\\Winora"), "Dev data directory is isolated");
        var productionPreferences = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Winora", "preferences.json");
        byte[]? ReadProductionPreferences() => File.Exists(productionPreferences) ? File.ReadAllBytes(productionPreferences) : null;
        var before = ReadProductionPreferences();
        var startupBefore = RegistrySnapshot(@"Software\Microsoft\Windows\CurrentVersion\Run");
        var trayPreferenceBefore = RegistrySnapshot(@"Software\Winora\TrayIcons\Preference");
        using var trayRoot = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
        var trayBefore = trayRoot?.GetSubKeyNames().ToDictionary(name => name, name => { using var key = trayRoot.OpenSubKey(name); return key?.GetValue("IsPromoted"); });
        var preferenceBefore = File.Exists(Settings.FilePath) ? File.ReadAllBytes(Settings.FilePath) : null;
        var configPath = Path.Combine(TaskbarService.EngineDirectory, "settings.json");
        var configBefore = File.Exists(configPath) ? File.ReadAllBytes(configPath) : null;
        try
        {
            foreach (var mode in Enum.GetValues<TaskbarMode>()) new TaskbarService().ApplyAsync(mode, true).GetAwaiter().GetResult();
            TaskbarService.SetStartup(true);
            TaskbarService.SetStartup(false);
            TaskbarService.StopAsync().GetAwaiter().GetResult();
            new TrayIconService().EnableAsync().GetAwaiter().GetResult();
            TrayIconService.DisableAsync().GetAwaiter().GetResult();
            Settings.Save(new UserSettings(TaskbarMode.Acrylic, true, true));
            Check(Settings.Load() == new UserSettings(TaskbarMode.Acrylic, true, true), "Dev preferences persist independently");
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
            ((CheckBox)window.FindName("TrayIconsCheckBox")).IsChecked = true;
            Check(previewPreferences.SequenceEqual(File.ReadAllBytes(Settings.FilePath)), "Offscreen preview interactions never save preferences");

            var liveWindow = new MainWindow();
            liveWindow.Dispatcher.BeginInvoke(new Action(() =>
            {
                ((RadioButton)liveWindow.FindName("DefaultMode")).IsChecked = true;
                ((RadioButton)liveWindow.FindName("TransparentMode")).IsChecked = true;
                ((RadioButton)liveWindow.FindName("AcrylicMode")).IsChecked = true;
            }));
            var immediateSaved = PumpUntil(() => Settings.Load().Mode == TaskbarMode.Acrylic && !Settings.Load().StartWithWindows
                && ((TextBlock)liveWindow.FindName("StatusLabel")).Text == "Preview saved");
            Check(immediateSaved, "Clicking a finish immediately saves the latest dev preference without Apply");
            liveWindow.Dispatcher.BeginInvoke(new Action(() => ((CheckBox)liveWindow.FindName("StartupCheckBox")).IsChecked = true));
            Check(PumpUntil(() => Settings.Load().StartWithWindows), "Startup changes save immediately without changing the finish");
            liveWindow.Dispatcher.BeginInvoke(new Action(() => ((CheckBox)liveWindow.FindName("TrayIconsCheckBox")).IsChecked = false));
            Check(PumpUntil(() => !Settings.Load().AlwaysShowTrayIcons)
                && ((FrameworkElement)liveWindow.FindName("TrayPreviewOverflow")).Visibility == Visibility.Visible
                && ((FrameworkElement)liveWindow.FindName("TrayPreviewApps")).Visibility == Visibility.Collapsed,
                "Tray preference and functional preview update immediately");
            Check(JsonSerializer.Serialize(before) == JsonSerializer.Serialize(ReadProductionPreferences())
                && startupBefore == RegistrySnapshot(@"Software\Microsoft\Windows\CurrentVersion\Run")
                && trayPreferenceBefore == RegistrySnapshot(@"Software\Winora\TrayIcons\Preference"),
                "Immediate dev UI changes leave production data and Windows startup/tray preferences unchanged");
        }
        finally
        {
            Restore(Settings.FilePath, preferenceBefore);
            Restore(configPath, configBefore);
        }
        return failures == 0 ? 0 : 1;
    }
    private static string RegistrySnapshot(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        return JsonSerializer.Serialize(key?.GetValueNames().Order().ToDictionary(name => name, name => key.GetValue(name)));
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
