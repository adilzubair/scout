# Scout website

The site is plain HTML and CSS in `dist/`. It has no build step. Serve that
directory with a local static server to preview it. Asset paths are relative so
the site also works under a project path such as GitHub Pages.

The current download links lead to the private beta release in
`adilzubair/scout`. They work for repository members only. A public launch
should keep the application source private and use a separate public site and
release repository. Publish the versioned ZIP and its SHA-256 file there, then
update both download and release-note links in `dist/index.html` before
enabling public hosting.

The beta is a portable Windows 11 x64 ZIP. It is unsigned and has no installer
or automatic updater.
