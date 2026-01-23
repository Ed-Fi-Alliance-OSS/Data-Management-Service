---
jira: DMS-963
jira_url: https://edfi.atlassian.net/browse/DMS-963
---

# Epic: Mapping Pack (`.mpack`) Generation and Consumption (Optional AOT Mode)

## Description

Implement the optional ahead-of-time (AOT) compilation workflow described in:

- `reference/design/backend-redesign/design-docs/aot-compilation.md`
- `reference/design/backend-redesign/design-docs/compiled-mapping-set.md` (unified `MappingSet` shape)
- `reference/design/backend-redesign/design-docs/mpack-format-v1.md` (normative PackFormatVersion=1)

Deliverables include:

- A shared protobuf “contracts” package for producer/consumer.
- A pack builder that embeds:
  - deterministic `dms.ResourceKey` seed mapping + fingerprints,
  - derived relational models,
  - dialect-specific compiled SQL plans (canonicalized text + binding metadata).
- A consumer/validator that selects packs by `(EffectiveSchemaHash, Dialect, RelationalMappingVersion, PackFormatVersion)` and rejects invalid/mismatched packs.
- Deterministic semantic manifests (`pack.manifest.json`, `mappingset.manifest.json`) to support testing without comparing raw `.mpack` bytes.

Plan compilation is shared with runtime compilation fallback and is owned by `reference/design/backend-redesign/epics/15-plan-compilation/EPIC.md`.

Authorization objects remain out of scope.

## Stories

- `DMS-964` — `00-protobuf-contracts.md` — Protobuf schema + contracts project/package
- `DMS-965` — `01-pack-payload-shape.md` — Payload object graph + ordering invariants
- `DMS-966` — `03-pack-build-cli.md` — CLI: `pack build` emits `.mpack`
- `DMS-967` — `04-pack-manifests.md` — Emit `pack.manifest.json` and `mappingset.manifest.json`
- `DMS-968` — `05-pack-loader-validation.md` — Load/validate/select packs + DB seed gate
- `DMS-969` — `06-pack-equivalence-tests.md` — Pack vs runtime compilation equivalence tests
- `DMS-970` — `07-pack-manifest-command.md` — CLI: `pack manifest` (inspect/validate existing packs)
