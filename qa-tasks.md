# QA Tasks: Reject JSON Schema `enum` in `jsonSchemaForInsert`

## Preconditions

- Ensure the contract is: JSON Schema keyword `enum` is unsupported anywhere in `resourceSchema.jsonSchemaForInsert`.

## Validation Behavior

- [ ] Run: `dotnet test --no-restore ./src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/EdFi.DataManagementService.Backend.Tests.Unit.csproj --filter "ValidateJsonSchemaStepTests"`
- [ ] Confirm the `enum` case fails with a path-inclusive message (e.g., contains `$.properties.status.enum`).

## Derivation Behavior

- [ ] Run: `dotnet test --no-restore ./src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/EdFi.DataManagementService.Backend.Tests.Unit.csproj --filter "DeriveColumnsAndDescriptorEdgesStepTests"`
- [ ] Confirm no derivation test treats JSON Schema `enum` as valid input; if a schema contains `enum`, it should fail during validation (unsupported keyword) rather than deriving columns.

## Authoritative / Regression

- [ ] Run: `dotnet test --no-restore ./src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/EdFi.DataManagementService.Backend.Tests.Unit.csproj --filter "Given_An_Authoritative_ApiSchema_For_Ed_Fi"`
- [ ] Confirm no authoritative fixture contains `"enum"` under any `resourceSchema.jsonSchemaForInsert` (a dedicated audit test should fail if it appears).

## Build

- [ ] Run: `dotnet build --no-restore ./src/dms/EdFi.DataManagementService.sln`
