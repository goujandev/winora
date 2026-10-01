using System.Globalization;
using System.Text;
using System.IO;
using System.Text.RegularExpressions;

namespace Winora;

public static class TilingConfiguration
{
    public const int MinimumGap = 0;
    public const int MaximumGap = 32;

    // GlazeWM v3.10.1 configuration. Keep every managed monitor on its own
    // workspace, with no workspace-switch bindings or external commands.
    public static string Build(int gap, int monitorCount = 1, string[]? excludedGameExecutables = null,
        string[]? tiledAppExecutables = null)
    {
        if (gap is < MinimumGap or > MaximumGap) throw new ArgumentOutOfRangeException(nameof(gap));
        if (monitorCount is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(monitorCount));
        var spacing = gap.ToString(CultureInfo.InvariantCulture);
        var config = new StringBuilder($$"""
            general:
              startup_commands: []
              shutdown_commands: []
              config_reload_commands: []
              focus_follows_cursor: false
              toggle_workspace_on_refocus: false
              cursor_jump:
                enabled: false
              hide_method: 'cloak'
              show_all_in_taskbar: true
            gaps:
              scale_with_dpi: true
              inner_gap: '{{spacing}}px'
              outer_gap:
                top: '{{spacing}}px'
                right: '{{spacing}}px'
                bottom: '{{spacing}}px'
                left: '{{spacing}}px'
            window_effects:
              focused_window:
                border: { enabled: false }
                hide_title_bar: { enabled: false }
                corner_style: { enabled: false }
                transparency: { enabled: false }
              other_windows:
                border: { enabled: false }
                hide_title_bar: { enabled: false }
                corner_style: { enabled: false }
                transparency: { enabled: false }
            window_behavior:
              initial_state: 'tiling'
              state_defaults:
                floating:
                  centered: false
                  shown_on_top: false
                fullscreen:
                  maximized: true
                  shown_on_top: false
            workspaces:
            """);
        for (var monitor = 0; monitor < monitorCount; monitor++)
        {
            config.Append('\n').Append("  - name: 'monitor-").Append(monitor + 1).Append("'\n")
                .Append("    bind_to_monitor: ").Append(monitor);
        }
        config.Append("\nwindow_rules:\n");
        AppendApplicationRule(config, "ignore", excludedGameExecutables);
        config.Append("""
              - commands: ['ignore']
                match:
                  - window_process: { regex: '^(SearchHost|SearchApp|StartMenuExperienceHost|ShellExperienceHost|ScreenClippingHost|LockApp|zebar|Zebar)$' }
                  - window_title: { regex: '[Pp]icture.in.[Pp]icture' }
                    window_class: { regex: 'Chrome_WidgetWin_1|MozillaDialogClass' }
            """);
        config.Append('\n');
        AppendApplicationRule(config, "set-tiling", tiledAppExecutables);
        config.Append("""
              - commands: ['set-floating --centered=false']
                match:
                  - window_class: { equals: '#32770' }
                  - window_class: { equals: 'OperationStatusWindow' }
            binding_modes: []
            keybindings:
              - commands: ['focus --direction left']
                bindings: ['alt+left']
              - commands: ['focus --direction right']
                bindings: ['alt+right']
              - commands: ['focus --direction up']
                bindings: ['alt+up']
              - commands: ['focus --direction down']
                bindings: ['alt+down']
              - commands: ['move --direction left']
                bindings: ['alt+shift+left']
              - commands: ['move --direction right']
                bindings: ['alt+shift+right']
              - commands: ['move --direction up']
                bindings: ['alt+shift+up']
              - commands: ['move --direction down']
                bindings: ['alt+shift+down']
              - commands: ['resize --width -2%']
                bindings: ['alt+ctrl+left']
              - commands: ['resize --width +2%']
                bindings: ['alt+ctrl+right']
              - commands: ['resize --height +2%']
                bindings: ['alt+ctrl+up']
              - commands: ['resize --height -2%']
                bindings: ['alt+ctrl+down']
              - commands: ['toggle-floating --centered=false']
                bindings: ['alt+shift+space']
              - commands: ['toggle-tiling-direction']
                bindings: ['alt+v']
              - commands: ['wm-toggle-pause']
                bindings: ['alt+shift+p']
              - commands: ['wm-exit']
                bindings: ['alt+shift+e']
            """);
        return config.Append('\n').ToString();
    }

    private static void AppendApplicationRule(StringBuilder config, string command, string[]? executables)
    {
        var names = (executables ?? []).Where(path => !string.IsNullOrWhiteSpace(path)
            && Path.IsPathFullyQualified(path) && string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            // GlazeWM 3.10.1 exposes the part before the first dot as processName.
            .Select(path => Path.GetFileName(path).Split('.')[0]).Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length == 0) return;
        config.Append("  - commands: ['").Append(command).Append("']\n    match:\n");
        foreach (var name in names)
            config.Append("      - window_process: { regex: '(?i)^")
                .Append(Regex.Escape(name).Replace("'", "''", StringComparison.Ordinal)).Append("$' }\n");
    }
}
