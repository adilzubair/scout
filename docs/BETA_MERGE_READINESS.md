# Experimental beta merge preparation

Date: 2026-10-19. Branch: `feature/multimodal-semantic-search`.
Candidate: **0.1.0-beta.3**. Status: prepared for review; packaging/security and
final installation verification remain blocked or open. No merge, push, tag,
GitHub release or external security submission has been performed.

The user selected **Prepare experimental beta merge** rather than completing
the full independent quality study first. Physical 16 GB testing is scheduled
after release. These decisions preserve experimental status and do not turn
unmeasured accuracy, performance or minimum hardware targets into passes.

## Evidence and acceptance

| Area | Current evidence or action |
| --- | --- |
| Native engineering checks | The clean beta 3 source export passes 355 checks and builds with zero warnings/errors. No unrelated interactive-demo changes are in that export. |
| UI and real-model workflows | TesterArmy 0.21.2 orchestrates native WPF controls, settings/restart/picker, authored Office/PDF/media, seven-family indexing, CPU/CUDA and Laya checks. Counts and retained failures are in the [E2E report](E2E_TESTING_2026_10_19.md). |
| PDF fixes | Transient receipt locking is bounded and cancellable; failed Previous/Next retains the displayed page. Deterministic recovery tests and 120 real renders pass. |
| Concurrent ordinary search | Result signatures remain identical during indexing/PDF and Laya workloads. Measured p95 is 19.44 ms baseline, 12.93 ms with indexing/PDF and 14.02 ms with Laya added; these are background development-machine samples. |
| Laya CPU limitation | Heavy shortlists reach the two-second fallback. A separate four-thread probe succeeds for one candidate but only one of three eight-candidate trials and none of three 32-candidate trials. Leave AI opt-in and retain local fallback. |
| Independent quality study | Deferred for this experimental beta by the user. Preserve the 400-task/seven-family protocol, negative/abstention work, corpus rights review and existing thresholds for later completion. |
| Physical 16 GB PC | Post-release follow-up at the user's direction; minimum hardware support remains provisional. |
| Packaging | Blocked. Bitdefender reports `CMD:Heur.BZC.ZFV.Boxter.974.9492EC4D` while the unchanged packaging contracts run. Source review has not established vendor clearance. |
| Installer/portable lifecycle | Still requires a complete normal build and clean standard-account install, first launch, interruption/retry, update and removal checks. Inno Setup 7.1.0 is present; compiler availability is not installation evidence. |
| Native distribution | Exact PyAV/codec source and notice review remains open. `runtime/semantic_search/distribution.json` retains `distributionApproved: false`; optional private installs and old package passes do not clear it. Model weights/private runtimes stay outside the base package. |
| Version | Public prereleases beta 1 and beta 2 already exist. Beta 3 builds as product version `0.1.0-beta.3`, file version `0.1.0.3`. Inno 7.1.0 preprocessing verifies project-supplied values, defaults and override propagation without producing an installer. |

The last fully exercised product code is `23b89f3`; its package hash and individual
runner reports are retained in the E2E report. The candidate metadata/documentation
update does not claim a newly validated distribution. The standalone CPU latency
diagnostic is `artifacts/diagnostics/release-20261019/laya-latency-v1/report.json`.
Its authored cases are timing diagnostics, not independent quality tasks.
The beta preparation's native log is `artifacts/diagnostics/release-20261019/beta-native.log`;
installer preprocessing is retained alongside it as `beta-preprocessed.iss`.
The clean build is not a published distribution and does not bypass the blocked
packaging phase. The blocked test and validator retain their original hashes.

## Remaining steps

1. Resolve the packaging detection through security review. Do not disable
   protection, add exclusions, rewrite blocked code to evade detection, or run
   release packaging through a substitute route. The private review packet is
   `artifacts/diagnostics/release-20261019/antivirus-review.txt`.
2. From the reviewed committed source, run the normal
   `./scripts/build.ps1 -Publish -Installer`. Require native/package checks,
   source-to-payload and ZIP inventory checks, installer compilation and final
   SHA-256 receipts. Keep historical failed evidence.
3. Rehearse final installer and portable lifecycle in an isolated standard Windows
   account. Preserve the existing user's install registration, Scout profile and
   model installations. An alternate install directory under the same installer
   AppId is insufficient isolation. Finish applicable distribution obligations.
4. Recheck remote main, review the complete outgoing branch and the resulting
   diff, and record any CI/reviewer outcome before merging. No remote CI result
   exists for these unpushed commits. Keep the unrelated local interactive-demo
   edits outside this change. Merge and push only the reviewed committed state.

Remote main was last checked at
`4b767e01a615148207d92005a941547bca9247d7` and is an ancestor of this branch.
Before this preparation, 81 commits were outgoing, including 14 already on local
main. The source diff contains 207 files and roughly 29,700 added lines, including
dependency locks and extensive tests/docs. History inventory and a clean diff
check are preparation evidence, not an independent review of every line. Main
was unprotected and had no check runs on that remote commit; that does not supply
a green CI result. No repository protections were changed.

## Prepared pull request description

Title: **Add experimental local multimodal search and harden Windows workflows**

Opted-in folders gain local content search and matched evidence previews across
documents, presentations, spreadsheets, code, images, audio and video. The add-on
uses an isolated full EmbeddingGemma 2 runtime, durable background indexing,
bounded sandboxed extraction and owned worker processes; it remains off by
default and works with Laya disabled. Office content uses dedicated readers and
does not require LibreOffice.

The branch also fixes folder-picker activation, saving folders while indexing is
pending, PDF renderer shutdown, transient PDF receipt locks, failed PDF page
navigation and fresh pinned Laya installation. TesterArmy drives native Windows
integration checks and preserves successful and failed reports.

Validation: 355 native checks, real WPF workflows, CPU/CUDA model checks,
seven-family authored indexing, PDF recovery/stress and ordinary-search isolation
have passing evidence described in the linked E2E report. Laya CPU fallback,
independent quality evaluation and physical 16 GB support remain explicit beta
limitations. Packaging/security, final installer lifecycle and native distribution
review are still open; this description must be updated with final artifact and
review results before being presented as merge-ready. The independent 400-task
study is deferred for this experimental beta at the user's direction.
