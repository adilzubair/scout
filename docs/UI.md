# Scout interface

Scout opens as a 780 × 64 px search field when the query is empty. Typing opens
the results panel; Down or Tab opens suggestions and scope filters. Clearing the
query returns to the compact field unless a group or workspace edit is active.
The search field opens near the upper third of the screen and keeps room below for
the expanded results panel.
The panel keeps up to six results visible before scrolling and stays within the
current monitor's work area. Existing launch, preview, group, and action
shortcuts work in both states.

The `ui/digup-inspired` branch tries the split search panel from
[DigUp](https://github.com/ARahim3/DigUp): a 330 px result column beside a
selected-item preview, an amber magnifier, compact rows, neutral surfaces and
a quiet footer. This is an original WPF implementation; no DigUp source or assets
ship with Scout. Scout retains its scope filters, launcher actions and workspace
controls.

Selecting a result shows its name, type, path and available search evidence.
Local images, PDF pages, bounded document/source text and current multimodal
extracts load automatically after a short selection delay. Audio/video and
unsupported formats show a type symbol; Quick look opens the existing dedicated
preview, including playback. Open, Quick look and Reveal use the same validated
actions as the keyboard shortcuts. Moving selection, changing the query or
hiding Scout cancels the previous preview. Remote paths and cloud placeholders
are not fetched for automatic previews.

## Visual system

Shared brushes in `src/Scout.App/App.xaml` use dynamic resources for XAML and
mutable brushes for controls created in code. Scout follows the Windows app
light/dark setting, including changes while it runs. Segoe UI Variable Text and
Display and Fluent icons keep the interface native to Windows. Amber identifies
search; blue selection fills identify the current result; moss green gives
fallback preview symbols a quiet accent.

| Role | Light | Dark |
| --- | --- | --- |
| Panel | `#FBFBFB` | `#1F1F21` |
| Primary text | `#1D1D1F` | `#F2F2F7` |
| Secondary text | `#6E6E73` | `#A1A1A6` |
| Search accent | `#B8660B` | `#F2A22A` |
| Selected result | `#CEE5FC` | `#233F60` |

Windows 11 draws native acrylic behind the transient launcher and short-lived
dialogs, including Settings, and Mica behind Quick look. The tinted panel, rim, and
native shadow keep the launcher defined over both bright and dark apps.
Windows 10, disabled transparency, and high-contrast mode use a solid themed
surface. All motion follows the Windows client-area animation preference.

Settings uses the launcher's 780 px width, amber header icon, 22 px display
title, Escape keycap, 18 px window corners and native acrylic surface. Its
navigation and content meet at a full-height divider, and its Save/Cancel row
uses the same bordered footer as search. Pages no longer sit inside a separate
rounded card. Shared 13 px body text, 12 px buttons and compact form fields also
apply to workspace dialogs and Quick look; window corners come from one shared
resource so these surfaces stay aligned.

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

The narrower `--controls-smoke-test` also checks the split layout, actual inline
text extraction, cancellation on selection/query changes and hide/reopen. It
saves `inline-note.png` and `inline-note-dark.png` alongside its control report.
Fallback previews are checked in light, dark and high-contrast themes to keep
the footer and action buttons inside the panel.
To build this trial while another Scout copy holds the normal output directory:

```powershell
dotnet build Scout.slnx -c Release -m:1 -p:OutputPath="$PWD/artifacts/digup-ui-unified/"
```

Trial validation on 20 October 2026: the build completed without warnings and
all 431 regression checks passed. The final native control run passed 22 of 23
checks, including the new inline readers and cancellation/layout checks. The
existing clipboard test could not read the current clipboard object
(`CLIPBRD_E_CANT_OPEN`, after its bounded retries). The full UI run completed
the remaining visual, Office, PDF and media checks using the verified runtime
under `artifacts/diagnostics/e2e/addon`, then retained that clipboard failure as
a failing overall exit status. Reports and screenshots stay untracked under
`artifacts/digup-ui-checks/`.

The unified Settings revision also builds without warnings. Native checks pass
for saving, reopening, restarting and folder selection; 22 of 23 control checks
pass, with the same existing clipboard block retained. The updated Settings was
visually checked in light, dark and high contrast, and its fields and Save button
stay within the smallest supported window. These reports and screenshots are
under `artifacts/digup-ui-unified-checks/`.

Quit the current Scout instance before opening `artifacts/digup-ui-unified/Scout.exe`
to try the branch; the normal single-instance launcher otherwise reopens the
already running copy.
