using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Velopack;

namespace Winora;

public static class Program
{
    private const string StartupMutex = @"Local\Winora.Startup";

    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack's install/update hooks must run before any app or engine work.
        if (!AppBuild.IsDevelopment) VelopackApp.Build().OnBeforeUninstallFastCallback(_ =>
        {
            if (Velopack.Locators.VelopackLocator.Current.AppId != "Winora") return;
            StartupService.Remove();
            TaskbarService.StopAsync().GetAwaiter().GetResult();
            TrayIconService.DisableAsync().GetAwaiter().GetResult();
            new TilingService().DisableAsync().GetAwaiter().GetResult();
        }).Run();
        if (!AppBuild.IsDevelopment && args.Length == 0 && Environment.GetEnvironmentVariable("WINORA_STARTUP_PROBE") is { Length: > 0 } probePath)
            args = ["--update-probe", probePath];
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                throw new PlatformNotSupportedException("Winora requires Windows 11.");
            if (args.Contains("--startup")) return RestoreFeaturesAtSignIn();
            if (AppBuild.IsDevelopment && args.Contains("--test-tiling") && !args.Contains("--render-preview"))
                AppBuild.EnableTilingTest();
            if (args.Contains("--launch-tiling")) return TilingService.LaunchEngine();
            if (args.Length >= 2 && args[0] == "--update-probe")
            {
                var updates = new UpdateService();
                var message = updates.CheckAndDownloadAsync().GetAwaiter().GetResult();
                File.WriteAllText(args[1], JsonSerializer.Serialize(new
                {
                    Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3),
                    updates.IsInstalled, HasUpdate = updates.PendingUpdate is not null, Message = message
                }));
                if (args.Contains("--apply-update") && updates.PendingUpdate is not null) updates.ApplyAndExit();
                return 0;
            }
            var app = new App();
            app.InitializeComponent();
            if (args.Length >= 2 && args[0] == "--render-preview")
            {
                var width = args.Length >= 5 ? int.Parse(args[3]) : 1080;
                var height = args.Length >= 5 ? int.Parse(args[4]) : 720;
                var window = new MainWindow(previewOnly: true) { Width = width, Height = height };
                if (args.Length >= 3)
                {
                    var mode = Enum.Parse<TaskbarMode>(args[2]);
                    if (mode == TaskbarMode.Default) window.DefaultMode.IsChecked = true;
                    else if (mode == TaskbarMode.Acrylic) window.AcrylicMode.IsChecked = true;
                }
                // Screenshot scenarios stay behind the no-side-effects preview guard.
                if (args.Contains("--dark")) window.DarkModeCheckBox.IsChecked = true;
                if (args.Contains("--settings")) window.SettingsNavigation.IsChecked = true;
                if (args.Contains("--tiling")) window.TilingNavigation.IsChecked = true;
                if (args.Contains("--tiling-enabled")) window.TilingEnabledCheckBox.IsChecked = true;
                if (args.Contains("--tray")) window.TrayIconsCheckBox.IsChecked = true;
                if (args.Contains("--checking"))
                {
                    window.ActivityLabel.Text = "Changing finish…";
                    window.ActivityLabel.Visibility = Visibility.Visible;
                }
                if (args.Contains("--error"))
                {
                    window.StatusLabel.Text = "Couldn’t change finish. Windows prevented the taskbar change.";
                    window.StatusLabel.Tag = "Error";
                    window.RetryButton.Visibility = Visibility.Visible;
                }
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(width, height));
                content.Arrange(new Rect(0, 0, width, height));
                content.UpdateLayout();
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(args[1]);
                encoder.Save(stream);
                return 0;
            }
            using var singleInstance = new Mutex(false, AppBuild.InstanceMutex);
            if (!TryAcquire(singleInstance)) return 0;
            try
            {
                var window = new MainWindow();
                if (AppBuild.IsTilingTest) window.TilingNavigation.IsChecked = true;
                return app.Run(window);
            }
            finally { singleInstance.ReleaseMutex(); }
        }
        catch (Exception error)
        {
            LogError(error);
            if (!args.Contains("--render-preview") && !args.Contains("--update-probe") && !args.Contains("--startup") && !args.Contains("--launch-tiling"))
                MessageBox.Show(error.Message, AppBuild.Name, MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    private static int RestoreFeaturesAtSignIn()
    {
        if (AppBuild.IsDevelopment) return 0;
        using var restoring = new Mutex(false, StartupMutex);
        if (!TryAcquire(restoring)) return 0;
        try
        {
            // A manually opened window restores its own features. If it opens during
            // this pass, its async load waits for this separate mutex without hiding UI.
            using var settingsWindow = new Mutex(false, AppBuild.InstanceMutex);
            if (!TryAcquire(settingsWindow)) return 0;
            settingsWindow.ReleaseMutex();
            var failures = FeatureStartup.RestoreAsync(Settings.Load()).GetAwaiter().GetResult();
            foreach (var error in failures) LogError(error);
            return failures.Count == 0 ? 0 : 1;
        }
        finally { restoring.ReleaseMutex(); }
    }

    internal static Task WaitForStartupAsync()
    {
        if (AppBuild.IsDevelopment) return Task.CompletedTask;
        return Task.Run(() =>
        {
            if (!Mutex.TryOpenExisting(StartupMutex, out var restoring)) return;
            using (restoring)
            {
                try { restoring.WaitOne(); }
                catch (AbandonedMutexException) { }
                restoring.ReleaseMutex();
            }
        });
    }

    private static bool TryAcquire(Mutex mutex)
    {
        try { return mutex.WaitOne(0); }
        catch (AbandonedMutexException) { return true; }
    }

    private static void LogError(Exception error)
    {
        try
        {
            Directory.CreateDirectory(Settings.DirectoryPath);
            File.AppendAllText(Path.Combine(Settings.DirectoryPath, "errors.log"),
                $"{DateTimeOffset.Now:O} {error}\n");
        }
        catch (Exception loggingError) when (loggingError is IOException or UnauthorizedAccessException) { }
    }
}
