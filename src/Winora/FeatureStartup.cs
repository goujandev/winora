namespace Winora;

/// <summary>Restores enabled features at sign-in without opening the settings window.</summary>
public static class FeatureStartup
{
    public static Task<IReadOnlyList<Exception>> RestoreAsync(UserSettings settings) => RestoreAsync(
        settings,
        mode => new TaskbarService().ApplyAsync(mode),
        () => new TrayIconService().EnableAsync(),
        gap => new TilingService().EnableAsync(gap));

    public static async Task<IReadOnlyList<Exception>> RestoreAsync(UserSettings settings,
        Func<TaskbarMode, Task> applyTaskbar, Func<Task> enableTray, Func<int, Task>? enableTiling = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(applyTaskbar);
        ArgumentNullException.ThrowIfNull(enableTray);
        var failures = new List<Exception>();
        if (AppBuild.IsDevelopment || !settings.StartWinoraWithWindows) return failures;
        // Features are independent: a taskbar-engine problem must not prevent tray automation.
        if (settings.Mode != TaskbarMode.Default)
        {
            try { await applyTaskbar(settings.Mode); }
            catch (Exception error) { failures.Add(new InvalidOperationException("Winora could not restore taskbar effects at sign-in.", error)); }
        }
        if (settings.AlwaysShowTrayIcons)
        {
            try { await enableTray(); }
            catch (Exception error) { failures.Add(new InvalidOperationException("Winora could not restore tray automation at sign-in.", error)); }
        }
        if (settings.TilingEnabled)
        {
            try { await (enableTiling ?? (gap => new TilingService().EnableAsync(gap)))(settings.TilingGap); }
            catch (Exception error) { failures.Add(new InvalidOperationException("Winora could not restore window tiling at sign-in.", error)); }
        }
        return failures;
    }
}
