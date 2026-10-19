# Packaging comparison with the published betas

Date: 2026-10-19. No security-vendor report or sample has been submitted.
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
| Native coverage | 295 reported checks | 356 passing checks on protected CI; 431 passing checks in the combined beta 2 integration |
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
runs on the authorized `ci/beta-package-verification` branch in the existing
private repository, and on pull requests to main. The user authorized the branch
push and hosted Windows runner use. It does not publish a GitHub release.

[Run 39294081382](https://github.com/adilzubair/scout/actions/runs/39294081382)
at `7a84fbdadac3370f97c845633aad946fca858d73` passed the normal build,
**356 native checks, 24 unchanged packaging contracts**, portable inventory and
installer compilation. Active Defender with no exclusions was checked before
checkout; the final scan recorded **zero new detections** with protection still
active. Downloaded artifact hashes match the CI receipts:

- ZIP: `f42e38f8df6f0d5f28a2eefa67074ea16edf5693d822a5c83e0434c531bf6c1a`
- Installer: `a024dbe03fefe47d30dfb6b6b86c664ff115a495897b81220b5e8e615e90acae`

This run predates the beta 2 integration below. Its development artifacts must
not replace the published beta. Receipts are retained locally under
`artifacts/diagnostics/release-20261019/ci-run-39294081382/`.

The first CI run, 39292728131, failed a test fixture that assumed consecutive
writes always produce distinct native change-time ticks. Commit `7a84fbd` seeds
an older fixture timestamp deterministically and adds a stronger same-size,
restored-metadata rewrite test: the snapshot content hash must reject the changed
bytes even when the metadata revision is identical. No production check or
packaging script was weakened. The original failed log is retained.

The combined integration at `af2cf904c7bae93d5cbe32d8dfca660da72b2503`
then passed [run 39296885807](https://github.com/adilzubair/scout/actions/runs/39296885807):
**431 native checks, 24 package contracts**, normal portable/installer build,
source/ZIP/hash verification and protected Defender scan with **zero new
detections**. Its source receipt confirms `publishedBeta2Included: true`.
Settings durable save/restart, slow/failed indexing, validation/cancellation,
actual native folder selection/cancel and repeated browsing all passed. TesterArmy
0.21.2 also passed the expanded launcher/workspace/settings control interactions
(run `4d1ad88d-2297-490f-a507-92d36be98fe3`). Downloaded receipts and packages are
retained under `artifacts/diagnostics/release-20261019/ci-run-39296885807/`.
Their checksums match locally:

- ZIP (84,860,013 bytes): `1bab7297505b6adc986170c023f1e8f7a98f1b56a44c629f2504f5c0539929c7`
- Installer (59,387,582 bytes): `5a6ae24fbcc64ff199f182ea59795fa98bcaa31313d37f8207cc0b6e3c1f4af3`

The job uses the same `scripts/build.ps1 -Publish -Installer`, including the
unchanged blocked contract script and validator. Their reviewed SHA-256 hashes
are checked before execution. It pins official GitHub actions by full commit and
Inno Setup 7.1.0 by its official SHA-256 and valid Authenticode signature.
Git attributes preserve the two reviewed scripts' LF bytes across fresh Windows
checkouts; their contents and existing SHA-256 values remain unchanged.

The [official hosted-image setup](https://github.com/actions/runner-images/blob/main/images/windows/scripts/build/Configure-WindowsDefender.ps1)
disables Defender and sets broad exclusions. The job therefore first
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
after this independent job succeeds; success supplies separate packaging
evidence, not Bitdefender clearance. Windows Server CI does not establish the
Windows 11 desktop/installer lifecycle or model quality gates.

## Published-release integration gap

The initial experimental branch included remote main but omitted **21 published
beta 2 commits**.
Their diff includes compact persisted search snapshots, selected-drive crawling,
folder indexing/type filters, explicit literal/regex content search, indexing
readiness UI and exclusion updates.

A non-mutating `git merge-tree --write-tree HEAD v0.1.0-beta.2` trial records
**14 conflicting files**, including indexing/search, settings, previews, the
test dispatcher and build metadata. Its receipt is retained at
`artifacts/diagnostics/release-20261019/beta2-merge-trial.txt`. The trial informed
an isolated integration worktree. Its conflicts have now
been resolved, preserving beta 3's multimodal settings, durable save handling,
PDF recovery and stricter package verification alongside beta 2's features.
The combined source builds without warnings/errors and passes **431/431 native
checks**, including hard extension/size/time filters for semantic eligibility
and rejection of semantic fusion for explicit grep/regex or invalid queries.
Updated real-WPF settings/control checks cover the unified folder list, content
opt-in, duplicate/add/remove handling and invalid-drive save ordering. The
protected workflow passed packaging and these UI checks on the combined
commit in run 39296885807. Unrelated interactive-demo
edits remain outside the integration.

Independent packaging verification has succeeded without waiting for a vendor
report. The combined candidate has passing engineering/package/UI evidence.
Final installer lifecycle evidence remains required before release approval;
local Bitdefender compatibility and
optional native add-on distribution review remain explicit open items.
