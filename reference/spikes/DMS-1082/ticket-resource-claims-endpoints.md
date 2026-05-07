---
jira: DMS-1148
jira_url: https://edfi.atlassian.net/browse/DMS-1148
---

# Story: Serve Resource Claims Endpoints from CMS Claims Hierarchy

## Description

CMS needs Admin API-compatible read endpoints for resource claim metadata:

- `GET /v2/resourceClaims`
- `GET /v2/resourceClaims/{id}`
- `GET /v2/resourceClaimActionAuthStrategies`

Resource claim structure is stored in the CMS claims hierarchy JSON. The public response metadata comes from `dmscs.ResourceClaim`, keyed by the same full claim URI used in the hierarchy. Action and authorization-strategy details come from the existing claim-set repository.

This story covers read-only projections over those existing stores. It does not add resource-claim write endpoints, synthetic ids, fallback ids, or a new claims persistence model.

## Acceptance Criteria

- `GET /v2/resourceClaims`:
  - loads the single CMS claims hierarchy,
  - projects every hierarchy node to a `ResourceClaimResponse`,
  - joins each hierarchy node to `dmscs.ResourceClaim` by full claim URI,
  - returns `id`, `name`, `parentId`, `parentName`, and `children`,
  - uses `dmscs.ResourceClaim.ResourceName` for `name`, not the claim URI,
  - uses `0` and `null` for root `parentId` and `parentName`,
  - fails explicitly if any hierarchy node is missing resource-claim metadata.
- `GET /v2/resourceClaims/{id}`:
  - searches the valid projected hierarchy by `dmscs.ResourceClaim.Id`,
  - returns the matching projected claim,
  - returns `404 Not Found` only when the hierarchy projects successfully and the requested id is absent.
- `GET /v2/resourceClaimActionAuthStrategies`:
  - returns one flat item for each hierarchy claim with `DefaultAuthorization` actions,
  - includes `resourceClaimId`, `resourceName`, `claimName`, and `authorizationStrategiesForActions`,
  - resolves action names through `IClaimSetRepository.GetActions`,
  - resolves authorization strategy names through `IClaimSetRepository.GetAuthorizationStrategies`,
  - fails explicitly when action or authorization-strategy lookup data is missing or cannot be loaded.
- Failure handling:
  - missing claims hierarchy returns `404 Not Found`,
  - multiple hierarchy rows, repository exceptions, duplicate claim metadata, missing metadata, missing parent metadata, missing action lookup, and missing authorization-strategy lookup are server failures,
  - repository failures are not converted into empty dictionaries, empty arrays, or zero-valued ids,
  - logs identify the failed claim URI, action name, or authorization strategy name when available.
- Response model types:
  - resource claim ids and parent ids use `long`,
  - authorization strategy ids use `long`,
  - action ids remain `int`,
  - matching is exact and case-sensitive unless a different policy is explicitly documented in code and tests.
- Datastore support:
  - repository registration follows the selected CMS datastore,
  - PostgreSQL support uses the existing `dmscs.ResourceClaim` schema and seed data,
  - MSSQL support is either implemented with equivalent schema, seed data, and repository behavior, or the feature is deliberately gated with a clear startup/configuration failure,
  - PostgreSQL-specific repository registration is not placed in common service wiring.
- Tests cover:
  - successful resource-claim hierarchy projection,
  - successful lookup by id and not-found lookup by id,
  - successful action/auth-strategy projection,
  - missing hierarchy metadata,
  - missing action lookup,
  - authorization-strategy repository failure,
  - missing authorization-strategy lookup,
  - datastore-specific registration or gating behavior.

## Tasks

1. Add the resource-claim read model and service projection over `IClaimsHierarchyRepository` plus `IResourceClaimRepository`.
2. Add a datastore-specific `IResourceClaimRepository` implementation and register it through datastore-specific wiring.
3. Add the resource-claims endpoint module and map the three routes listed in this story.
4. Map service result cases to explicit endpoint responses, preserving `404` only for missing hierarchy and absent id after a valid projection.
5. Resolve action and authorization-strategy metadata through the existing claim-set repository APIs.
6. Add focused unit and endpoint tests for the success and failure cases listed above.
7. Update the spike or implementation note if the final datastore support differs from the intended PostgreSQL-plus-MSSQL behavior.
8. Run the relevant backend and frontend tests and format changed C# files with `dotnet csharpier format`.
