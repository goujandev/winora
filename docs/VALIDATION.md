# Prototype verification

Development PC: Windows 11 25H2, build 26200.9457, x64.

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

The installer is unsigned. First-run downloads of missing shared frameworks
are additional to the installer size. Runtime bootstrap may require Windows
elevation. Background memory reduction is the main remaining architecture target.
