# Request/Response Capture (DMS-1419)

## Overview

[DMS-1419](https://edfi.atlassian.net/browse/DMS-1419) scopes request/response capture for epic [DMS-1418](https://edfi.atlassian.net/browse/DMS-1418) Request/Response Logging.
The capability is operator-controlled, per-API-client, time-bounded capture of full API request and response content for troubleshooting, with parity to the ODS/API `RequestResponseContentLog`.
It fixes the two ODS limitations the API work group named: capture is all-or-nothing across clients, and changing it needs a restart.

The argument, the decisions, and the evidence are in [design.md](./design.md).
This file is the manifest.

## Acceptance Criteria

| AC | Criterion | Where | State |
| --- | --- | --- | --- |
| AC1 | Relationship to DMS-62 decided | [D1](./design.md#d1-relationship-to-dms-62) | Drafted: keep separate, `relates to` |
| AC2 | Redaction requirements agreed for credentials and sensitive payload fields | [D4](./design.md#d4-credential-exclusion), [D5](./design.md#d5-payload-policy) | Credentials drafted (D4); payload policy pending (D5) |
| AC3 | Configuration surface and log sink chosen | [D2](./design.md#d2-control-surface), [D6](./design.md#d6-capture-store-and-retention) | Drafted: control surface (D2, readiness in D3) and the capture store (D6) |
| AC4 | Capture, configuration, redaction and documentation split into estimated stories | [Stories](./design.md#stories) | Drafted: six stories, 11 SP; story 06 depends on D5 |
| AC5 | This placeholder is closed once those stories exist | [Disposition](./design.md#disposition) | Pending |

## Documents

| Document | Status | Covers |
| --- | --- | --- |
| [design.md](./design.md) | **Drafting**, not yet approved | Requirements, current state, the ODS precedent, decisions D1 to D6, the story set, and the disposition |
| [plugins-DMS-1462/design.md](../plugins-DMS-1462/design.md) | Approved | The plugin `ILoggerProvider` rule that keeps capture out of the logging pipeline (R10) |
| [secrets-DMS-1503/README.md](../secrets-DMS-1503/README.md) | Filed | The format precedent for this folder, and the secret-sourcing options for a DMS setting |

## Tickets

Six stories under epic [DMS-1418](https://edfi.atlassian.net/browse/DMS-1418), not yet filed.
They split the scope the way AC4 names it: configuration (01, 04), capture (03, 05), redaction (02, and 06 if required), and documentation (07).

| Ticket | Draft | Title | Depends on | SP | Status |
| --- | --- | --- | --- | --- | --- |
| - | [01](./01-add-cms-request-capture-grants.md) | Add Request-Capture Grants to the Configuration Service | None | 2 | drafted |
| - | [02](./02-add-dms-credential-exclusion.md) | Add Credential Exclusion for Request/Response Capture | None | 2 | drafted |
| - | [03](./03-add-dms-capture-store-and-retention.md) | Add the Capture Store, Writer, and Retention Purge | 02 | 2 | drafted |
| - | [04](./04-add-dms-grant-polling-and-freshness.md) | Add Grant Polling and Freshness to DMS | 01 | 2 | drafted |
| - | [05](./05-add-dms-capture-pipeline.md) | Capture Request and Response Content in the DMS Pipeline | 02, 03, 04 | 2 | drafted |
| - | 06 | Payload redaction | - | - | only if the D5 policy requires redaction |
| - | [07](./07-document-request-response-capture.md) | Document Request/Response Capture | 01 to 05 | 1 | drafted |

The total is 11 SP, plus story 06 if it is filed.
The epic's Confluence estimate is 10 SP; grant polling was split out of the capture pipeline so that no story exceeds 2 SP.

### Blockers

| Ticket | Blocked by | Why |
| --- | --- | --- |
| 03 | 02 | The capture table is generated from the capture-row contract, which credential exclusion owns |
| 04 | 01 | The poller reads the active-grants endpoint |
| 05 | 02, 03, 04 | Every row passes through credential exclusion, is queued to the writer, and is selected from the snapshot |
| 07 | 01 to 05 | The guide documents their behavior |

If the payload policy requires redaction, story 06 is filed, and it also blocks 05 and 07: capture cannot be activated, or documented, without it.
Nothing else depends on the policy's answer.

## Filing

Not yet filed.
No Jira write happens before the team approves the decisions and stories, and the D5 payload policy must be recorded before that approval.

Each story is then filed under epic DMS-1418 with:

- fix version `Ed-Fi API v8.2`
- DMS-1419's labels: `no-blockers`, `stretch-goal`, `v8.2-phase4`
- its estimate as story points
- its TL;DR as the Executive Summary, and the rest of the file, without the frontmatter, as the description
- sprint API Platform Refinement
- a `Relates` link to DMS-1419, and `Blocks` links per the table above

DMS-1418 is linked `relates to` DMS-62 ([D1](./design.md#d1-relationship-to-dms-62)), and its provisional description is replaced with the agreed scope.
