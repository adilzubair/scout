# Scout 0.1.0 beta 3 — 2026-10-20

This beta redesigns the search panel, changes the launch shortcut to
**Ctrl+Space**, and adds an optional, experimental multimodal content index.

## Changes

- **New shortcut.** Press **Ctrl+Space** to open Scout. **Ctrl+Alt+Space** now
  previews the selected file. Profiles still using the old default move to
  Ctrl+Space automatically; custom shortcuts are kept.
- **Split search panel.** Results sit beside a preview of the selected item,
  with its path, evidence and Open / Quick look / Reveal actions. Text, source,
  PDF, image and Office previews appear inline.
- **Refreshed Settings and dialogs** that match the search panel, in light, dark
  and high-contrast themes.
- **Multimodal add-on (experimental, off by default).** Under **Settings →
  Multimodal**, Scout can install a separate local runtime and the EmbeddingGemma 2
  model to index the contents of folders you choose: documents, presentations,
  spreadsheets, code, images (with Windows OCR), audio and video. Matching
  evidence appears in previews. Content is processed only by the local worker;
  Laya never receives document bodies.
- Settings now save before indexing changes are applied, so folders no longer
  disappear when Settings is reopened during indexing.
- Fixed a crash when activating the folder picker, PDF previews that failed after
  a transient lock or failed navigation, and a launcher footer that could be cut
  off.
- Packaging verifies source membership, excludes private data, and checks the
  ZIP inventory and hashes before producing downloads.

## Install or upgrade

Quit the running Scout, then run `Scout-Setup.exe`. It installs for your Windows
account without elevation and keeps your settings, pins, history and
workspaces. For the portable edition, extract all of `Scout-win-x64.zip` into a
fresh folder and run `Scout.exe`. SHA-256 checksum files accompany both
downloads.

## Limitations

This is an unsigned, experimental beta with manual updates. Windows may show a
SmartScreen warning. A clean-machine install and uninstall test is still
outstanding. The multimodal add-on downloads several GB, has not passed an
independent retrieval-quality study, and has not been measured on 16 GB
hardware. Laya CPU ranking can exceed its two-second budget and fall back to
local results. See [third-party components](THIRD_PARTY.md) for licenses.

# Scout 0.1.0 beta 2 — 2026-10-09

This beta adds a compact saved search index, folder/type filters, opt-in file-content
search and clearer indexing controls while preserving existing filename and
natural-language queries.

## Changes

- Search folders, including empty folders: `/folder projects`. Filter files with
  `/image holiday`, `/pdf roadmap` and other types; enter `/` for the full list.
- One folder list now controls filename search and optional content search.
  Folder selection no longer freezes, and Settings scrolling is slower and more
  predictable. Each folder shows its indexing readiness.
- Completed indexing stays quiet in the launcher. Unreadable locations show a
  count and details in Settings rather than a generic attention warning.
- `temp`, `tmp`, generated `pytest-of-*` trees, dot-prefixed folders and existing
  dependency/build exclusions are skipped. Older cached entries from newly
  excluded folders are removed automatically on upgrade.
- File create/edit/rename/delete notifications update the index in the background.
  Startup and 30-minute reconciliation catch missed changes. Filename results are
  available before selected-folder content extraction finishes.
- Explicit local content modes support `grep:"literal text"` and `regex:"pattern"`,
  combined with `in:`, `ext:`, `size:` and `mtime:`. Verified excerpts appear in
  previews. Content indexing is off by default, selected-folder only, and extracted
  document text stays local and is not sent to the model.
- Optional whole-disk filename indexing covers selected fixed drives with normal
  user permissions. It is off by default and independent of content search.
- Saved metadata now uses a compact, rebuildable base with incremental changes;
  SQLite remains authoritative for metadata, pins and personal activity.

## Install or upgrade

Download `Scout-Setup.exe` from the public beta release. Quit the running Scout,
then run Setup. It installs for your Windows account without elevation. Existing
settings, pins, history and workspaces are retained. The first upgraded startup
rebuilds the cache for the new exclusion rules; progress appears while it runs.

For the portable edition, extract all of `Scout-win-x64.zip` into a fresh folder
and run `Scout.exe` after quitting the old copy. SHA-256 checksum files accompany
both downloads. Press **Ctrl+Alt+Space** to open Scout (beta 3 changes this to **Ctrl+Space**); closing its panel leaves it
running in the tray.

## Validation and limitations

The implementation passed 295 C# checks, desktop smoke checks and native folder
picker checks. Frozen comparison tests preserve results, recall and notices on
584 compatibility cases with identical candidate coverage. Package-specific
validation is recorded in INSTALLER.md.

This remains an unsigned Windows 11 x64 beta with manual updates. A clean-machine
Windows 11 installation/uninstallation test remains outstanding. Large imports
can delay updates, and whole-drive startup/crawl outliers remain. Some regex
constructs are unsupported and large content queries can return explicitly
labelled partial results. Content extraction is limited to supported text and
Office/OpenDocument files, 16 MiB per file and 64,000 characters; PDF contents and
OCR are not indexed.

The million-candidate search median improved about 24% in the prior paired
comparison, missing the 30% target. That synthetic measurement is not a claim
about every PC or whole-drive crawl speed. Local AI remains optional, with
separate runtime/model downloads and existing semantic-ranking limitations. CPU
ranking can exceed its two-second budget and abstain; local filename search
remains available.
