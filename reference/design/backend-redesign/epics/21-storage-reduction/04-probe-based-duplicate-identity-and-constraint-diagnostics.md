---
jira: DMS-1446
jira_url: https://edfi.atlassian.net/browse/DMS-1446
epic: DMS-1402
---

# Story: Move Duplicate-Identity and Constraint Diagnostics to Compiled Probes

## Outcome

Remove diagnostic and constraint-classification dependencies on ReferentialIdentity trigger metadata
before the RI triggers are removed.

## Design References

- [Natural-key resolution](../../design-docs/natural-key-resolution.md)
- [E21 dependency chain](EPIC.md#dependency-chain)

## Dependencies

- Depends on [DMS-1445 — natural-key probe metadata](03-natural-key-probe-metadata.md).
- This story and DMS-1450 block DMS-1451.

## Implementation Scope

- Build `duplicateIdentityValues` responses from compiled own-key probe metadata.
- Resolve root natural-key columns from compiled own-key probe metadata.
- Do not parse trigger metadata, constraint names, discriminator strings, or generated SQL to
  determine natural-key members.
- Use the probe's canonical identity JSON path as the response member name and its ordered physical
  columns as the root natural-key constraint members.

## Acceptance Criteria

- Duplicate-identity response bodies and paths remain unchanged on PostgreSQL and SQL Server.
- Duplicate-identity mapping and root natural-key constraint classification pass when
  `ReferentialIdentityMaintenance` trigger metadata is withheld from a test mapping set.
- Descriptor-valued identity diagnostics and constraint members retain semantic key-column order.
