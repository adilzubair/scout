# Experimental beta merge preparation

Date: 2026-10-19. Branch: `feature/multimodal-semantic-search`.
Candidate: **0.1.0-beta.3**. Status: engineering/package/UI checks passed;
prepared for experimental beta merge review. The authorized verification branch
has been pushed and protected Windows CI passed on the combined integration
at `af2cf90` in run 39296885807. Require green CI on the final committed head.
No main merge/push, release tag, GitHub release or vendor report was performed.

The user selected **Prepare experimental beta merge** rather than completing
the full independent quality study first. Physical 16 GB testing is scheduled
after release. These decisions preserve experimental status and do not turn
unmeasured accuracy, performance or minimum hardware targets into passes.

## Evidence and acceptance

| Area | Current evidence or action |
| --- | --- |
| Native engineering checks | The combined beta 2 integration passes 431 checks locally and on protected Windows CI and builds with zero warnings/errors. Unrelated interactive-demo changes remain outside the integration. |
| UI and real-model workflows | TesterArmy 0.21.2 orchestrates native WPF controls, settings/restart/picker, authored Office/PDF/media, seven-family indexing, CPU/CUDA and Laya checks. Counts and retained failures are in the [E2E report](E2E_TESTING_2026_10_19.md). |
| PDF fixes | Transient receipt locking is bounded and cancellable; failed Previous/Next retains the displayed page. Deterministic recovery tests and 120 real renders pass. |
| Concurrent ordinary search | Result signatures remain identical during indexing/PDF and Laya workloads. Measured p95 is 19.44 ms baseline, 12.93 ms with indexing/PDF and 14.02 ms with Laya added; these are background development-machine samples. |
| Laya CPU limitation | Heavy shortlists reach the two-second fallback. A separate four-thread probe succeeds for one candidate but only one of three eight-candidate trials and none of three 32-candidate trials. Leave AI opt-in and retain local fallback. |
| Independent quality study | Deferred for this experimental beta by the user. Preserve the 400-task/seven-family protocol, negative/abstention work, corpus rights review and existing thresholds for later completion. |
| Physical 16 GB PC | Post-release follow-up at the user's direction; minimum hardware support remains provisional. |
| Packaging | Protected combined Windows CI run [39296885807](https://github.com/adilzubair/scout/actions/runs/39296885807) passes the normal build, all 24 unchanged contracts, ZIP/source/hash verification, installer compilation and active Defender scan with zero new detections. Local Bitdefender's `CMD:Heur.BZC.ZFV.Boxter.974.9492EC4D` remains recorded; the user declined vendor submission. |
| Published beta 2 integration | All 21 missing published commits have been reconciled, resolving 14 files. Both beta 2 and remote main are ancestors of the candidate; combined native/package and real-WPF settings/control CI passed. The actual CPU-model seven-family scheduler also passes, including cleanup. |
| Installer/portable lifecycle | The complete normal build compiles the installer and verifies portable payloads. Clean standard-account Windows 11 install, first launch, interruption/retry, update and removal checks remain open; successful compilation is not installation evidence. |
| Native distribution | Exact PyAV/codec source and notice review remains open. `runtime/semantic_search/distribution.json` is an unapproved development reference, and the notice collector records `distributionApproved: false`; optional private installs and old package passes do not clear it. Model weights/private runtimes stay outside the base package. |
| Version | Public prereleases beta 1 and beta 2 already exist. The protected normal build produces beta 3 as product version `0.1.0-beta.3`, file version `0.1.0.3`, using Inno 7.1.0. |

The earlier fully exercised product code is `23b89f3`; its package hash and individual
runner reports are retained in the E2E report. Combined source at `af2cf90` has
passing protected package/UI evidence; this does not grant final release
approval or retroactively rerun every earlier model/performance study. The standalone CPU latency
diagnostic is `artifacts/diagnostics/release-20261019/laya-latency-v1/report.json`.
Its authored cases are timing diagnostics, not independent quality tasks.
The beta preparation's native log is `artifacts/diagnostics/release-20261019/beta-native.log`;
installer preprocessing is retained alongside it as `beta-preprocessed.iss`.
The protected CI run supplies independent packaging evidence without bypassing
the blocked local script. The test and validator retain their original hashes.

## Remaining steps

1. Before merging, require the protected build/package/UI result on the final
   committed head. The combined integration is green; the subsequent test-only
   checkpoint cleanup fix passes the actual CPU-model smoke. The user declined
   vendor reporting; the successful CI route and earlier-release comparison are in
   [the packaging comparison](PACKAGING_RELEASE_COMPARISON.md). Do not disable
   protection, add exclusions, or alter/reroute blocked tests to evade scanning.
   Keep the local Bitdefender compatibility finding open and distinguish it from
   independent build/scan evidence.
2. Before release approval, rehearse final installer and portable lifecycle in an isolated standard Windows
   account. Preserve the existing user's install registration, Scout profile and
   model installations. An alternate install directory under the same installer
   AppId is insufficient isolation. Finish applicable distribution obligations.
3. Review the outgoing committed source and combined search/index/UI evidence. Recheck
   remote main, review the complete outgoing branch and the resulting
   diff, and record the combined CI/reviewer outcome before merging. Keep the unrelated local interactive-demo
   edits outside this change. Merge and push only the reviewed committed state.

Remote main was last checked at
`4b767e01a615148207d92005a941547bca9247d7` and is an ancestor of this branch.
Before this preparation, 81 commits were outgoing, including 14 already on local
main. After beta 2 integration the source diff contains 247 files and roughly 33,800 added lines, including
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

Validation: 431 combined native checks locally and on protected Windows CI,
24 packaging contracts, combined real WPF settings,
restart, native folder-picker and control workflows, CPU/CUDA model checks,
seven-family authored indexing, PDF recovery/stress and ordinary-search isolation
have passing evidence described in the linked E2E report. Laya CPU fallback,
independent quality evaluation and physical 16 GB support remain explicit beta
limitations. Protected packaging/scan evidence passes; local Bitdefender
compatibility, final clean Windows 11 installer lifecycle and optional native
distribution review remain explicit release items. The independent 400-task
study is deferred for this experimental beta at the user's direction.
