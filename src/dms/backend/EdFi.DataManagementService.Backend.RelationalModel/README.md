# EdFi.DataManagementService.Backend.RelationalModel

This project derives a deterministic, dialect-neutral relational model from effective `ApiSchema.json` payloads.
It includes both:
- per-resource derivation (`RelationalModelBuilderPipeline` + `Build/Steps`)
- set-level derivation across all resources (`DerivedRelationalModelSetBuilder` + `SetPasses`)

Design references:
- Epic: `../../../../reference/design/backend-redesign/epics/01-relational-model/EPIC.md`
- Story (naming + overrides): `../../../../reference/design/backend-redesign/epics/01-relational-model/02-naming-and-overrides.md`
- Redesign summary: `../../../../reference/design/backend-redesign/design-docs/summary.md`
- Flattening/reconstitution rules: `../../../../reference/design/backend-redesign/design-docs/flattening-reconstitution.md`
- Unified mapping model target (`DerivedRelationalModelSet`): `../../../../reference/design/backend-redesign/design-docs/compiled-mapping-set.md`

## Folder taxonomy

- `Build/`: orchestration entry points, builder contexts, canonicalization/order helpers, and manifest emission.
- `Build/Steps/`: per-resource pipeline steps (`ExtractInputsStep`, `ValidateJsonSchemaStep`, `DiscoverExtensionSitesStep`, `DeriveTableScopesAndKeysStep`, `DeriveColumnsAndBindDescriptorEdgesStep`, `CanonicalizeOrderingStep`).
- `SetPasses/`: set-level derivation passes and the `IRelationalModelSetPass` contract.
- `Schema/`: schema input models, normalization, JSONPath helpers, traversal conventions, and scalar type resolution.
- `Validation/`: JSON-schema and set-level invariant validators.
- `Naming/`: name-override parsing/lookup and naming convention helpers.
- `Constraints/`: constraint identity and dialect-aware constraint naming/derivation helpers.
- `DescriptorPaths/`: descriptor path inference and descriptor path map construction.
- `Diagnostics/`: collision detection, collision records, and table-column accumulation diagnostics.

## What you get

Per-resource derivation returns `RelationalModelBuildResult` with:
- `RelationalResourceModel` containing storage classification, derived tables, columns, constraints, and descriptor edge metadata.
- discovered `ExtensionSite` metadata used by set-level extension table derivation.

Set-level derivation returns `DerivedRelationalModelSet` with:
- all derived concrete resources in endpoint order
- extension table expansion
- abstract identity table derivation
- reference binding and constraint enrichment
- dialect-aware identifier hashing/shortening
- canonical ordering for deterministic emission

## Inputs and assumptions

The per-resource derivation is driven by `resourceSchema.jsonSchemaForInsert` from `ApiSchema.json`, plus supporting metadata extracted from schema:
- `identityJsonPaths`
- `documentPathsMapping`
- `decimalPropertyValidationInfos`
- `stringMaxLengthOmissionPaths`

Core schema constraints are enforced by `ValidateJsonSchemaStep` and `JsonSchemaUnsupportedKeywordValidator`:
- `jsonSchemaForInsert` must be dereferenced/expanded (`$ref`, `oneOf`/`anyOf`/`allOf`, `enum` are not supported here).
- `patternProperties` and array-valued `type` are rejected.
- Objects are traversed through `properties`; unsupported `additionalProperties` usage is rejected/pruned according to traversal rules.
- Arrays are expected to use object `items` except descriptor-specific scalar-array handling.

## Quick start (build one resource model)

```csharp
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel;

var apiSchemaRoot = JsonNode.Parse(File.ReadAllText("path/to/ApiSchema.json"))
    ?? throw new InvalidOperationException("ApiSchema parsed null.");

var pipeline = new RelationalModelBuilderPipeline(
[
    new ExtractInputsStep(),
    new ValidateJsonSchemaStep(),
    new DiscoverExtensionSitesStep(),
    new DeriveTableScopesAndKeysStep(),
    new DeriveColumnsAndBindDescriptorEdgesStep(),
    new CanonicalizeOrderingStep(),
]);

var context = new RelationalModelBuilderContext
{
    ApiSchemaRoot = apiSchemaRoot,
    ResourceEndpointName = "schools",
};

var result = pipeline.Run(context);
```

## Quick start (build a derived relational model set)

```csharp
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.RelationalModel;

var builder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());
var dialectRules = new PgsqlDialectRules();

DerivedRelationalModelSet set = builder.Build(
    effectiveSchemaSet,
    SqlDialect.Pgsql,
    dialectRules);
```

`RelationalModelSetPasses.CreateDefault()` currently runs these pass types, in order:
1. `BaseTraversalAndDescriptorBindingPass`
2. `ExtensionTableDerivationPass`
3. `AbstractIdentityTableDerivationPass`
4. `ReferenceBindingPass`
5. `RootIdentityConstraintPass`
6. `ReferenceConstraintPass`
7. `ArrayUniquenessConstraintPass`
8. `ApplyConstraintDialectHashingPass`
9. `ApplyDialectIdentifierShorteningPass`
10. `CanonicalizeOrderingPass`

## Debugging and snapshot output

`RelationalModelManifestEmitter` emits deterministic JSON manifests used by golden/snapshot tests:

```csharp
string manifestJson = RelationalModelManifestEmitter.Emit(result);
```

## Tests

Unit tests live in `../EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit/`.

- Run unit tests:
  - `dotnet test --no-restore ./src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit/EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit.csproj`
- Regenerate golden fixtures when intended:
  - `UPDATE_GOLDENS=1 dotnet test --no-restore ./src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit/EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit.csproj`
- Build DMS solution:
  - `dotnet build --no-restore ./src/dms/EdFi.DataManagementService.sln`
