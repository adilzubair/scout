# Scout

A fast, private launcher for Windows. Press **Ctrl+Space** and describe what you
want: an app, a file, a page you visited, a calculation or a system setting.
Scout searches locally and can use an optional on-device model to understand
descriptive queries such as *"the pdf I just downloaded"*.

[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
![Platform: Windows 11 x64](https://img.shields.io/badge/platform-Windows%2011%20x64-0078d4)
![Status: beta](https://img.shields.io/badge/status-experimental%20beta-orange)

[Website](https://adilzubair.github.io/scout-site/) ·
[Download](https://github.com/adilzubair/scout-site/releases) ·
[User guide](docs/USER_GUIDE.md) ·
[Release notes](docs/RELEASE_NOTES.md)

<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="website/dist/scout-arrival-dark-3x.png">
    <img src="website/dist/scout-arrival-3x.png" alt="Scout finding &quot;the pdf I just downloaded&quot;, with results on the left and a preview of the selected file on the right" width="720">
  </picture>
</p>

> [!NOTE]
> Scout is an experimental beta. Retrieval accuracy and minimum hardware
> requirements haven't been broadly validated, the installer is unsigned, and
> there is no automatic updater.

## Features

- **Natural-language search** over apps, files and browser history:
  `latest 3 files in downloads`, `files I opened in the past two hours`,
  `last pdf in "Project Files"`.
- **Evidence-based time filters.** "Created", "modified", "downloaded", "opened"
  and "visited" each use the matching Windows or browser timestamp. A result
  without that evidence never matches.
- **Groups and workspaces.** Open several results at once, then save the set as
  a named workspace you can recall later.
- **Previews** for PDFs, images, text and source files, Office documents, audio
  and video.
- **System actions** such as dark mode, Wi-Fi, Bluetooth, volume, brightness,
  lock and sleep, plus your own `.lnk`/`.url` shortcuts and Power Automate links.
- **Calculator and web fallback**: `15% of 240`, `sqrt(81)`.
- **Optional local AI.** [Laya](https://huggingface.co/convaiinnovations/laya)
  re-ranks results on your machine. An experimental multimodal add-on can also
  search inside documents, images, audio and video.
- **Private by default.** Everything runs locally. Nothing launches without
  Enter or an explicit Open, and models can only choose among known candidates.
  They never produce shell commands.

## Install

Requires Windows 11 x64.

1. Download `Scout-Setup.exe` from the
   [Releases page](https://github.com/adilzubair/scout-site/releases) and run it.
   Setup installs for your account without admin rights.
2. Press **Ctrl+Space** to open Scout. Closing the panel keeps Scout in the
   notification area. **Ctrl+Q** quits.

Windows may show a SmartScreen warning because the installer is unsigned. Each
release publishes SHA-256 checksums you can verify first. A portable
`Scout-win-x64.zip` is also available: extract it and run `Scout.exe`.

To uninstall, use **Uninstall Scout** from Start, or **Settings → Apps**. Your
settings and workspaces stay in `%LOCALAPPDATA%\Scout` for a later reinstall.

## Usage

Try queries like:

- `notepad`, `vscode`, `budget excel`
- `the last presentation I created`, `the pdf I just downloaded`
- `latest json file in downloads`, `files modified yesterday in documents`
- `latest screenshot in pictures`, `open downloads folder`
- `docs links I visited today`
- `/pdf roadmap`, `/image holiday`, `/folder projects` (type `/` for all filters)
- `dark`, `wifi off`, `bluetooth on`, `volume up`, `mute`, `lock screen`

| Shortcut | Action |
|---|---|
| Ctrl+Space | Open or hide Scout (global) |
| Up / Down | Select a result |
| Enter | Run the selected action, or Open all |
| Tab / Shift+Tab | Cycle All, Files, Apps, Links, Workspaces |
| Ctrl+K | Result actions |
| Ctrl+Alt+Space | Preview a file |
| Ctrl+P | Pin or unpin |
| Ctrl+R | Reveal in Explorer |
| Ctrl+Shift+C | Copy path, URL or calculation |
| Ctrl+Shift+Space | Add or remove a group member |
| Ctrl+, | Settings |
| Escape | Close the current overlay, then the panel |

The [user guide](docs/USER_GUIDE.md) covers folder restrictions, slash filters,
time evidence, workspaces, system actions and indexing rules in detail.

## Local AI

Local AI is opt-in and off by default. In **Settings**, choose **cpu** or
**cuda** (NVIDIA GPUs) and click **Set up local AI**. Setup downloads an isolated
Python runtime and pinned model weights (several GB). It never touches your
system Python. Inference is fully offline after setup.

Local results always appear first; Laya can refine them. Its model services
listen only on random `127.0.0.1` ports with per-session bearer tokens, and are
released after the panel has been hidden for a minute. CPU mode works, but it's
slow on typical laptops; see the [measurements](docs/research/LIGHTWEIGHT_BASELINE.md).

The experimental **Settings → Multimodal** add-on uses
[EmbeddingGemma 2](https://huggingface.co/google/embeddinggemma-2) to index the contents of
folders you opt in, including documents, spreadsheets, presentations, code,
images, audio and video. See the [user guide](docs/USER_GUIDE.md#local-ai).

## Privacy

- Search runs locally and Scout has no telemetry. Data is stored in
  `%LOCALAPPDATA%\Scout`. Network access is limited to opening web searches you
  choose and downloading optional AI components from their publishers.
- Chrome and Edge history and file-content search are separately opt-in.
  Incognito data, cookies and open tabs are never read.
- Laya receives query text, candidate titles and limited context such as recent
  app names and the clipboard's *kind*, never its contents. It never receives
  document bodies.
- Turning content search off clears its index.

## Build from source

Prerequisites: Windows 11 x64 and the .NET SDK version pinned in
[`global.json`](global.json). Python is only needed for the optional AI runtime,
and setup installs its own. The installer build also needs
[Inno Setup 7](https://jrsoftware.org/isinfo.php).

```powershell
dotnet run --project src/Scout.App
```

Build, test and package:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1             # build + regression checks
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1 -Publish    # + portable ZIP
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1 -Installer  # + Scout-Setup.exe
```

Outputs go to `artifacts/`. For the optional AI runtime, run
`scripts/setup-laya.ps1` (add `-Cuda` for NVIDIA GPUs), then the Python checks:

```powershell
.runtime/.venv/Scripts/python.exe runtime/test_runtime.py
.runtime/.venv/Scripts/python.exe runtime/test_http.py
```

`Scout.exe` also has isolated diagnostic modes for UI and integration testing.
Point `SCOUT_DATA_DIR` at a throwaway directory first. See
[CONTRIBUTING.md](CONTRIBUTING.md) and the
[end-to-end test guide](tests/Scout.E2E/README.md).

## Project layout

| Path | Contents |
|---|---|
| `src/Scout.Core` | Search engine, query parsing, ranking policy, indexing and storage (SQLite) |
| `src/Scout.App` | WPF launcher, settings, previews, Windows integration, model hosts |
| `runtime/` | Python sidecars: Laya ranking server and the multimodal embedding worker |
| `tests/Scout.Tests` | Native regression checks (console runner) |
| `tests/Scout.E2E` | Windows end-to-end runner |
| `scripts/` | Build, packaging, setup and evaluation tooling ([index](scripts/README.md)) |
| `installer/` | Inno Setup script |
| `evaluation/` | Frozen evaluation protocols |
| `docs/` | User and contributor docs; `docs/research/` holds development notes |
| `website/` | Static project website |

## Documentation

- [User guide](docs/USER_GUIDE.md): detailed behavior and settings
- [Release notes](docs/RELEASE_NOTES.md)
- [Installer](docs/INSTALLER.md): packaging and verification
- [Interface](docs/UI.md): visual system and UI test modes
- [Parity](docs/PARITY.md): how Intern's behavior maps to Windows
- [Third-party components](docs/THIRD_PARTY.md): licenses and attributions
- [Research notes](docs/research/README.md): evaluations, benchmarks and model screens

## Contributing

Issues and pull requests are welcome. Please read [CONTRIBUTING.md](CONTRIBUTING.md)
first, and report security issues as described in [SECURITY.md](SECURITY.md).

## Acknowledgements

Scout's behavior is modeled on [Intern](https://github.com/dabit3/intern), a
macOS launcher. Scout is an independent C#/WPF implementation and contains no
Intern code or assets. The split search panel is inspired by
[DigUp](https://github.com/ARahim3/DigUp), and the compact index borrows ideas
from [fsearch](https://github.com/noahdunnagan/fsearch). See
[third-party components](docs/THIRD_PARTY.md) for full attributions.

## License

[MIT](LICENSE) © Muhamed Adil
