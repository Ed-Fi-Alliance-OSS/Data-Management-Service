---
jira: TBD
source_spike: DMS-1245
epic: TBD
related:
  - DMS-1246
---

# Story: Wire CDC Enablement to Two-Table Source Guarantees

## Description

Make CDC enablement validate the complementary source roles defined by DMS-1245:

- `dms.DocumentCache` create/update/snapshot events supply document upserts,
- `dms.Document` deletes supply authoritative tombstones,
- cache deletes/truncates and all other document operations are ignored.

`dms.DocumentCache` is optional for normal API correctness but conditionally required for
CDC upserts. Its projector remains asynchronous. Cache absence or failure never blocks
API deletion because domain lifecycle comes from `dms.Document`.

## Acceptance Criteria

- CDC has explicit enablement separate from read acceleration and Kafka UI startup.
- CDC applies only to an explicit deployment-configured target list keyed by
  `(tenant key, DataStoreId)`.
- Registration prerequisites verify, per target:
  - `dms.DocumentCache`, `dms.Document`, and projector state objects exist,
  - asynchronous projection and stale-write fencing are enabled,
  - provider-specific CDC setup captures exactly both tables,
  - `DocumentUuid` key setup is valid for both tables,
  - PostgreSQL `dms.Document` replica identity is valid for non-primary-key delete capture,
  - the source-aware operation filter/tombstone transform is installed,
  - no other configured target resolves to the same physical database.
- Registration may occur before initial DocumentCache backfill. CDC is not advertised as
  ready until the bounded backfill epoch is complete, unresolved current projection
  failures are absent, projection lag is within threshold, and the connector has
  snapshotted/caught up.
- Readiness exposes the completed backfill epoch id and target content version as the
  upsert cutover marker.
- Readiness is keyed by `(tenant key, DataStoreId)` and evaluated from explicit execution
  context rather than HTTP route/JWT selection.
- The configured target must remain present and resolve to its startup provider/physical
  source binding. Confirmed drift is latched as
  `CdcSourceDriftRequiresDeployment`; same-source credential/operational changes are not
  drift.
- Duplicate or semantically equivalent physical database targets are rejected without
  logging credentials or unsanitized physical identifiers.
- Diagnostics distinguish missing tables, projector disabled/unhealthy, incomplete
  backfill, projection lag/failures, invalid key/replica setup, missing operation filter,
  unsupported provider, missing configured target, retryable source-identity resolution,
  and confirmed source drift.
- API correctness, authorization, routing, GET/query, Change Queries, and all mutations
  remain independent of CDC readiness.
- Delete readiness does not inspect DocumentCache. Provider CDC tests prove
  `dms.Document` delete key/filter/tombstone behavior in the CDC epic.

## Tasks

1. Add a data-store-specific prerequisite/readiness abstraction that distinguishes
   `CanRegisterConnector` from completed end-to-end readiness.
2. Bind CDC enablement and the explicit target list separately from read acceleration and
   Kafka UI settings.
3. Add provider-specific physical database identity resolution and conflict tests.
4. Validate two-table capture, `DocumentUuid` keys, PostgreSQL replica identity, and
   source-operation filtering before registration.
5. Consume DMS-1246's projector/backfill/failure health for the upsert-readiness portion.
6. Add post-start configured-target/source-binding diagnostics without changing routing.
7. Add tests that non-CDC startup remains valid without `dms.DocumentCache`.
8. Add tests proving cache failure does not block API deletion.

## Out of Scope

- Implementing the projector.
- Implementing connector registration.
- Publishing Kafka records.
