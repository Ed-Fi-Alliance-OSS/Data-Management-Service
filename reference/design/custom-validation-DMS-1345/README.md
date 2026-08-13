# Custom Validation Extension Point (DMS-1345)

## Overview

Epic DMS-1345 implements a custom validation extension point in the Data Management Service, so that a district or vendor can enforce its own business rules on data as it is written, with the rules living in their own versioned assembly rather than in DMS core source.

The extension point is a public abstractions contract that a custom validator implements, delivered by referencing a published package and registering the validator into DMS's composition at build time, guarded by an unconditional fail-loud startup check. Custom validators are document-oriented and async, and they run only on the write path, on POST and PUT requests. They receive the request document, its resource identity, the write verb, the scope the write belongs to (tenant and route qualifiers), a trace id, and the request's cancellation token; this version's contract gives them no access to stored data.

Runtime loading of a validator assembly that was not part of the build is deferred to its own design spike, which is planned but not yet filed.

## Provenance

This design was produced by spike DMS-1346. The driving discussion is recorded on Confluence: [page 2588835841](https://edfi.atlassian.net/wiki/spaces/GOV/pages/2588835841).

## Design Document

[design.md](./design.md) is the design document for this epic. It states the goals and non-goals, the problem the current DMS validator set poses, the design itself (the injection decision, the validator contract, resource applicability, per-invocation inputs, registration and composition, lifetime, pipeline placement, and startup semantics), how validator failures surface to the API caller, the ODS/API precedent and what of it was refused, the rejected and deferred alternatives, the testing strategy, and the level of effort.

Field-level grounding for the three driving scenarios lives in [draft 05](./05-prove-custom-validation-end-to-end.md) under "## Scenario Grounding", not in the design document.

Six things are deliberately out of scope for this epic, each recorded in design.md with its reasoning rather than left to be rediscovered:

| Out of scope | Where design.md records it | Consequence |
| --- | --- | --- |
| Runtime loading of an assembly that was not part of the build | "## Rejected Alternatives" | Deferred to its own spike, which inherits this contract, the fan-in step, the failure surfacing, and the startup guard unchanged |
| Out-of-process validation over a webhook or sidecar | "## Rejected Alternatives" | Rejected as scoped rather than deferred; a future requirement would be a new decision against its own evidence |
| Any store-read capability for validators | "## Out of Scope" | Limits Scenario 3 to rules expressed against descriptor URIs, and makes the ODS UniqueId not-changed rule inexpressible, which DMS-1414 inherits |
| Validation on DELETE | "## Verb Coverage" | Custom validation runs on POST and PUT only, matching the ODS precedent, which has no delete-time resource validation either |
| A wildcard in `AppliesTo` | "### Resource Applicability" | A validator enumerates every resource it applies to; adding a wildcard later is additive to `ValidatedResource` and breaks nothing |
| Implementing any of the three driving scenarios | "## Driving Scenarios" | They are requirement drivers, not deliverables; the end-to-end story ships a neutral fixture validator instead |

## Ticket Drafts

This spike produced five ticket drafts, listed below in dependency order.

| Draft | Title | Depends on | Status |
| --- | --- | --- | --- |
| [01](./01-add-custom-validator-abstractions-contract.md) | Add the Custom-Validator Abstractions Contract | none | draft - unfiled |
| [02](./02-add-custom-validator-fan-in-pipeline-step.md) | Add the Custom-Validator Fan-In Pipeline Step and Failure Surfacing | 01 | draft - unfiled |
| [03](./03-add-custom-validator-composition-seam-and-startup-guard.md) | Add the Custom-Validator Composition Seam and Startup Guard | 01 | draft - unfiled |
| [04](./04-document-custom-validator-implementer-guide.md) | Document the Custom-Validator Implementer Guide | 01, 02, 03 | draft - unfiled |
| [05](./05-prove-custom-validation-end-to-end.md) | Prove Custom Validation End-to-End with a Fixture Validator | 01, 02, 03 | draft - unfiled |

## Filing Gate

These five ticket drafts are filed in Jira only after the design is reviewed and approved. On filing, each ticket is linked back to DMS-1346, its dependency links are set in Jira to mirror the `Depends on` column above, and the assigned Jira id and URL are backfilled into that ticket's frontmatter, replacing the placeholder values. The Status column is updated at the same time, from `draft - unfiled` to the filed ticket's id.

A sixth ticket is also filed at that point: the design spike for plugin-folder delivery, linked to this epic as deferred follow-on work.
