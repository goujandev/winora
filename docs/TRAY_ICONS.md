# App tray visibility

Winora's optional **Always show app tray icons** preference uses the Windows 11
per-user `HKCU\Control Panel\NotifyIconSettings\<entry>\IsPromoted` DWORD. A value
of 1 promotes a registered app icon out of the overflow menu. This is an
undocumented Explorer preference, not a supported public shell API. Winora
checks whether the preference root exists before enabling the feature; Windows
configurations without it show an unavailable message and disable the control.

The separate native `Winora.TrayAgent.exe` watches the root and its descendants
using [RegNotifyChangeKeyValue](https://learn.microsoft.com/en-us/windows/win32/api/winreg/nf-winreg-regnotifychangekeyvalue).
It promotes existing entries, newly registered entries, and entries whose
visibility is reset or removed. It handles replacement of the root and reports
registry errors instead of silently treating a failed scan as success. It
blocks on notifications while idle, coalesces change bursts, and has no app list,
Explorer injection, scheduled PowerShell loop, or dependence on the settings UI.

Enabling installs a per-user `Winora.TrayIcons` sign-in entry and starts the
helper. This is independent of the translucency startup option and works with
Default selected. The helper stays active after the settings window closes.
Its executable is copied to a versioned data directory so a settings-app update
can replace application files safely; opening the updated app migrates the
helper. Uninstalling stops it and removes its startup entry. Turning the option
off stops enforcement; already promoted icons remain visible until changed in
Windows Settings. Turning it off does not hide icons as a side effect.

The preference is off by default and existing settings files migrate with it
off. App icons must actually be registered and active. It cannot launch apps,
override applications that deliberately hide/remove their own icons, or control
Windows-managed system and temporary indicators. Microsoft documents that
[Shell_NotifyIcon](https://learn.microsoft.com/en-us/windows/win32/shell/notification-area)
registers application icons; it does not provide a supported API for promoting
every other app's icon. Explorer may change its private preference mechanism in
future Windows releases. Support is capability checked, but visual behavior
cannot be guaranteed across untested shell replacements or future builds.

Validation on Windows 11 build 26200:

- Isolated watcher tests covered existing/new entries, reset/deleted values,
  replacement of the root, duplicate instances, and graceful disable.
- A real test icon registered with Shell_NotifyIcon was promoted. Its rectangle,
  returned by Shell_NotifyIconGetRect, was inside the actual taskbar bounds.
- Resetting the real icon's IsPromoted value was corrected automatically.
- Native activation and the sign-in registry entry were verified. The original
  icon visibility values were restored and the test icon removed afterward.
- The native helper is approximately 22 KiB. One three-second idle sample showed
  approximately 7.4 MiB working set and zero measured CPU time.

Run `scripts/Test-TrayAgent.ps1` for the isolated test. After building the native
helper, run `dotnet run --project tests/Winora.Checks -c Release -- --tray-live`
for the opt-in real-shell test on an interactive desktop with tray automation
disabled. The test restores original promotion values in a finally block.
