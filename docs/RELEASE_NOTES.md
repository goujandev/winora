Winora 0.1.3 for Windows 11 x64 (preview).

- Compact global update checking beside the installed version, with checking,
  current, update-ready, and retry states.
- Optional **Always show app tray icons**, with a small native watcher that
  promotes existing/new tray entries and restores reset visibility preferences.
- Tray automation runs at sign-in and remains active after closing Winora,
  independently of translucency. Turning it off stops enforcement without hiding
  icons. Existing preferences migrate with the new option off.
- Unsupported Windows configurations and registry failures are surfaced in the
  tray setting. Windows-managed or deliberately hidden app icons are excluded
  from any guarantee; the Explorer visibility preference is undocumented.
- Existing taskbar effects, finish previews, and update installation preserved.

The installer bootstraps the .NET 10 Desktop Runtime if it is missing. The native
taskbar engine is downloaded from its official release on first use; missing
Microsoft UI frameworks are provisioned separately. Internet access is required
for these downloads and updates. The preview installer is unsigned.

This preview has not been validated across every Windows 11 build. A separate
running TranslucentTB instance must be closed before enabling Winora effects.

Measured native background working memory was 72–75 MiB on the development PC.
The prototype is functional, but an exceptionally small memory footprint remains
an optimisation target. The settings app exits fully when its window is closed.
