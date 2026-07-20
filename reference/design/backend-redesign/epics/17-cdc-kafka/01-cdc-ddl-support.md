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
3. Verify exact provider identifiers and syntax against generated DDL and the pinned
   connector version.
4. Add negative validation for missing key, replica-identity, or capture prerequisites.

## Acceptance Evidence

- PostgreSQL DB-apply tests validate publication, replica identity, and a
  `DocumentUuid`-keyed canonical delete source record.
- SQL Server DB-apply tests validate capture instances and the equivalent delete key.
- Both providers distinguish cache maintenance records for later filtering.
- Ordinary relational provisioning tests prove CDC artifacts remain opt-in.

## Out of Scope

- Connector JSON generation or REST registration.
- Projector implementation.
