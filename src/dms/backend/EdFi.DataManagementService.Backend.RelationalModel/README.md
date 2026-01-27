# EdFi.DataManagementService.Backend.RelationalModel

This project derives a deterministic, dialect-neutral relational model for a single Ed-Fi resource from the effective `ApiSchema.json` payload. It is the in-memory “compiler” used by the Backend Redesign Relational Primary Store work (tables-per-resource).

Design references:
- Epic: `../../../../reference/design/backend-redesign/epics/01-relational-model/EPIC.md`
- Story (base schema traversal): `../../../../reference/design/backend-redesign/epics/01-relational-model/00-base-schema-traversal.md`
- Redesign summary: `../../../../reference/design/backend-redesign/design-docs/summary.md`
- Flattening/reconstitution rules: `../../../../reference/design/backend-redesign/design-docs/flattening-reconstitution.md`
- Unified mapping model target (`DerivedRelationalModelSet`): `../../../../reference/design/backend-redesign/design-docs/compiled-mapping-set.md`

## What you get

The pipeline produces a `RelationalModelBuildResult`:
- `RelationalResourceModel` with:
  - root table for scope `$`
  - one child table per array path (including nested arrays)
  - derived columns (scalar columns and descriptor FK columns)
  - derived FK constraints (root → `dms.Document`, child → parent scope)
  - descriptor edge metadata (`DescriptorEdgeSource`) for later descriptor resolution/reconstitution
- Discovered extension sites (`ExtensionSite`) so later steps can derive `_ext` tables aligned to the owning scope.

This project does not connect to a database and does not emit SQL. It’s intended to be consumed by later layers (DDL generation and runtime plan compilation).

## Inputs and assumptions

The derivation is driven by `resourceSchema.jsonSchemaForInsert` from `ApiSchema.json`, plus supporting metadata extracted from the schema:
- `identityJsonPaths` (for identity-component tagging/constraints)
- `documentPathsMapping` (used to discover descriptor value paths)
- `decimalPropertyValidationInfos` / `stringMaxLengthOmissionPaths` (type metadata inputs)

Schema constraints enforced by `ValidateJsonSchemaStep` and `JsonSchemaUnsupportedKeywordValidator`:
- `jsonSchemaForInsert` must be fully dereferenced/expanded (no `$ref`, `oneOf`/`anyOf`/`allOf`, `enum`).
- `patternProperties` and array-valued `type` are rejected.
- Objects are traversed through `properties` only; `additionalProperties` is treated as “prune/ignore” (closed-world persistence).
- Arrays normally require `items.type == "object"` (a descriptor scalar array is a special-case, driven by descriptor path metadata).

## Quick start (build one resource model)

The canonical pipeline order is:

```csharp
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.RelationalModel;

var apiSchemaRoot = JsonNode.Parse(File.ReadAllText("path/to/ApiSchema.json"))
  ?? throw new InvalidOperationException("ApiSchema parsed null.");

var pipeline = new RelationalModelBuilderPipeline(
  new IRelationalModelBuilderStep[]
  {
    new ExtractInputsStep(),
    new ValidateJsonSchemaStep(),
    new DiscoverExtensionSitesStep(),
    new DeriveTableScopesAndKeysStep(),
    new DeriveColumnsAndDescriptorEdgesStep(),
    new CanonicalizeOrderingStep(),
  }
);

var context = new RelationalModelBuilderContext
{
  ApiSchemaRoot = apiSchemaRoot,
  ResourceEndpointName = "schools",
};

var result = pipeline.Run(context);
var model = result.ResourceModel;
```

If you already have the per-resource schema inputs, you can skip `ExtractInputsStep` and populate `RelationalModelBuilderContext` directly (see the required fields validated by each step).

## How derivation works (at a glance)

- **Tables**
  - Root scope `$` becomes the root table (named from `resourceName` under the project’s physical schema).
  - Every array scope like `$.addresses[*]` becomes a child table.
- **Keys**
  - Root table PK: `DocumentId` (FK → `dms.Document(DocumentId)`).
  - Child table PK: `{Root}_DocumentId`, ancestor `{ParentCollection}Ordinal` key parts, and `Ordinal` for the current collection.
  - Child table FK to parent key parts uses `ON DELETE CASCADE`.
- **Columns**
  - Objects inline (except `_ext`) by prefixing scalar descendants into the owning scope’s table.
  - Scalars become typed columns with nullability derived from JSON Schema required-ness, `x-nullable`, and optional-ancestor rules.
  - Descriptor value paths are stored as `*_DescriptorId` columns (FK → `dms.Descriptor`) and recorded as `DescriptorEdgeSource` entries; the raw descriptor string column is suppressed.
- **Determinism**
  - Traversal sorts `properties` with `StringComparer.Ordinal`.
  - JSONPaths are canonicalized via `JsonPathExpressionCompiler`.
  - `CanonicalizeOrderingStep` produces stable ordering for tables/columns/constraints/edges/sites.

## Debugging and snapshot output

`RelationalModelManifestEmitter` emits a stable, human-readable JSON manifest:

```csharp
var manifestJson = RelationalModelManifestEmitter.Emit(result);
```

The unit test project uses this emitter for golden/snapshot tests.

## Tests

Unit tests live in `../EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit/`.

- Run: `dotnet test ./src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit/EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit.csproj`
- To regenerate the authoritative golden manifest fixture, set `UPDATE_GOLDENS=1` and run the tests.

