using System.IO;
using System.Text.Json;
using Velopack;
using Velopack.Sources;

namespace Winora;

public sealed record ReleaseConfiguration(string? RepositoryUrl);

public sealed class UpdateService
{
    private UpdateManager? manager;
    public UpdateInfo? PendingUpdate { get; private set; }
    public bool IsConfigured => manager is not null;
    public bool IsInstalled => manager?.IsInstalled == true;

    public UpdateService()
    {
        var localSource = Environment.GetEnvironmentVariable("WINORA_UPDATE_SOURCE");
        if (!string.IsNullOrWhiteSpace(localSource))
        {
            manager = new UpdateManager(localSource);
            return;
        }
        var configPath = Path.Combine(AppContext.BaseDirectory, "release.json");
        if (!File.Exists(configPath)) return;
        var config = JsonSerializer.Deserialize<ReleaseConfiguration>(File.ReadAllText(configPath));
        if (Uri.TryCreate(config?.RepositoryUrl, UriKind.Absolute, out var uri)
            && uri.Scheme == "https" && uri.Host == "github.com" && uri.Segments.Length == 3)
            manager = new UpdateManager(new GithubSource(uri.ToString().TrimEnd('/'), null, false));
    }

    public async Task<string> CheckAndDownloadAsync()
    {
        if (manager is null) return "The release feed hasn’t been configured yet.";
        if (!manager.IsInstalled) return "Update checks are available in the installed app.";
        if (PendingUpdate is not null) return "An update is ready. Restart Winora to install it.";
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var update = await manager.CheckForUpdatesAsync();
        if (update is null) return "You’re up to date.";
        await manager.DownloadUpdatesAsync(update, cancelToken: cancellation.Token);
        PendingUpdate = update;
        return "An update is ready. Restart Winora to install it.";
    }

    public void ApplyAndRestart()
    {
        if (manager is null || PendingUpdate is null)
            throw new InvalidOperationException("No update is ready to install.");
        manager.ApplyUpdatesAndRestart(PendingUpdate);
    }

    public void ApplyAndExit()
    {
        if (manager is null || PendingUpdate is null) throw new InvalidOperationException("No update is ready to install.");
        manager.ApplyUpdatesAndExit(PendingUpdate);
    }
}
