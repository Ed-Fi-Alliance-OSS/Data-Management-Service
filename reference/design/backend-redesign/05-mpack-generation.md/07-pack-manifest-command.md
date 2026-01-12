# Story: CLI Command — `pack manifest` (Inspect/Validate Existing `.mpack`)

## Description

Provide a CLI command that reads an existing `.mpack`, validates it, and emits deterministic semantic manifests without rebuilding the pack.

This supports:
- debugging pack distribution issues,
- CI diagnostics,
- and harness workflows that want to compare decoded semantics rather than raw bytes.

Aligns with the suggested `dms-schema pack manifest` deliverable in `reference/design/backend-redesign/ddl-generation.md` and the manifest guidance in `reference/design/backend-redesign/ddl-generator-testing.md`.

## Acceptance Criteria

- CLI accepts:
  - `.mpack` file path,
  - optional output directory (or stdout),
  - optional flags controlling which manifests to emit.
- CLI validates the pack per `reference/design/backend-redesign/mpack-format-v1.md`:
  - envelope key fields,
  - bounded decompression,
  - `payload_sha256` verification,
  - payload invariant validation.
- CLI emits:
  - `pack.manifest.json` (decoded payload semantics),
  - optionally `mappingset.manifest.json` (after `MappingSet.FromPayload(...)`).
- Manifest outputs are deterministic and match the library emitters used by tests (`05-mpack-generation.md/04-pack-manifests.md`).

## Tasks

1. Add CLI parsing and help text for `pack manifest`.
2. Implement `.mpack` read + validate + decode using the shared pack consumer/validator.
3. Emit `pack.manifest.json` and optionally `mappingset.manifest.json` to files or stdout.
4. Add CLI unit tests covering:
   1. missing/invalid files,
   2. wrong header fields,
   3. bad `payload_sha256`,
   4. successful manifest emission.

