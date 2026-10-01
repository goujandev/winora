# Tiling windows

The optional **Tiling** section uses the unmodified GlazeWM 3.10.1 engine to
automatically arrange ordinary application windows. It starts disabled.
Enabling it downloads the verified engine on demand; it does not run an MSI
installer or register GlazeWM globally.

Change **Window gaps** to adjust spacing immediately. **Pause** leaves the
current arrangement in place and releases tiling control until resumed.
**Rearrange** redraws the layout; resume first if paused. Turn the main toggle
off to stop the engine and restore surviving managed windows to the positions
saved before enabling it. Windows opened afterwards remain where they are.

| Shortcut | Action |
| --- | --- |
| Alt + arrows | Focus a neighbouring window |
| Alt + Shift + arrows | Move a window |
| Alt + Ctrl + arrows | Resize a window |
| Alt + Shift + Space | Switch between tiled and floating |
| Alt + V | Change split direction |
| Alt + Shift + P | Pause / resume |
| Alt + Shift + E | Exit the engine when not paused |

If paused, resume before using the exit shortcut. The Winora toggle and engine
tray menu can stop tiling directly. Winora's window and Windows shell surfaces
are excluded; ordinary dialogs float. There is one tiling workspace per monitor.
This first version does not add virtual desktops, application rules, configurable
shortcuts, or a separate bar.

On production builds, the existing **Start Winora with Windows** preference
restores enabled tiling at sign-in. Closing the production settings window leaves
the engine running. An already-running independent GlazeWM or Komorebi instance
must be exited first; Winora refuses to control a different manager.

Tiling currently requires Windows 11 x64. Applications with unusual window
frames, fixed sizes, or elevated privileges may not arrange normally. Pause,
float an affected window, or disable tiling if an application behaves poorly.
The engine's own tray menu remains available as an independent recovery route.

For local testing, run `.\dev.cmd -TestTiling`, then enable the toggle. The
**LIVE TILING TEST** build keeps preferences and engine files under
`%LocalAppData%\WinoraDev\tiling`; production uses `%LocalAppData%\Winora\tiling`.
Winora passes its own configuration explicitly and does not modify an
independently installed GlazeWM configuration. The upstream engine always appends
diagnostic errors to `%UserProfile%\.glzr\glazewm\errors.log`; that vendor log is
shared, including in test mode. Closing this test window stops its engine.
Real window arrangement affects the current desktop; ordinary
`.\dev.cmd` continues to preview changes without moving windows.

GlazeWM is a separate GPL-3.0 process. See
[third-party notices](../THIRD_PARTY_NOTICES.txt),
[upstream source](https://github.com/glzr-io/glazewm/tree/v3.10.1), and
[development instructions](DEVELOPMENT.md).
