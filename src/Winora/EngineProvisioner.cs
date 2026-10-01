using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace Winora;

public static class EngineProvisioner
{
    private const string EngineUrl = "https://github.com/TranslucentTB/TranslucentTB/releases/download/2026.2/TranslucentTB-portable-x64.zip";
    private const string EngineHash = "0DBE8E0255C20E131CDE536DCD0AE490D45989A7D360E26D0150DFF1922AC420";
    private const string XamlUrl = "https://api.nuget.org/v3-flatcontainer/microsoft.ui.xaml/2.8.7/microsoft.ui.xaml.2.8.7.nupkg";
    private const string XamlHash = "79207B10FE243EB1A8DCDC29BECEBA2F472F145ED31F147F7AE3F43B0659C9F7";
    private const string FrameworksUrl = "https://github.com/microsoft/winget-cli/releases/download/v1.29.380/DesktopAppInstaller_Dependencies.zip";
    private const string FrameworksHash = "BA875AFE9D190F61218985AC0292A99D1DB710BF93E13C68944CA9D89F0D82D1";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(3) };
    private static readonly Dictionary<string, string> EngineFiles = new()
    {
        ["ExplorerHooks.dll"] = "5AB9CA00B41A0676C94430C43A5DE106ACEF2ADE5C42E6C2EE45912DE7A718D6",
        ["ExplorerTAP.dll"] = "C48F1A06168ACD7839269DDE0D15CCF5D09C702512B94DD6BA9A05DF1B153DC2",
        ["ProgramLog.dll"] = "0AC96AC53F5575369AD7B2F12EE2BD272E77438DE8D041EC8AA6D4B900D2F701",
        ["resources.pri"] = "6B6766492E75AA80BFC802265F10D3C32ED7757BE2DC9FFA96738B0E159AEB3C",
        ["TranslucentTB.exe"] = "F933F5BF70405E13FEADBCC53883F2676B5A8A1DAE214B52C0FE0971C583A0FF",
        ["Xaml.dll"] = "473CC6ED8E9A1F37DBFEE0C0B4EF04DE9BBE5B8872B7F75D7CB2C9E50576F13C"
    };

    public static async Task EnsureAsync(IProgress<string>? progress)
    {
        if (AppBuild.IsDevelopment) throw new InvalidOperationException("Native effects are disabled in Winora Dev.");
        if (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64)
            throw new PlatformNotSupportedException("This prototype supports Windows 11 on x64 PCs.");
        var needs = await RunPowerShellAsync("""
            $ErrorActionPreference = 'Stop'
            function HasPackage($name, $minimum) {
                return [bool](Get-AppxPackage -Name $name | Where-Object { $_.Architecture -eq 'X64' -and [version]$_.Version -ge [version]$minimum })
            }
            if (-not (HasPackage 'Microsoft.VCLibs.140.00' '14.0.33519.0')) { 'vclibs' }
            if (-not (HasPackage 'Microsoft.UI.Xaml.2.8' '8.2501.31001.0')) { 'xaml' }
            """);
        var cache = Path.Combine(Settings.DirectoryPath, "downloads");
        Directory.CreateDirectory(cache);
        if (needs.Contains("vclibs", StringComparison.Ordinal))
        {
            progress?.Report("Installing a missing Microsoft framework (93 MB download)…");
            var zip = Path.Combine(cache, "microsoft-frameworks.zip");
            await DownloadVerifiedAsync(FrameworksUrl, FrameworksHash, zip);
            var appx = Path.Combine(cache, "Microsoft.VCLibs.x64.appx");
            ExtractOne(zip, "x64/Microsoft.VCLibs.140.00_14.0.33519.0_x64.appx", appx);
            await InstallFrameworkAsync(appx);
            File.Delete(zip); File.Delete(appx);
        }
        if (needs.Contains("xaml", StringComparison.Ordinal))
        {
            progress?.Report("Installing the missing Microsoft UI framework (19 MB download)…");
            var zip = Path.Combine(cache, "microsoft-ui-xaml.nupkg");
            await DownloadVerifiedAsync(XamlUrl, XamlHash, zip);
            var appx = Path.Combine(cache, "Microsoft.UI.Xaml.x64.appx");
            ExtractOne(zip, "tools/AppX/x64/Release/Microsoft.UI.Xaml.2.8.appx", appx);
            await InstallFrameworkAsync(appx);
            File.Delete(zip); File.Delete(appx);
        }
        var needsEngine = false;
        foreach (var (name, hash) in EngineFiles)
        {
            var path = Path.Combine(TaskbarService.EngineDirectory, name);
            if (!File.Exists(path) || !await HasHashAsync(path, hash)) { needsEngine = true; break; }
        }
        if (needsEngine)
        {
            if (new TaskbarService().IsRunning) throw new InvalidOperationException("Exit the TranslucentTB tray icon, then apply the appearance again to repair its files.");
            progress?.Report("Downloading the native taskbar engine (1.7 MB)…");
            var zip = Path.Combine(cache, "TranslucentTB-2026.2.zip");
            await DownloadVerifiedAsync(EngineUrl, EngineHash, zip);
            Directory.CreateDirectory(TaskbarService.EngineDirectory);
            ZipFile.ExtractToDirectory(zip, TaskbarService.EngineDirectory, overwriteFiles: true);
            File.Delete(zip);
        }
        foreach (var (name, hash) in EngineFiles)
        {
            var path = Path.Combine(TaskbarService.EngineDirectory, name);
            if (!File.Exists(path) || !await HasHashAsync(path, hash))
                throw new InvalidOperationException("The taskbar engine failed its integrity check. Try applying the appearance again to repair it.");
        }
    }

    public static async Task DownloadVerifiedAsync(string url, string hash, string path)
    {
        if (File.Exists(path) && await HasHashAsync(path, hash)) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        await using (var output = File.Create(path))
        await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
            await input.CopyToAsync(output, timeout.Token);
        if (!await HasHashAsync(path, hash)) { File.Delete(path); throw new InvalidOperationException("The download failed its integrity check. Please try again."); }
    }
    private static async Task<bool> HasHashAsync(string path, string expected)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)) == expected;
    }
    private static void ExtractOne(string archivePath, string entryName, string destination)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.GetEntry(entryName) ?? throw new InvalidDataException("The Microsoft dependency package is incomplete.");
        entry.ExtractToFile(destination, overwrite: true);
    }
    private static Task<string> InstallFrameworkAsync(string appxPath) => RunPowerShellAsync(
        "$ErrorActionPreference = 'Stop'; Add-AppxPackage -Path $env:WINORA_FRAMEWORK_PATH -ErrorAction Stop", appxPath);
    private static async Task<string> RunPowerShellAsync(string script, string? frameworkPath = null)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand"); start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        if (frameworkPath is not null) start.Environment["WINORA_FRAMEWORK_PATH"] = frameworkPath;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Couldn’t check Microsoft framework prerequisites.");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(); throw new TimeoutException("Windows took too long to prepare the required Microsoft frameworks."); }
        if (process.ExitCode != 0) throw new InvalidOperationException($"Windows couldn’t prepare the Microsoft frameworks: {await error}");
        return await output;
    }
}
