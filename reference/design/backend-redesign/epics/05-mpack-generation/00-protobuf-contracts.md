# Story: Protobuf Contracts for PackFormatVersion=1

## Description

Define the protobuf schema for `.mpack` files and provide a shared C# contracts project/package that both pack producers and consumers reference, per:

- `reference/design/backend-redesign/mpack-format-v1.md` (normative)
- `reference/design/backend-redesign/aot-compilation.md` (“contracts package” guidance)

Key requirements:
- PackFormatVersion=1 supports `MappingPackEnvelope` + compressed `MappingPackPayload`.
- Protobuf determinism must be supported by the chosen runtime (`Google.Protobuf` deterministic serialization option).
- The `.proto` schema must avoid `map<...>` fields to keep ordering explicit and comparable.

## Acceptance Criteria

- A `.proto` schema exists for PackFormatVersion=1 that encodes:
  - envelope key fields (`effective_schema_hash`, `dialect`, `relational_mapping_version`, `pack_format_version`)
  - compression metadata (`compression_algorithm`, `zstd_uncompressed_payload_length`)
  - integrity fields (`payload_sha256`) and `payload_zstd`.
- The payload schema uses only `repeated` lists (no `map<>`) where ordering matters.
- A shared C# contracts project/package is buildable without requiring developers to manually run `protoc`.
- Versioning rules are documented/enforced:
  - field numbers are never reused,
  - additive changes are backward compatible,
  - PackFormatVersion gates breaking changes.

## Tasks

1. Create/identify a dedicated “mapping pack contracts” project for `.proto` + generated C# types.
2. Implement the `.proto` messages for `MappingPackEnvelope` and `MappingPackPayload` per `mpack-format-v1.md`.
3. Configure deterministic protobuf serialization capability in producer/consumer usage.
4. Add a small unit test that:
   1. serializes a minimal payload deterministically,
   2. round-trips decode,
   3. validates required fields are present.
5. Document field-numbering and compatibility policy in the contracts project README.

