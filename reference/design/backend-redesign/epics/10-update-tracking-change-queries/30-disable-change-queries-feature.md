---
jira: DMS-1191
jira_url: https://edfi.atlassian.net/browse/DMS-1191
---

# Spike: Runtime Feature Flag to Disable Change Queries

## Description

ODS exposes a `ApiSettings:Features:ChangeQueries` flag that, when false, returns the `Feature Disabled` (404) ProblemDetails for `/deletes`, `/keyChanges`, `/availableChangeVersions`, and the `?minChangeVersion=…&maxChangeVersion=…` filters on live resource and descriptor endpoints. Implementers also have to run SQL scripts to drop the tracking objects (triggers, tracked-change tables, mirror columns, auth views, `GetMaxChangeVersion`, sequence) to fully remove the feature's write-time and storage cost.

DMS v1.0 ships Change Queries as always-on. `reference/design/backend-redesign/design-docs/change-queries.md` § "How to disable the feature" describes the ODS shape; § ProblemDetails "Feature Disabled" notes the corresponding ProblemDetails is deferred. This spike investigates how DMS should support a disable workflow.

## Acceptance Criteria

- Decide whether DMS's disable surface is runtime-only (cheap; endpoints refuse but the trigger/storage cost remains), DDL-aware (also strips/skips the tracked-change DDL emission and the `tracked_changes_*` tables), or both with a documented progression.
- Specify the `Feature Disabled` ProblemDetails per `change-queries.md` § "Feature Disabled (404 Not Found)" (`urn:ed-fi:api:system:configuration:feature-disabled`) and which endpoints emit it. Cover `/availableChangeVersions`, `/deletes`, `/keyChanges`, and the live resource/descriptor `?minChangeVersion=…&maxChangeVersion=…` parameters.
- Specify whether disabling the feature retains the `ContentVersion`/`ContentLastModifiedAt` mirror columns.
- Specify the trigger behavior when the feature is disabled: are the `*_Stamp` triggers stripped of their `DocumentStamping.ChangeTracking` tombstone/key-change branches, or are the triggers removed entirely (in which case mirror maintenance has to be reattached)?
- Specify provisioning behavior: a fresh DMS instance provisioned with the feature disabled, and the migration path for an instance that wants to disable the feature post-provision.
- Cover the OpenAPI surface impact: are `/deletes` and `/keyChanges` paths excluded from emitted OpenAPI when the feature is disabled, or always emitted with runtime refusal?
- Once the proposal is reviewed and approved, create the implementation tickets covering config plumbing, ProblemDetails emission, conditional DDL emission, trigger conditional rendering, and provisioning idempotency. Link those follow-on tickets back to this spike.
