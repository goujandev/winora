# Prototype verification

Development PC: Windows 11 25H2, build 26200.9457, x64.

## Experimental tiling (development branch)

Fullscreen-game routing verification:

- 102 production checks and 44 isolated Dev checks pass, including 31 routing
  policy scenarios and fullscreen preference failure/Retry behavior.
- A bounded live fixture managed only four disposable tiled windows, one
  fullscreen window explicitly configured as a game, and a floating test
  control. Real everyday application windows were excluded; shortcuts were off.
- The four tiles moved physically from the main monitor to the other two
  monitors, two per monitor. The fullscreen window retained native focus. Tiles
  returned to their original monitor after fullscreen ended. Routing OFF stopped
  its helper; tiling OFF restored the original native rectangles.
- All 13 live checks passed. Production preferences were unchanged, and test
  preferences, configuration, journals and log contents were restored afterward.
- After warmup, a ten-second sample while the simulated game stayed fullscreen
  measured approximately 58 MiB working set, 21 MiB private memory, and 2.97% of
  one CPU core for the routing helper. This is one sample on this PC, not a
  hardware-independent performance guarantee. The helper runs only while both
  tiling and its fullscreen-game preference are enabled.
- The compact tiling controls were inspected in an offscreen dark render.
  Real exclusive-mode games, other graphics APIs, elevated games, anti-cheat,
  sleep/resume and monitor hotplug still need hands-on beta testing. Borderless
  detection uses an explicit game executable choice; automatic exclusive
  Direct3D detection follows the documented Windows notification-state API.

- Release and Development builds compile without warnings or errors.
- Production configuration checks cover opt-in migration, gap persistence and
  bounds, independent feature restoration through the existing global startup
  preference, and monitor-bound workspace configuration.
- A bounded live test used the actual Winora Dev child launcher, verified engine
  download/extraction, and exercised the real service IPC against two disposable
  resizable WPF windows. All other application windows were excluded explicitly;
  test shortcuts were disabled.
- On this three-monitor desktop, both fixtures physically tiled. Pause, resume,
  rearrange, and disable passed. Disable restored the exact original rectangles,
  and both the engine and its recovery watcher exited. Test configuration,
  snapshots, and log contents were restored afterward.
- Normal Dev isolation checks cover the new page and immediate preference/gap
  changes without launching the engine or changing production data, existing
  GlazeWM configuration, Windows startup, tray state, or Windows theme.
- Offscreen Tiling views were inspected in light, dark, and narrow layouts.

This first version still needs hands-on checks with the user's everyday apps,
keyboard shortcuts, elevated windows, monitor changes, and sleep/resume before
a production release. The upstream diagnostic error log is shared; see
[tiling limitations](TILING.md).

## Earlier taskbar and release verification

- Build succeeded with zero warnings and zero errors.
- Eleven checks passed for native taskbar modes, consistent appearance across
  taskbar states, invalid modes, missing/corrupt preferences, and persistence.
- Integration checks started the pinned TranslucentTB engine in transparent
  mode, switched its configuration to acrylic, and stopped it gracefully when
  Default was selected. The settings app was not required to remain running.
- Three ten-second samples measured approximately 72–75 MiB working set,
  58 MiB private memory, and 0.16–0.47% of one CPU core for the native process.
- The self-contained installer test installed 0.1.0, downloaded and applied a
  local-feed update to 0.1.1, verified the installed version, and uninstalled it.
- The framework-dependent installer test bootstrapped Microsoft .NET Desktop
  Runtime 10.0.12, then passed the same complete installed update test.
- The small installer was approximately 6.4 MiB; its full update package was
  approximately 2.1 MiB. The version-only local delta was about 4.7 KiB. Actual
  future update size depends on what changes.
- GitHub Actions independently passed configuration checks, the complete
  installed-update test, and release packaging on a hosted Windows runner.
- Public GitHub feed verification installed the published 0.1.0 installer,
  discovered and downloaded 0.1.1 without an app-side GitHub token, applied it
  automatically at the next launch, and confirmed the installed 0.1.1 app was
  up to date. The temporary test installation was uninstalled afterward.
- The offscreen WPF layout was rendered and visually reviewed.

Taskbar-bound screenshots were captured during integration checks, but they
are not sufficient proof of visual correctness in this environment. A manual
check of the visible taskbar, a real Explorer restart, monitor changes, sleep /
resume, and other Windows 11 builds remains necessary before a production claim.

UI refinement (0.1.2): Release builds passed with no warnings or errors; the
existing eleven configuration checks passed. Offscreen WPF renders were reviewed
at 780 x 650 and 680 x 650, including Default, Transparent, and Acrylic previews.
The installer was packaged with the new multi-resolution application icon.
Window chrome uses WPF WindowChrome and standard system window commands; actual
pointer interactions and Windows Snap behavior still need a desktop smoke check.

The installer is unsigned. First-run downloads of missing shared frameworks
are additional to the installer size. Runtime bootstrap may require Windows
elevation. Background memory reduction is the main remaining architecture target.
