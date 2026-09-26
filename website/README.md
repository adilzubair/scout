# Scout website

The site is plain HTML and CSS in `dist/`. It has no build step. Serve that
directory with a local static server to preview it. Asset paths are relative so
the site also works under a project path such as GitHub Pages.

The public site and beta downloads are published from
[`adilzubair/scout-site`](https://github.com/adilzubair/scout-site). The
application source remains in the private `adilzubair/scout` repository. The
public repository contains only these static site files and release assets.
GitHub Pages serves the files from its `main` branch root.

The Windows 11 x64 beta offers a per-user installer as the primary download and
a portable ZIP as a secondary option. It is unsigned and has no automatic updater.
