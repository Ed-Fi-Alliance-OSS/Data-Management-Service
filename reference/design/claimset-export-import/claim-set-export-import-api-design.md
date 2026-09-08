# Claim Set Export / Import API Design

- [Executive Summary](#executive-summary)
- [Background](#background)
- [Key Concepts](#key-concepts)
  - [Configured vs expanded hierarchy](#configured-vs-expanded-hierarchy)
  - [Authorization strategy: default vs override](#authorization-strategy-default-vs-override)
  - [Parent-child configurations](#parent-child-configurations)
- [Design Decision 1: Response Structure](#design-decision-1-response-structure)
- [Design Decision 2: Authorization Strategy Representation](#design-decision-2-authorization-strategy-representation)
- [Complete Recommended Format](#complete-recommended-format)
  - [GET /v3/claimSets/{id} and GET /v3/claimSets/{id}/export](#get-v3claimsetsid-and-get-v3claimsetsidexport)
  - [POST /v3/claimSets/import](#post-v3claimsetsimport)
- [Payload Differences (v2 vs v3)](#payload-differences-v2-vs-v3)
  - [Side-by-side example: DistrictHostedSISVendor](#side-by-side-example-districthostedsisvendor)
    - [Admin API v2](#admin-api-v2)
    - [CMS v3](#cms-v3)
- [Admin App Impact](#admin-app-impact)
  - [Rendering sketch for v3](#rendering-sketch-for-v3)

## Executive Summary

This document defines the revised payload and behavior for `GET /v3/claimSets/{id}`, `GET /v3/claimSets/{id}/export`, and `POST /v3/claimSets/import` in Configuration Management Service (CMS, Ed-Fi API v8). CMS already exposes these routes; this design replaces the existing claim set retrieval, export, and import contracts with the v3 payload described here.

| Decision                     | Recommendation                             | Rationale                                                                                          |
|------------------------------|--------------------------------------------|----------------------------------------------------------------------------------------------------|
| What to expose               | Configured hierarchy nodes only            | 11 nodes for SISVendor, not 200+ expanded leaf nodes — preserves intent, enables round-trip import |
| Response structure           | Flat list with `parentClaimName`           | Linear import, clean OpenAPI schema; UI reconstructs tree with one `groupBy`                       |
| Auth strategy representation | Default + override separately (`_` prefix) | Preserves override intent; import writes only what's explicitly set                                |
| Format adapter               | In `CmsHierarchy` utility, not the CMS API | CMS accepts one clean format; Admin API translation stays in migration tooling                     |

See [Claims Migration](claims-migration.md) for the migration pipeline design.

## Background

- **Admin API** (`AdminAPI-2.x`) — supports Management API v1/v2 (v3 planned). Stores authorization hierarchy in relational tables. Implements `GET /v2/claimSets/{id}`, `GET /v2/claimSets/{id}/export`, and `POST /v2/claimSets/import`.

- **CMS** (`Data-Management-Service/src/config`) — implements Management API v3 only. Stores authorization hierarchy at runtime in `dmscs.ClaimsHierarchy`; `AuthorizationHierarchy.json` is the seed/import artifact used to populate that table. The current claim set retrieval, export, and import endpoints must be updated to use the revised v3 contract in this document.

- **Admin App** — must support all three API versions simultaneously. The v2 claim set UI is read-only; v1 has write capability.

---
## Key Concepts

### Configured vs expanded hierarchy

The CMS runtime hierarchy stored in `dmscs.ClaimsHierarchy` keeps authorization at the **node where it is configured**. `AuthorizationHierarchy.json` uses the same hierarchy shape as a seed/import artifact. A domain-level entry covers all children by inheritance:

```text
systemDescriptors (domain)   → SISVendor: [Read]   ← 1 configured entry
  └─ absenceEventCategoryDescriptor                 ← inherits Read (not stored)
  └─ academicSubjectDescriptor                      ← inherits Read (not stored)
  ... (209 child descriptors)
```

CMS `GET /v3/authorizationMetadata`, consumed by DMS at runtime, walks to leaf nodes to resolve effective permissions — correct for the authorization middleware and effective authorization review, wrong for authorization configuration and management.

Exporting at the configured level preserves intent, keeps payload size manageable, and supports correct round-trip import. Exporting leaf nodes loses configuration intent, produces redundant entries, and breaks round-trip behavior.

### Authorization strategy: default vs override

Authorization strategies come from two sources:

| Source                           | Stored on               | Applies to                  |
|----------------------------------|-------------------------|-----------------------------|
| `defaultAuthorization`           | Resource hierarchy node | All claim sets on this node |
| `authorizationStrategyOverrides` | Claim set entry         | This claim set only         |

Example: `BootstrapDescriptorsandEdOrgs` on `systemDescriptors` — the node default for `Create` is `NamespaceBased`, but this claim set overrides it to `NoFurtherAuthorizationRequired`. `SISVendor` on the same node has no override and inherits the default.

Separating these fields preserves intent, simplifies import logic, and enables clear UI representation.

### Parent-child configurations

A claim set can appear at both a domain node and a specific child node with different settings — the child entry is a deliberate exception:

| Claim Set                 | Domain                   | Domain Actions | Child                       | Child Actions             |
|---------------------------|--------------------------|----------------|-----------------------------|---------------------------|
| `DistrictHostedSISVendor` | `educationOrganizations` | Read           | `school`                    | CRUD                      |
| `DistrictHostedSISVendor` | `educationOrganizations` | Read           | `localEducationAgency`      | Read, Update              |
| `AssessmentVendor`        | `systemDescriptors`      | Read           | `academicSubjectDescriptor` | CRUD                      |
| `EdFiAPIPublisherWriter`  | `relationshipBasedData`  | CRUD           | `communityProviderLicense`  | CRUD + strategy overrides |

---
## Design Decision 1: Response Structure

**Decision: Flat list with** `parentClaimName`

All configured nodes at the same array level; parent relationship expressed via `parentClaimName`.

```json
{
  "claimSetName": "DistrictHostedSISVendor",
  "resourceClaims": [
    {
      "name": "educationOrganizations",
      "claimName": "http://ed-fi.org/identity/claims/domains/educationOrganizations",
      "parentClaimName": null,
      "actions": [{ "name": "Read", "enabled": true }],
      "_defaultAuthorizationStrategies": [
        { "actionName": "Read", "authorizationStrategies": [{ "authStrategyName": "NoFurtherAuthorizationRequired" }] }
      ],
      "authorizationStrategyOverrides": []
    },
    {
      "name": "school",
      "claimName": "http://ed-fi.org/identity/claims/ed-fi/school",
      "parentClaimName": "http://ed-fi.org/identity/claims/domains/educationOrganizations",
      "actions": [
        { "name": "Create", "enabled": true }, { "name": "Read", "enabled": true },
        { "name": "Update", "enabled": true }, { "name": "Delete", "enabled": true }
      ],
      "_defaultAuthorizationStrategies": [
        { "actionName": "Create", "authorizationStrategies": [{ "authStrategyName": "NoFurtherAuthorizationRequired" }] },
        { "actionName": "Read",   "authorizationStrategies": [{ "authStrategyName": "NoFurtherAuthorizationRequired" }] },
        { "actionName": "Update", "authorizationStrategies": [{ "authStrategyName": "NoFurtherAuthorizationRequired" }] },
        { "actionName": "Delete", "authorizationStrategies": [{ "authStrategyName": "NoFurtherAuthorizationRequired" }] }
      ],
      "authorizationStrategyOverrides": []
    }
  ]
}
```

`school` is a sibling of `educationOrganizations`, not nested inside it. The Admin App can reconstruct the tree with one `groupBy(parentClaimName)` pass.

**Not chosen: nested** `children` **array** (Admin API v2 shape) — requires recursive import traversal and produces awkward OpenAPI `$ref` cycles.

---
## Design Decision 2: Authorization Strategy Representation

**Decision: Separate default and override fields**

```json
"_defaultAuthorizationStrategies": [
  { "actionName": "Create", "authorizationStrategies": [{ "authStrategyName": "NamespaceBased" }] },
  { "actionName": "Read",   "authorizationStrategies": [{ "authStrategyName": "NoFurtherAuthorizationRequired" }] }
],
"authorizationStrategyOverrides": [
  { "actionName": "Create", "authorizationStrategies": [{ "authStrategyName": "NoFurtherAuthorizationRequired" }] }
]
```

- `_defaultAuthorizationStrategies` — read-only context (`_` prefix signals not writable on import); sourced from `node.defaultAuthorization`

- `authorizationStrategyOverrides` — writable; empty means "using defaults", non-empty means "deliberately different"

**Not chosen: single effective strategy field** — cannot distinguish override from default; import would need to infer override intent by comparing against the hierarchy.

---
## Complete Recommended Format

### `GET /v3/claimSets/{id}` and `GET /v3/claimSets/{id}/export`

Both endpoints return the same response. `/export` is retained as an alias to avoid breaking changes for clients already calling it.

```json
{
  "id": 42,
  "claimSetName": "DistrictHostedSISVendor",
  "_isSystemReserved": false,
  "_applications": [
    { "applicationName": "My SIS Application" }
  ],
  "resourceClaims": [
    {
      "name": "educationOrganizations",
      "claimName": "http://ed-fi.org/identity/claims/domains/educationOrganizations",
      "parentClaimName": null,
      "actions": [{ "name": "Read", "enabled": true }],
      "_defaultAuthorizationStrategies": [
        { "actionName": "Read",   "authorizationStrategies": [{ "authStrategyName": "NoFurtherAuthorizationRequired" }] },
        { "actionName": "Create", "authorizationStrategies": [{ "authStrategyName": "NoFurtherAuthorizationRequired" }] },
        { "actionName": "Update", "authorizationStrategies": [{ "authStrategyName": "NoFurtherAuthorizationRequired" }] },
        { "actionName": "Delete", "authorizationStrategies": [{ "authStrategyName": "NoFurtherAuthorizationRequired" }] }
      ],
      "authorizationStrategyOverrides": []
    },
    {
      "name": "school",
      "claimName": "http://ed-fi.org/identity/claims/ed-fi/school",
      "parentClaimName": "http://ed-fi.org/identity/claims/domains/educationOrganizations",
      "actions": [
        { "name": "Create", "enabled": true }, { "name": "Read", "enabled": true },
        { "name": "Update", "enabled": true }, { "name": "Delete", "enabled": true }
      ],
      "_defaultAuthorizationStrategies": [
        { "actionName": "Create", "authorizationStrategies": [{ "authStrategyName": "NoFurtherAuthorizationRequired" }] },
        { "actionName": "Read",   "authorizationStrategies": [{ "authStrategyName": "NoFurtherAuthorizationRequired" }] },
        { "actionName": "Update", "authorizationStrategies": [{ "authStrategyName": "NoFurtherAuthorizationRequired" }] },
        { "actionName": "Delete", "authorizationStrategies": [{ "authStrategyName": "NoFurtherAuthorizationRequired" }] }
      ],
      "authorizationStrategyOverrides": []
    }
  ]
}
```

### `POST /v3/claimSets/import`

Accepts the same `resourceClaims` array. Per entry:

| Field                                                                           | On import                                         |
|---------------------------------------------------------------------------------|---------------------------------------------------|
| `claimName`                                                                     | Locates the node in the stored CMS hierarchy      |
| `actions[].name` + `enabled`                                                    | Written to `claimSets[].actions` on the node      |
| `authorizationStrategyOverrides`                                                | Written to matching actions if non-empty          |
| `parentClaimName`, `_defaultAuthorizationStrategies`, `id`, `_isSystemReserved` | Ignored                                           |

- Node found, claim set exists → upsert

- Node found, claim set absent → insert

- Node not found → warning in response, processing continues (non-fatal)

- Additive/surgical — other claim sets on the same node are unaffected

⚠️ Import does not create new hierarchy nodes. See [Claims Migration](claims-migration.md) for handling custom resources.

These are intentional behavior changes for the existing CMS import route: duplicate claim set names are merged by upsert, missing resource claim nodes are reported as warnings rather than stopping the whole import, and the accepted payload is the flat URI-based v3 shape rather than the nested Admin API v2 shape.

---
## Payload Differences (v2 vs v3)

| Aspect                      | Admin API v2                               | CMS v3                                                            |
|-----------------------------|--------------------------------------------|-------------------------------------------------------------------|
| Resource identifier         | Short name: `"school"`                     | Full claim URI: `"http://ed-fi.org/identity/claims/ed-fi/school"` |
| Parent-child relationship   | Nested `children` array                    | Flat list with `parentClaimName`                                  |
| Default auth strategy field | `_defaultAuthorizationStrategiesForCrud`   | `_defaultAuthorizationStrategies`                                 |
| Override field              | `authorizationStrategyOverridesForCRUD`    | `authorizationStrategyOverrides`                                  |
| IDs                         | `actionId`, `authStrategyId` (DB integers) | Not present — identified by name                                  |
| `isInheritedFromParent`     | Present                                    | Not present — derivable from `parentClaimName`                    |
| `IsParent` flag             | Present                                    | Not present — implied by `parentClaimName: null`                  |
| Export level                | May include all leaf resources             | Configured hierarchy nodes only                                   |
| `ForCrud` / `ForCRUD` suffix | Present                                    | Removed — v3 also covers `ReadChanges`                            |

### Side-by-side example: `DistrictHostedSISVendor`

#### Admin API v2

```json
{
  "id": 7,
  "name": "DistrictHostedSISVendor",
  "_isSystemReserved": false,
  "_applications": [],
  "resourceClaims": [
    {
      "id": 24,
      "name": "educationOrganizations",
      "actions": [{ "name": "Read", "enabled": true }],
      "_defaultAuthorizationStrategiesForCrud": [
        { "actionId": 2, "actionName": "Read", "authorizationStrategies": [
            { "authStrategyId": 1, "authStrategyName": "NoFurtherAuthorizationRequired", "isInheritedFromParent": false }
        ]}
      ],
      "authorizationStrategyOverridesForCRUD": [],
      "children": [
        {
          "id": 31,
          "name": "school",
          "actions": [
            { "name": "Create", "enabled": true }, { "name": "Read", "enabled": true },
            { "name": "Update", "enabled": true }, { "name": "Delete", "enabled": true }
          ],
          "_defaultAuthorizationStrategiesForCrud": [
            { "actionId": 1, "actionName": "Create", "authorizationStrategies": [{ "authStrategyId": 1, "authStrategyName": "NoFurtherAuthorizationRequired", "isInheritedFromParent": true }] },
            { "actionId": 2, "actionName": "Read",   "authorizationStrategies": [{ "authStrategyId": 1, "authStrategyName": "NoFurtherAuthorizationRequired", "isInheritedFromParent": true }] },
            { "actionId": 3, "actionName": "Update", "authorizationStrategies": [{ "authStrategyId": 1, "authStrategyName": "NoFurtherAuthorizationRequired", "isInheritedFromParent": true }] },
            { "actionId": 4, "actionName": "Delete", "authorizationStrategies": [{ "authStrategyId": 1, "authStrategyName": "NoFurtherAuthorizationRequired", "isInheritedFromParent": true }] }
          ],
          "authorizationStrategyOverridesForCRUD": [],
          "children": []
        }
      ]
    }
  ]
}
```

#### CMS v3

```json
{
  "id": 7,
  "claimSetName": "DistrictHostedSISVendor",
  "_isSystemReserved": false,
  "resourceClaims": [
    {
      "name": "educationOrganizations",
      "claimName": "http://ed-fi.org/identity/claims/domains/educationOrganizations",
      "parentClaimName": null,
      "actions": [{ "name": "Read", "enabled": true }],
      "_defaultAuthorizationStrategies": [
        { "actionName": "Read",   "authorizationStrategies": [{ "authStrategyName": "NoFurtherAuthorizationRequired" }] },
        { "actionName": "Create", "authorizationStrategies": [{ "authStrategyName": "NoFurtherAuthorizationRequired" }] },
        { "actionName": "Update", "authorizationStrategies": [{ "authStrategyName": "NoFurtherAuthorizationRequired" }] },
        { "actionName": "Delete", "authorizationStrategies": [{ "authStrategyName": "NoFurtherAuthorizationRequired" }] }
      ],
      "authorizationStrategyOverrides": []
    },
    {
      "name": "school",
      "claimName": "http://ed-fi.org/identity/claims/ed-fi/school",
      "parentClaimName": "http://ed-fi.org/identity/claims/domains/educationOrganizations",
      "actions": [
        { "name": "Create", "enabled": true }, { "name": "Read", "enabled": true },
        { "name": "Update", "enabled": true }, { "name": "Delete", "enabled": true }
      ],
      "_defaultAuthorizationStrategies": [
        { "actionName": "Create", "authorizationStrategies": [{ "authStrategyName": "NoFurtherAuthorizationRequired" }] },
        { "actionName": "Read",   "authorizationStrategies": [{ "authStrategyName": "NoFurtherAuthorizationRequired" }] },
        { "actionName": "Update", "authorizationStrategies": [{ "authStrategyName": "NoFurtherAuthorizationRequired" }] },
        { "actionName": "Delete", "authorizationStrategies": [{ "authStrategyName": "NoFurtherAuthorizationRequired" }] }
      ],
      "authorizationStrategyOverrides": []
    }
  ]
}
```

`school` moves from `educationOrganizations.children[]` to a sibling entry with `parentClaimName`. All relational IDs and `isInheritedFromParent` are dropped — v3 is self-contained with URIs and string names.

See [Claims Migration](claims-migration.md) for the translation steps.

---
## Admin App Impact

A new `ResourceClaimsTableV3.tsx` is required regardless of format choice.

| Area                  | Impact                                                                                                                     |
|-----------------------|----------------------------------------------------------------------------------------------------------------------------|
| Tree rendering        | One `groupBy(parentClaimName)` before render                                                                               |
| Auth strategy display | Merge `_defaultAuthorizationStrategies` + `authorizationStrategyOverrides` per cell — same logic as v2 `AuthStrategyBadge` |
| Import UI (future)    | Construct flat array for POST body                                                                                         |
| V1/V2 components      | No change                                                                                                                  |

### Rendering sketch for v3

```text
resourceClaims
  → groupBy(parentClaimName)          // Map<uri|null, entry[]>
  → render roots (parentClaimName null) as top-level rows
  → render grouped children indented under their parent
  → per action cell:
      override present  → blue badge
      no override       → gray italic badge (default strategy)
      action disabled   → red "Denied" badge
```
