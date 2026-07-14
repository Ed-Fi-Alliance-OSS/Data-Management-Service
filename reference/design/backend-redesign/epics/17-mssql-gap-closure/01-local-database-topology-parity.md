---
jira: DMS-1270
jira_url: https://edfi.atlassian.net/browse/DMS-1270
---

# Story: Align Local Database Topology Across Engines with an Optional Separate CMS Database

## Description

Provide one consistent local topology contract for PostgreSQL and MSSQL. The default remains the shared
database delivered by `DMS-1255`; operators may explicitly select a production-like topology in which CMS
uses a dedicated configuration database and DMS instances use separate datastore databases.

The topology choice must flow through the local start scripts, published-image start scripts, bootstrap
wrappers, and `build-dms.ps1 StartEnvironment`. It must not be inferred from the selected database engine.

## Topology Contract

| Mode | CMS database | DMS datastore | Required behavior |
| --- | --- | --- | --- |
| Shared (default) | Selected local database | Same physical database | Preserve the `DMS-1255` default for PostgreSQL and MSSQL. |
| Separate | Dedicated configuration database | Selected DMS datastore database | Create and initialize CMS independently; never redirect DMS schema or template restore into the CMS database. |

Expose the separate mode through `-SeparateConfigDatabase`. Use one effective configuration-database name
as the interpolation seam for both engines rather than embedding engine-specific names throughout the
scripts. The switch and effective name must be forwarded consistently by every wrapper and entry point.

## Database Creation

- MSSQL keeps its guarded CMS-database creation behavior in the OpenIddict initialization path.
- PostgreSQL gains equivalent guarded creation for the separate CMS database.
- The PostgreSQL container initialization path must also create the separate CMS database when Keycloak mode
  bypasses the OpenIddict setup path.
- Document that PostgreSQL container-init changes require a fresh database volume; initialization scripts do
  not rerun against an existing volume.
- Creation must be idempotent and must fail with a clear diagnostic when an existing database cannot be used.

## Coordination with Template Restore

[`DMS-1271`](02-database-template-restore-workflow.md) owns template restore. In shared mode, restore occurs
before CMS initialization, after which CMS creates its schema in the restored database. In separate mode,
restore targets only the DMS datastore and never the CMS database.

## Acceptance Criteria

- `-SeparateConfigDatabase` is available on local/published start scripts, bootstrap wrappers, and
  `build-dms.ps1 StartEnvironment` with consistent forwarding and validation.
- Omitting the switch preserves the shared-database default on both engines.
- Selecting the switch points CMS at a dedicated configuration database without changing the DMS datastore
  selection.
- PostgreSQL and MSSQL can create a missing dedicated CMS database through every supported identity-provider
  path.
- All four engine-by-topology cells start successfully, and CMS/DMS schemas land in the intended databases.
- Pester coverage verifies forwarding, defaults, mutual exclusions, and target database selection.
- README and environment-file documentation explain both modes and the PostgreSQL fresh-volume requirement.

## Non-Goals

- Changing the shared local default delivered by `DMS-1255`.
- Designing production multi-tenant routing or a new CMS datastore model.
- Restoring database-template packages; `DMS-1271` owns restore sequencing and execution.
- Combining CMS and DMS persistence implementations.

## Design References

- [`../16-bootstrap/EPIC.md`](../16-bootstrap/EPIC.md)
- [`../../design-docs/bootstrap/bootstrap-design.md`](../../design-docs/bootstrap/bootstrap-design.md)
- [`../../design-docs/bootstrap/command-boundaries.md`](../../design-docs/bootstrap/command-boundaries.md)
- [`02-database-template-restore-workflow.md`](02-database-template-restore-workflow.md)
