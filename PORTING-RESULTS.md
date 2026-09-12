# DMS-1324 port execution

## Revisions and environment

- Source checkout: `/home/brad/work/dms-root/DMS-1324`, implementation/history retained at `1c1e3a1fbedfa5d4f572a67b9eaf8750c4b9eedd` (implementation/story at `bb3aecb98cd887f9b05a7cebccda1d978ce40c8b`; subsequent commit adds the plan).
- Source delta begins after `45046c1d3a26927d93e94394f901731e0aaa12fe`.
- Target: `/home/brad/work/dms-root/DMS-1324-port`, branch `DMS-1324-port`, no upstream.
- Replacement base: `c3ae3bfffbd0a049abf7b8768eb4f14744a6bbaf`; local and remote DMS-1323 agree after fetch.
- Both solution restores, tool restore, and Husky installation passed.
- Revised 05 story copied; replacement 04 and normative designs retained.
- Existing unrelated Docker stack remains running; CDC qualification will use isolated fixture resources.

## Validation

- Baseline Contract lane completed with existing failures: `TestResults/port-baseline-contract-01`. Controller unit: 3502 passed / 4 failed; CLI: 685 passed / 30 failed; offline integration: 92 passed; wrappers: 346 passed / 25 failed. No skips. The stale-offset fixture also waited for containment because its mock never reported stopped. Baseline evidence is retained separately from the port runs.
- Independent subset: 265 passed, 0 failed/skipped (`TestResults/port-independent-01`).
- Adapted focused admission subset: 672 passed, 0 failed/skipped (`TestResults/port-admission-03`). The original 626 cases gained 40 lag-identity assertions and 6 numeric-string offset rejection assertions. The valid PostgreSQL beyond-barrier case now uses a JSON number, matching the current production parser.
- Admission composes `CdcConnectRestAdapter` / `ICdcConnectTransport`, `CdcControllerObservations.Offset`, and current provider adapters. No production parser or obsolete Control library was imported. Optional lag percentiles are null in the valid baseline. Ownership/projection/history/lag remain synthetic prerequisites.

## Additional validation and baseline repairs

- All port message unit tests: 957 passed, no failures/skips (`TestResults/port-message-unit-01`).
- Candidate-tree checks before transfer: offline integration 263 passed, serialized contracts
  964 passed on the shipped image, broker contracts 34 passed. These establish transfer
  compatibility; final worktree qualification remains required.
- Final worktree pinned-image smoke: 6 passed, no failures/skips (`TestResults/port-smoke-final-01`).
- Focused corrected CLI cases: 16 passed (`TestResults/port-cli-fixed-01`).
- CI and affected wrapper Pester checks: 223 passed, no failures/skips.
- Packaged unit assembly: 20 traceability/asset tests passed from `/tmp`, outside the checkout.
- PowerShell 7.4.10 cannot represent the absent-versus-empty environment values required by
  the existing wrapper tests. Validation now uses an isolated PowerShell 7.5.3 tool with
  runtime roll-forward to installed .NET 10; the user's global tool was not changed.
- Baseline fixture repairs are listed in PORTING-INVENTORY.md. The first secured-Kafka run
  exposed recursive session acquisition in its worker-start fixture callback; the callback
  now uses the existing shared adapter inspection method. The lane is rerunning.
- The user's requested plan clarification was applied to the source checkout and copied
  here. No source implementation or branch history was changed.

## Remaining work

Full Contract, PostgreSQL, MSSQL, and Kafka lanes, packaged integration verification,
final source/scenario disposition review, and evidence audit remain in progress.
No full qualification claim yet.
