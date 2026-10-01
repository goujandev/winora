# Winora

A Windows 11 appearance customiser prototype: C# / WPF settings, a separate
native TranslucentTB taskbar engine, and a Velopack installer and updater.

## Try it

Download `Winora-win-Setup.exe` from
[GitHub Releases](https://github.com/goujandev/winora/releases).
Windows 11 **x64** is required. The preview installer is unsigned.

Choose Default, Transparent, or Acrylic, then select **Apply appearance**.
The settings window exits completely when closed. The separate native engine
maintains the taskbar effect and keeps its original TranslucentTB tray menu as
an independent exit route. Default stops the engine and restores Windows.
**Start taskbar effects with Windows** is optional and off initially.

Winora checks for updates and downloads them when the settings app opens.
Select **Restart to update** to install a downloaded release. There is no
always-running .NET updater or scheduled update task in this prototype. A PC
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
does not contain or link its GPLv3 code. Winora's own licensing has not yet been
selected.
