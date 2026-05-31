# Permanent Fix Plan: Reference Lookup Descriptor Identity Domain

## Goal

Stop the branch from flipping between descriptor URI and descriptor document-id fixes by making the reference lookup contract explicit and enforcing it in code and tests.

Reference lookup verification compares request-side expected identity to a database-projected witness. Both sides must use the same identity domain:

- API/Core natural identity for lookup verification and `dms.ReferentialIdentity` values.
- Relational storage identity only inside authoritative relational tables and flattened write rows.

For descriptor-valued natural identity members, API/Core identity is the normalized descriptor URI, not the descriptor FK document id.

## Root Cause

The regression loop is caused by mixing two valid but different descriptor identity representations:

- `$.termDescriptor=uri://ed-fi.org/termdescriptor#fall semester`
- `$.termDescriptor=10`

Commit `c4c1d5b` made SQL lookup verification project descriptor FK document IDs. That matched some relational storage/test helper values but broke normal Core-extracted document references, whose expected keys contain descriptor URIs.

Commit `d1e427aac6` restored URI projection in SQL. That fixed normal Core extraction but exposed manually synthesized `DocumentReference` and seeded `dms.ReferentialIdentity` rows that still used descriptor document IDs in `DocumentIdentity`.

The permanent fix is not to choose URI or ID globally. It is to keep each representation on the correct side of the boundary.

## Contract

1. `DocumentIdentity` values used by `ReferentialIdCalculator` are API natural-key values.
2. `ReferenceLookupRequestEntry.ExpectedVerificationIdentityKey` is built from `DocumentIdentity` and therefore uses API natural-key values.
3. Descriptor identity values in `DocumentIdentity` are normalized lowercase descriptor URIs.
4. Relational authoritative table columns of `ColumnKind.DescriptorFk` store descriptor document IDs.
5. Reference lookup SQL must convert descriptor FK columns back to descriptor URI when building `VerificationIdentityKey`.
6. Top-level descriptor lookups continue to use the descriptor table URI directly.

## Implementation Steps

1. Preserve URI witness projection in lookup SQL.

   Keep the descriptor join/projection behavior in:

   - `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/PostgresqlReferenceLookupCommandBuilder.cs`
   - `src/dms/backend/EdFi.DataManagementService.Backend.Mssql/MssqlReferenceLookupSmallListStrategy.cs`

   SQL Server bulk lookup reuses the small-list command text, so verify it through `MssqlReferenceLookupBulkStrategyTests`.

2. Harden verification metadata for descriptor FK identity columns.

   `ReferenceLookupVerificationSupport` already tracks `IsDescriptorReference` for concrete resource root columns. Verify and keep this behavior:

   - Concrete column source has `ColumnKind.DescriptorFk` -> `IsDescriptorReference = true`.
   - SQL projection joins `dms.Descriptor` on the FK document id and emits `lower(Uri)`.

   Extend the same metadata to abstract union-view verification if needed. `AbstractUnionViewOutputColumn` currently carries `SourceJsonPath` and `TargetResource`, but not the original `ColumnKind`. Add either `ColumnKind` or an explicit `IsDescriptorReference` flag to the abstract output metadata, populate it from the derived identity column, and have `BuildAbstractColumnByPath` preserve it. This prevents abstract target lookups with descriptor-valued identity members from falling back to raw FK document IDs.

3. Fix all manually synthesized document references.

   Search for hand-built `DocumentIdentityElement` values where the identity path is descriptor-valued and the value is derived from a descriptor document id, for example:

   - `programTypeDescriptorId.ToString(CultureInfo.InvariantCulture)`
   - `graduationPlanTypeDescriptorId.ToString(CultureInfo.InvariantCulture)`
   - `*DescriptorDocumentId.ToString(...)`

   Replace those with the normalized descriptor URI from the request/reference value or a URI constant.

   Known affected integration helpers include:

   - `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/PostgresqlRelationalWriteAuthoritativeSampleSmokeTests.cs`
   - `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/PostgresqlRelationalWriteAuthoritativeSampleStudentSchoolAssociationSmokeTests.cs`
   - `src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/PostgresqlRelationalWriteAuthoritativeSampleStudentSectionAssociationSmokeTests.cs`
   - `src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/MssqlRelationalWriteAuthoritativeSampleStudentArtProgramAssociationSmokeTests.cs`
   - `src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/MssqlRelationalWriteAuthoritativeSampleStudentSchoolAssociationSmokeTests.cs`

4. Fix manually seeded `dms.ReferentialIdentity` rows.

   Search for test seed helpers that call `CreateReferentialId` or insert `dms.ReferentialIdentity` for target resources whose natural identity includes descriptor-valued members.

   The `ReferentialId` must be calculated from descriptor URI values. The authoritative resource table row should still store descriptor FK document IDs.

5. Add guardrail tests around the contract.

   Add or update unit tests that make the identity-domain expectation explicit:

   - PostgreSQL lookup SQL for a document target with descriptor-valued identity member projects URI through `dms.Descriptor`.
   - SQL Server small-list lookup SQL does the same.
   - SQL Server bulk lookup, through the reused command text, also contains the descriptor URI projection.
   - `ReferenceLookupVerificationSupport` marks concrete descriptor FK identity members as descriptor references.
   - If abstract union-view identity metadata supports descriptor FKs, add a focused test that the abstract projection metadata also marks those identity members as descriptor references.
   - `ReferenceResolver` accepts matching URI-domain keys for descriptor-valued document identity members.

6. Remove test expectations that encode descriptor document IDs as natural identity values.

   Any assertion expecting a key such as:

   ```text
   $.graduationPlanTypeDescriptor=4
   ```

   should expect:

   ```text
   $.graduationPlanTypeDescriptor=uri://ed-fi.org/graduationplantypedescriptor#foundation
   ```

7. Keep the guarded no-op unit-test fix separate.

   The `DefaultRelationalWriteExecutorTests` failure is unrelated to descriptor identity. Keep the assertion update that expects no persistence for guarded no-op update success, and do not use descriptor lookup changes to address that test.

## Verification Plan

1. Format changed files:

   ```bash
   dotnet csharpier format src/dms/backend
   ```

2. Run focused unit tests:

   ```bash
   dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/EdFi.DataManagementService.Backend.Tests.Unit.csproj --filter "FullyQualifiedName~ReferenceLookup"
   dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/EdFi.DataManagementService.Backend.Tests.Unit.csproj --filter "FullyQualifiedName~DefaultRelationalWriteExecutorTests"
   ```

3. Run affected PostgreSQL integration tests:

   ```bash
   dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration.csproj --filter "FullyQualifiedName~AuthoritativeSample"
   ```

4. Run affected SQL Server tests when MSSQL is available:

   ```bash
   ConnectionStrings__MssqlAdmin='Server=localhost,1434;User Id=sa;Password=<StrongPassword>;TrustServerCertificate=True;Encrypt=True' dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Mssql.Tests.Integration/EdFi.DataManagementService.Backend.Mssql.Tests.Integration.csproj --filter "FullyQualifiedName~AuthoritativeSample"
   ```

## Acceptance Criteria

- No `ReferenceLookupCorruptionException` reports a URI-vs-document-id mismatch for descriptor-valued identity members.
- No lookup SQL path projects raw descriptor FK document IDs into `VerificationIdentityKey`.
- No manually built `DocumentIdentity` for a descriptor-valued identity member uses a descriptor document ID.
- Relational authoritative tables continue to store descriptor document IDs in descriptor FK columns.
- Unit tests lock the identity-domain contract so future changes fail close to the source instead of in broad integration suites.
