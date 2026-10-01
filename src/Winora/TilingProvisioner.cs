using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Winora;

public static class TilingProvisioner
{
    public const string EngineVersion = "3.10.1";
    public const string PackageUrl = "https://github.com/glzr-io/glazewm/releases/download/v3.10.1/standalone-glazewm-v3.10.1-x64.msi";
    public const string PackageHash = "AD4122DD4420690E502E7DF3CE8D1E4772BF1246CC815DB0DD2AAAC0E4DC583F";
    private const string EngineHash = "F5AE12FB6EADDD70E3D1575229A345C255FD0A5083DF67FA8D36EC9B1D2D8F77";
    private const string WatcherHash = "19FA3756C94C8406EA7144486ECABF85AB4B710092446A6A9EF9D061C0076EE6";
    private const string LicenseUrl = "https://raw.githubusercontent.com/glzr-io/glazewm/v3.10.1/LICENSE.md";
    private const string LicenseHash = "3972DC9744F6499F0F9B2DBF76696F2AE7AD8AF9B23DDE66D6AF86C9DFB36986";
    private static readonly SemaphoreSlim Provisioning = new(1, 1);

    public static async Task EnsureAsync(IProgress<string>? progress = null)
    {
        if (!AppBuild.AllowTilingEffects) throw new InvalidOperationException("Real tiling is disabled in this development preview.");
        if (RuntimeInformation.OSArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Tiling currently supports Windows 11 on x64 PCs.");
        await Provisioning.WaitAsync();
        try
        {
            var watcher = Path.Combine(TilingService.EngineDirectory, "glazewm-watcher.exe");
            var license = Path.Combine(TilingService.EngineDirectory, "LICENSE.md");
            if (await HasHashAsync(TilingService.EngineExecutable, EngineHash) &&
                await HasHashAsync(watcher, WatcherHash) && await HasHashAsync(license, LicenseHash)) return;
            if (new TilingService().IsRunning)
                throw new InvalidOperationException("Turn tiling off before repairing its engine files.");
            var cache = Path.Combine(Settings.DirectoryPath, "downloads");
            Directory.CreateDirectory(cache);
            var package = Path.Combine(cache, "GlazeWM-3.10.1-x64.msi");
            progress?.Report("Downloading the tiling engine (5 MB)…");
            await EngineProvisioner.DownloadVerifiedAsync(PackageUrl, PackageHash, package);
            var staging = Path.Combine(cache, "tiling-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                var cabinet = Path.Combine(staging, "engine.cab");
                // Read the signed, pinned MSI as a database only. No installer,
                // custom actions, registration, PATH changes, or elevation.
                ExtractCabinet(package, cabinet);
                await ExpandFileAsync(cabinet, "Custom_RootExe", staging);
                await ExpandFileAsync(cabinet, "Custom_RootWatcherExe", staging);
                var executable = Path.Combine(staging, "Custom_RootExe");
                var watcherSource = Path.Combine(staging, "Custom_RootWatcherExe");
                if (!await HasHashAsync(executable, EngineHash) || !await HasHashAsync(watcherSource, WatcherHash))
                    throw new InvalidDataException("The tiling engine failed its integrity check. Try enabling it again.");
                var licenseSource = Path.Combine(staging, "LICENSE.md");
                await EngineProvisioner.DownloadVerifiedAsync(LicenseUrl, LicenseHash, licenseSource);
                Directory.CreateDirectory(TilingService.EngineDirectory);
                File.Copy(executable, TilingService.EngineExecutable, overwrite: true);
                File.Copy(watcherSource, watcher, overwrite: true);
                File.Copy(licenseSource, license, overwrite: true);
                await File.WriteAllTextAsync(Path.Combine(TilingService.EngineDirectory, "SOURCE.txt"),
                    "GlazeWM v3.10.1, unmodified upstream binaries. GPL-3.0.\n" +
                    "Source: https://github.com/glzr-io/glazewm/tree/v3.10.1\n" +
                    "Package: " + PackageUrl + "\nSHA256: " + PackageHash + "\n");
            }
            finally
            {
                // This directory is created above under our own downloads cache.
                if (Path.GetDirectoryName(Path.GetFullPath(staging)) == Path.GetFullPath(cache))
                    Directory.Delete(staging, recursive: true);
            }
            File.Delete(package);
        }
        finally { Provisioning.Release(); }
    }

    private static async Task<bool> HasHashAsync(string path, string expected)
    {
        if (!File.Exists(path)) return false;
        await using var input = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(input)) == expected;
    }

    private static async Task ExpandFileAsync(string cabinet, string identifier, string destination)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "expand.exe");
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-F:" + identifier);
        start.ArgumentList.Add(cabinet);
        start.ArgumentList.Add(destination);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows couldn’t unpack the tiling engine.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        { process.Kill(); throw new TimeoutException("Windows took too long to unpack the tiling engine."); }
        _ = await output;
        if (process.ExitCode != 0) throw new InvalidOperationException("Windows couldn’t unpack the tiling engine: " + await error);
    }

    private static void ExtractCabinet(string package, string destination)
    {
        CheckMsi(MsiOpenDatabase(package, 0, out var database));
        try
        {
            CheckMsi(MsiDatabaseOpenView(database, "SELECT `Data` FROM `_Streams` WHERE `Name` = 'cab1.cab'", out var view));
            try
            {
                CheckMsi(MsiViewExecute(view, 0));
                CheckMsi(MsiViewFetch(view, out var record));
                try
                {
                    using var output = File.Create(destination);
                    var buffer = new byte[65536];
                    while (true)
                    {
                        var length = (uint)buffer.Length;
                        CheckMsi(MsiRecordReadStream(record, 1, buffer, ref length));
                        if (length == 0) break;
                        output.Write(buffer, 0, checked((int)length));
                    }
                }
                finally { _ = MsiCloseHandle(record); }
            }
            finally { _ = MsiCloseHandle(view); }
        }
        finally { _ = MsiCloseHandle(database); }
    }

    private static void CheckMsi(uint result)
    { if (result != 0) throw new InvalidDataException($"Couldn’t read the tiling engine package (Windows code {result})."); }

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiOpenDatabaseW")]
    private static extern uint MsiOpenDatabase(string path, nint persistence, out uint database);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiDatabaseOpenViewW")]
    private static extern uint MsiDatabaseOpenView(uint database, string query, out uint view);
    [DllImport("msi.dll")] private static extern uint MsiViewExecute(uint view, uint record);
    [DllImport("msi.dll")] private static extern uint MsiViewFetch(uint view, out uint record);
    [DllImport("msi.dll")] private static extern uint MsiRecordReadStream(uint record, uint field, byte[] buffer, ref uint length);
    [DllImport("msi.dll")] private static extern uint MsiCloseHandle(uint handle);
}
