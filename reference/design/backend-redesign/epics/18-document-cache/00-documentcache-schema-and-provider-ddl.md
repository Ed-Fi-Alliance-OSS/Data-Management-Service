---
jira: TBD
source_spike: DMS-1246
epic: TBD
related:
  - DMS-1245
---

# Story: Finalize DocumentCache Schema and Provider DDL

## Design References

- [Cached document contract](../../design-docs/cdc/0001-relational-cdc-projector-and-sources.md#cached-document-contract)
- [Relational data model](../../design-docs/data-model.md)
- [DDL generation](../../design-docs/ddl-generation.md)

## Outcome

Deliver the always-provisioned PostgreSQL and SQL Server schema foundation for newly
provisioned databases, consumed by all DocumentCache materialization, write,
reconciliation, read, health, and CDC work.

## Dependencies

- Depends on E02's delivered deterministic DDL/provisioning infrastructure and E10's
  canonical `dms.Document` representation stamps.
- Unblocks E18 stories 18-02 through 18-07 and supplies the ordinary source/cache schema
  consumed by E19. Provider publication and capture artifacts remain E19-owned.

## Deliverables

1. Revise the provider-equivalent `dms.DocumentCache` definition and always provision it.
   Replace the obsolete `Etag` column with required non-null `StreamEtag`; retain
   `DocumentId`, `DocumentUuid`, `ProjectName`, `ResourceName`, `ResourceVersion`,
   `ContentVersion`, `LastModifiedAt`, `DocumentJson`, and `ComputedAt` with the canonical
   provider types and JSON-object constraints.
2. Keep compact `DocumentId` as the cache primary key and foreign key to `dms.Document`
   with `ON DELETE CASCADE`. Remove `UX_DocumentCache_DocumentUuid` and
   `IX_DocumentCache_ProjectName_ResourceName_LastModifiedAt`; do not add a canonical
   `(DocumentId, DocumentUuid)` index. Always provision the
   `dms.Document(ContentVersion, DocumentId)` discovery/audit index.
3. Emit the provider-specific cache insert/update trigger that rejects a `DocumentUuid`
   mismatch with the canonical `dms.Document` row. Use the stable
   `TR_DocumentCache_ValidateDocumentUuid` trigger name and, for PostgreSQL, the stable
   `TF_DocumentCache_ValidateDocumentUuid` function name. The trigger performs no work for
   ordinary canonical writes.
4. Add the always-provisioned singleton `dms.DataStoreIdentity` table and deterministic
   insert-if-absent SQL that asks the database to generate a random `SourceIdentity` UUID.
   Provisioning reruns validate and preserve the existing singleton; independently
   provisioned databases receive different UUIDs while emitted SQL remains deterministic.
5. Add the always-provisioned singleton `dms.DocumentCacheState` row with
   `CacheAheadRecoveryRequired` initially clear. Enforce the singleton key and ensure
   provisioning reruns never reset an existing latch.
6. Update the relational model, both DDL emitters, provisioning composition, unit and
   snapshot fixtures, DB-apply manifests, and create-only provisioning documentation to
   match the complete column, constraint, identity, state, and access-path inventory. State
   that v1 supports only newly provisioned databases and does not alter a legacy cache
   schema in place.

## Acceptance Evidence

- PostgreSQL and SQL Server DDL tests prove every emitted schema includes
  `dms.DataStoreIdentity`, singleton `dms.DocumentCacheState` initialized clear,
  `dms.DocumentCache.StreamEtag`, and
  `dms.Document(ContentVersion, DocumentId)`; preserves the cache `DocumentId` primary/FK;
  emits no obsolete `DocumentCache.Etag`; and excludes
  `IX_DocumentCache_ProjectName_ResourceName_LastModifiedAt`,
  `UX_DocumentCache_DocumentUuid`, and any new canonical
  `(DocumentId, DocumentUuid)` index.
- Unit and snapshot tests pin the provider-equivalent table, column, constraint, index,
  trigger/function, and singleton initialization inventory without normalizing away schema
  differences other than the generated source UUID.
- Provisioning and DB-apply rerun tests prove `dms.DataStoreIdentity.SourceIdentity` is
  generated once and retained, independently provisioned databases receive different
  values, and emitted SQL remains deterministic.
- Provider DB-apply tests prove cache insert/update statements with the matching canonical
  UUID succeed, mismatches fail atomically through the validation trigger, no mismatched
  CDC row can commit, and ordinary canonical writes perform no cache-trigger work.
- Singleton-state tests prove first provisioning creates exactly one clear row, another key
  is rejected, and rerunning provisioning preserves a previously set
  `CacheAheadRecoveryRequired` latch.
- Introspection manifests and create-only provisioning tests cover the new objects for both
  providers and detect accidental reintroduction of the obsolete cache inventory.
- Scope tests and documentation prove the v1 schema is created for new databases only; no
  emitted path renames legacy `Etag`, removes the obsolete UUID constraint, or otherwise
  upgrades an already-provisioned database.

## Out of Scope

- Runtime materialization, cache upsert, reconciliation, cache-backed reads, or health.
- V1 support for already-provisioned databases, including schema migration or upgrade;
  provisioning remains create-only.
- CDC heartbeat/publication objects, replica/capture configuration, connectors, topics, or
  Kafka message shaping.
