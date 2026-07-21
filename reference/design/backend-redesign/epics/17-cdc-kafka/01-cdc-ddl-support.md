---
jira: TBD
source_spike: DMS-1245
epic: TBD
---

# Story: Emit/Provision Provider CDC Key and Database Support

## Design References

- [Connector topology and provider setup](../../../cdc-streaming.md#connector-topology-and-provider-setup)
- [DDL and query support](../../../cdc-streaming.md#ddl-and-query-support)
- [Projector and source decision](../../design-docs/cdc/0001-relational-cdc-projector-and-sources.md)

## Outcome

Implement opt-in PostgreSQL and SQL Server database provisioning required by the
relational CDC source/key design.

## Deliverables

1. Add provider-specific publication/capture and delete-key setup.
2. Extend generated provisioning manifests and diagnostics with CDC setup status.
3. Derive publication/slot or capture-instance identity from the immutable deployment
   binding generation and validate existing artifacts against that binding.
4. Verify exact provider identifiers and syntax against generated DDL and the pinned
   connector version.
5. Add negative validation for missing key, replica-identity, capture prerequisites, or
   artifacts that exist without matching binding state.
6. Validate that `DocumentCache.DocumentUuid` is available as the non-indexed custom
   Debezium key column and that the provider cache trigger enforces equality with the
   canonical UUID for the same `DocumentId`; do not add a cache UUID or canonical
   composite identity index as a CDC prerequisite.

## Acceptance Evidence

- PostgreSQL DB-apply tests validate publication, replica identity, and a
  `DocumentUuid`-keyed canonical delete source record.
- SQL Server DB-apply tests validate capture instances and the equivalent delete key.
- Cache-source records for both providers expose the DMS-projected `StreamEtag` required
  by connector shaping.
- Cache-source smoke tests use the non-indexed `DocumentUuid` custom key and prove its
  serialized value matches the canonical delete key for the same document.
- Both providers distinguish cache maintenance records for later filtering.
- Ordinary relational provisioning tests prove CDC artifacts remain opt-in.

## Out of Scope

- Connector JSON generation or REST registration.
- Projector implementation.
