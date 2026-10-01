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
    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack's install/update hooks must run before any app or engine work.
        VelopackApp.Build().OnBeforeUninstallFastCallback(_ =>
        {
            if (Velopack.Locators.VelopackLocator.Current.AppId != "Winora") return;
            TaskbarService.SetStartup(false);
            TaskbarService.StopAsync().GetAwaiter().GetResult();
        }).Run();
        if (args.Length == 0 && Environment.GetEnvironmentVariable("WINORA_STARTUP_PROBE") is { Length: > 0 } probePath)
            args = ["--update-probe", probePath];
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                throw new PlatformNotSupportedException("Winora requires Windows 11.");
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
            if (args.Length == 2 && args[0] == "--render-preview")
            {
                var window = new MainWindow(previewOnly: true) { Width = 960, Height = 800 };
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(960, 800));
                content.Arrange(new Rect(0, 0, 960, 800));
                content.UpdateLayout();
                var bitmap = new RenderTargetBitmap(960, 800, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(args[1]);
                encoder.Save(stream);
                return 0;
            }
            using var singleInstance = new Mutex(true, @"Local\Winora.Settings", out var firstInstance);
            if (!firstInstance) return 0;
            return app.Run(new MainWindow());
        }
        catch (Exception error)
        {
            try
            {
                Directory.CreateDirectory(Settings.DirectoryPath);
                File.AppendAllText(Path.Combine(Settings.DirectoryPath, "errors.log"),
                    $"{DateTimeOffset.Now:O} {error}\n");
            }
            catch (Exception loggingError) when (loggingError is IOException or UnauthorizedAccessException) { }
            if (!args.Contains("--render-preview") && !args.Contains("--update-probe"))
                MessageBox.Show(error.Message, "Winora", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
}
