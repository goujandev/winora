Winora 0.1.6 for Windows 11 x64 (beta preview).

- One **Start Winora with Windows** toggle in app Settings, enabled by default.
- Sign-in quietly restores every enabled feature, including taskbar effects and
  tray automation, then exits without opening the settings window.
- Removed the taskbar-specific startup control and migrated legacy per-feature
  startup entries. Selecting the Windows default taskbar keeps app startup enabled.
- Turning startup off leaves the current session's features running.
- Redesigned ribbon logo used consistently in the app, executable, and installer.
- Existing immediate finish selection, tray automation, app Dark mode, update
  controls, and isolated development workflow preserved.

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
