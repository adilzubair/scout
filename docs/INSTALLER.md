# Windows beta installer

`scripts/build.ps1 -Installer` runs the app checks, publishes the self-contained
Windows x64 app, builds the portable ZIP, and compiles `installer/Scout.iss` with
Inno Setup 7. It writes `artifacts/Scout-Setup.exe` and matching SHA-256 files.
`-Publish` still builds only the portable ZIP. Build artifacts remain ignored by
Git.

Setup installs into `%LOCALAPPDATA%\Programs\Scout` without elevation. It adds
Scout and Uninstall Scout to the current user's Start menu, registers an
uninstaller under HKCU, and launches Scout at the end. Uninstall retains
`%LOCALAPPDATA%\Scout` user settings and workspaces. The installer is unsigned;
there is no automatic updater.

## Validation for 0.1.0 beta 1

On 2026-09-26, the release build passed 182/182 C# checks. A local non-admin
Windows 11 Pro session (build 26200) installed `Scout-Setup.exe`, created both
Start menu shortcuts and the HKCU uninstall entry, and launched Scout. The
installed `Scout.exe --diagnose` reported `passed: true` and
`globalHotkey: true` after exercising Ctrl+Alt+Space. Two diagnostic runs
timed out at the synthetic hotkey step before a retry passed. A direct check
on the final installed build confirmed Ctrl+Alt+Space hid and reopened the
visible Scout window. Silent uninstall returned 0 and removed the executable,
shortcuts, and uninstall entry while retaining user data. The installer
signature check reported `NotSigned`.

A separate clean Windows setup was unavailable, and the user waived that test.
Do not describe this validation as a clean-machine test. Before the next beta,
run the same install, first launch, hotkey, and uninstall checks in a fresh
Windows 11 VM or spare PC.
