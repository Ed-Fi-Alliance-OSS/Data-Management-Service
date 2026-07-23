---
jira: DMS-1320
source_spike: DMS-1245
epic: DMS-1309
---

# Story: Emit/Provision Provider CDC Key and Database Support

## Design References

- **Connector topology and provider setup**: reference/design/cdc-streaming.md#connector-topology-and-provider-setup
- **Schema and query integration**: reference/design/cdc-streaming.md#schema-and-query-integration
- **Physical CDC heartbeat object**: reference/design/backend-redesign/design-docs/data-model.md#8-dmscdcheartbeat-opt-in-cdc-integration-object
- **Projector and source decision**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md

The referenced design sections define the opt-in provider objects and access requirements.
This story is only the work package for implementing them.

## Outcome

Deliver the PostgreSQL and SQL Server database setup consumed by relational CDC.

## Dependencies

- Depends on the ordinary source/cache schema from 18-00.

## Implementation Scope

- Add provider DDL/provisioning for the CDC source, key, capture, and heartbeat objects.
- Integrate those objects with generated manifests, binding-aware validation, and
  diagnostics.
- Add least-privilege connector access setup.
- Add provider metadata queries consumed by 19-00 continuity checks.

## Acceptance Evidence

- PostgreSQL and SQL Server DB-apply tests cover the provider object inventory and source
  records defined by the design references.
- Provisioning tests cover opt-in, eligibility, rerun, and binding-aware validation.
- Principal-access and provider-metadata tests cover the design-owned security and
  continuity observations.

## Not Assigned to This Story

- Connector JSON generation and registration are assigned to 19-02 and 19-04.
- Projector implementation is assigned to E18.
