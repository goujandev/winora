Winora 0.1.8 for Windows 11 x64 (beta preview).

- Maximising a managed window while tiling is active now automatically restores
  its tiled position. Previously floating windows return to floating.
- Fullscreen games and videos retain their existing behaviour. Pausing tiling
  suspends automatic placement and repair.

Tiling remains optional and experimental. Elevated, fixed-size and unusual
applications may not tile reliably. Taskbar finishes, tray automation, app
settings and updates remain available.

The unsigned installer bootstraps the .NET 10 Desktop Runtime if missing.
Taskbar and tiling engines are downloaded on first use. Internet access is
required for those downloads and updates.
