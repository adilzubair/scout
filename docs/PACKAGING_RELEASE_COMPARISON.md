# Packaging comparison with the published betas

Date: 2026-10-19. No security report or source has been submitted externally.
The user declined vendor submission and asked for another verification path.

## What changed

The published beta 2 release is dated 2026-10-09. Its annotated source tag points
to commit `ff20acd33c4e1293966782ef68266edeb669ec6d`.
Its build script runs the native suite, publishes the self-contained app, makes
a portable ZIP with `Compress-Archive`, and compiles the Inno Setup installer.
The public release has an installer, portable ZIP and two SHA-256 files.

Commit `2498c48` on 2026-10-15 added the current packaging contract tests,
source allowlist validator and canonical ZIP writer. The normal build now runs
those tests before publishing and verifies payload hashes and ZIP membership.
The TesterArmy runner was added later; it calls the same packaging contract
script through the phase wrapper. It did not introduce the packaging contracts.

| Area | Published beta 2 | Current experimental branch |
| --- | --- | --- |
| Packaging contracts now blocked locally | Not present | Added on 15 October; 24 contracts cover valid packages and rejection paths |
| ZIP creation | `Compress-Archive` | Canonical forward-slash names, source/inventory/hash verification |
| Private payload protection | Earlier packaging process | Explicit rejection of models, private runtimes, indexes and unexpected runtime files |
| Native coverage | 295 reported checks | 355 passing checks in the latest clean-source run |
| Installer | Per-user, unsigned, Inno Setup 7 | Same basic installer technology; beta 3 metadata prepared |
| Independent clean Windows install | Still open in beta 2's own INSTALLER.md | Still open for the new candidate |

The current retained failure is at `scripts/test-package.ps1:39`, while a
PowerShell filter reads `packaging/runtime-files.txt`, before the fixture payload
is populated. The user supplied Bitdefender's detection name,
`CMD:Heur.BZC.ZFV.Boxter.974.9492EC4D`. This identifies the blocked PowerShell
activity; it does not prove that Scout.exe or Scout-Setup.exe was rejected or
that the detection is a false positive. Earlier successful releases did not
execute this newly added test. The same test also has historical passing runs;
the logs do not establish which vendor signature or execution-context change
accounts for its later rejection.

## Independent verification path

[Windows package verification](../.github/workflows/windows-package-verification.yml)
is prepared for a dedicated `ci/beta-package-verification` branch in the existing
private repository, and for pull requests to main. It has not been pushed or run.
Running it requires permission to push that verification branch and use the
repository's hosted Windows runner minutes. It does not publish a GitHub release.

The job uses the same `scripts/build.ps1 -Publish -Installer`, including the
unchanged blocked contract script and validator. Their reviewed SHA-256 hashes
are checked before execution. It pins official GitHub actions by full commit and
Inno Setup 7.1.0 by its official SHA-256 and valid Authenticode signature.
Git attributes preserve the two reviewed scripts' LF bytes across fresh Windows
checkouts; their contents and existing SHA-256 values remain unchanged.

The [official hosted-image setup](https://github.com/actions/runner-images/blob/main/images/windows/scripts/build/Configure-WindowsDefender.ps1)
disables Defender and sets broad exclusions. The proposed job therefore first
enables the disposable runner's protection and removes its scan exclusions.
It fails before checkout/build if protection is inactive or script/archive
scanning is disabled. Automatic sample submission stays disabled; no vendor
review is requested. This setup is confined to the temporary CI machine and
does not change the user's PC.

After the normal build passes, the job performs a
[Defender custom scan](https://learn.microsoft.com/en-us/powershell/module/defender/start-mpscan?view=windowsserver2025-ps),
checks for new detections, and rechecks artifact hashes, payload integrity,
ZIP inventory and checksums. It retains diagnostics and successful development
artifacts for seven days. A failed or unavailable protection/scan step is not a
successful security check. The local Bitdefender finding remains recorded even
if this independent job succeeds; success would supply separate packaging
evidence, not Bitdefender clearance. Windows Server CI does not establish the
Windows 11 desktop/installer lifecycle or model quality gates.

## Published-release integration gap

Remote main is an ancestor of the experimental branch, but the published beta 2
tag is not. There are **21 beta 2 commits absent from this branch's history**.
Their diff includes compact persisted search snapshots, selected-drive crawling,
folder indexing/type filters, explicit literal/regex content search, indexing
readiness UI and exclusion updates. This history must be reconciled before the
new candidate can be treated as a successor to the published beta.

A non-mutating `git merge-tree --write-tree HEAD v0.1.0-beta.2` trial records
**14 conflicting files**, including indexing/search, settings, previews, the
test dispatcher and build metadata. Its receipt is retained at
`artifacts/diagnostics/release-20261019/beta2-merge-trial.txt`. The working index
and files were not merged; unrelated interactive-demo edits remain preserved.
No conflict resolution or regression coverage is claimed yet.

The current candidate is therefore not ready to merge/publish. Independent
packaging verification can proceed without waiting for a vendor report, while
release-history integration and final installer/distribution work remain open.
