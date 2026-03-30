# Requirements Traceability

## Purpose

In this matrix, project and file references are informative traceability aids. The normative design obligations remain the contract, behavior, data-model, authorization, and rollout rules captured in the numbered package.

## Constraint and Output Mapping

| Requirement or output | Package coverage | Story mapping |
| --- | --- | --- |
| Implement Change Query API without breaking API interface | `01-Feature-Summary-and-Decisions.md`, `02-API-Contract-and-Synchronization.md`, `03-Architecture-and-Execution.md` | `CQ-STORY-05`, `CQ-STORY-06`, `CQ-STORY-07` |
| Align behavior to the Ed-Fi Changed Record Queries model adopted by this package | `01-Feature-Summary-and-Decisions.md`, `02-API-Contract-and-Synchronization.md`, `03-Architecture-and-Execution.md`, `05-Authorization-and-Delete-Semantics.md`, `06-Validation-Rollout-and-Operations.md` | `CQ-STORY-05`, `CQ-STORY-06`, `CQ-STORY-07`, `CQ-STORY-08` |
| Prefer support without requiring snapshots while still defining both `Use-Snapshot`-selected synchronization flows explicitly | `01-Feature-Summary-and-Decisions.md`, `02-API-Contract-and-Synchronization.md`, `03-Architecture-and-Execution.md`, `06-Validation-Rollout-and-Operations.md` | `CQ-STORY-05`, `CQ-STORY-06`, `CQ-STORY-07`, `CQ-STORY-08` |
| Make snapshot-backed synchronization operationally implementable through explicit instance-scoped snapshot binding and pass-stability rules | `01-Feature-Summary-and-Decisions.md`, `02-API-Contract-and-Synchronization.md`, `03-Architecture-and-Execution.md`, `06-Validation-Rollout-and-Operations.md` | `CQ-STORY-05`, `CQ-STORY-07`, `CQ-STORY-08` |
| Prefer ChangeVersion column model | `01-Feature-Summary-and-Decisions.md`, `04-Data-Model-and-DDL.md` | `CQ-STORY-01`, `CQ-STORY-05` |
| Treat `dms.Document` as canonical source of truth | `01-Feature-Summary-and-Decisions.md`, `03-Architecture-and-Execution.md`, `04-Data-Model-and-DDL.md` | `CQ-STORY-01`, `CQ-STORY-05` |
| Reliable delete detection | `02-API-Contract-and-Synchronization.md`, `04-Data-Model-and-DDL.md`, `05-Authorization-and-Delete-Semantics.md` | `CQ-STORY-03`, `CQ-STORY-07`, `CQ-STORY-08` |
| Reliable key-change detection with one public row per authorized committed key-change event | `02-API-Contract-and-Synchronization.md`, `03-Architecture-and-Execution.md`, `04-Data-Model-and-DDL.md`, `05-Authorization-and-Delete-Semantics.md`, `06-Validation-Rollout-and-Operations.md` | `CQ-STORY-04`, `CQ-STORY-07`, `CQ-STORY-08` |
| Deterministic ordering | `02-API-Contract-and-Synchronization.md`, `03-Architecture-and-Execution.md`, `04-Data-Model-and-DDL.md` | `CQ-STORY-05`, `CQ-STORY-06`, `CQ-STORY-07` |
| API behavior definition | `02-API-Contract-and-Synchronization.md` | `CQ-STORY-05`, `CQ-STORY-06`, `CQ-STORY-07` |
| Preserve ODS-compatible independently optional change-query bounds, including max-only windows | `01-Feature-Summary-and-Decisions.md`, `02-API-Contract-and-Synchronization.md`, `06-Validation-Rollout-and-Operations.md` | `CQ-STORY-05`, `CQ-STORY-08` |
| Provide configurable Change Queries feature gating aligned to the Ed-Fi default-on posture while still allowing explicit disablement for rollout control | `01-Feature-Summary-and-Decisions.md`, `02-API-Contract-and-Synchronization.md`, `06-Validation-Rollout-and-Operations.md` | `CQ-STORY-05` |
| Architecture design | `03-Architecture-and-Execution.md` | all stories |
| DDL proposal | `04-Data-Model-and-DDL.md` as the normative source; `Appendix-A-Feature-DDL-Sketch.sql` as an informative sketch | `CQ-STORY-01`, `CQ-STORY-02`, `CQ-STORY-03`, `CQ-STORY-04` |
| Synchronization algorithm | `02-API-Contract-and-Synchronization.md` | `CQ-STORY-05`, `CQ-STORY-06`, `CQ-STORY-07` |
| Delete and key-change tracking strategy | `04-Data-Model-and-DDL.md`, `05-Authorization-and-Delete-Semantics.md` | `CQ-STORY-03`, `CQ-STORY-04`, `CQ-STORY-07` |
| Migration and backfill strategy | `04-Data-Model-and-DDL.md`, `06-Validation-Rollout-and-Operations.md` | `CQ-STORY-01`, `CQ-STORY-02`, `CQ-STORY-08` |
| Performance considerations | `03-Architecture-and-Execution.md`, `06-Validation-Rollout-and-Operations.md` | `CQ-STORY-06`, `CQ-STORY-07`, `CQ-STORY-08` |
| Validation scenarios | `06-Validation-Rollout-and-Operations.md` | `CQ-STORY-08` |
| Include authorization tables and authorization semantics | `05-Authorization-and-Delete-Semantics.md` | `CQ-STORY-03`, `CQ-STORY-04`, `CQ-STORY-06`, `CQ-STORY-07`, `CQ-STORY-08` |
| Preserve the documented tracked-change authorization contract for `/deletes` and `/keyChanges`, including ODS-style delete-aware relationship visibility plus the accepted DMS-specific ownership exception justified by redesign-auth docs | `02-API-Contract-and-Synchronization.md`, `05-Authorization-and-Delete-Semantics.md`, `06-Validation-Rollout-and-Operations.md` | `CQ-STORY-03`, `CQ-STORY-04`, `CQ-STORY-07`, `CQ-STORY-08` |
| Preserve retained tracked-change authorization safety through explicit `AuthorizationBasis.contractVersion` evolution rules | `04-Data-Model-and-DDL.md`, `05-Authorization-and-Delete-Semantics.md`, `06-Validation-Rollout-and-Operations.md`, `07-Jira-Story-Input.md` | `CQ-STORY-05`, `CQ-STORY-07`, `CQ-STORY-08` |
| Align to backend-redesign update-tracking and authorization direction | `01-Feature-Summary-and-Decisions.md`, `03-Architecture-and-Execution.md`, `04-Data-Model-and-DDL.md`, `05-Authorization-and-Delete-Semantics.md` | `CQ-STORY-01`, `CQ-STORY-02`, `CQ-STORY-03`, `CQ-STORY-04`, `CQ-STORY-07` |
| Explain `DocumentChangeEvent` vs tombstones and key-change tracking | `01-Feature-Summary-and-Decisions.md`, `04-Data-Model-and-DDL.md`, `03-Architecture-and-Execution.md` | `CQ-STORY-02`, `CQ-STORY-03`, `CQ-STORY-04` |
| Produce artifacts usable for Jira story creation | `07-Jira-Story-Input.md`, `08-Requirements-Traceability.md` | not applicable |

