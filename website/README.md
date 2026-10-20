# Scout website

The site is plain HTML, CSS, and a small progressive-enhancement script in `dist/`.
It has no build step. Serve that
directory with a local static server to preview it. Asset paths are relative so
the site also works under a project path such as GitHub Pages.

The Find / Control / Resume buttons switch between examples. Without
JavaScript, all three examples remain visible. The website never searches the
visitor's computer or executes Windows commands. FAQ entries use native HTML
disclosure controls. No external font services or analytics are loaded.

The visual design is inspired by the current [Atomic Chat](https://atomic.chat/)
website: charcoal surfaces, large sentence-case headings, white download buttons,
a subtle hero grid, and spacious sections. Scout retains its own branding,
capabilities, and downloads. Mobile navigation uses a native disclosure menu
and works without JavaScript.

The search screenshots (`scout-*-3x.png`, light and dark) are rendered by the
app itself from authored fixture data, so they never show personal files.
Regenerate them after interface changes. Point `SCOUT_DATA_DIR` at an empty
directory, then run `Scout.exe --ui-smoke-test` and copy `site-recent-download*.png`
and `site-download-group*.png` from that directory. The "Control" example
remains an HTML illustration.

With JavaScript available, mobile navigation also closes after choosing a link
or pressing Escape. All native navigation and FAQ disclosures remain usable
when the script is unavailable.

Uncut Sans is self-hosted in `dist/fonts/` and comes from the official
[`kaspernordkvist/uncut_sans`](https://github.com/kaspernordkvist/uncut_sans)
project. Its SIL Open Font License is included in `UncutSans-LICENSE.txt`.
The HTML illustration uses self-hosted Inter from the official `rsms/inter`
project, with its license in `dist/fonts/LICENSE.txt`.

The public site and beta downloads are published from
[`adilzubair/scout-site`](https://github.com/adilzubair/scout-site), which
contains only these static site files and release assets. The application
source lives in this repository.
Vercel deploys its `main` branch root to
[`scout.muhamedadil.com`](https://scout.muhamedadil.com/). GitHub Pages also serves
the same files at `https://adilzubair.github.io/scout-site/`. Preserve the public
repository's `vercel.json` and copy only the contents of `dist/` when publishing.

The Windows 11 x64 beta offers a per-user installer as the primary download and
a portable ZIP as a secondary option. It is unsigned and has no automatic updater.
Download links currently target `v0.1.0-beta.2`, including both SHA-256 checksum
files. Update every installer, ZIP, checksum, version label, and release-note link
together when publishing a later beta.
