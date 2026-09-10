# DMS-1440: Add a CMS target-database education-organization reader

[Back to DMS-1334 story index](candidate-implementation-stories.md)

## Summary

Build the CMS target-database education-organization reader consumed by DMS-1441 refresh jobs.

- Endpoint: none; this is a backend/provider capability consumed by DMS-1441.
- Contract first: define the minimal DMS relational read contract from generated PostgreSQL and SQL Server DDL before implementing provider SQL.
- Projection: enumerate State Education Agency, Education Service Center, Local Education Agency, and School rows into the Admin API v3 `educationOrganizationModel` shape.
- Validation: fail closed on incompatible `dms.EffectiveSchema`, missing required objects/columns, duplicate IDs, contradictory relationships, or target-provider mismatch.
- Boundaries: no DMS HTTP call, OAuth credential, DMS project/package reference, job, snapshot table, or public CMS/DMS route.

### Description

CMS needs to read the four core education-organization types from each configured DMS database for the Management API projection. Existing DMS token-info code is request- and mapping-specific and returns token ancestry; it is evidence for physical relationships, not an appropriate CMS dependency.

This story first defines the minimal relational read contract and provider fixtures from the existing generated DMS PostgreSQL and SQL Server DDL, then delivers a reader that enumerates a target data store's core education organizations in the Management API projection shape.

**Scope**

- Define a minimal relational read contract from the existing generated DMS PostgreSQL and SQL Server DDL, covering supported Data Standard/effective-schema versions; `dms.EffectiveSchema` compatibility fields; required provider objects, columns, types, nullability, joins, hierarchy precedence, and deterministic ordering; and representative provider fixtures.
- Define a provider-neutral async reader contract in the existing CMS backend project returning education-organization ID, institution name, nullable short name, discriminator, and nullable direct parent ID.
- Implement PostgreSQL and SQL Server adapters in the existing CMS provider projects using the stable DMS database shape for the four pinned Admin API core types: State Education Agency, Education Service Center, Local Education Agency, and School.
- Validate the configured target provider, the target `dms.EffectiveSchema` contract/version, and every required core table and column before projection.
- Encode the exact Management API discriminator and direct-parent rules as CMS projection behavior. Extension-defined education-organization types are not queried because the Management API contract does not define their discriminator or parent semantics.
- Return deterministic, unique results with provider-neutral error categories and cancellation.
- Keep the reader independent of HTTP request/authentication state and CMS persistence.

**API surface**

No public route is delivered. The output must be sufficient for the OpenAPI `educationOrganizationModel` embedded by DMS-1441.

Provider-neutral reader output must map exactly to the embedded `educationOrganizationModel` wire fields:

```json
{
  "educationOrganizationId": 255901001,
  "nameOfInstitution": "Grand Bend High School",
  "shortNameOfInstitution": "Grand Bend HS",
  "discriminator": "edfi.School",
  "parentId": 255901
}
```

The reader contract may use internal model names, but it must preserve these field meanings and nullability for DMS-1441. `educationOrganizationId` and `parentId` are `int64`/`long`; `shortNameOfInstitution` and `parentId` are nullable.

**Architecture and boundaries**

CMS owns the reader because it supports a Management API projection and runs inside a CMS refresh job. The provider-neutral contract and provider adapters follow the existing CMS layering; no CMS project references a DMS project. DMS continues to own Resources, Descriptors, and Discovery behavior, while the reader treats the generated DMS relational schema as its versioned integration boundary.

The reader performs an explicit Management API projection against the pinned contract only. It does not reuse the token-info response discriminator (`Ed-Fi:School`), require DMS `RequestInfo`, load DMS mapping/schema packages, call the public DMS API, or require an OAuth client. A target incompatible with the supported versioned database contract fails before returning a partial projection.

**Dependencies**

- Existing CMS backend, PostgreSQL, and SQL Server project boundaries and service-registration conventions.
- Existing generated DMS PostgreSQL and SQL Server DDL as the source for the minimal relational read contract and provider fixtures.
- The target-provider setting from DMS-1438, which selects the target-database adapter independently of the CMS catalog provider.
- Current Admin API behavior and DMS token-info implementation as behavioral evidence only, not runtime dependencies or reuse seams.

**Blockers**

- [DMS-1438](DMS-1438-template-provisioner.md) — blocks final shared target-provider-setting wiring. The provider-neutral interface/model can be developed before DMS-1438 completes.

**Out of scope**

- CMS snapshot persistence or refresh endpoints.
- Application education-organization assignment validation.
- A new public DMS resource endpoint or any DMS application/code change.
- Extension-defined education-organization types until the Management API contract defines their projection semantics.
- Historical/deleted education organizations not present in the active target data store.

**Risks and implementation considerations**

