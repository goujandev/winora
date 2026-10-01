# Interface direction

The workspace centers on a live taskbar preview, three finish choices, and two
setting rows. Winora currently has one customization section, so a permanent
navigation rail adds no useful navigation. The version and updater stay in
application chrome. There are no decorative subtitles or instructional paragraphs.

The light canvas uses cool whites, sea-glass teal, opaque dark text, and one
original folded-surface wallpaper. Glass is concentrated in finish controls and
the preview. Most content stays on a stable surface. The ribbon W mark is an
original vector, shared by the app header, SVG and multi-resolution Windows icon.
Regenerate the icon with `scripts/Build-Icon.ps1` after editing `Assets/Brand.xaml`
and keep the matching SVG in sync.

References studied before implementation:

- [Apple Materials](https://developer.apple.com/design/human-interface-guidelines/materials):
  a distinct material layer for controls; preserve readable content beneath it.
- [Fluent 2 Layout](https://fluent2.microsoft.design/layout): proximity and consistent
  spacing create hierarchy without wrapping every group in a card.
- [Fluent 2 Motion](https://fluent2.microsoft.design/motion): state changes should feel
  responsive; motion should have a purpose and respect reduced motion.
- [Lively Wallpaper](https://github.com/lively-community/lively): visual results deserve
  priority in a desktop customization app.
- [Auto Dark Mode](https://github.com/AutoDarkMode/Windows-Auto-Night-Mode) and
  [its interface examples](https://autodarkmode.org/): focused automation with compact
  settings and explicit states.
- [FluentHub](https://github.com/0x5bfa/FluentHub): integrated native window chrome.

These references informed the interaction and material principles; their artwork
and components were not copied. The interface remains native WPF with no added
framework, font download, bitmap wallpaper, or component dependency.

Finish and startup changes apply immediately through one serialized queue. The
preview responds at once; completion confirms the saved state. Newer requests
replace pending requests, failures roll back to the last committed choice, and
Retry repeats the failed request. Tray changes retain their existing persistent
automation and rollback behavior. Update installation waits until mutations finish.

Radio buttons retain standard keyboard and screen-reader semantics. Toggles,
window controls and actions have visible keyboard focus. Status announcements are
polite. Hover and preview transitions honor Windows client-area animation settings.
The isolated Development configuration preserves its existing no-Windows-changes
behavior; use `dev.cmd` to inspect the design before publishing a release.

Offscreen visual checks use `Winora.Dev.exe --render-preview <path> <finish>
<width> <height>`, with optional `--tray`, `--checking`, or `--error` scenarios.
They never save preferences or run Windows effects. Normal, narrow, loading,
error, and tray-enabled previews were inspected during this redesign.
