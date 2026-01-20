# Story: Optional Descriptor Seeding During Provisioning (`ddl provision --seed-descriptors`)

## Description

Provide an optional provisioning workflow to pre-load descriptor reference data before first DMS run.

Inputs should support reuse of existing Ed-Fi ecosystem assets (e.g., ODS-style `InterchangeDescriptors` XML), while the output is DMS-native writes to:

- `dms.Document` (descriptor documents),
- `dms.Descriptor` (descriptor fields), and
- `dms.ReferentialIdentity` (descriptor referential ids keyed by descriptor type + normalized URI).

This is not required for correctness if descriptor resources are created via the API, but it improves first-run ergonomics and supports offline provisioning pipelines.

## Acceptance Criteria

- The CLI supports `ddl provision --seed-descriptors <path>` where `<path>` is a file or directory.
- Seeding is idempotent and safe to rerun:
  - existing descriptor rows are treated as no-ops when identical,
  - conflicts on identity keys fail fast with actionable diagnostics.
- Seeding supports at least one input format compatible with existing Ed-Fi assets:
  - `InterchangeDescriptors` XML (ODS-style).
- Seeding uses the same normalization rules as Core for descriptor URIs and referential-id computation.
- Seeding can run without the DMS server process (direct DB connection) and works for PostgreSQL and SQL Server.

## Tasks

1. Define the seed input contract (directory scanning rules, file formats, error handling).
2. Implement an `InterchangeDescriptors` XML reader that produces descriptor upsert commands keyed by descriptor resource type and URI.
3. Implement transactional seed execution against the provisioned DB:
   - upsert `dms.Document`, `dms.Descriptor`, and `dms.ReferentialIdentity`,
   - respect descriptor identity immutability rules.
4. Add a small fixture-based integration test for descriptor seeding (one descriptor type, a few rows) for both dialects where feasible.
