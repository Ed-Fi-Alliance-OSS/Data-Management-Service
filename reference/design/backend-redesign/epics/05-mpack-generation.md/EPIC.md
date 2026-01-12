# Epic: Mapping Pack (`.mpack`) Generation and Consumption (Optional AOT Mode)

## Description

Implement the optional ahead-of-time (AOT) compilation workflow described in:

- `reference/design/backend-redesign/aot-compilation.md`
- `reference/design/backend-redesign/mpack-format-v1.md` (normative PackFormatVersion=1)

Deliverables include:

- A shared protobuf “contracts” package for producer/consumer.
- A pack builder that embeds:
  - deterministic `dms.ResourceKey` seed mapping + fingerprints,
  - derived relational models,
  - dialect-specific compiled SQL plans (canonicalized text + binding metadata).
- A consumer/validator that selects packs by `(EffectiveSchemaHash, Dialect, RelationalMappingVersion, PackFormatVersion)` and rejects invalid/mismatched packs.
- Deterministic semantic manifests (`pack.manifest.json`, `mappingset.manifest.json`) to support testing without comparing raw `.mpack` bytes.

Authorization objects remain out of scope.

## Stories

- `00-protobuf-contracts.md` — Protobuf schema + contracts project/package
- `01-pack-payload-shape.md` — Payload object graph + ordering invariants
- `02-plan-compilation.md` — Compile and canonicalize SQL plans for packs
- `03-pack-build-cli.md` — CLI: `pack build` emits `.mpack`
- `04-pack-manifests.md` — Emit `pack.manifest.json` and `mappingset.manifest.json`
- `05-pack-loader-validation.md` — Load/validate/select packs + DB seed gate
- `06-pack-equivalence-tests.md` — Pack vs runtime compilation equivalence tests
- `07-pack-manifest-command.md` — CLI: `pack manifest` (inspect/validate existing packs)

