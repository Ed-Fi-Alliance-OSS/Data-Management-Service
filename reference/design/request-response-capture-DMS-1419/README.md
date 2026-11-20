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
| AC4 | Capture, configuration, redaction and documentation split into estimated stories | [Stories](./design.md#stories) | Pending |
| AC5 | This placeholder is closed once those stories exist | [Disposition](./design.md#disposition) | Pending |

## Documents

| Document | Status | Covers |
| --- | --- | --- |
| [design.md](./design.md) | **Drafting**, not yet approved | Requirements, current state, the ODS precedent, decisions D1 to D6, the story set, and the disposition |
| [plugins-DMS-1462/design.md](../plugins-DMS-1462/design.md) | Approved | The plugin `ILoggerProvider` rule that keeps capture out of the logging pipeline (R10) |
| [secrets-DMS-1503/README.md](../secrets-DMS-1503/README.md) | Filed | The format precedent for this folder, and the secret-sourcing options for a DMS setting |

## Tickets

Pending.
The story drafts, their dependencies, and the blocker table are added once the decisions are written.

## Filing

Pending.
No Jira write happens before the team approves the decisions and stories.
Each story is then filed under epic DMS-1418 with fix version `Ed-Fi API v8.2`, DMS-1419's labels, and sprint API Platform Refinement.
