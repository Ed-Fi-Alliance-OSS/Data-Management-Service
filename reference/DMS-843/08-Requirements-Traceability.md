# Requirements Traceability

## Purpose

This matrix maps the spike requirements, local project constraints, and expected deliverables to the consolidated numbered-package artifacts in `reference/DMS-843` and the proposed Jira story set.

In this matrix, project and file references are informative traceability aids. The normative design obligations remain the contract, behavior, data-model, authorization, and rollout rules captured in the numbered package.

## Constraint and Output Mapping

| Requirement or output | Package coverage | Story mapping |
| --- | --- | --- |
| Do not introduce breaking API changes | `01-Feature-Summary-and-Decisions.md`, `02-API-Contract-and-Synchronization.md`, `03-Architecture-and-Execution.md` | `CQ-STORY-04`, `CQ-STORY-05`, `CQ-STORY-06`, `CQ-STORY-07` |
| Avoid snapshot history tables if possible while defining the no-snapshot tradeoffs and the non-snapshot synchronization algorithm explicitly | `01-Feature-Summary-and-Decisions.md`, `02-API-Contract-and-Synchronization.md`, `06-Validation-Rollout-and-Operations.md` | `CQ-STORY-05`, `CQ-STORY-06`, `CQ-STORY-07` |
| Prefer ChangeVersion column model | `01-Feature-Summary-and-Decisions.md`, `04-Data-Model-and-DDL.md` | `CQ-STORY-01`, `CQ-STORY-05` |
| Treat `dms.Document` as canonical source of truth | `01-Feature-Summary-and-Decisions.md`, `03-Architecture-and-Execution.md`, `04-Data-Model-and-DDL.md` | `CQ-STORY-01`, `CQ-STORY-05` |
| Reliable delete detection | `02-API-Contract-and-Synchronization.md`, `04-Data-Model-and-DDL.md`, `05-Authorization-and-Delete-Semantics.md` | `CQ-STORY-02`, `CQ-STORY-06`, `CQ-STORY-07` |
| Reliable key-change detection | `02-API-Contract-and-Synchronization.md`, `03-Architecture-and-Execution.md`, `04-Data-Model-and-DDL.md`, `05-Authorization-and-Delete-Semantics.md` | `CQ-STORY-03`, `CQ-STORY-06`, `CQ-STORY-07` |
| Deterministic ordering | `02-API-Contract-and-Synchronization.md`, `03-Architecture-and-Execution.md`, `04-Data-Model-and-DDL.md` | `CQ-STORY-05`, `CQ-STORY-06`, `CQ-STORY-07` |
| API behavior definition | `02-API-Contract-and-Synchronization.md` | `CQ-STORY-04`, `CQ-STORY-05`, `CQ-STORY-06` |
| Architecture design | `03-Architecture-and-Execution.md` | all stories |
| DDL proposal | `04-Data-Model-and-DDL.md` as the normative source; `Appendix-A-Feature-DDL-Sketch.sql` as an optional informative sketch | `CQ-STORY-01`, `CQ-STORY-02`, `CQ-STORY-03`, `CQ-STORY-08` |
| Synchronization algorithm | `02-API-Contract-and-Synchronization.md` | `CQ-STORY-05`, `CQ-STORY-06`, `CQ-STORY-07` |
| Delete and key-change tracking strategy | `04-Data-Model-and-DDL.md`, `05-Authorization-and-Delete-Semantics.md` | `CQ-STORY-02`, `CQ-STORY-03`, `CQ-STORY-06` |
| Migration and backfill strategy | `04-Data-Model-and-DDL.md`, `06-Validation-Rollout-and-Operations.md` | `CQ-STORY-01`, `CQ-STORY-08` |
| Performance considerations | `03-Architecture-and-Execution.md`, `06-Validation-Rollout-and-Operations.md` | `CQ-STORY-05`, `CQ-STORY-06`, `CQ-STORY-08` |
| Validation scenarios | `06-Validation-Rollout-and-Operations.md` | `CQ-STORY-07` |
| Include authorization tables and authorization semantics | `05-Authorization-and-Delete-Semantics.md` | `CQ-STORY-02`, `CQ-STORY-03`, `CQ-STORY-06`, `CQ-STORY-07` |
| Align to backend-redesign update-tracking direction | `01-Feature-Summary-and-Decisions.md`, `03-Architecture-and-Execution.md`, `04-Data-Model-and-DDL.md` | `CQ-STORY-08` |
| Explain `DocumentChangeEvent` vs tombstones and key-change tracking | `01-Feature-Summary-and-Decisions.md`, `04-Data-Model-and-DDL.md`, `03-Architecture-and-Execution.md` | `CQ-STORY-08` |
| Produce artifacts usable for Jira story creation | `07-Jira-Story-Input.md`, `story-map.json`, `workitems/tasks.json`, `workitems/progress.json` | not applicable |

## Project-Context Alignment Notes

| Project context item | Design response |
| --- | --- |
| DMS is the target application in `src/dms` | All implementation touchpoints are in `src/dms` paths only. |
| Current backend is JSON-backed PostgreSQL | The design centers on `dms.Document`, public core service seams, and relational-backend DDL and metadata modules so the planned backend replacement can implement it directly; the transitional compatibility backend remains only a current-behavior reference. |
| `tasks.json` and progress artifacts are expected repo artifacts | `workitems/tasks.json` and `workitems/progress.json` are provided. |
| Existing testing style matters | The story input and validation docs call out unit, integration, and E2E coverage expectations. |

## Review Outcome Expected from This Matrix

A reviewer should be able to confirm that:

- every spike constraint is represented in the consolidated design
- every expected spike output exists in the numbered package
- the optional `DocumentChangeEvent` artifact is integrated coherently without changing delete semantics
- `keyChanges` is treated as a peer Change Queries endpoint to `/deletes`, not deferred scope
- the story breakdown traces back to concrete design sections
