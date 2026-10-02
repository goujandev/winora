Winora 0.1.7 for Windows 11 x64 (beta preview).

- Added optional **Tiling**, disabled by default. New tiled windows use the least
  crowded available monitor; ties prefer the main monitor, then display order.
- Landscape layouts use one full window, two side by side, three with a large
  left tile and two stacked right tiles, then a 2 × 2 grid. Portrait displays
  transpose these patterns; larger sets use balanced rows and columns.
- Continuous native-frame checks repair apps that resize themselves. Resizing
  respects native minimum sizes and reflows neighbours. Valid manual split sizes,
  directions and ordering persist until the tile count changes or **Rearrange**.
- Fullscreen games disperse background tiles across other available monitors and
  return them after fullscreen ends. Add borderless games through **Games…** using
  their actual executable. Registered games keep their chosen monitor in every
  window mode. Fullscreen browser/video windows reserve their monitor without
  triggering game-only redistribution.
- **Apps…** remembers custom-frame applications that otherwise start floating.
  Winora's own window also participates in tiling. Restrictive apps report an
  arrangement limitation and can remain floating.
- **Pause** suspends placement and repair. Turning tiling off restores surviving
  managed windows to their saved pre-tiling positions. The existing global startup
  preference restores enabled tiling at sign-in; closing production settings
  leaves it running.
- Taskbar finishes, tray automation, app settings and updates remain available.
  The separate Winora Dev workflow continues to isolate preferences and updates.

Tiling downloads the verified, unmodified GlazeWM 3.10.1 engine on first use.
Its single Winora helper handles both layout checks and fullscreen routing.
Another independently running GlazeWM or Komorebi instance must be exited first.
Elevated, fixed-size and unusual applications may not tile reliably; pause,
float the affected window or disable tiling when needed. Monitor hotplug,
sleep/resume and additional Windows builds still need hands-on beta testing.

Tray automation uses an undocumented Explorer visibility preference. Unsupported
configurations are reported; Windows-managed and deliberately hidden app icons
cannot be guaranteed visible.

The unsigned installer bootstraps the .NET 10 Desktop Runtime if missing.
Taskbar and tiling engines are downloaded on first use; missing Microsoft UI
frameworks are provisioned separately. These downloads and updates need internet
access. See the repository's validation notes for measured footprint and coverage.
