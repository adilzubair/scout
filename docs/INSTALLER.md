# Windows installer

`scripts/build.ps1 -Installer` runs the app checks, publishes the self-contained
Windows x64 app, builds the portable ZIP, and compiles `installer/Scout.iss` with
[Inno Setup 7](https://jrsoftware.org/isinfo.php). It writes
`artifacts/Scout-Setup.exe` and matching SHA-256 files. `-Publish` builds only
the portable ZIP. Build artifacts are ignored by Git.

Setup installs into `%LOCALAPPDATA%\Programs\Scout` without elevation. It adds
Scout and Uninstall Scout to the current user's Start menu, registers an
uninstaller under HKCU, and launches Scout at the end. Uninstall retains
`%LOCALAPPDATA%\Scout` user settings and workspaces. The installer is unsigned;
there is no automatic updater.

Product and file versions are set in both `src/Scout.App/Scout.App.csproj` and
`installer/Scout.iss`; keep them in sync when cutting a release. Keep the
installer `AppId` GUID stable so upgrades replace the previous installation.

## Verification

CI (`.github/workflows/windows-package-verification.yml`) builds the installer
on a Windows Server runner, runs the native checks and packaging contracts,
verifies the ZIP contents and checksums, and scans the output with an active
Microsoft Defender.

Per-release verification so far covered a non-admin Windows 11 install, Start
menu shortcuts, the HKCU uninstall entry, first launch, the global hotkey via
`Scout.exe --diagnose`, and silent uninstall that keeps user data.

## Known gaps

- Install, first launch, upgrade, interrupted setup and uninstall have not been
  verified on a clean Windows 11 machine or VM.
- Setup is unsigned, so Windows may show an unknown-publisher or SmartScreen
  warning.
- Some third-party antivirus products may flag the PowerShell build scripts
  heuristically. Don't disable security tooling to work around it.
