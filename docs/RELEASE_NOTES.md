Winora 0.1.9 for Windows 11 x64 (beta preview).

- Tiling recovers automatically from temporary engine connection failures.
- Gap, scaling and usable-area changes preserve manual split choices. Layouts
  try alternate arrangements before moving or floating windows that cannot fit.
- Healthy windows can still be repaired when another window cannot be read.
- Fullscreen routing remembers the original monitor after automatic layout moves
  or size-related floating, while respecting manual changes.
- Saved window positions can be restored after the tiling engine exits. Failed
  restorations remain available for Retry.
- Settings changes and background tiling updates are coordinated. Closing settings
  waits for pending tiling changes, and newly connected monitors have reserved
  workspaces available without restarting the engine.

Tiling remains optional and experimental. Elevated, fixed-size and unusual
applications may not tile reliably. Physical monitor hotplug, live fullscreen
transitions and native position restoration still need hands-on beta testing
for these changes. Existing taskbar effects, tray automation and updates remain
available.

The unsigned installer bootstraps the .NET 10 Desktop Runtime if missing.
Taskbar and tiling engines are downloaded on first use. Internet access is
required for those downloads and updates.
