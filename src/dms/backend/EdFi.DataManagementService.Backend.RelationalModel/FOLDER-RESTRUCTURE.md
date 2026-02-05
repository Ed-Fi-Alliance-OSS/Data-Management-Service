# RelationalModel Folder Restructure Plan

## Scope
- Project: `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel`
- Input set: all current top-level `*.cs` files in the project root (`maxdepth=1`)
- This is a planning artifact only; no implementation changes are part of this task.

## Target Taxonomy
- `Build/`: top-level orchestration and composition entry points for resource/set derivation.
- `Build/Steps/`: per-resource pipeline step implementations used by `RelationalModelBuilderPipeline`.
- `SetPasses/`: set-level derivation pass contracts and pass implementations.
- `Schema/`: schema input models, schema normalization, JSONPath/schema helpers, scalar type resolution.
- `Validation/`: schema and set-level invariant validation.
- `Naming/`: override/name convention types and naming policy helpers.
- `Constraints/`: constraint identity and constraint naming/derivation helpers.
- `DescriptorPaths/`: descriptor path inference and map construction.
- `Diagnostics/` (optional): collision detectors/records and accumulator diagnostics.

## Orchestration Discoverability Rule
The following orchestration types stay under `Build/` for discoverability:
- `RelationalModelBuilderPipeline`
- `RelationalModelSetPasses`
- `DerivedRelationalModelSetBuilder`

## File-To-Folder Mapping (Current Top-Level Files)
| Current file | Target folder |
|---|---|
| `AbstractIdentityTableDerivationRelationalModelSetPass.cs` | `SetPasses/` |
| `ApplyConstraintDialectHashingRelationalModelSetPass.cs` | `SetPasses/` |
| `ApplyDialectIdentifierShorteningRelationalModelSetPass.cs` | `SetPasses/` |
| `ArrayUniquenessConstraintRelationalModelSetPass.cs` | `SetPasses/` |
| `BaseTraversalAndDescriptorBindingRelationalModelSetPass.cs` | `SetPasses/` |
| `CanonicalizeOrderingRelationalModelSetPass.cs` | `SetPasses/` |
| `CanonicalizeOrderingStep.cs` | `Build/Steps/` |
| `CollisionDetectorCore.cs` | `Diagnostics/` |
| `ConstraintDerivationHelpers.cs` | `Constraints/` |
| `ConstraintIdentity.cs` | `Constraints/` |
| `ConstraintNaming.cs` | `Constraints/` |
| `DeriveColumnsAndBindDescriptorEdgesStep.cs` | `Build/Steps/` |
| `DeriveTableScopesAndKeysStep.cs` | `Build/Steps/` |
| `DerivedRelationalModelSetBuilder.cs` | `Build/` |
| `DescriptorPathInference.cs` | `DescriptorPaths/` |
| `DescriptorPathMapBuilder.cs` | `DescriptorPaths/` |
| `DiscoverExtensionSitesStep.cs` | `Build/Steps/` |
| `ExtensionTableDerivationRelationalModelSetPass.cs` | `SetPasses/` |
| `ExtractInputsStep.cs` | `Build/Steps/` |
| `GlobalUsings.cs` | `Build/` |
| `IRelationalModelSetPass.cs` | `SetPasses/` |
| `IdentifierCollisionDetector.cs` | `Diagnostics/` |
| `IdentifierCollisionRecord.cs` | `Diagnostics/` |
| `JsonPathExpressionCompiler.cs` | `Schema/` |
| `JsonSchemaTraversalConventions.cs` | `Schema/` |
| `JsonSchemaUnsupportedKeywordValidator.cs` | `Validation/` |
| `NameOverrideEntry.cs` | `Naming/` |
| `NameOverrideProvider.cs` | `Naming/` |
| `OverrideCollisionDetector.cs` | `Diagnostics/` |
| `ProjectSchemaNormalizer.cs` | `Schema/` |
| `ReferenceBindingRelationalModelSetPass.cs` | `SetPasses/` |
| `ReferenceConstraintRelationalModelSetPass.cs` | `SetPasses/` |
| `RelationalModelBuilderPipeline.cs` | `Build/` |
| `RelationalModelCanonicalization.cs` | `Build/` |
| `RelationalModelManifestEmitter.cs` | `Build/` |
| `RelationalModelOrdering.cs` | `Build/` |
| `RelationalModelSetBuilderContext.cs` | `Build/` |
| `RelationalModelSetPasses.cs` | `Build/` |
| `RelationalModelSetSchemaHelpers.cs` | `Schema/` |
| `RelationalModelSetValidation.cs` | `Validation/` |
| `RelationalNameConventions.cs` | `Naming/` |
| `RelationalScalarTypeResolver.cs` | `Schema/` |
| `RootIdentityConstraintRelationalModelSetPass.cs` | `SetPasses/` |
| `SchemaInputModels.cs` | `Schema/` |
| `TableColumnAccumulator.cs` | `Diagnostics/` |
| `ValidateJsonSchemaStep.cs` | `Build/Steps/` |

## Coverage Check
- Expected mapped files: `46`
- Expected source files (`find ... -maxdepth 1 -name '*.cs'`): `46`
- Mapping is one-to-one and complete for current top-level files.
