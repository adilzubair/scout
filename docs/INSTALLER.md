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

## Validation for the experimental 0.1.0 beta 3 candidate

Protected Windows Server CI run
[39296885807](https://github.com/adilzubair/scout/actions/runs/39296885807) at
`af2cf90` passed the normal build with Inno Setup 7.1.0, 431 native checks,
24 unchanged packaging contracts, ZIP/source/checksum verification and an active
Defender scan with zero new detections. Product/file versions are
`0.1.0-beta.3` / `0.1.0.3`. Published beta 2 is integrated, and the combined
candidate's settings/restart/native-picker and TesterArmy control checks pass.

Compiler/package/scan success does not establish a clean standard-account
Windows 11 install, first launch, interrupted setup, upgrade or removal. Those
lifecycle checks remain open. The local Bitdefender PowerShell finding remains
recorded separately. No antivirus settings on the user's PC were changed, and
no vendor report was submitted.

## Validation for 0.1.0 beta 2

On 2026-10-09, the release build passed 295/295 C# checks, 13 Python adapter
checks, seven native folder-picker checks and an isolated desktop UI smoke test.
The HTTP contract checks cover authentication, browser-origin rejection, ranking,
folder candidates, malformed requests and the typed inference endpoint. On this
PC, CPU ranking sometimes exceeds its two-second budget and returns complete
abstention; this is recorded separately from a successful ranking response.
No production model, prompt or deadline was changed for release validation.

The release uses self-contained Windows x64 binaries and Inno Setup 7, with
version 0.1.0-beta.2 / file version 0.1.0.2. Package validation checks the archive
contents, binary version, checksums and an isolated desktop smoke run of the
published executable. Those diagnostics and private data remain outside the
package. See [RELEASE_NOTES.md](RELEASE_NOTES.md) for upgrade instructions.

A fresh Windows 11 VM or spare PC was unavailable. Installation, first launch
and uninstall on a clean machine remain unverified for beta 2; the beta 1
installation evidence below does not establish beta 2 installer behavior.
Setup remains unsigned and updates are manual.

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
Do not describe this validation as a clean-machine test. A future clean-machine check should cover installation, first launch, hotkey
and uninstall in a fresh Windows 11 VM or spare PC.
