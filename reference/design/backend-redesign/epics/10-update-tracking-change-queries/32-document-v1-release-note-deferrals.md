---
jira: DMS-1194
jira_url: https://edfi.atlassian.net/browse/DMS-1194
---

# Story: Document DMS v1.0 Change Queries Deferred Features in Release Notes

## Description

Document the DMS v1.0 Change Queries deferred-feature limitations in the DMS v1.0 release notes.

The Change Queries implementation for DMS v1.0 intentionally does not include snapshot support, a way to disable the Change Queries feature, or custom view-based authorization strategies for Change Query endpoints. 

The release notes must call out these limitations explicitly so operators, API Publisher users, and implementers do not assume ODS parity in these areas.

Optional: The release notes links to the follow-up tickets:
- `reference/design/backend-redesign/epics/10-update-tracking-change-queries/29-snapshot-support.md`
- `reference/design/backend-redesign/epics/10-update-tracking-change-queries/30-disable-change-queries-feature.md`
- `reference/design/backend-redesign/epics/10-update-tracking-change-queries/31-custom-view-based-readchanges-authorization.md`