## Project-Context Alignment Notes

| Project context item | Design response |
| --- | --- |
| DMS is the target application in `src/dms` | All implementation touchpoints are in `src/dms` paths only. |
| Current backend scope includes PostgreSQL legacy compatibility plus PostgreSQL and MSSQL relational backend targets | The design centers on `dms.Document`, public core service seams, and shared and dialect-specific relational-backend DDL and metadata modules so the planned implementation can be carried across both supported database engines; the transitional compatibility backend remains only a current-behavior reference. |
| Additional local planning artifacts may exist during implementation | The numbered package remains reviewable without any supplemental local planning files. |
| Existing testing style matters | The story input and validation docs call out unit, integration, and E2E coverage expectations. |
| `/keyChanges` is not named in the Jira ticket text | The ticket asks to implement the Ed-Fi Changed Record Queries model; the Ed-Fi Changed Record Queries specification ([platform guide](https://docs.ed-fi.org/reference/ods-api/platform-dev-guide/features/changed-record-queries/), [client guide](https://docs.ed-fi.org/reference/ods-api/client-developers-guide/using-the-changed-record-queries/)) defines `/keyChanges`, `/deletes`, and `availableChangeVersions` as the three surfaces of that model; `/keyChanges` is therefore in scope by reference to the published specification rather than by explicit Jira ticket wording. |

## Review Outcome Expected from This Matrix

A reviewer should be able to confirm that:

- every spike constraint is represented in the consolidated design
- the matrix traces back to the sparse Jira ask rather than only to internal package constraints
- every expected spike output exists in the numbered package
- the required `DocumentChangeEvent` artifact is integrated coherently without changing delete semantics
- `keyChanges` is treated as a peer Change Queries endpoint to `/deletes`, not deferred scope, and preserves one-row-per-event ODS-style semantics
- the story breakdown traces back to concrete design sections
