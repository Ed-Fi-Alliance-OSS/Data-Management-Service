---
jira: DMS-966
jira_url: https://edfi.atlassian.net/browse/DMS-966
---

# Story: CLI Command — `pack build` Emits `.mpack`

## Description

Provide a CLI workflow to build mapping packs for one effective schema and one dialect, per:

- `reference/design/backend-redesign/design-docs/aot-compilation.md`
- `reference/design/backend-redesign/design-docs/mpack-format-v1.md` (envelope/payload + validation rules)

The pack builder must:
- build a deterministic payload,
- compute `payload_sha256` over the uncompressed payload bytes,
- compress with zstd,
- and emit a `MappingPackEnvelope` protobuf.

## Acceptance Criteria

- CLI can build packs for:
  - PostgreSQL dialect (`PGSQL`)
  - SQL Server dialect (`MSSQL`)
- The emitted envelope fields match the builder inputs:
  - `effective_schema_hash`, `dialect`, `relational_mapping_version`, `pack_format_version=1`.
- Envelope includes:
  - `payload_zstd`,
  - `payload_sha256` (SHA-256 over decompressed payload bytes),
  - `zstd_uncompressed_payload_length` (used for bounded decompression).
- Payload bytes are deterministically serialized (protobuf deterministic option) and stable for the same selection key.
- File naming/layout follows the recommended convention (non-normative) but the consumer relies on envelope fields, not filenames.

## Tasks

1. Add CLI parsing for `pack build`:
   - inputs (explicit ApiSchema list/fixture),
   - dialect selection,
   - output folder/root.
2. Implement payload build:
   - effective schema load + hash,
   - seed derivation,
   - relational model derivation,
   - plan compilation.
3. Implement pack envelope assembly:
   1. deterministic protobuf serialize payload bytes,
   2. compute SHA-256,
   3. zstd compress with bounded metadata,
   4. serialize envelope protobuf.
4. Add basic CLI tests validating successful pack creation and header correctness.
