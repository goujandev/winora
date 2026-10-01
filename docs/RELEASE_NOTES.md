Winora preview for Windows 11 x64.

- Windows 11 styled appearance settings.
- Default, transparent, and acrylic taskbar finishes, powered by TranslucentTB.
- Optional taskbar effects at sign-in; the settings window can exit fully.
- Built-in update checks and downloads on app launch, with restart to install.
- Restores the Windows taskbar when Default is applied.

The installer bootstraps the .NET 10 Desktop Runtime if it is missing. The native
taskbar engine is downloaded from its official release on first use; missing
Microsoft UI frameworks are provisioned separately. Internet access is required
for these downloads and updates. The preview installer is unsigned.

This preview has not been validated across every Windows 11 build. A separate
running TranslucentTB instance must be closed before enabling Winora effects.

Measured native background working memory was 72–75 MiB on the development PC.
The prototype is functional, but an exceptionally small memory footprint remains
an optimisation target. The settings app exits fully when its window is closed.
