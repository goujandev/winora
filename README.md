# Winora

A Windows 11 desktop customiser in **beta**: C# / WPF settings, separate native
taskbar and optional tiling engines, and a Velopack installer and updater.

## Try it

Download `Winora-win-Setup.exe` from
[GitHub Releases](https://github.com/goujandev/winora/releases).
Windows 11 **x64** is required. The preview installer is unsigned.

Select Default, Transparent, or Acrylic to change the finish immediately.
The settings window exits completely when closed. The separate native engine
maintains the taskbar effect and keeps its original TranslucentTB tray menu as
an independent exit route. Default stops the engine and restores Windows.
**Settings**, at the bottom of the sidebar, contains **Start Winora with Windows**
and **Dark mode**. Startup is on by default and restores every enabled feature
at sign-in without opening the settings window. One startup entry controls all
features; turning it off leaves the current session's effects running. Dark mode
changes Winora's interface immediately without changing Windows.

**Always show app tray icons** is a separate optional preference. A small native
helper keeps existing and newly registered app icons visible, restores reset
visibility, and resumes at sign-in when **Start Winora with Windows** is enabled.
It stays active with the settings window closed. Disabling
it stops enforcement and leaves current icon visibility for Windows to manage.
The control reports unavailable Windows configurations and failures. See
[tray behavior, limitations, and validation](docs/TRAY_ICONS.md).

Winora checks for updates and downloads them when the settings app opens.
The version and **Check for updates** action are in the application footer.
Select **Restart to update** there to install a downloaded release immediately;
otherwise the staged update is applied when you next reopen Winora. There is no
always-running .NET updater or scheduled update task in this prototype. Startup
restoration exits after launching the selected native features. A PC
that is offline or has the app closed receives an update after it next opens
Winora online. Updates preserve preferences under `%LocalAppData%\Winora`.

## Footprint and limits

The production package is framework-dependent to keep the installer small.
Setup installs the shared .NET 10 Desktop Runtime if missing; that download and
shared runtime footprint are additional to Winora's installer size.

The taskbar engine is downloaded directly from the official TranslucentTB
2026.2 release on first use (1.7 MB download; about 3.5 MB extracted). Missing
Microsoft UI frameworks are installed per user as signed packages. They are
normally present on Windows 11: missing WinUI needs a 19 MB NuGet download;
missing VCLibs currently needs a 93 MB Microsoft archive to extract a 0.9 MB
package. Those temporary archives are removed after successful installation.
These worst-case prerequisite downloads are a remaining optimisation target.

Initial measurements on Windows 11 25H2 build 26200 showed about **72–75 MiB working
set / 58 MiB private memory** for the native engine, and **0.16–0.47% of one CPU core**
over a ten-second sample after startup. These are single-machine measurements,
not guarantees. This prototype demonstrates functionality; its background
memory usage does **not yet meet an exceptionally small footprint target**.

Modern taskbar effects depend on Explorer internals and may need fixes after
Windows updates. The native engine handles Explorer recreation, but a real
Explorer-restart scenario and additional Windows builds still require manual
validation. Another independently installed TranslucentTB instance must exit
before Winora enables its effects. ARM64 is not supported in this prototype.

## Build

Version 0.1.9 includes optional, experimental automatic window tiling. Enable it
on the **Tiling** page. New windows go to the least crowded available monitor;
balanced layouts, native-size checks and fullscreen-game routing run while tiling
is enabled. It starts off and downloads its verified engine on first use. See
[tiling controls and limitations](docs/TILING.md).

To test changes locally, run `.\dev.cmd -TestTiling` in **Winora Dev**, then enable
the toggle on the Tiling page. Closing that test window stops its engine.

Install the .NET 10 SDK on Windows and the pinned packaging tool:

```powershell
dotnet tool install --global vpk --version 1.2.0
dotnet run --project tests/Winora.Checks -c Release
./scripts/Pack.ps1 -Version 0.1.0 -RepositoryUrl https://github.com/goujandev/winora
```

The pack script also recognises workspace-local tools in `.tools/dotnet` and
`.tools/vpk-1.2.0`. It writes installers and update feed assets to
`artifacts/releases`. Pass `-SelfContained` to bundle the runtime instead.

For integration checks:

```powershell
# Temporarily applies effects, measures the engine, captures only the taskbar,
# and requests restoration in finally. Run with no existing taskbar engine.
dotnet run --project tests/Winora.Checks -c Release -- --engine artifacts/validation

# Installs an isolated test app, updates 0.1.0 to 0.1.1, verifies its version,
# and uninstalls it. SelfContained isolates the updater test from runtime setup.
./scripts/Test-Updates.ps1 -SelfContained
```

The update check avoids Velopack 1.2.0's upstream `EXE_ARGS` setup parser bug by
running its probe after installation. Every pack operation publishes to a new
directory to prevent stale binaries from an earlier build entering a release.

## Publish updates

For fast, isolated local UI testing, see [Winora Dev](docs/DEVELOPMENT.md). Launch
it with `.\dev.cmd` (or double-click `dev.cmd` in Explorer).
It uses separate preferences and previews changes without modifying Windows or
the installed production app. No release is needed.

The GitHub Actions workflow builds and checks changes to main and pull
requests. A tag such as `v0.1.1` runs the installed-update test and publishes
the installer, full update package, and feed JSON as a GitHub Release. Failed
checks prevent publication. Increment the tag/version for each release.
The feed uses public GitHub Releases; no GitHub credential is shipped in the app.

```powershell
git tag v0.1.1
git push origin main v0.1.1
```

Local update testing can use `WINORA_UPDATE_SOURCE` as a folder or HTTPS feed.
`WINORA_STARTUP_PROBE` is a test-only process environment variable used to avoid
opening UI during installer verification. Neither is registered system-wide.

After two releases are published, `./scripts/Test-GitHubUpdates.ps1` verifies an
actual update through the public GitHub feed on a PC with no existing Winora
installation. See [validation notes](docs/VALIDATION.md) for results and remaining
manual checks.

## Dependencies

See [third-party notices](THIRD_PARTY_NOTICES.txt) and
[Velopack's licence](licenses/Velopack.txt). TranslucentTB is an unmodified,
separate upstream executable downloaded directly on demand. Winora's installer
does not contain or link its GPLv3 code. Optional tiling similarly downloads and
runs the unmodified GlazeWM engine as a separate process. Winora's own licensing
has not yet been selected.
