---
jira:
epic: DMS-1418
source_spike: DMS-1419
---

# Story: Document Request/Response Capture

## TL;DR

Writes the operator guide for request/response capture: how to turn it on and off, how quickly changes take effect, and the privacy and retention duties that come with storing student data.
Also brings the logging, configuration, and requirements documents and the 8.2.0 changelog in line with the feature.

## Description

Captured content is student data, so the documentation has to state the privacy and retention obligations plainly (R7).
This story writes the operator guide and updates every document the feature changes.
User and release documentation cites public sources only: code and feature files are verification, never citations.

Per `reference/design/request-response-capture-DMS-1419/design.md`, all decisions, and the payload-policy decision recorded in "## D5: Payload Policy".

**Citation convention.**
Unprefixed paths are relative to the repository root.

## Acceptance Criteria

**Operator guide**

A new `docs/REQUEST-RESPONSE-CAPTURE.md` states:

- how to provision the capture database, with the SchemaTools command, and how to configure DMS to use it
- how to enable, list, and end a grant for an API client, with the CMS endpoints, the modes, and the maximum duration
- the propagation bound: enable and disable reach every instance within `A` while CMS is reachable; while it is not, disable still takes effect and enable waits
- what is captured and what is never captured, including the D4 withholding rule and the R4 scope statement, word for word
- the payload policy as decided in design.md, D5
- that the capture database holds student data and must be secured and access-controlled like a data store, across every tenant it holds
- retention: `RetentionDays`, the purge, and the volume a grant under `All` can generate
- example SQL a BI or workflow tool can run against the table

**Other documents**

- `docs/CONFIGURATION.md` documents every `RequestResponseCapture` DMS setting and the CMS `RequestCapture:MaximumDurationMinutes` setting, with defaults.
- `docs/LOGGING.md` states that captured content never passes through logging, and stays consistent with the outcome of [DMS-1550](https://edfi.atlassian.net/browse/DMS-1550).
- `docs/PRD-Ed-Fi-API-v8.0.md`:
    - NFR-OBS-2 and its warning reflect that request capture expires on its own
    - NFR-OBS-3 states how request capture meets it, per the interpretation recorded in design.md, D5
- `docs/changelog/8.2.0.md` adds the feature under Improvements, linking the operator guide.

**Checks**

- Relative links and anchors in every changed document resolve.

## Dependencies

- CMS request-capture grants (01), credential exclusion (02), capture store and retention (03), grant polling and freshness (04), and the capture pipeline (05): the guide documents their behavior.

## Estimate

1 SP.
