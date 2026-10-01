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
this workspace's previous dev window, rebuilds the changed files, and reopens
the app. Production can remain installed and running. The usual build takes
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
- App settings such as Dark mode change the dev interface immediately and persist
  only in its own preferences. The production app's appearance stays independent.
- Dev uses its own single-instance mutex and bypasses all Velopack hooks and
  release feeds, including environment-based update-source overrides.
- Taskbar finishes, the startup checkbox, and tray automation can be exercised
  as preview preferences. Selections save immediately to dev files. Dev never downloads or
  launches the native engine/helper, changes registry/startup entries, installs
  frameworks, or stops production processes.
- Windows taskbars and tray visibility are shared per desktop/user. Real effect
  testing cannot be fully isolated on the same desktop. Use a separate Windows
  account or VM for real OS-effect integration tests. The dev app intentionally
  labels its controls as previews instead of pretending it applied an effect.

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
