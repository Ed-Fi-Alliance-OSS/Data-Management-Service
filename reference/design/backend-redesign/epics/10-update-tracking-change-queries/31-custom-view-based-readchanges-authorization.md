---
jira: DMS-TBD
jira_url: https://edfi.atlassian.net/browse/DMS-TBD
---

# Spike: Custom View-Based `ReadChanges` Authorization

## Description

`change-queries.md` explicitly defers the custom view-based authorization strategy for DMS `/deletes` and `/keyChanges` endpoints. DMS v1.0 treat it as unsupported for `ReadChanges` and returns the existing security-configuration ProblemDetails when such a strategy is configured.

This spike investigates how DMS should support custom view-based authorization for Change Query tracked-change endpoints while preserving ODS behavior where practical. The design must account for the DMS relational backend's `DocumentId`-based person storage, tracked-change old/new columns, per-resource `tracked_changes_*` tables, and the existing authorization composition rules from `auth.md`.

Refer to `reference/design/backend-redesign/design-docs/auth.md` section "View-based authorization strategy" and `reference/design/backend-redesign/design-docs/change-queries.md` section "Authorization" for the current behavior and deferred scope.

## Acceptance Criteria

- Specify how custom view-based `ReadChanges` strategies are recognized, including ODS-compatible strategy-name parsing, basis-resource selection, and standard-over-extension precedence.
- Specify how `/deletes` and `/keyChanges` join tracked-change rows to custom authorization views, including old-value vs. new-value handling and the ODS restriction that Change Query custom view authorization cannot map through non-primary-key target columns.
- Identify whether tracked-change tables must store additional basis-resource key columns to support custom views, and define how those columns would be derived, emitted, and populated if needed.
- Preserve the authorization composition semantics from `auth.md`: custom view-based strategies are AND-composed with other non-relationship strategies and apply as additional filters.
- Define the expected error behavior for missing custom views, invalid view columns, unsupported basis resources, and null required authorization values. Decide which ODS ProblemDetails are preserved and where DMS should use existing security-configuration errors instead.
- Once the proposal is reviewed and approved, create the implementation tickets covering derived metadata changes, SQL planning, validation/error handling, tests, and documentation. Link those follow-on tickets back to this spike.
