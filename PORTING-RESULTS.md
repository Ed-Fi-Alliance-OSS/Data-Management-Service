# DMS-1324 port execution

## Revisions and environment

- Source checkout: `/home/brad/work/dms-root/DMS-1324`, unchanged at `1c1e3a1fbedfa5d4f572a67b9eaf8750c4b9eedd` (implementation/story at `bb3aecb98cd887f9b05a7cebccda1d978ce40c8b`; subsequent commit adds the plan).
- Source delta begins after `45046c1d3a26927d93e94394f901731e0aaa12fe`.
- Target: `/home/brad/work/dms-root/DMS-1324-port`, branch `DMS-1324-port`, no upstream.
- Replacement base: `c3ae3bfffbd0a049abf7b8768eb4f14744a6bbaf`; local and remote DMS-1323 agree after fetch.
- Both solution restores, tool restore, and Husky installation passed.
- Revised 05 story copied; replacement 04 and normative designs retained.
- Existing unrelated Docker stack remains running; CDC qualification will use isolated fixture resources.

## Validation

- Baseline Contract lane running: `TestResults/port-baseline-contract-01`. Its unchanged PostgreSQL stale-offset fixture waits for the five-minute containment deadline when the mocked connector never reports stopped. A testhost dump confirms `SetupRefresh` -> `CdcInitialReadiness.ContainLossAsync` -> `CdcSourceHistoryContainment.StopAsync`; the process remains live.
- Independent subset: 265 passed, 0 failed/skipped (`TestResults/port-independent-01`).
- Adapted focused admission subset: 672 passed, 0 failed/skipped (`TestResults/port-admission-03`). The original 626 cases gained 40 lag-identity assertions and 6 numeric-string offset rejection assertions. The valid PostgreSQL beyond-barrier case now uses a JSON number, matching the current production parser.
- Admission composes `CdcConnectRestAdapter` / `ICdcConnectTransport`, `CdcControllerObservations.Offset`, and current provider adapters. No production parser or obsolete Control library was imported. Optional lag percentiles are null in the valid baseline. Ownership/projection/history/lag remain synthetic prerequisites.

## Remaining work

Stages 2–8 of PORTING-PLAN.md remain in progress. No full qualification claim yet.
