# Scout interface

Scout opens as a 720 × 76 px search field when the query is empty. Typing opens
the results panel; Down or Tab opens suggestions and scope filters. Clearing the
query returns to the compact field unless a group or workspace edit is active.
The search field opens near the upper third of the screen and keeps room below for
the expanded results panel.
The panel keeps up to six results visible before scrolling and stays within the
current monitor's work area. Existing launch, preview, group, and action
shortcuts work in both states.

## Visual system

Shared brushes in `src/Scout.App/App.xaml` use dynamic resources for XAML and
mutable brushes for controls created in code. Scout follows the Windows app
light/dark setting, including changes while it runs. Segoe UI Variable Text and
Display, Fluent icons, and restrained blue accents keep the interface native
to Windows. A slim accent edge identifies the selected result.

| Role | Light | Dark |
| --- | --- | --- |
| Panel | `#F7F9FC` | `#1B2028` |
| Primary text | `#202733` | `#F3F5F8` |
| Secondary text | `#626C7A` | `#AAB4C2` |
| Accent | `#4568D4` | `#91A8FF` |
| Selected result | `#E8EEFC` | `#2A3650` |

Windows 11 draws native acrylic behind the transient launcher and short-lived
dialogs, and Mica behind settings and Quick look. The tinted panel, rim, and
native shadow keep the launcher defined over both bright and dark apps.
Windows 10, disabled transparency, and high-contrast mode use a solid themed
surface. All motion follows the Windows client-area animation preference.

The 160 ms opening fade and slight translation show the launcher without
delaying input. Expansion changes the HWND height immediately, then reveals
content over 130 ms. Selection feedback lasts about 100 ms, and newly arrived
results fade in only when a query is unchanged. The window height is never
animated: doing so previously delayed searches on the tested graphics stack.

## Visual checks

Run `scripts/build.ps1`, then use an isolated data directory for the UI smoke
test:

```powershell
$env:SCOUT_DATA_DIR = Join-Path $PWD 'artifacts/ui-check'
& 'src/Scout.App/bin/Release/net10.0-windows10.0.19041.0/Scout.exe' --ui-smoke-test
```

The test checks compact/expanded state, clearing, reduced-motion behavior,
search states, settings navigation, validation focus, workspace naming, and
preview. It saves light, dark, and high-contrast WPF screenshots. It also tries
to capture the native compositor over controlled white and black backdrops.
If the test host has no interactive desktop capture handle, it writes
`desktop-capture-unavailable.txt`; the WPF screenshots still run, but they
cannot prove the appearance of acrylic or its native shadow. Review those
compositor captures manually on an interactive Windows desktop before release.

Run `--smoke-test` separately for PDF, image, Office, and text previews. Both
modes write `smoke-result.txt` in the isolated data directory and leave the
user's normal settings alone.
