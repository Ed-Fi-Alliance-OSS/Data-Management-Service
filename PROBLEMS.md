# PROBLEMS

The DMS-931 `relational.nameOverrides` semantics conflict with authoritative input fixtures, causing the
existing authoritative tests to fail. These inputs are marked as production-level goldens and may not be
modified per instructions.

## Details

1. **Inside-reference-object overrides present in authoritative inputs**
   - Example: `GraduationPlan` in `src/dms/backend/EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit/Fixtures/authoritative/ds-5.2/inputs/ds-5.2-api-schema-authoritative.json`
     has `relational.nameOverrides` entry:
     - `$.graduationSchoolYearTypeReference.schoolYear` -> `GraduationSchoolYear`
   - DMS-931 acceptance requires inside-reference-object keys to be invalid and fail fast.
   - Current implementation correctly throws, which breaks `RelationalModelAuthoritativeStringMaxLengthAuditTests`.

2. **Missing descendant collection overrides in authoritative inputs**
   - Example: `CommunityOrganization` in the same authoritative fixture has overrides:
     - `$.indicators[*]` -> `EducationOrganizationIndicator`
     - (No override for `$.indicators[*].periods[*]`)
   - DMS-931 acceptance requires **all descendant collection scopes** to have explicit overrides when any
     ancestor collection is overridden.
   - Current implementation correctly throws, which breaks `RelationalModelAuthoritativeGoldenTests` and
     `DerivedRelationalModelSetAuthoritativeGoldenTests`.

## Needed Decision

To proceed, one of the following is required:

- Update authoritative input fixtures to comply with the new rules (disallowed by current instructions), or
- Relax/alter the DMS-931 rules to accept these authoritative overrides (would conflict with acceptance), or
- Update authoritative tests to expect failures (would change test intent).

Please advise how to reconcile these authoritative inputs with the new DMS-931 requirements.
