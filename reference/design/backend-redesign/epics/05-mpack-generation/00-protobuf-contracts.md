---
jira: DMS-964
jira_url: https://edfi.atlassian.net/browse/DMS-964
---

# Story: Protobuf Contracts for PackFormatVersion=1

## Description

Define the protobuf schema for `.mpack` files and provide a shared C# contracts project/package that both pack producers and consumers reference, per:

- `reference/design/backend-redesign/design-docs/mpack-format-v1.md` (normative)
- `reference/design/backend-redesign/design-docs/aot-compilation.md` (“contracts package” guidance)
- `reference/design/backend-redesign/epics/15-plan-compilation/02-plan-contracts-and-deterministic-bindings.md` (E15 normalized DTO roundtrip vectors)

Key requirements:
- PackFormatVersion=1 supports `MappingPackEnvelope` + compressed `MappingPackPayload`.
- Protobuf determinism must be supported by the chosen runtime (`Google.Protobuf` deterministic serialization option).
- The `.proto` schema must avoid `map<...>` fields to keep ordering explicit and comparable.
- Once the contracts project/package exists, E15 test-only normalized DTOs in Plans unit tests are replaced by protobuf-generated contracts without changing test intent or vector coverage.

## Acceptance Criteria

- A `.proto` schema exists for PackFormatVersion=1 that encodes:
  - envelope key fields (`effective_schema_hash`, `dialect`, `relational_mapping_version`, `pack_format_version`)
  - compression metadata (`compression_algorithm`, `zstd_uncompressed_payload_length`)
  - integrity fields (`payload_sha256`) and `payload_zstd`.
- The payload schema uses only `repeated` lists (no `map<>`) where ordering matters.
- A shared C# contracts project/package is buildable without requiring developers to manually run `protoc`.
- E15 roundtrip tests swap DTO contract types to protobuf-generated contract types while preserving existing test vectors and canonical JSON/hash assertions (ordering/binding drift detection remains intact).
- Deterministic protobuf serialization is required in producer/consumer and test paths (runtime deterministic option enabled when producing bytes for comparisons/hashes).
- Unit-test verification for this story includes running the existing Plans unit test project: `dotnet test --no-restore ./src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj`.
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
5. Replace E15 test-only normalized DTO contract usage with protobuf-generated contracts once the contracts package is available, preserving the same roundtrip vector fixtures and canonical JSON/hash assertions.
6. Ensure deterministic serialization options are enabled in any protobuf serialization path used by tests that compare bytes/hashes.
7. Run `dotnet test --no-restore ./src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit/EdFi.DataManagementService.Backend.Plans.Tests.Unit.csproj` to verify DTO→protobuf swap did not change test intent.
8. Document field-numbering and compatibility policy in the contracts project README.
