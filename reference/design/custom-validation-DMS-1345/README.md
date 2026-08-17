# Custom Validation Extension Point (DMS-1345)

## Overview

Epic DMS-1345 implements a custom validation extension point in the Data Management Service, so that a district or vendor can enforce its own business rules on data as it is written, with the rules living in their own versioned assembly rather than in DMS core source.

The extension point is a public abstractions contract that a custom validator implements, delivered by referencing a published package and registering the validator into DMS's composition at build time, guarded by an unconditional fail-loud startup check. Custom validators are document-oriented and async, and they run only on the write path, on POST and PUT requests. They receive the request document, its resource identity, the write verb, the scope the write belongs to (tenant and route qualifiers), a trace id, and the request's cancellation token; this version's contract gives them no access to stored data.

Runtime loading of a validator assembly that was not part of the build is deferred to its own design stream, deliberately not pre-filed as a ticket; see the Filing Gate below.

## Provenance

This design was produced by spike DMS-1346. The driving discussion is recorded on Confluence: [page 2588835841](https://edfi.atlassian.net/wiki/spaces/GOV/pages/2588835841), "July 2026 - Ed-Fi Data Management Service Workgroup". Epic DMS-1345 itself carries no description, so that page is the authoritative statement of what was asked for.

## Design Document

[design.md](./design.md) is the design document for this epic. It states the goals and non-goals, the problem the current DMS validator set poses, the design itself (the injection decision, the validator contract, resource applicability, per-invocation inputs, registration and composition, lifetime, pipeline placement, and startup semantics), how validator failures surface to the API caller, the ODS/API precedent and what of it was refused, the rejected and deferred alternatives, the testing strategy, and the level of effort.

Field-level grounding for the three driving scenarios lives in [draft 05](./05-prove-custom-validation-end-to-end.md) under "## Scenario Grounding", not in the design document.

Six things are deliberately out of scope for this epic, each recorded in design.md with its reasoning rather than left to be rediscovered:

| Out of scope | Where design.md records it | Consequence |
| --- | --- | --- |
| Runtime loading of an assembly that was not part of the build | "## Rejected Alternatives" | Deferred to its own design stream, unfiled, which would inherit this contract, the fan-in step, the failure surfacing, and the startup guard unchanged |
| Out-of-process validation over a webhook or sidecar | "## Rejected Alternatives" | Rejected as scoped rather than deferred; a future requirement would be a new decision against its own evidence |
| Any store-read capability for validators | "## Out of Scope" | Limits Scenario 3 to rules expressed against descriptor URIs, and makes the ODS UniqueId not-changed rule inexpressible, which DMS-1414 inherits. Store reads are one of two things that rule needs; it also needs the persisted document's identity, so granting store access alone would not deliver it |
| Validation on GET or DELETE | "## Verb Coverage" | Custom validation runs on POST and PUT only, matching the ODS precedent, which has no delete-time resource validation either |
| A wildcard in `AppliesTo` | "### Resource Applicability" | A validator enumerates every resource it applies to; adding a wildcard later is additive to `ValidatedResource` and breaks nothing |
| Implementing any of the three driving scenarios | "## Driving Scenarios" | They are requirement drivers, not deliverables; the end-to-end story ships a neutral fixture validator instead |

## Ticket Drafts

This spike produced five ticket drafts, listed below in dependency order.

| Draft | Title | Depends on | Status |
| --- | --- | --- | --- |
| [01](./01-add-custom-validator-abstractions-contract.md) | Add the Custom-Validator Abstractions Contract | none | [DMS-1432](https://edfi.atlassian.net/browse/DMS-1432) |
| [02](./02-add-custom-validator-fan-in-pipeline-step.md) | Add the Custom-Validator Fan-In Pipeline Step and Failure Surfacing | 01 | [DMS-1433](https://edfi.atlassian.net/browse/DMS-1433) |
| [03](./03-add-custom-validator-composition-seam-and-startup-guard.md) | Add the Custom-Validator Composition Seam and Startup Guard | 01 | [DMS-1434](https://edfi.atlassian.net/browse/DMS-1434) |
| [04](./04-document-custom-validator-implementer-guide.md) | Document the Custom-Validator Implementer Guide | 01, 02, 03 | [DMS-1435](https://edfi.atlassian.net/browse/DMS-1435) |
| [05](./05-prove-custom-validation-end-to-end.md) | Prove Custom Validation End-to-End with a Fixture Validator | 01, 02, 03 | [DMS-1436](https://edfi.atlassian.net/browse/DMS-1436) |

## Filing Gate

**Closed.** All five drafts were filed as DMS-1432 through DMS-1436 on 2026-08-17, after the design was reviewed and approved. Each is a child of epic DMS-1345, carries a `relates to` link back to DMS-1346, and sits in API Platform Sprint 66. The `Depends on` column above is mirrored in Jira as `blocks` / `is blocked by` links, and each draft's frontmatter carries its filed id and URL.

Runtime plugin-folder delivery is **not** pre-filed as a ticket. It is recorded as deferred follow-on work in design.md ("## Rejected Alternatives" for its scope, "### Deferred Follow-On Work" for what it inherits unchanged), to be raised when a deployment actually needs a validator it cannot compile into its own build. Filing a spike for it in advance of that demand would put an unscoped design stream on a sprint board ahead of the work that makes it useful.
