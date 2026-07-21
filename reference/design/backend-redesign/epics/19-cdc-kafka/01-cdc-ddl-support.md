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

## Dependencies

- Depends on the ordinary source/cache schema, UUID validation trigger, and singleton
  identity/state foundation from 18-00.

## Deliverables

1. Add provider-specific publication/capture and delete-key setup.
2. Extend generated provisioning manifests and diagnostics with CDC setup status.
3. Derive publication/slot or capture-instance identity from the immutable deployment
   binding generation and validate existing artifacts against that binding.
4. Verify exact provider identifiers and syntax against generated DDL and the pinned
   Debezium 3.6 connector version.
5. Add negative validation for missing key, replica-identity, capture prerequisites, or
   artifacts that exist without matching binding state.
6. Validate that `DocumentCache.DocumentUuid` is available as the non-indexed custom
   Debezium key column and that the provider cache trigger enforces equality with the
   canonical UUID for the same `DocumentId`; do not add a cache UUID or canonical
   composite identity index as a CDC prerequisite.
7. Create and seed the opt-in singleton `dms.CdcHeartbeat` table with constrained
   `HeartbeatId`, nonnegative `HeartbeatSequence`, and `HeartbeatAt` columns. Include it
   in the narrow PostgreSQL publication and enable a dedicated SQL Server CDC capture
   instance. Emit provider-qualified identifiers and the fixed atomic sequence-increment
   statement consumed by connector template generation; ordinary non-CDC provisioning
   does not create it.
8. Grant the connector principal only the provider replication/CDC reads and heartbeat
   singleton read/update access needed by the generated query; grant no document-table
   write through this setup.

## Acceptance Evidence

- PostgreSQL DB-apply tests validate publication, replica identity, and a
  `DocumentUuid`-keyed canonical delete source record.
- SQL Server DB-apply tests validate capture instances and the equivalent delete key.
- SQL Server DB-apply and connector smoke tests include SQL Server 2025 as the required
  Ed-Fi known-working qualification target.
- Cache-source records for both providers expose the DMS-projected `StreamEtag` required
  by connector shaping.
- Cache-source smoke tests use the non-indexed `DocumentUuid` custom key and prove its
  serialized value matches the canonical delete key for the same document.
- Both providers distinguish cache maintenance records for later filtering.
- PostgreSQL DB-apply tests prove the generated heartbeat update is visible through the
  logical publication. SQL Server DB-apply tests prove its update after-image exposes
  `__$start_lsn`, `__$seqval`, and the incremented sequence through the dedicated capture
  instance. Repeated setup preserves the singleton and its sequence.
- Least-privilege tests prove the connector principal can advance the heartbeat but cannot
  insert, update, or delete canonical or cache document rows.
- Ordinary relational provisioning tests prove CDC artifacts remain opt-in.

## Out of Scope

- Connector JSON generation or REST registration.
- Projector implementation.
