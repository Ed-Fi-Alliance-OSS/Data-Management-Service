# Fix Plan: DMS-987 — Review Feedback

**Source:** Peer review (code review)
**Date:** 2026-04-03
**Valid findings:** 2

---

## Fix 1: Normalize descriptor Uri before persistence and identity comparison (High)

**Finding:** Descriptor Uri is built from raw `namespace#codeValue` without lowercasing, diverging from Core's normalization rules. The immutability check uses ordinal comparison against the raw Uri.
**Files:**
- `src/dms/backend/EdFi.DataManagementService.Backend/DescriptorWriteBodyExtractor.cs:75`
- `src/dms/backend/EdFi.DataManagementService.Backend/DescriptorWriteHandler.cs:164`
- `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/DescriptorWriteBodyExtractorTests.cs:99-115`

**What to change:**
1. In `DescriptorWriteBodyExtractor.Extract`, change the Uri computation from `$"{ns}#{codeValue}"` to `$"{ns}#{codeValue}".ToLowerInvariant()` (matching `DescriptorDocument.cs:44`).
2. The identity check at `DescriptorWriteHandler.cs:164` can remain `StringComparison.Ordinal` since both sides will now be normalized.
3. Update the `ExtractedDescriptorBody` doc-comment for `Uri` — change "original case" to "normalized (lowercased)".
4. Update test `It_preserves_original_case_in_uri` to assert the lowercased Uri instead (e.g., `"uri://ed-fi.org/schooltypedescriptor#alternative"`), and rename it to something like `It_normalizes_uri_to_lowercase`.
5. Update the `It_computes_uri_as_namespace_hash_codeValue` test assertion to use the lowercased form.

**Why:** The spec requires Uri normalization consistent with Core. Without it, the persisted Uri diverges from the referential identity used for reference resolution, and case-only differences are incorrectly rejected as identity changes.

---

## Fix 2: Upsert dms.ReferentialIdentity on POST-as-update path (Medium)

**Finding:** When a descriptor POST resolves to `ExistingDocument`, the update SQL only touches `dms.Descriptor` and `dms.Document` — the `dms.ReferentialIdentity` row is not upserted.
**Files:**
- `src/dms/backend/EdFi.DataManagementService.Backend/DescriptorWriteHandler.cs:337-366` (`UpdateDescriptorForUpsertAsync`)
- `src/dms/backend/EdFi.DataManagementService.Backend/DescriptorWriteHandler.cs:444-489` (update SQL builders)

**What to change:**
1. `UpdateDescriptorForUpsertAsync` needs the `ReferentialId` and `ResourceKeyId` parameters (currently only passed to the insert path).
2. Append a `dms.ReferentialIdentity` upsert to the PostgreSQL update command: `INSERT INTO dms."ReferentialIdentity" (...) VALUES (...) ON CONFLICT ("DocumentId", "ResourceKeyId") DO UPDATE SET "ReferentialId" = EXCLUDED."ReferentialId"`.
3. Append a `dms.ReferentialIdentity` MERGE (or DELETE+INSERT) to the SQL Server update command.
4. Pass `@referentialId` and `@resourceKeyId` parameters in the update parameter builder.
5. Add/update integration test coverage to verify the RI row exists after a POST that hits the upsert-as-update path.

**Why:** The spec (AC line 24) explicitly requires POST to insert/update the referential identity row. Skipping it on the upsert path means migrated or partially-failed data won't have its RI row repaired, breaking reference resolution.
