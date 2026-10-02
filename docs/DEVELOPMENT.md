# Local development

From the repository root, run:

```powershell
.\dev.cmd
```

You can also double-click `dev.cmd` in Explorer. The equivalent full command is:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Run-Dev.ps1
```

This incrementally builds and opens **Winora Dev**. It uses the existing local
.NET SDK if available, otherwise the .NET 10 SDK on PATH. No installer, packaging
tool, native compiler, GitHub release, or GitHub credentials are needed. The
execution-policy override applies only to this PowerShell process.

Edit the WPF XAML or C# files, then run the same command again. It closes only
this workspace's previous dev window, allows up to 30 seconds for graceful
cleanup, and then waits up to 15 seconds for its headless helpers to exit before
rebuilding and reopening the app. It never force-stops a helper. Production can
remain installed and running. The usual build takes
seconds after dependencies are restored. Changes require rebuilding/reopening;
this workflow does not add a hot-reload tool.

For an IDE, select the **Development** configuration. With an SDK on PATH, the
equivalent direct command is:

```powershell
dotnet run --project src/Winora/Winora.csproj -c Development
```

Use **Development** explicitly. Debug and Release retain the existing production
behavior. Dev binaries are under `src/Winora/bin/Development/.../Winora.Dev.exe`;
production binaries remain under their existing Debug/Release directories.

## Isolation and scope

- The window title, header, and executable identify **Winora Dev**. An amber
  **PREVIEW ONLY** badge stays visible.
- Dev preferences, preview configuration, and error logs use
  `%LocalAppData%\WinoraDev`, separate from `%LocalAppData%\Winora`.
- App settings such as Dark mode and Start Winora with Windows persist only in
  dev preferences. The single startup toggle is in Settings and defaults on;
  in Dev it previews the preference without registering Windows startup.
  The production app's settings stay independent.
- Dev uses its own single-instance mutex and bypasses all Velopack hooks and
  release feeds, including environment-based update-source overrides.
- Taskbar finishes, the startup checkbox, and tray automation can be exercised
  as preview preferences. Selections save immediately to dev files. Ordinary Dev never downloads or
  launches the native engine/helper, changes registry/startup entries, installs
  frameworks, or stops production processes.
- Windows taskbars and tray visibility are shared per desktop/user. Real effect
  testing cannot be fully isolated on the same desktop. Use a separate Windows
  account or VM for real OS-effect integration tests. The dev app intentionally
  labels its controls as previews instead of pretending it applied an effect.

## Tiling test

The normal dev launcher previews tiling without moving Windows windows. To test
the real optional engine, use:

```powershell
.\dev.cmd -TestTiling
```

The window shows **LIVE TILING TEST** and opens the Tiling page. Turn on
**Automatically tile windows** to begin. This mode uses Winora Dev preferences
and engine files, and keeps taskbar effects, tray changes, startup registration,
and production updates disabled. Window arrangement is shared with your desktop,
so real tiling necessarily moves your open application windows. Closing the test
window stops its tiling engine. An existing independently running window manager
must be stopped before testing. The upstream engine's diagnostic error log is
shared at `%UserProfile%\.glzr\glazewm\errors.log`; its existing configuration is
left alone. Ordinary `dev.cmd` remains the isolated preview.

For placement and resizing tests, leave fullscreen routing off if desired:
the same headless helper still checks ordinary layouts on a 250 ms polling loop.
Open new app windows to verify least-load monitor placement. Visible floating
apps count, minimized apps do not; ties prefer the primary monitor and then
Windows display-number order. Fullscreen-occupied monitors are excluded.

On a landscape monitor, one through four tiles should form a full-area tile,
two equal halves, a left tile with two stacked right tiles, then a 2 × 2 grid.
Portrait monitors transpose that pattern. Resize a split to check neighbouring
reflow and native size limits; open or close a tile to rebuild the default layout.
**Rearrange** resets manual split choices. **Pause** stops layout repairs and
automatic monitor placement until resumed.

Windows that cannot fit their native limits or repeatedly refuse sizing float
with an arrangement status. An automatic lack-of-space fallback can recover
when room becomes available; a user-floated or manually moved/resized window
stays under user control. Native size queries are bounded, and a 160 × 120 pixel
safety minimum prevents unusable tiles. Check actual window frames as well as
the engine state when testing custom-window apps.

To test fullscreen-game routing with at least two monitors:

1. In the live tiling test, enable tiling and leave **Move tiles away from
   fullscreen games** on.
2. For borderless fullscreen, open **Games… → Add game…** and select the game's
   actual executable. Exclusive Direct3D fullscreen games are detected
   automatically. Registered games stay outside the tiling engine, including
   windowed mode, so they keep their chosen monitor when launched.
3. Enter fullscreen on one monitor, then leave fullscreen. Tiles should move to
   another available monitor and return when fullscreen ends.

Normal Dev saves this preference and the game list only as previews; it does not
move application windows. Use `-TestTiling` for the live test.

Winora's own window now participates in tiling. For a custom-window application
that starts floating, use **Apps… → Add app…** to remember its executable. Native
application size limits are respected. Games and standard dialogs take
precedence over this override.

## Checks and production releases

Close Winora Dev before running the isolation checks, since they temporarily
exercise and then restore its dev-only preferences.

```powershell
# Build without opening a window
.\dev.cmd -BuildOnly

# Exercise dev settings and assert production storage/registry are unchanged
dotnet run --project tests/Winora.DevChecks -c Development

# Existing production configuration checks
dotnet run --project tests/Winora.Checks -c Release
```

After inspecting the dev build, use the existing production process: update the
version/release notes, commit, and push a version tag. `scripts/Pack.ps1` still
publishes **Release**, and GitHub Actions still tests/packages/publishes the
normal **Winora** installer. Development creates no release and makes no change
to the installed app. Keep release creation as a separate deliberate step.
