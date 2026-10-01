Winora 0.1.4 for Windows 11 x64 (preview).

- Redesigned Taskbar page with grouped finish and system-tray settings.
- Larger default window, refreshed finish selectors, and a dark scrollbar.
- Existing taskbar effects, tray automation, saved preferences, and global
  update controls preserved.

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
