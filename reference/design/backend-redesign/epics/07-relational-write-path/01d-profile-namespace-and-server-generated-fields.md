---
jira:
jira_url:
---

# Story: Profile Namespace and Server-Generated Fields

## Description

Implement the DMS Core enforcement of the profile DSL's namespace boundary established in `reference/design/backend-redesign/design-docs/profiles.md` §"Profile Namespace": server-generated fields (`id`, `link`, `_etag`, `_lastModifiedDate`) are not profile-addressable, fail validation at profile load when named, and pass through readable-profile projection by construction whenever their enclosing object survives projection.

The design contract landed alongside `DMS-622`. This story delivers the runtime pieces so the contract is enforceable and load-bearing for downstream stories:

- a canonical, shared constant set of server-generated field names in Core,
- a validator rule in `ProfileDataValidator` that rejects profiles naming any of those fields,
- a defensive pass-through short-circuit in `ReadableProfileProjector` and `ProfileResponseFilter`, and
- the corresponding unit and E2E tests.

This story is a prerequisite for the runtime behavior of `DMS-622` (link injection). The DMS-622 story no longer carries a feature-local projector task for `link` preservation; preservation is a consequence of the contract enforced here.

Align with:

- `reference/design/backend-redesign/design-docs/profiles.md` §"Profile Namespace" and §"Validation and Error Semantics" — the contract this story implements.
- `reference/design/backend-redesign/design-docs/link-injection.md` §Authorization ("Profile interaction") — consumer of the contract; the feature now relies on namespace-based preservation rather than a feature-local projector rule.
- ODS equivalents (reference implementation, for parity verification):
  - `Application/EdFi.Ods.Common/Models/Resource/ResourceClassBase.cs` (member filtering at resource-model construction),
  - `Application/EdFi.Ods.Common/Models/Resource/ProfileResourceMembersFilterProvider.cs` (profile member-name validation),
  - `Application/EdFi.Ods.Common/Metadata/Schemas/Ed-Fi-ODS-API-Profiles.xsd` (profile XML grammar).

Blocks:

- `DMS-622` — `reference/design/backend-redesign/epics/08-relational-read-path/06-link-injection.md`. DMS-622's runtime link-preservation behavior under readable profiles depends on this story.

Coordinates with:

- `DMS-1113` — `01a-c7-readable-profile-projection.md` (Readable Profile Projection After Reconstitution). C7 owns the post-reconstitution readable projector. This story's pass-through short-circuit must be present in both the existing `ReadableProfileProjector` / `ProfileResponseFilter` and in any new projector introduced by C7. Whichever story lands second wires to the shared constant and adds the equivalent short-circuit; the shared constant is the single source of truth for both.

## Acceptance Criteria

- A new file `src/dms/core/EdFi.DataManagementService.Core/Profile/ServerGeneratedFields.cs` exposes the canonical set `{ "id", "link", "_etag", "_lastModifiedDate" }` as a shared, case-sensitive `HashSet<string>` (or equivalent). The set is the single source of truth consumed by the validator, the projector short-circuits, and the OpenAPI schema filter.
- `src/dms/core/EdFi.DataManagementService.Core/OpenApi/ProfileOpenApiSpecificationFilter.cs:23-29` no longer defines a private `_serverGeneratedFields` literal; it imports the shared constant. Generated OpenAPI schema output is unchanged (regression-tested).
- `src/dms/core/EdFi.DataManagementService.Core/Profile/ProfileDataValidator.cs` rejects, with a validation error, any profile whose `MemberSelection.IncludeOnly` or `MemberSelection.ExcludeOnly` set contains a server-generated field name. The rejection surfaces through the same validation-result shape as the existing "identity fields cannot be excluded" error. Validation runs at profile load via `CachedProfileService.GetOrFetchProfileStoreAsync`; there is no per-request cost.
- `src/dms/core/EdFi.DataManagementService.Core/Profile/ReadableProfileProjector.cs` and `Profile/ProfileResponseFilter.cs` short-circuit on server-generated field names before consulting `PropertyNameSet`, so those fields pass through regardless of `IncludeOnly` membership. This guard is defensive (the validator is the primary enforcement point) but is required so a loosened validator cannot silently degrade response shapes.
- Link preservation under readable profiles is asserted by the tests introduced in this story rather than by DMS-622. DMS-622 no longer carries a `link`-preservation acceptance criterion or feature-local projector task.
- Unit tests in `ProfileDataValidatorTests` cover:
  - a profile naming `link`, `id`, `_etag`, or `_lastModifiedDate` in `IncludeOnly` fails validation,
  - the same names in `ExcludeOnly` fail validation,
  - a profile addressing only ordinary schema members (e.g., `schoolReference.schoolId`) continues to validate,
  - the validation error for a server-generated field surfaces through the same shape as the existing identity-field rule.
- Unit tests in `ProfileResponseFilterTests` and `ReadableProfileProjectorTests` cover:
  - a fixture containing `schoolReference.link` with `IncludeOnly` listing only `schoolReference.schoolId` — `link` survives on the filtered reference,
  - a fixture containing root-level `_etag` and `_lastModifiedDate` with `IncludeOnly` not listing them — both survive,
  - a regression fixture asserting that ordinary members still filter exactly as before (the short-circuit must not over-preserve).
- `ProfileOpenApiSpecificationFilterTests` includes a regression test asserting that the refactor to consume the shared constant does not change writable- or readable-schema output for representative resources.
- An E2E scenario in `src/dms/tests/EdFi.InstanceManagement.Tests.E2E/Features/.../ProfileReferenceFiltering.feature` stores a document containing a nested reference `link` and confirms the profiled GET response still includes the `link`.

## Tasks

1. Create `src/dms/core/EdFi.DataManagementService.Core/Profile/ServerGeneratedFields.cs` with the canonical set. Refactor `OpenApi/ProfileOpenApiSpecificationFilter.cs:23-29` to import it (no behavior change).
2. Extend `Profile/ProfileDataValidator.cs` with the server-generated-field rejection rule. Surface the error through the same validation-result shape as the identity-field rule. Confirm it is invoked from `CachedProfileService.GetOrFetchProfileStoreAsync`; wire if needed.
3. Add the pass-through short-circuit in `Profile/ReadableProfileProjector.cs` and `Profile/ProfileResponseFilter.cs` before the existing `PropertyNameSet.Contains` check.
4. Add unit tests in `ProfileDataValidatorTests`, `ProfileResponseFilterTests`, `ReadableProfileProjectorTests`, and the regression test in `ProfileOpenApiSpecificationFilterTests`.
5. Add the E2E scenario in `ProfileReferenceFiltering.feature` covering a nested reference `link` under a readable profile that does not list `link`.
