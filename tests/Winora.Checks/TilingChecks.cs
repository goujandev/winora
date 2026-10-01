using System.Text.RegularExpressions;
using Winora;

internal static class TilingChecks
{
    public static void Run(Action<bool, string> check)
    {
        foreach (var gap in new[] { 0, 8, 32 })
        {
            var configuration = TilingConfiguration.Build(gap);
            var innerGap = Regex.Match(configuration, @"(?m)^\s*inner_gap:\s*['""]?(\d+)px['""]?\s*$");
            var outerGaps = new[] { "top", "right", "bottom", "left" }
                .Select(side => Regex.Match(configuration, $@"(?m)^\s*{side}:\s*['""]?(\d+)px['""]?\s*$"));
            check(innerGap.Success && int.Parse(innerGap.Groups[1].Value) == gap
                && outerGaps.All(match => match.Success && int.Parse(match.Groups[1].Value) == gap),
                $"A {gap}px gap configures both window spacing and every screen edge");
        }

        foreach (var gap in new[] { -1, 33 })
        {
            try
            {
                TilingConfiguration.Build(gap);
                check(false, "Out-of-range gaps cannot reach the native tiling configuration");
            }
            catch (ArgumentOutOfRangeException)
            {
                check(true, "Out-of-range gaps cannot reach the native tiling configuration");
            }
        }

        var config = TilingConfiguration.Build(8);
        check(Regex.IsMatch(config, @"(?m)^\s*startup_commands:\s*\[\s*\]\s*$")
            && Regex.IsMatch(config, @"(?m)^\s*shutdown_commands:\s*\[\s*\]\s*$")
            && !config.Contains("shell-exec", StringComparison.OrdinalIgnoreCase),
            "The tiling engine cannot launch bars, scripts or other apps from Winora's configuration");
        check(!config.Contains("^Winora", StringComparison.Ordinal)
            && config.Contains("SearchHost", StringComparison.Ordinal),
            "Winora tiles with ordinary apps while Windows shell surfaces remain excluded");
        var gamePath = @"C:\Games\Sample.Game.exe";
        var appPath = @"C:\Apps\Custom+App.exe";
        var rules = TilingConfiguration.Build(8, 3, [gamePath, gamePath.ToUpperInvariant()], [appPath, gamePath]);
        check(rules.Contains("window_process: { regex: '(?i)^Sample$' }", StringComparison.Ordinal)
            && rules.IndexOf("commands: ['ignore']", StringComparison.Ordinal) < rules.IndexOf("commands: ['set-tiling']", StringComparison.Ordinal),
            "Registered games are excluded before a custom-app tiling override can relocate them");
        check(rules.Contains("(?i)^Custom\\+App$", StringComparison.Ordinal)
            && Regex.Matches(rules, @"commands: \['ignore'\]").Count == 2,
            "Executable matching escapes punctuation and handles GlazeWM's first-dot process names");
        check(rules.IndexOf("commands: ['set-tiling']", StringComparison.Ordinal) < rules.IndexOf("commands: ['set-floating", StringComparison.Ordinal),
            "Custom-window app overrides retain floating standard dialogs and copy dialogs");
        check(!TilingConfiguration.Build(8, excludedGameExecutables: ["relative.exe", @"C:\Games\invalid.txt"])
            .Contains("(?i)", StringComparison.Ordinal),
            "Invalid executable selections cannot generate game-exclusion rules");
        check(config.Contains("wm-toggle-pause", StringComparison.Ordinal)
            && config.Contains("alt+shift+p", StringComparison.Ordinal)
            && config.Contains("wm-exit", StringComparison.Ordinal)
            && config.Contains("alt+shift+e", StringComparison.Ordinal),
            "Pause and exit shortcuts provide a direct escape from automatic tiling");
        check(!config.Contains("--workspace", StringComparison.Ordinal)
            && Regex.IsMatch(config, @"(?m)^\s*show_all_in_taskbar:\s*true\s*$"),
            "The first tiling version keeps all windows discoverable and adds no hidden-workspace shortcuts");

        var workspaces = Section(config, "workspaces");
        check(Regex.Matches(workspaces, @"(?m)^\s*-\s*name:").Count == 1
            && Regex.IsMatch(workspaces, @"(?m)^\s*bind_to_monitor:\s*0\s*$"),
            "A single monitor uses one visible workspace without hiding other application windows");
        workspaces = Section(TilingConfiguration.Build(8, monitorCount: 3), "workspaces");
        check(Regex.Matches(workspaces, @"(?m)^\s*-\s*name:").Count == 3
            && new[] { 0, 1, 2 }.All(monitor => Regex.IsMatch(workspaces, $@"(?m)^\s*bind_to_monitor:\s*{monitor}\s*$")),
            "Each monitor receives its own bound workspace for independent tiling");
        foreach (var monitorCount in new[] { 0, 65 })
        {
            try
            {
                TilingConfiguration.Build(8, monitorCount);
                check(false, "Invalid monitor counts cannot generate an unusable workspace configuration");
            }
            catch (ArgumentOutOfRangeException)
            {
                check(true, "Invalid monitor counts cannot generate an unusable workspace configuration");
            }
        }
    }

    private static string Section(string yaml, string name)
    {
        var match = Regex.Match(yaml, $@"(?m)^{Regex.Escape(name)}:\s*$");
        if (!match.Success) return "";
        var start = match.Index + match.Length;
        var next = Regex.Match(yaml[start..], @"(?m)^\w[^\r\n]*:");
        return next.Success ? yaml.Substring(start, next.Index) : yaml[start..];
    }
}
