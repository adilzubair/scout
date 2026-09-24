# Scout interface

Scout uses a Spotlight-inspired layout with Windows-native typography, icons,
window shadows, and keyboard shortcuts. A single frosted surface places search
first, with compact filters and a quiet result list. The panel fits its
contents, up to six results, then scrolls. It remains inside the screen's work area.

## Visual system

The shared resources live in `src/Scout.App/App.xaml`.

| Role | Color |
| --- | --- |
| Solid fallback | `#F8F9FC` |
| Frosted tint | `#C9F8FAFF` over native acrylic |
| Content surface | `#FFFFFF` |
| Primary text | `#242832` |
| Secondary text | `#6C7380` |
| Accent and primary actions | `#4568D4` |
| Selected result | `#154568D4` over the surface |

Segoe UI Variable Text, with Segoe UI as fallback, keeps text native and legible.
Search uses 25 px Segoe UI Variable Display, result titles 15 px, and supporting
labels 12–13 px. Results have no outlines and use regular-weight titles. The
native Windows 11 acrylic backdrop respects the system transparency preference;
older Windows versions and high-contrast mode use a solid fallback. The 140 ms
opening fade respects Windows' animation preference. Result changes resize the
window immediately: animating the native acrylic window height caused roughly
200 ms of additional query delay on the tested graphics stack.

Settings has General, Folders, Local AI, and Privacy pages, with Save and Cancel
always visible, and switches for preferences. Validation returns to the affected page and focuses the field.
Workspace dialogs, actions, and Quick look share the same controls and palette.

## Visual checks

Build with `scripts/build.ps1`, or run `dotnet run --project src/Scout.App`.
Quit an existing Scout instance before opening another normal build.

Use an isolated data directory for desktop checks:

```powershell
$env:SCOUT_DATA_DIR = Join-Path $PWD 'artifacts/ui-check'
& 'artifacts/Scout-UI-v2/Scout.exe' --ui-smoke-test
```

`--ui-smoke-test` exercises search states, settings navigation, validation focus,
workspace naming, and text preview. It saves screenshots of empty and populated
results, groups, calculations, long names, every settings page, and dialogs.
Run the existing `--smoke-test` separately to check PDF, image, and Office
previews without offscreen screenshot captures. Each mode exits when finished
and writes `smoke-result.txt`.

Both checks use the normal rendering path. Client-area screenshots use the solid
fallback because the native desktop backdrop is outside WPF's visual tree.
Running the PDF renderer and UI screenshot captures in separate processes avoids
a DXGI shutdown fault observed on the development machine's graphics stack.
