---
jira: TBD
source_spike: DMS-1245
epic: TBD
related:
  - DMS-1246
---

# Story: Wire CDC Targets to Two-Table Source Guarantees

## Description

Make every configured CDC target validate the complementary source roles defined by
DMS-1245:

- `dms.DocumentCache` create/update/snapshot events supply document upserts,
- `dms.Document` deletes supply authoritative tombstones,
- cache deletes/truncates and all other document operations are ignored.

`dms.DocumentCache` is optional for normal API correctness but conditionally required for
CDC upserts. Its projector remains asynchronous. Cache absence or failure never blocks
API deletion because domain lifecycle comes from `dms.Document`.

## Acceptance Criteria

- `KafkaCdc:Targets` is the sole runtime CDC enablement contract: an empty list disables
  CDC, and every `(tenant key, DataStoreId)` entry opts in exactly that data store.
- Every target entry implies asynchronous DocumentCache projection for that data store;
  it does not require `DocumentCache:Enabled` or `ReadAcceleration:Enabled` and does not
  select unlisted CMS data stores.
- Kafka UI and process-wide DocumentCache/read-acceleration settings do not add CDC
  targets.
- Registration prerequisites verify, per target:
  - `dms.DocumentCache` and `dms.Document` exist,
  - asynchronous projection selected by the target entry and stale-write fencing are
    available,
  - provider-specific CDC setup captures exactly both tables,
  - `DocumentUuid` key setup is valid for both tables,
  - PostgreSQL `dms.Document` replica identity is valid for non-primary-key delete capture,
  - the source-aware operation filter/tombstone transform is installed,
  - no other configured target resolves to the same physical database.
- Registration occurs before reconciliation writes that must be observed. CDC is not
  advertised as ready until the exact current projection mismatch count is zero and the
  connector has snapshotted/caught up through a source position at or after that
  observation.
- Readiness does not use a backfill epoch or maximum projected `ContentVersion` as an
  upsert cutover marker.
- Readiness is keyed by `(tenant key, DataStoreId)` and evaluated from explicit execution
  context rather than HTTP route/JWT selection.
- The configured target must remain present and resolve to its startup provider/physical
  source binding. Confirmed drift is latched as
  `CdcSourceDriftRequiresDeployment`; same-source credential/operational changes are not
  drift.
- Duplicate or semantically equivalent physical database targets are rejected without
  logging credentials or unsanitized physical identifiers.
- Diagnostics distinguish missing tables, projector disabled/unhealthy, nonzero
  mismatch count/age, invalid key/replica setup, missing operation filter,
  unsupported provider, missing configured target, retryable source-identity resolution,
  and confirmed source drift.
- API correctness, authorization, routing, GET/query, Change Queries, and all mutations
  remain independent of CDC readiness.
- Delete readiness does not inspect DocumentCache. Provider CDC tests prove
  `dms.Document` delete key/filter/tombstone behavior in the CDC epic.

## Tasks

1. Add a data-store-specific prerequisite/readiness abstraction that distinguishes
   `CanRegisterConnector` from completed end-to-end readiness.
2. Bind the explicit target list as CDC enablement and contribute each entry to the
   effective projection target set, independently of read acceleration and Kafka UI
   settings.
3. Add provider-specific physical database identity resolution and conflict tests.
4. Validate two-table capture, `DocumentUuid` keys, PostgreSQL replica identity, and
   source-operation filtering before registration.
5. Consume DMS-1246's mismatch-derived projection health/completeness for the
   upsert-readiness portion.
6. Add post-start configured-target/source-binding diagnostics without changing routing.
7. Add tests that an empty target list performs no CDC setup and remains valid without
   `dms.DocumentCache` when no other capability selects projection.
8. Add tests proving cache failure does not block API deletion.

## Out of Scope

- Implementing the projector.
- Implementing connector registration.
- Publishing Kafka records.
