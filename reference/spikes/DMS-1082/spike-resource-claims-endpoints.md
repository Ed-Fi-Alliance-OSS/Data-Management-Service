# DMS-1082 Spike: Resource Claims Endpoints in CMS

## Purpose

This spike documents the investigation behind the CMS resource claim endpoints and the implementation approach that should be used.

It is intended to help the next developer understand:

- why the endpoints are needed
- how CMS data differs from the Admin API data model
- what approach was used to bridge that gap
- what risks remain with this shape of the solution

It is also intended to give future implementation work enough context to avoid filling in contract gaps incorrectly. The executable story for this spike lives in `reference/spikes/DMS-1082/ticket-resource-claims-endpoints.md`; this document explains why that story is shaped the way it is.

The endpoints in scope are:

- `GET /v2/resourceClaims`
- `GET /v2/resourceClaims/{id}`
- `GET /v2/resourceClaimActionAuthStrategies`

## Why This Spike Exists

The Ed-Fi Admin API exposes resource claim browsing endpoints that are used by tooling and administrators to inspect claim structure and authorization defaults. DMS authorization still follows the ODS/API resource-claim model: a request first needs the resource/action grant, and then any action-specific authorization strategy is evaluated against the data.

CMS did not previously expose equivalent read endpoints. The spike was needed to answer two questions:

1. Can the CMS data model support the same information in read form?
2. If yes, what is the least risky way to expose it without changing the claims model or adding write paths?

The answer is yes, but with important differences in how the data is stored and resolved.

## Key Data-Model Difference

The most important distinction is this:

- In the Admin API, resource claims are represented as relational rows with a resource-claim table and integer identifiers.
- In CMS, the claim structure is stored as a hierarchical JSON document.

That difference changes the implementation shape completely.

### CMS hierarchy JSON

CMS stores the claims tree as a nested document. In the current CMS model, a claim node contains:

- `name`, whose value is the full claim URI
- optional `defaultAuthorization`
- child `claims`
- a `Parent` navigation property that is populated during deserialization

This means the hierarchy already exists in memory as a tree, but it is not indexed as relational rows.

### Resource claim metadata table

The CMS also has persisted resource-claim metadata in `dmscs.ResourceClaim`.

That table provides the values needed for the Admin API-compatible response shape, including:

- a stable `long` id backed by the PostgreSQL `BIGINT` column
- a display resource name
- the claim URI used to match against the hierarchy document

This table is what makes the CMS response contract possible without changing the hierarchy schema.

The hierarchy remains the structural source of truth. The metadata table enriches hierarchy nodes; it does not decide which nodes are present in the tree response. A metadata row without a hierarchy node is therefore not returned by these read endpoints.

## Why the Endpoints Make Sense

These endpoints are justified because the information already exists in CMS, just not in the same form as the Admin API.

The implementation is effectively a projection:

- the hierarchy JSON provides structure
- the resource-claim table provides the public-facing identifiers and display names
- the claim-set metadata provides authorization-action and strategy details

That makes the endpoints read-only projections over existing configuration, not a new domain model.

## Approach Used

The intended service implementation walks the claim hierarchy at runtime and joins each node with the corresponding metadata row from `dmscs.ResourceClaim`.

That produces the response tree for:

- `GET /v2/resourceClaims`
- `GET /v2/resourceClaims/{id}`

For the auth-strategy endpoint, the service:

- traverses the same claim hierarchy
- filters to claims that define default authorization
- resolves action names from the existing claim-set action list
- resolves authorization strategy names from the existing authorization-strategy list
- emits a flat response shape for each claim with default authorization

This is a reasonable approach because it reuses the existing configuration stores instead of introducing a parallel persistence model.

## Implementation Contract Summary

Use the companion story document for the final acceptance criteria. The core contract is:

- `GET /v2/resourceClaims` returns a hierarchy of projected `ResourceClaimResponse` nodes.
- `GET /v2/resourceClaims/{id}` searches the successfully projected hierarchy by metadata id and returns `404` only when the id is absent from that valid projection.
- `GET /v2/resourceClaimActionAuthStrategies` returns a flat list for claims whose `DefaultAuthorization` contains at least one action.
- `ResourceClaimResponse.Name` is the display resource name from `dmscs.ResourceClaim.ResourceName`, not the claim URI.
- `ResourceClaimActionAuthStrategyResponse.ClaimName` is the full claim URI.
- `ResourceClaimResponse.Id`, `ParentId`, `ResourceClaimActionAuthStrategyResponse.ResourceClaimId`, and `AuthStrategyId` should be `long` to match CMS persisted identifiers.
- `ActionId` should remain `int`, matching the existing action repository model.
- Missing lookup data is an integrity failure, not a reason to omit nodes or return zero ids.

Example resource-claim node:

```json
{
  "id": 42,
  "name": "Student",
  "parentId": 0,
  "parentName": null,
  "children": []
}
```

Example action/auth-strategy item:

```json
{
  "resourceClaimId": 42,
  "resourceName": "Student",
  "claimName": "http://ed-fi.org/identity/claims/ed-fi/student",
  "authorizationStrategiesForActions": [
    {
      "actionId": 1,
      "actionName": "Read",
      "authorizationStrategies": [
        {
          "authStrategyId": 7,
          "authStrategyName": "RelationshipsWithEdOrgsOnly"
        }
      ]
    }
  ]
}
```

## Technical Tradeoffs

### 1. Runtime join versus materialized claim table

