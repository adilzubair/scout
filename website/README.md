# Scout website

The site is plain HTML and CSS in `dist/`. It has no build step. Serve that
directory with a local static server to preview it. Asset paths are relative so
the site also works under a project path such as GitHub Pages.

The public site and beta downloads are published from
[`adilzubair/scout-site`](https://github.com/adilzubair/scout-site). The
application source remains in the private `adilzubair/scout` repository. The
public repository contains only these static site files and release assets.
GitHub Pages serves the files from its `main` branch root.

The beta is a portable Windows 11 x64 ZIP. It is unsigned and has no installer
or automatic updater.