- A future DMS database-layout change can break direct reads. The DMS database contract must be versioned, validated before use, and changed compatibly or with a coordinated CMS adapter update.
- Mixed target providers remain out of scope until ordinary data stores carry provider metadata; the configured target provider must match every queried data store.

**Minimum relational contract to define**

DMS-1440 must check in a provider contract document or fixtures that name the exact DMS source objects, columns, provider types, and nullability before implementing SQL. At minimum it must cover:

| Projection field | Required source meaning | Portable type intent |
| --- | --- | --- |
| `educationOrganizationId` | Core education-organization identifier for SEA, ESC, LEA, and School | `int64`/`bigint`/`long`, non-null |
| `nameOfInstitution` | Institution name for the core type | bounded text/string, non-null |
| `shortNameOfInstitution` | Short name when present | nullable bounded text/string |
| `discriminator` | CMS-computed Admin API value, not DMS token-info value | bounded text/string, non-null |
| `parentId` | Direct parent selected by Admin API precedence | nullable `int64`/`bigint`/`long` |

The contract must also name the required `dms.EffectiveSchema` compatibility fields, required hierarchy relationship columns, and every provider-specific type/nullability rule needed to fail closed on incompatible DMS databases.

### Acceptance Criteria

1. A provider-neutral CMS backend contract asynchronously returns the v3 projection fields using non-nullable internal models except for fields nullable in the contract.
2. PostgreSQL and SQL Server implementations reside in the existing CMS provider projects and require no `RequestInfo`, authenticated DMS request, DMS HTTP call, DMS project/package reference, or DMS startup component.
3. Before provider SQL is implemented, DMS-1440 defines and checks in a minimal relational read contract derived from the existing generated DMS PostgreSQL and SQL Server DDL. It specifies supported Data Standard/effective-schema versions, required `dms.EffectiveSchema` compatibility fields, required provider objects/columns/types/nullability, joins, hierarchy precedence, deterministic ordering, and representative fixtures for both providers.
4. The reader enumerates exactly State Education Agency, Education Service Center, Local Education Agency, and School without requiring the caller to know IDs in advance.
5. Before projecting data, the reader validates the pinned contract version, target `dms.EffectiveSchema` compatibility fields, and every required core object/column/type/nullability rule. A missing or incompatible contract produces a typed non-transient failure and no partial projection is returned.
6. Core discriminators match the pinned Admin API projection values: `edfi.StateEducationAgency`, `edfi.EducationServiceCenter`, `edfi.LocalEducationAgency`, and `edfi.School`. The existing token-info values such as `Ed-Fi:School` are not exposed on this contract.
7. Core `parentId` precedence matches the pinned Admin API behavior: School uses its Local Education Agency; Local Education Agency uses parent Local Education Agency, then Education Service Center, then State Education Agency; Education Service Center uses State Education Agency; State Education Agency has no parent. Self and transitive-only ancestors are not returned as the direct parent.
8. Duplicate identifiers or contradictory core relationships fail with actionable diagnostics rather than returning ambiguous results.
9. PostgreSQL and SQL Server return equivalent ordered results, preserve `int64` identifiers, and correctly handle an empty data store.
10. Missing required tables/columns or an incompatible effective-schema contract returns a typed non-transient failure suitable for DMS-1441 job reporting.
11. Connection/transient provider failures remain distinguishable for retry classification and do not include secrets.
12. The reader adds no public DMS or CMS route, job, snapshot table, OAuth credential, or HTTP dependency.
13. Extension-defined education-organization types are excluded. The reader neither guesses nor synthesizes discriminator or parent semantics absent from the Management API contract.
14. The CMS contract and adapters are implemented within the existing CMS project graph; no CMS-to-DMS project/package reference, runtime library, or Docker build-context change is introduced.

### Tasks

**Implementation**

1. Define and check in the minimal relational read contract and representative PostgreSQL/SQL Server fixtures derived from the existing generated DMS DDL.
2. Add provider-neutral projection models, reader interface, compatibility result, and typed error categories in the existing CMS backend project.
3. Add contract tests for the fixtures, then implement PostgreSQL and SQL Server adapters without inferring any unlisted object or join.
4. Wire the adapter through the validated target-provider setting after DMS-1438 supplies it.
5. Document the supported contract versions, compatibility diagnostics, provider assumptions, and coordinated DMS/CMS upgrade process.

**Verification**

- Reader unit tests for the exact four core discriminators and parent-precedence rules, exclusion of extension types, short-name nullability, duplicate IDs, contradictory relationships, deterministic ordering, and typed failures.
- Contract-validation tests prove a missing/incompatible target effective schema or required table/column prevents a partial projection and returns the non-transient classification.
- PostgreSQL and SQL Server integration tests against realistic State Education Agency, Education Service Center, Local Education Agency, and School hierarchies, including `int64` IDs.
