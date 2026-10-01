Winora 0.1.5 for Windows 11 x64 (beta preview).

- New light interface, original ribbon icon, and live desktop/taskbar preview.
- Finish and startup selections apply immediately, with queued changes,
  failure recovery, and retry. The Apply button has been removed.
- Compact sidebar with Taskbar and app Settings; an icon rail in narrow windows.
- Saved app-only Dark mode, available in Settings at the bottom of the sidebar.
- Existing taskbar effects, persistent tray automation, saved preferences, and
  global update controls preserved.
- Isolated Winora Dev workflow for local UI testing without changing the installed
  production app or Windows preferences.

Tray automation uses an undocumented Explorer visibility preference. Windows
configurations without it are reported as unsupported; Windows-managed and
deliberately hidden app icons cannot be guaranteed visible.

The installer bootstraps the .NET 10 Desktop Runtime if it is missing. The native
taskbar engine is downloaded from its official release on first use; missing
Microsoft UI frameworks are provisioned separately. Internet access is required
for these downloads and updates. The preview installer is unsigned.

This preview has not been validated across every Windows 11 build. A separate
running TranslucentTB instance must be closed before enabling Winora effects.

Measured native background working memory was 72–75 MiB on the development PC.
The prototype is functional, but an exceptionally small memory footprint remains
an optimisation target. The settings app exits fully when its window is closed.