The chosen design performs the hierarchy-to-metadata match at request time.

Benefits:

- no schema change
- no data migration
- no extra write path
- easy to reason about because the source of truth stays in CMS configuration

Costs:

- response quality depends on both the hierarchy JSON and the metadata table
- the service can fail or return incomplete data if those sources drift
- each request requires walking the tree and resolving lookup data in memory

### 2. Exact match required between hierarchy and metadata

The hierarchy and the resource-claim table must agree on the claim URI.

This is the critical dependency in the current design.

If the hierarchy contains a node that is not present in `dmscs.ResourceClaim`, the endpoint cannot safely infer the missing row. The same is true if the metadata exists but the hierarchy node is absent.

That means this feature depends on data integrity across two sources, not one.

Implementation consequence:

- A hierarchy node without metadata must fail the projection.
- A parent hierarchy node without metadata must fail the projection because child `ParentId` and `ParentName` cannot be produced reliably.
- A metadata row without a hierarchy node may be ignored by these read endpoints because the hierarchy determines structure.
- Duplicate metadata rows for the same claim URI should fail the projection.

### 3. Tree traversal versus targeted lookup

The single-claim endpoint is served by loading the hierarchy and searching it in memory.

Benefits:

- one service path for both list and single-item views
- no need for a separate query shape
- easy to test

Costs:

- the request still pays the cost of building the hierarchy projection
- lookup is linear in the size of the projected tree

For the current dataset size, that is acceptable. If the hierarchy grows substantially, this could become a performance concern.

### 4. Graceful degradation versus explicit failure

Projection-style APIs have a specific risk: missing supporting data can produce partial output if the service treats lookup failures as optional.

That is dangerous because the API can look successful while silently omitting claim nodes or authorization information.

For this feature, the safer behavior is to fail explicitly when required metadata cannot be resolved.

Concrete examples of unsafe graceful degradation:

- returning `200 OK` after skipping a hierarchy node whose claim URI is absent from `dmscs.ResourceClaim`
- returning `200 OK` with `actionId: 0` because an action name did not resolve
- returning `200 OK` with `authStrategyId: 0` because authorization strategies failed to load
- returning an empty action/auth-strategy list because the authorization-strategy repository returned a failure

Those cases should be service failures with useful logs, not successful responses.

## Risks With This Approach

### Risk: incomplete responses when metadata drifts

If the hierarchy JSON and `dmscs.ResourceClaim` fall out of sync, the API can return an incomplete tree or miss claims entirely.

This is the highest operational risk in the design.

### Risk: misleading success on partial lookup failures

If authorization strategies or action names cannot be resolved, the endpoint can return a `200 OK` with incomplete nested data unless the service handles that failure explicitly.

That makes it harder for clients and operators to detect a data problem.

### Risk: datastore-specific wiring

The current CMS service wiring supports both `postgresql` and `mssql` values for `AppSettings:Datastore`, but the resource-claim schema and seed data currently exist only in the PostgreSQL deployment scripts.

If the service is deployed against a datastore that does not have an equivalent table, seed data, repository implementation, and registration path, the new endpoints can fail at startup or runtime.

This should be treated as a deployment constraint, not an incidental detail.

The final implementation should choose one of these outcomes:

- implement `IResourceClaimRepository` and deployment support for every CMS datastore supported by `main`
- or document PostgreSQL-only support and gate endpoint/repository registration so unsupported datastores fail deliberately with a clear configuration error

Leaving a PostgreSQL repository registered in common service wiring is not sufficient because it hides the deployment constraint and conflicts with the existing datastore selection pattern.

### Risk: endpoint contract depends on source-data stability

The API contract is only as stable as the persisted resource-claim metadata.

If downstream consumers expect the identifiers, resource names, or hierarchy shape to be stable, the CMS must maintain consistent seed data and repository behavior.

## What the Developer Should Take Away

- CMS can support these endpoints without a schema redesign.
- The key bridge is the match between hierarchy JSON and the resource-claim metadata table.
- The implementation is justified because it exposes existing configuration in a read-only form.
- The approach is workable, but it is not self-healing if the data sources drift.
- The service should treat missing support data as an integrity problem, not a normal success path.

## Implementation Guidance

When working in this area, the next developer should keep these rules in mind:

- preserve the hierarchy JSON as the structural source of truth
- preserve the resource-claim table as the lookup source for response metadata
- keep the endpoints read-only
- fail explicitly when required lookup data is missing
- make datastore support explicit in startup wiring
- keep tests focused on both the success path and the failure path

## Implementation Notes

Implementation should start from the companion story and the current `main` code paths rather than assuming the service, module, or repository already exists. The important paths to compare are the endpoint module pattern, repository result records, datastore registration, `IClaimsHierarchyRepository.GetClaimsHierarchy`, `IClaimSetRepository.GetActions`, and `IClaimSetRepository.GetAuthorizationStrategies`.

The service should validate the full projection before returning success. Missing hierarchy metadata, failed action lookup, failed authorization-strategy lookup, and datastore support gaps should surface through explicit result cases and endpoint mappings instead of becoming empty collections or zero-valued ids.

The scope remains read-only. Do not add write endpoints, schema redesigns, synthetic ids, or fallback ids to make the projection succeed.

## Outcome

The spike supports adding the endpoints because the necessary information already exists in CMS.

The important caveat is that the implementation is a projection over two data sources, so correctness depends on them staying aligned.

That is the main design risk to carry forward in maintenance and future changes.
