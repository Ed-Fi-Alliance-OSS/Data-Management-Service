# Claims Migration: Options, Recommendations, and Two-Phase Approach for Ed-Fi API Transition

- [Executive Summary](#executive-summary)
- [Existing Utility: CmsHierarchy](#existing-utility-cmshierarchy)
- [Option 1: Bulk XML Migration](#option-1-bulk-xml-migration)
- [Option 2: Per-Claim-Set API Migration](#option-2-per-claim-set-api-migration)
- [Recommendation: Two-Phase Approach](#recommendation-two-phase-approach)
- [Work Required](#work-required)
- [Decision Table](#decision-table)

## Executive Summary

Organizations moving from ODS API v7 / Admin API v2 to Ed-Fi API v8 / CMS need to migrate their security configuration — both the **resource hierarchy** (domains, leaf resources, default authorization strategies) and **claim set assignments** (which claim sets access which resources, with what actions and overrides).

Two complementary options exist, serving different phases:

| Scope                          | Option 1 — Bulk XML    | Option 2 — Per-claim-set API                 |
|--------------------------------|------------------------|----------------------------------------------|
| Migrates hierarchy             | ✅ Yes                 | ❌ No — must already exist                   |
| Migrates custom resources      | ✅ Yes                 | ❌ No — skipped with warning if absent       |
| Migrates claim set assignments | ✅ All at once         | ✅ One at a time                             |
| When to use                    | Initial CMS deployment | Incremental migration and ongoing operations |

⚠️ **Critical risk:** if a resource does not exist in the CMS hierarchy, Option 2 reports a warning and skips that resource assignment. Organizations with custom or extension resources must run Option 1 first.

**Recommendation:** Run Option 1 once to establish the full hierarchy baseline. Use Option 2 for all subsequent claim set operations.

See [Claim Set Export / Import API Design](claim-set-export-import-api-design.md) for the CMS API format design.

## Existing Utility: `CmsHierarchy`

`eng/CmsHierarchy/CmsHierarchy.csproj` already covers the core of both migration paths:

`ParseXml` — converts security XML export into `AuthorizationHierarchy.json` (full hierarchy, all claim sets, custom resources).

`Transform` — merges per-claim-set JSON files into an existing hierarchy. Handles plural-to-singular name normalization to match Admin API short names to CMS URI segments.

⚠️ **Current behaviour in** `Transform`**:** if `ClaimSetToAuthHierarchy.TransformClaims` cannot find a resource name in the existing hierarchy, the entry is silently skipped because the method has no missing-resource branch. A claim set export does not carry the parent relationship, claim URI, or `defaultAuthorization` needed to create a missing node. The new Option 2 flow must surface these cases as warnings instead of silent skips.

Both options extend this utility with two new commands: `SeedDatabase` and `ConvertClaimSet`.

## Option 1: Bulk XML Migration

### Pipeline

```text
ODS/API Security Database
    │  (declarative security policies export)
    ▼
security.xml
    │  dotnet CmsHierarchy --command ParseXml
    ▼
AuthorizationHierarchy.json
    │  NEW: dotnet CmsHierarchy --command SeedDatabase
    ▼
CMS PostgreSQL (dmscs.ClaimsHierarchy table)
```

### Pros

- Complete and lossless — entire security setup including custom/extension resources
- `ParseXml` already exists — no new parsing logic needed
- No CMS API dependency — works before any CMS endpoints are built

### Cons

- All-or-nothing — overwrites entire hierarchy; CMS-specific changes are lost if re-run
- Not API-driven — writes directly to the database, bypasses CMS validation
- Requires ODS DB access — not available via Admin API

### Missing piece: `--command SeedDatabase`

```shell
dotnet CmsHierarchy --command SeedDatabase \
    --input AuthorizationHierarchy.json \
    --connectionString "Host=localhost;Database=edfi_cms;..."
```

Upserts the JSON into `dmscs.ClaimsHierarchy`. Run once at initial CMS deployment; not repeated.

## Option 2: Per-Claim-Set API Migration

### Pipeline

```text
Admin API GET /v2/claimSets/{id}/export
    │  NEW: dotnet CmsHierarchy --command ConvertClaimSet
    ▼
CMS POST /v3/claimSets/import
    │
    ▼
CMS PostgreSQL (claim set entries merged into existing hierarchy)
```

### Pros

- Selective and non-destructive — only touches named claim sets
- API-driven — goes through CMS validation
- Incremental — validate one claim set before importing the next
- Ongoing use — same API serves post-migration operations

### Cons

- Requires CMS hierarchy baseline — cannot create missing nodes
- Custom resources are skipped with warnings when a resource is absent
- Requires the revised `POST /v3/claimSets/import` contract described in the API design
- Format adapter needed — Admin API v2 format → CMS v3 format
- Claim set name constraints differ — CMS claim set names do not allow
    spaces, while legacy ODS/Admin API claim sets may include spaces

### Format Adapter: `--command ConvertClaimSet`

New command in `CmsHierarchy` that translates an Admin API v2 export into a CMS v3 import payload. Reuses existing `SearchRecursive` + `PluralToSingular` from `ClaimSetToAuthHierarchy.cs`.

```shell
dotnet CmsHierarchy --command ConvertClaimSet \
    --input sis-vendor-claimset.json \
    --output cms-sis-vendor.json \
    --outputFormat ToFile
```

Translation steps:

1. **Flatten** `children` — emit each nested entry as a sibling, setting `parentClaimName` to the parent's resolved claim URI
2. **Resolve names to URIs** — `"school"` → `"http://ed-fi.org/identity/claims/ed-fi/school"` via `SearchRecursive` + `PluralToSingular`
3. **Drop relational IDs** — remove `id`, `actionId`, `authStrategyId`, `isInheritedFromParent`
4. **Rename fields** — `_defaultAuthorizationStrategiesForCrud` → `_defaultAuthorizationStrategies`, `authorizationStrategyOverridesForCRUD` → `authorizationStrategyOverrides`
5. **Normalize claim set names for CMS** — when a source claim set name
    includes spaces, convert it to the CMS-compliant form before import
    (for example, by removing spaces) so migration payloads are accepted

See the [v2 vs v3 payload differences](claim-set-export-import-api-design.md) for a full before/after example.

## Recommendation: Two-Phase Approach

### Phase 1 - Initial deployment (run once)

```shell
# 1. Export security configuration from ODS (per declarative security policies doc)
#    Produces: security.xml

# 2. Convert to CMS hierarchy format
dotnet CmsHierarchy --command ParseXml \
    --input security.xml \
    --output AuthorizationHierarchy.json \
    --outputFormat ToFile

# 3. Seed CMS database
dotnet CmsHierarchy --command SeedDatabase \
    --input AuthorizationHierarchy.json \
    --connectionString "Host=...;Database=edfi_cms;..."
```

### Phase 2 - Ongoing operations

Note: CMS claim set names do not allow spaces. If a legacy claim set name
contains spaces, normalize it to a CMS-compliant value during
`ConvertClaimSet` processing before calling `POST /v3/claimSets/import`.

```text
# Export from Admin API
GET /v2/claimSets/{id}/export  ->  sisvendor.json

# Convert format
dotnet CmsHierarchy --command ConvertClaimSet \
    --input sisvendor.json \
    --output cms-sisvendor.json

# Import into CMS
POST /v3/claimSets/import  (body: cms-sisvendor.json)
```

## Work Required

### Utility additions (`CmsHierarchy`)

| Command                       | Description                                                       | Effort       |
|-------------------------------|-------------------------------------------------------------------|--------------|
| `--command SeedDatabase`      | Upsert `AuthorizationHierarchy.json` into `dmscs.ClaimsHierarchy` | Small        |
| `--command ConvertClaimSet`   | Translate Admin API v2 export → CMS v3 import format              | Small-Medium |
| Warning output in `Transform` | Log resource names not found instead of silent skip               | Small        |

### CMS API additions

| Endpoint                    | Description                                                           | Effort                                                       |
|-----------------------------|-----------------------------------------------------------------------|--------------------------------------------------------------|
| `GET /v3/claimSets/{id}` | Return configured-hierarchy-level claim set with defaults + overrides | Medium — share configured-node traversal with `/export`      |
| `GET /v3/claimSets/{id}/export` | Return configured-hierarchy-level claim set with defaults + overrides | Medium — implement configured-node hierarchy traversal       |
| `POST /v3/claimSets/import` | Accept flat `resourceClaims` array; upsert claim set and replace its hierarchy assignments with warning support | Medium — adapt CMS runtime hierarchy-merge path |

Export must not reuse `AuthorizationMetadataResponseFactory` as its traversal
algorithm. That factory supports `GET /v3/authorizationMetadata`, which expands
effective permissions to leaf resources for DMS runtime authorization. Claim set
retrieval/export must instead walk the stored hierarchy, collect nodes whose
`claimSets` contains the requested claim set, and emit:

- `parentClaimName` from the configured node's parent
- `_defaultAuthorizationStrategies` from the node's `defaultAuthorization`
- `authorizationStrategyOverrides` from the matching claim set entry
- `actions` from the matching claim set entry

`GET /v3/claimSets/{id}` and `GET /v3/claimSets/{id}/export` must use the same
configured-node response builder so the two routes return the same payload.

Import should reuse or adapt the CMS runtime hierarchy merge flow used by
`ClaimSetRepository.Import` and `ClaimsHierarchyManager.ApplyImportedClaimSetToHierarchy`.
The import API treats `claimSetName` as the natural key: create the claim set row
when it is absent, update the existing row when present, remove that claim set's
current hierarchy assignments, and apply the valid submitted configured nodes.
Missing hierarchy nodes and `parentClaimName` mismatches produce warnings in the
response and do not prevent valid nodes from committing.
`ClaimSetToAuthHierarchy.TransformClaims` remains useful as an offline utility
analog for migration concepts such as existing-hierarchy lookup and name
normalization, but it should not be treated as the CMS API implementation target.

## Decision Table

| Scenario                                          | Recommended approach                                             |
|---------------------------------------------------|------------------------------------------------------------------|
| Initial migration, standard Ed-Fi resources only  | Option 1 or Option 2 (if CMS ships with full standard hierarchy) |
| Initial migration, any custom/extension resources | Option 1 required first                                          |
| Running Admin API and CMS in parallel             | Option 2 for ongoing sync                                        |
| Net-new claim set created in CMS                  | Option 2 import API or CMS UI                                    |
| Disaster recovery / full re-seed                  | Option 1                                                         |
