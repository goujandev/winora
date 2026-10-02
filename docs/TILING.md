# Tiling windows

The optional **Tiling** section uses the unmodified GlazeWM 3.10.1 engine to
automatically arrange ordinary application windows. It starts disabled.
Enabling it downloads the verified engine on demand; it does not run an MSI
installer or register GlazeWM globally.

Change **Window gaps** to adjust spacing immediately. **Pause** leaves the
current arrangement in place and releases tiling control until resumed.
**Rearrange** resets the layout to its balanced default; resume first if paused. Turn the main toggle
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
tray menu can stop tiling directly. Winora tiles alongside other applications;
Windows shell surfaces are excluded and ordinary dialogs float. There is one
tiling workspace per monitor. This version does not add virtual desktops,
configurable shortcuts, or a separate bar.

On production builds, the existing **Start Winora with Windows** preference
restores enabled tiling at sign-in. Closing the production settings window leaves
the engine running. An already-running independent GlazeWM or Komorebi instance
must be exited first; Winora refuses to control a different manager.

Tiling currently requires Windows 11 x64. Applications with unusual window
frames, fixed sizes, or elevated privileges may not arrange normally. Pause,
float an affected window, or disable tiling if an application behaves poorly.
The engine's own tray menu remains available as an independent recovery route.

## Placement and resizing

New tiled windows go to the available monitor with the fewest visible application
windows, including floating apps. Minimized and cloaked windows do not count.
Ties prefer the primary monitor, then Windows display-number order. Fullscreen
apps reserve their monitor. Existing windows stay on their current monitor unless
fullscreen routing or insufficient space requires a move.

On landscape monitors, the default arrangement is:

| Windows | Layout |
| --- | --- |
| 1 | Full usable area |
| 2 | Equal left and right halves |
| 3 | One left tile, two stacked right tiles |
| 4 | A 2 × 2 grid; the fourth window splits the left tile |

Portrait monitors transpose these layouts. Larger sets use balanced rows or
columns. Opening or closing a tile rebuilds the default arrangement; otherwise,
manual split sizes, direction and window order are retained. Resizing a tile
reflows its neighbours into the remaining space. **Rearrange** clears manual
split choices.

Winora checks the engine tree and actual window frames on a 250 ms polling loop,
including apps that change their own size after opening. Repairs wait during
interactive dragging/resizing and stop while paused. App minimum and maximum
tracking sizes are queried with a bounded timeout; a 160 × 120 pixel safety
minimum also prevents custom frames collapsing into specks.

If a tile cannot fit its native limits, Winora first tries another available
monitor. If none can fit it, or the app repeatedly refuses its requested size,
it floats and the status identifies an arrangement limitation. A window floated
automatically for lack of space can rejoin tiling when it fits. Windows floated
by the user remain floating; manually moving or resizing an automatic fallback
also takes precedence over recovery. Some fixed-size or elevated apps still
cannot be tiled reliably.

**Apps… → Add app…** remembers applications with custom or fixed-size frames
that would otherwise start floating. Their eligible windows join the tiling
layout immediately and on future launches. Standard dialogs and picture-in-picture
windows keep their existing behavior. Native size restrictions still apply;
Winora cannot make an application resize its contents if it refuses to resize.
Game exclusions take precedence over custom-app tiling rules.

## Fullscreen games

**Move tiles away from fullscreen games** is enabled by default when tiling is
on. After a game settles into fullscreen, Winora distributes background tiles
across the other available monitors, favouring monitors with fewer tiled apps.
New tiles on the occupied monitor are handled too. Floating, hidden, minimised,
dragged and focused windows remain untouched. With one monitor, or no available
destination, the existing windows stay behind the game.

Windows positively reports exclusive Direct3D fullscreen through
[SHQueryUserNotificationState](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ne-shellapi-query_user_notification_state).
For borderless games, choose **Games… → Add game…** and select the game's actual
executable, rather than its launcher. Other graphics APIs may also require this
selection. Registered games stay outside tiling in every window mode, so the
tiling engine cannot move them to its currently focused monitor during launch.
Fullscreen background redistribution still follows their actual monitor.
Full-monitor borderless client surfaces are detected even when a registered game
retains maximized/title-bar style flags. Adding a game reloads the rules for
already managed windows; removing an exclusion requires reopening that game
before its windows can join tiling again.

Fullscreen browser/video windows reserve their monitor but do not
trigger redistribution unless you explicitly add their application or they use
exclusive Direct3D themselves. Windows reports a graphics mode, not a game label.

When fullscreen ends, surviving background tiles return to their original
physical monitors once available. Moving an app to another monitor/workspace,
or switching it out of tiling, takes precedence over automatic restoration.
The engine rebuilds the tiling layout; the previous split order is not guaranteed.
Focused apps are deferred to avoid interrupting what you are using. For games
excluded from GlazeWM, one background app may also remain behind because the
engine still considers it focused; Winora reports this limitation.

Pausing tiling pauses redistribution. Switching the fullscreen preference off
returns eligible background tiles, even if the game is still fullscreen, and
keeps ordinary layout checking active. Focused tiles can remain on their current monitor; the status
reports that. Turning tiling off restores the usual saved pre-tiling positions.

One headless Winora process handles both layout checking and fullscreen routing
whenever its owned tiling engine is running, including when the fullscreen
preference is off. It shares the existing runtime, keeps its journal/status in
Winora's own tiling data directory, and continues after closing the production
settings window. Helper failures expose **Retry**.
Ordinary Dev preview never starts this process; live Dev closing stops it along
with its test engine.

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
