# Scout

A native Windows launcher built against [Intern](https://github.com/dabit3/intern)'s behavior, using local [Laya](https://huggingface.co/convaiinnovations/laya) instead of Jev. C#/.NET 10, WPF, SQLite and an authenticated Python sidecar. Intern is cloned under `.reference/intern` for reference; its code and assets are not redistributed.

[Website](https://adilzubair.github.io/scout-site/) · [Published beta download](https://github.com/adilzubair/scout-site/releases/tag/v0.1.0-beta.2)

This branch prepares **0.1.0-beta.3**, an experimental beta. It has not been
published. The independent multimodal quality study and physical 16 GB validation
are deferred; retrieval accuracy and minimum hardware support remain unvalidated.
Packaging security, installer lifecycle and distribution checks are still open.
See [beta merge readiness](docs/BETA_MERGE_READINESS.md).

## Download and install

On Windows 11 x64, download `Scout-Setup.exe` from the [public Releases page](https://github.com/adilzubair/scout-site/releases) and open it. Setup installs for your Windows account without admin access, adds **Scout** and **Uninstall Scout** to the Start menu, and launches Scout when it finishes. Press **Ctrl+Space** to open the panel. Closing it leaves Scout in the notification area; **Ctrl+Q** quits. To uninstall, choose **Uninstall Scout** from Start or use Windows Settings → Apps → Installed apps. Uninstalling leaves your settings and workspaces in `%LOCALAPPDATA%\Scout` for a later reinstall.

Scout is an unsigned beta. Windows may show an unknown-publisher or SmartScreen warning. Check that the installer came from the Scout GitHub release and use its SHA-256 checksum if you want to verify it before choosing to proceed. Updates are manual: quit Scout and run the installer from a newer release. There is no automatic updater.

The portable `Scout-win-x64.zip` remains available on the same release. Extract the entire ZIP to a folder, then run `Scout.exe`. To update a portable copy, quit Scout and replace the extracted application folder with the contents of a newer ZIP. Your data in `%LOCALAPPDATA%\Scout` stays in place. From source: `dotnet run --project src/Scout.App`.

Try:

- `notepad`, `vscode`, `budget excel`
- `the last presentation I created`
- `latest file in telegram folder`, `latest 3 files in downloads`
- `show me the 3 newest files in telegram folder`, `latest json file in downloads`
- `in "Telegram Desktop", find the latest file`
- `last pdf in "Telegram Desktop"`, `files modified yesterday in telegram folder`
- `latest screenshot in pictures`, `open Telegram Desktop folder`
- `the pdf I just downloaded`
- `files I opened in the past two hours`
- `slides about the direction of our company`
- `devin links I visited today`
- `15% of 240`, `sqrt(81)`, `2(3+4)`
- `dark`, `wifi off`, `bluetooth on`, `volume up`, `mute`, `brightness down`, `lock screen`

Local results appear first. Laya can refine them or find a relevant item among broader candidates. Missing or ambiguous matches fall back locally. An accented return arrow marks a clear local selection; it never runs an action automatically. Raw Laya probabilities are not displayed as calibrated certainty.

## Local AI

Local AI is opt-in and remains off by default. Open **Settings**, choose **cpu** or **cuda**, and click **Set up local AI**. CPU is the initial device choice, but it does not enable AI automatically. Setup downloads an isolated Python runtime and pinned model weights; save Settings afterward to enable Laya. Existing nearby runtimes are detected automatically. CUDA requires a supported NVIDIA GPU. English is the recommended checkpoint based on the current evaluation. CPU mode works but the [measured baseline](docs/LIGHTWEIGHT_BASELINE.md) is too slow and error-prone to recommend as Scout's default on ordinary laptops. The [offline evaluation](docs/OFFLINE_EVALUATION.md) tracks smaller local alternatives without a Jev API key.

The 2.5 GB Qwen helper is now research-only and unavailable in the standard
Settings flow. It improved a small authored semantic evaluation from 38/64 to
56/64 but is too large for Scout's target of ordinary Windows laptops. Existing
experimental installations can still be tested by starting Scout with
`SCOUT_EXPERIMENTAL_SEMANTIC_HELPER=1`. See [the helper evaluation](docs/LOCAL_SEMANTIC_HELPER.md).
Keyword content search is separate from local AI. Its document text stays in
Scout's private index and is not sent to Laya or the Qwen helper.

This branch also includes a **Settings → Multimodal** experimental preview for
the optional full EmbeddingGemma 2 add-on. It installs and tests text, image,
audio and video support independently of Laya. LibreOffice is not required;
Office content will use dedicated readers for text, cells, chart data and
embedded images, with extracted evidence previews rather than exact Office
layout. Opted-in folders support background indexing, content search and matched
evidence previews across documents, presentations, spreadsheets, code, images,
audio and video. Selected content is processed by the local embedding worker;
Laya and Qwen continue to receive no document bodies. The add-on is off by default,
and authored smoke checks do not establish general retrieval accuracy. See
[add-on status and checks](docs/MULTIMODAL_ADDON.md).

Command-line setup is also available:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/setup-laya.ps1 -Cuda
```

Omit `-Cuda` for CPU. Setup installs its own `uv` if necessary and never replaces system Python. Models and dependencies require several GB, with additional space for download caches. Interrupted downloads are resumable. The application ZIP excludes Python and model weights.

When AI is enabled, Scout loads Laya while the launcher is active and releases
the model processes after the panel has been hidden for one minute. Reopening the
panel starts them again; local results remain available during startup. Closing
the panel leaves Scout in the notification area, while **Ctrl+Q** or **Quit**
stops the app and its processes. Exact names and learned aliases need no AI
round trip. Superseded requests cannot replace current results; the interface
remains usable during inference. Services bind only to random `127.0.0.1` ports
with fresh bearer tokens. Inference is offline after setup. The local API includes
`/v1/systemone` and Scout's `/rank` adapter. No Jev key is needed.

## Actions and workspaces

| Shortcut | Action |
|---|---|
| Ctrl+Space | Open/hide Scout (global) |
| Up / Down | Select a result |
| Enter | Run the selected action or Open all; copy a calculator result |
| Escape | Close the current overlay/group, then dismiss the panel |
| Tab / Shift+Tab | Cycle All, Files, Apps, Links, Workspaces |
| Ctrl+K | Result actions |
| Ctrl+Alt+Space | Preview a file |
| Ctrl+P | Pin/unpin |
| Ctrl+R | Reveal in Explorer |
| Ctrl+Shift+C | Copy path, URL or calculation |
| Ctrl+Shift+Space | Add/remove a group member |
| Ctrl+, | Settings |
| Ctrl+Q | Quit |

Plural searches can offer **Open all**. Edit member checkboxes or use **Review group**, then **Save workspace** to recall the set by name. Workspaces contain 2–25 apps/files/links, with up to 20 saved. Their actions support editing and deletion. Manual selection and membership edits survive reranking.

Previews support PDF pages, images, bounded text/source files, Office/OpenDocument text and Windows-supported audio/video playback. Office previews show text, not the application's full layout. Copy/reveal/preview are also understood as query actions. Unknown formats offer an explicit Open file button.

Windows actions include dark mode, Wi-Fi on/off, Bluetooth on/off, notification settings, sleep, lock, Recycle Bin and hidden files. The approved media additions use only these fixed command IDs: `volume-up`, `volume-down`, `mute-on`, `mute-off`, `brightness-up` and `brightness-down`. Volume commands target the default Windows audio output. Brightness commands target integrated displays and work only when Windows exposes brightness control, which commonly excludes external desktop monitors. Radio and media changes can be blocked by Windows policy, drivers, hardware switches or unsupported hardware; failures are reported. Bluetooth controls only Bluetooth radios and verifies the requested state; it does not disable PnP devices or alter Wi-Fi. Empty Recycle Bin uses Windows' confirmation dialog. System commands cannot be included in a workspace. The language model cannot create or alter system commands.

For automations, add a folder of your `.lnk` or `.url` shortcuts in Settings. This supports user-created Windows shortcuts and Power Automate desktop links. Search by name to run one explicitly.

## Search evidence and privacy

Folder descriptions are restrictions on the results and model candidates. Names such as `telegram` can match `Telegram Desktop`; quoted names and absolute paths also work. `in` includes subfolders; `directly in` limits results to immediate children. Standard Windows folders take priority over project directories with the same name. Unknown explicit folders show an explanation instead of unrelated files. Exact filenames and paths are treated literally, including words such as `copy` or `today`.

Optional slash filters work alongside normal search: `/folder projects`, `/image holiday`, `/pdf roadmap`, or `/folder` to list indexed directories, including empty folders. Type `/` for the filter list. Other filters cover files, video, audio, documents, spreadsheets, presentations, archives, apps, links and workspaces. Words after a slash filter match names; operators such as `in:`, `ext:`, `size:`, `mtime:` and explicit file-content patterns remain available. Existing filename and natural-language queries continue to work. Filters respect the selected scope tab and configured indexing locations.

The launcher shows indexing progress while work is running or a location needs attention. Once indexing completes, the launcher stays quiet; Settings → Folders retains per-folder readiness and filename/content counts. Unreadable subfolders show a skipped-folder count; hover their status in Settings to see the paths.

Filename and content indexing automatically skip folders named `temp` or `tmp` (case-insensitive), generated `pytest-of-*` folders, and any folder starting with `.`. Existing dependency/build exclusions also apply. Names such as `Templates` and `Temperature` remain searchable. Older cached entries from newly excluded folders are removed automatically when the updated app starts.

`latest` uses modification dates unless another activity is named. Matching latest/oldest searches use recorded dates immediately without waiting for Laya. `latest 3 files` limits both the results and the suggested group; groups remain capped at 25 items. Screenshot searches require screenshot evidence in the filename or folder and cannot match every image. Images with arbitrary names outside such folders may need a normal image/name search.

Counts work before or after ordering words (`3 newest files`, `newest three files`). Explicit format names such as CSV, JSON, YAML and XML filter by extension. Partial filenames preserve punctuation, and compound names such as `ClientBillingReport.pdf` can be found using their separate words. The model shortlist reserves room for broader candidates and limits repeated titles; constrained locations stay enforced before any model request. Laya receives readable filename labels and short folder context when it matches the description, with separate relevance checks when several candidates share the choice probability.

| Description | Evidence |
|---|---|
| Created / made | Windows filesystem creation date |
| Modified / edited / saved | File modification date |
| Downloaded | Completed Chrome/Edge download when enabled; otherwise an explicitly labeled arrival estimate |
| Added / arrived | Scout file watcher, falling back to Windows creation date |
| Opened / used | Windows Recent Items and successful Scout launches |
| Visited / read a page | Chrome/Edge visit timestamp |

Creation dates can change when copying a file and do not prove authorship. Access timestamps are never treated as proof of opening. Applications differ in whether they populate Recent Items. Unknown evidence cannot satisfy the corresponding time filter. Hover a result to inspect the source and timestamp.

- Downloads, Desktop, Documents, Pictures and Screenshots use Windows Known Folders, including redirected locations. Extra roots and exclusions are configurable. Hidden paths, dependency/cache/build directories, virtual environments, reparse points and offline placeholders are skipped. Project source remains searchable.
- Windows Search optionally expands filename/type retrieval across indexed locations in the user profile and configured roots. Local indexing continues if that service is unavailable. Watchers batch changed files; full reconciliation runs every 30 minutes or after watcher overflow.
- Chrome and Edge history are separately opt-in. Regular profiles supply up to 10,000 distinct HTTP(S) URLs and 10,000 completed downloads from the last 90 days. Databases are read-only; a locked database can use a bounded disposable snapshot with consistency checks. Incognito data, cookies and open tabs are not read.
- Calendar phrases use the Windows timezone, including daylight-saving boundaries. `last week` is the preceding Monday–Sunday; `past seven days` is rolling. Singular superlatives use the requested evidence even when filenames also match.
- Content search is off by default. When enabled, it searches only selected folders that Scout already searches and supports bounded text/source files plus Office and OpenDocument files. Files larger than 16 MiB are skipped, and at most 64,000 characters are extracted from each supported file. PDF contents and OCR are not indexed.
- Extracted keyword-search text is stored in a private local full-text index. Turning keyword content search off clears that index. Laya/Qwen receive no document body text. Laya receives query text, shortlisted titles/kinds, relevant evidence and limited local context: foreground/recent app names, clipboard **kind**, time and weekday. Clipboard contents are not read for ranking. The separate multimodal preview uses a local embedding worker and currently tests authored inputs; selected folders are not yet content-indexed.
- Data is stored in `%LOCALAPPDATA%\Scout\scout.db`, with content text kept in its separate local index. Pins, workspaces and successful launches are retained locally. Disabling a browser removes its history source; explicitly saved or previously launched items remain in the personal library. Clear launch history removes learned aliases and launch boosts while retaining pins and workspaces.

Every launch requires Enter or an explicit Open action. A model can select only known candidate IDs; it cannot produce shell commands. Each group member is validated before launching, with partial failures reported. Successful shell dispatch does not guarantee the receiving app finished loading.

## Build and test

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1 -Installer
.runtime/.venv/Scripts/python.exe runtime/test_runtime.py
.runtime/.venv/Scripts/python.exe runtime/test_http.py
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/evaluate.ps1 -Device cuda
```

The build pins .NET SDK 10.0.400 and produces a self-contained directory, `artifacts/Scout-win-x64.zip`, `artifacts/Scout-Setup.exe`, and SHA-256 checksums. The installer is built with Inno Setup 7 and installs per user. The beta remains unsigned and has no automatic updater or startup registration.

With `SCOUT_DATA_DIR` set to an isolated directory, `Scout.exe --smoke-test` checks actual WPF rendering/selection, groups, arithmetic, web fallback and PDF/image/Office/text previews, then exits. Run `--ui-smoke-test` separately for interface screenshots, settings navigation, and validation checks; this mode requires an isolated data directory. Set `SCOUT_TEST_PYTHON` and optionally `SCOUT_TEST_DEVICE=cuda` to include the model host. `--diagnose` exercises actual installed apps, fast typing, file watchers and the global hotkey, then exits. Keep the normal instance closed during diagnostics. Test reports and UI screenshots remain under the isolated data directory. See [interface notes](docs/UI.md) for the visual system and native backdrop behavior.

See [validation](docs/VALIDATION.md) for measured results and limits, and [parity](docs/PARITY.md) for Windows mappings. English queries are supported; Unicode filenames are preserved. No live Jev comparison has been run, so feature coverage and local benchmarks do not establish equal general model quality.

The [search upgrade evaluation](docs/SEARCH_EVALUATION.md) measures the current build against a saved baseline on 504 frozen queries grounded in local file metadata. It also records remaining descriptive-search failures; those results are not a claim of general Intern parity.

## Workspace cleanup

Run `pwsh -File scripts/clean.ps1` to preview removable build output, retired test
packages, old UI builds, and bytecode caches. Use `-Apply` to delete the listed
files, or `-Apply -WhatIf` to exercise the deletion preview. Close any Scout or
test processes running from those directories first. Build again with
`scripts/build.ps1`; publishing uses `scripts/build.ps1 -Installer`.

The cleanup retains the standard release outputs, the latest verified internal
preview (`Scout-multimodal-preview-e2e-fixed`), model weights, Python environments,
fixtures, evaluation data, diagnostic reports and linked test installations.
The static website's `dist/` directory is its source and stays in place. Active
regression tests remain part of the build. Cleanup refuses tracked content, links
and nested Git repositories; it never touches installed Scout settings under
`%LOCALAPPDATA%`. Run `pwsh -File scripts/test-clean.ps1` to verify its deletion
and preservation contracts in a disposable repository.

The retired hard-coded prompt experiment and failed portable LibreOffice
download spike have been removed. Their history remains in Git; the documented
MSI renderer experiment remains available for reproducing its research results.

For a separate preview of retired research storage, run
`pwsh -File scripts/clean.ps1 -RetiredResearch`; add `-Apply` to delete it.
This removes the obsolete Qwen 1.7B weights, downloaded LibreOffice renderer,
and diagnostic database copies, generated search-index binaries and ZIP archives.
Directories named for fixtures, diagnostic reports, current release packages,
current build output and active AI models/environments are retained. Old database
snapshots cannot be replayed after removal, and archived inputs require rebuilding
or downloading again. The documented LibreOffice research setup can reinstall
that optional renderer.
