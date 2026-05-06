# API Contract and Synchronization

## Feature Availability

The Change Queries API surface is controlled by `AppSettings.EnableChangeQueries`.

Required contract:

- DMS follows the Ed-Fi ODS/API default-on posture for this feature, so if the flag is absent DMS treats it as `true`
- when the flag is off, the dedicated Change Query routes are not exposed and requests to them return `404 Not Found`
- feature-off handling must reserve the `/deletes`, `/keyChanges`, and `availableChangeVersions` paths so those requests cannot fall through to the generic collection or item-by-id routes
- the feature-off `404 Not Found` behavior for those dedicated routes intentionally treats the endpoints as not exposed; DMS-843 does not define a bespoke Change-Query-specific response-body contract for that hidden-route case
- when the flag is off, collection GET requests that include `minChangeVersion`, `maxChangeVersion`, or `Use-Snapshot = true` return `400 Bad Request` with the feature-disabled problem details defined below
- when the flag is off, DMS must not silently treat Change Query requests as ordinary non-Change-Query requests
- when the flag is on, the Change Queries API surface is available subject to the contracts in this document

## Public Endpoints

## 1. Available change versions

Route:

```text
GET /{routePrefix}changeQueries/v1/availableChangeVersions
```

Example response:

```json
{
  "oldestChangeVersion": 0,
  "newestChangeVersion": 24822
}
```

Semantics:

- instance-scoped to the resolved DMS instance identified by route prefix and instance-resolution rules; change-version values are scoped per resolved DMS instance and watermarks from one instance must not be used as bounds against a different instance
- authenticated like other DMS endpoints
- not filtered by per-resource authorization rules
- readable profile headers do not apply to this endpoint; any profile media type is ignored and the response remains ordinary `application/json`
- aligned to the Ed-Fi ODS/API single synchronization surface used across `keyChanges`, changed-resource queries, and `deletes`; see the [platform guide](https://docs.ed-fi.org/reference/ods-api/platform-dev-guide/features/changed-record-queries/) and [client guide](https://docs.ed-fi.org/reference/ods-api/client-developers-guide/using-the-changed-record-queries/)
- provides the watermarks used to define a bounded synchronization window
- for the initial DMS-843 scope, no purge-driven replay-floor advancement is assumed, so `oldestChangeVersion` is `0`
- `newestChangeVersion` is the current synchronization ceiling for that same surface
- to preserve ODS watermark semantics and avoid commit-order visibility gaps, `newestChangeVersion` is derived from the selected source's change-version allocation ceiling (`dms.ChangeVersionSequence` high-watermark, equivalent to `next value - 1`)
- `newestChangeVersion` is not computed as max committed retained row value across participating tracking tables
- when `Use-Snapshot = true`, both bounds are computed from the snapshot-visible synchronization surface rather than from the live primary database, so `newestChangeVersion` is the ceiling for that snapshot-backed pass
- callers must pair `availableChangeVersions` with later data reads that use the same `Use-Snapshot` choice; mixing snapshot and non-snapshot reads in one pass invalidates the advertised ceiling, and stable snapshot-backed passes additionally depend on the active snapshot binding remaining unchanged across those requests
- when the synchronization surface has no allocated change versions and no replay-floor metadata exists, the endpoint returns `0` for both values; this is ODS-output-compatible behavior because current ODS and DMS both read the sequence ceiling (`next value - 1`), which normalizes to `0` before the first allocation
- if a later retention phase introduces replay-floor metadata, `oldestChangeVersion` becomes the lowest valid inclusive `minChangeVersion` that can still return complete results, and `newestChangeVersion = oldestChangeVersion =` the greatest participating replay floor when all participating sources are empty after purge

ODS-compatible snapshot derivative context:

- synchronization requests operate on one resolved instance derivative at a time: live-primary derivative when `Use-Snapshot` is absent or `false`, snapshot-visible derivative when `Use-Snapshot = true`
- route-prefix instance resolution still determines the base DMS instance; `Use-Snapshot` only selects which derivative of that same instance supplies the synchronization surface
- snapshot selection is configuration-driven per resolved instance: either no snapshot derivative is bound, or exactly one read-only snapshot derivative binding is available for `Use-Snapshot = true`
- DMS resolves that active binding from operator-managed instance configuration (`SnapshotConnectionString` or equivalent) at request time; the public contract does not carry a snapshot binding id, derivative identity, or pass token
- the selected derivative must expose a complete synchronization surface, including `dms.ChangeVersionSequence`, `dms.DocumentChangeEvent`, `dms.DocumentDeleteTracking`, `dms.DocumentKeyChangeTracking`, `dms.ResourceKey`, and required authorization companion artifacts used by tracked-change authorization
- ODS-style client flow applies unchanged: obtain one window from `availableChangeVersions`, then execute changed-resource, `/keyChanges`, and `/deletes` against that same derivative
- DMS validates and uses the currently active instance-scoped binding on each `Use-Snapshot = true` request; multi-request pass stability therefore depends on operations keeping that binding unchanged for the full pass
- if no usable binding exists for a given `Use-Snapshot = true` request, DMS returns the documented snapshot-unavailable contract; DMS-843 does not define server-side pinning or rotation detection across requests
- DMS-843 does not require ODS-specific infrastructure naming, but any snapshot implementation must preserve the same derivative-selection behavior and equivalent synchronization artifacts

## 2. Changed-resource collection query

Route:

```text
GET /{routePrefix}data/{projectNamespace}/{endpointName}?minChangeVersion={min}&maxChangeVersion={max}&limit={limit}&offset={offset}&totalCount={totalCount}
```

Examples:

```text
GET /data/ed-fi/students?minChangeVersion=100&maxChangeVersion=250
GET /data/ed-fi/students?maxChangeVersion=250
GET /tenant-a/data/ed-fi/students?minChangeVersion=100&maxChangeVersion=250&limit=25&offset=0
GET /district-100/2026/data/ed-fi/students?minChangeVersion=100
```

Semantics:

- when both `minChangeVersion` and `maxChangeVersion` are absent, the route behaves exactly like the current collection GET route; if `Use-Snapshot = true`, it still uses ordinary collection-GET semantics but reads from the chosen snapshot source rather than from the live primary
- when either `minChangeVersion` or `maxChangeVersion` is present, the route switches into changed-resource mode
- the payload shape remains the normal resource array returned by the current collection GET route
- the API returns the latest current representation of each qualifying resource once
- when `Use-Snapshot = true`, "current representation" means the representation current inside the snapshot rather than the representation current on the live primary at request time
- the API does not return every intermediate mutation
- `limit`, `offset`, and `totalCount` in changed-resource mode apply to the final authorized one-row-per-resource result set; internal journal candidates eliminated by verification or authorization do not consume page space or count toward `totalCount`
- the final changed-resource result set is ordered by `ChangeVersion` plus a stable backend-local document tie-breaker (`DocumentPartitionKey`, `DocumentId` on the current backend; `DocumentId` alone on redesign-aligned backends where `DocumentPartitionKey` is absent)
- when a readable profile is applied, change eligibility is still resource-level based on the underlying resource `ChangeVersion`
- readable profiles filter only the returned representation; they do not create profile-specific change tracking or suppress an otherwise qualifying changed resource

## 3. Delete query

Route:

```text
GET /{routePrefix}data/{projectNamespace}/{endpointName}/deletes?minChangeVersion={min}&maxChangeVersion={max}&limit={limit}&offset={offset}&totalCount={totalCount}
```

Example response:

```json
[
  {
    "id": "65ce14c8-4cf0-4cf7-8b34-bd65e1c07ab0",
    "changeVersion": 234550,
    "keyValues": {
      "studentUniqueId": "1001"
    }
  }
]
```

Delete query semantics:

- results represent deleted resources whose delete `ChangeVersion` falls in the requested window
- the lower and upper bounds are independently optional, following ODS behavior
- when `Use-Snapshot = true`, the delete surface is the delete surface visible inside the snapshot rather than the live primary delete surface at request time
- `id` is the canonical public resource identifier for the deleted item and is sourced from the stored `DocumentUuid`
- delete rows are ordered deterministically
- delete rows preserve the natural-key values needed by downstream synchronization clients
- the public `/deletes` surface suppresses a tombstone when the same **natural-key identity** (not the same `DocumentUuid`) is visible again as a current live row on the same selected source when `/deletes` is evaluated; the surviving live row is not filtered by the requested delete window, so suppression compares the tombstone's stored identity to the selected source's current live identity set for that resource
- this is intended legacy ODS parity rather than an explicit DMS-specific contract break: suppression depends on current live-row visibility on the selected source, not on whether the re-added live row itself falls inside the requested delete window
- delete rows are authorization-filtered according to the tracked-change contract defined in `05-Authorization-and-Delete-Semantics.md`, including the accepted DMS-specific ownership exception
- readable profile headers do not apply; the endpoint must ignore profile media types and must not alter the payload shape or media type based on profile rules
- `/deletes` responses include RFC 5988 `Link` headers (`first`, `prev`, `next`, `last`) consistent with ODS collection GET pagination behavior, alongside the `Total-Count` header when `totalCount=true` is requested

## 4. Key change query

Route:

```text
GET /{routePrefix}data/{projectNamespace}/{endpointName}/keyChanges?minChangeVersion={min}&maxChangeVersion={max}&limit={limit}&offset={offset}&totalCount={totalCount}
```

Example response:

```json
[
  {
    "id": "65ce14c8-4cf0-4cf7-8b34-bd65e1c07ab0",
    "changeVersion": 234560,
    "oldKeyValues": {
      "studentUniqueId": "1001"
    },
    "newKeyValues": {
      "studentUniqueId": "1002"
    }
  }
]
```

Key change semantics:

- results represent natural-key changes whose key-change `ChangeVersion` falls in the requested window
- the lower and upper bounds are independently optional, following ODS behavior
- when `Use-Snapshot = true`, the key-change surface is the surface visible inside the snapshot rather than the live primary key-change surface at request time
- the route is valid for all change-query-enabled resources
- resources that support identity updates return key-change rows when qualifying transitions exist
- resources that do not support identity updates return `200 OK` with an empty array and `totalCount = 0` when `totalCount` is requested
- `id` is the canonical public resource identifier for the affected item and is sourced from the stored `DocumentUuid`
- the row `changeVersion` is the public key-change token allocated for the tracked key-change row
- this token is distinct from both the live resource `ChangeVersion` and the internal `IdentityVersion`
- DMS-843 adopts the legacy ODS model here so key-change rows sort on their own tracked-change token rather than on the live representation-change stamp of the identity-changing write
- key-change rows are ordered deterministically
- key-change-query authorization is evaluated against each tracking row's stored pre-update tracked-change authorization data before collapse, ordering, paging, and `totalCount` are finalized
- key-change-query authorization follows the tracked-change contract defined in `05-Authorization-and-Delete-Semantics.md`, including the accepted DMS-specific ownership exception
- the public route collapses the surviving authorized tracking rows for one affected resource item within the requested window into one response row with the window's initial `oldKeyValues`, final `newKeyValues`, and final tracked key-change `changeVersion`
- if multiple key changes occur for the same resource inside one window, the API returns one row for that affected resource item rather than one row per transition
- the collapsed row's `changeVersion` is the final surviving tracked key-change token for that resource item in the requested window
- `limit`, `offset`, and `totalCount` on `/keyChanges` apply to that final authorized collapsed result set rather than to the raw tracking-row stream
- key-change rows are authorization-filtered
- readable profile headers do not apply; the endpoint must ignore profile media types and must not alter the payload shape or media type based on profile rules
- `/keyChanges` responses include RFC 5988 `Link` headers (`first`, `prev`, `next`, `last`) consistent with ODS collection GET pagination behavior, alongside the `Total-Count` header when `totalCount=true` is requested

## Key payload field naming and ordering

`keyValues`, `oldKeyValues`, and `newKeyValues` are resource-scoped objects derived from the routed resource's `IdentityJsonPaths`.

Required contract:

- start with the leaf property name for each identity path
- if two identity paths for the same resource share the same leaf, prepend parent property segments until each field name is unique within that resource
- emit the chosen suffix in lower camel case without separators
- preserve declared `IdentityJsonPaths` order when materializing the response object

Examples:

- `$.studentUniqueId` -> `studentUniqueId`
- `$.schoolReference.schoolId` and `$.sessionReference.schoolId` -> `schoolReferenceSchoolId` and `sessionReferenceSchoolId`

Composite example when duplicate leaf names exist:

```json
{
  "localCourseCode": "ALG-1",
  "schoolReferenceSchoolId": 255901001,
  "sessionReferenceSchoolId": 255901001,
  "schoolYear": 2026,
  "sessionName": "Fall Semester"
}
```

The field-name contract is evaluated per routed resource, so different resources may legitimately expose different key shapes.

## Request Rules

## `Use-Snapshot`

- boolean request header on `availableChangeVersions`, collection GET, `/deletes`, and `/keyChanges`
- when absent, the effective value is `false`
- when absent or `false`, requests execute against the ordinary current committed state visible to the resolved DMS instance
- when `true`, requests execute against the configured read-only snapshot source for the resolved DMS instance
- DMS never silently downgrades `Use-Snapshot = true` requests to live reads; if the snapshot source is unavailable, the request fails explicitly
- clients must use the same `Use-Snapshot` choice across one synchronization pass because `availableChangeVersions` and the subsequent data reads must all target the same source
- collection GET requests used for initial full-load synchronization may send `Use-Snapshot = true` even when both change-version bounds are absent; the route still behaves as an ordinary collection GET, but it reads from the snapshot source
- snapshot lifecycle handling is in scope only at request-time binding resolution and validation: DMS must resolve the currently active instance-scoped binding for each request and reject the request if no usable binding exists
- DMS-843 does not add a binding identifier, derivative identity echo, or other client-visible pinning mechanism; stable multi-request snapshot passes therefore rely on operators keeping the active binding unchanged rather than on a server-enforced pass token

### Authorization semantics for snapshot-backed initial loads

- Authorization for requests that use `Use-Snapshot = true` follows the same resource/operation mapping as live reads; DMS does not introduce a separate permission model for snapshot-backed reads. Specifically:
  - Ordinary collection GETs that omit `minChangeVersion`/`maxChangeVersion` (for example, an initial full-load executed with `Use-Snapshot = true`) are authorized using the resource `Read` permission/claim set mapping.
  - Collection GETs that include either `minChangeVersion` or `maxChangeVersion` (changed-resource mode), regardless of `Use-Snapshot`, are authorized using the `ReadChanges` (tracked-change) permission/claim set mapping.
  - `/deletes` and `/keyChanges` when executed with `Use-Snapshot = true` are authorized under the tracked-change authorization contract (the same `ReadChanges` mapping and tracked-change companion rules, including ownership/document-id constraints documented in `05-Authorization-and-Delete-Semantics.md`).

These rules ensure that selecting `Use-Snapshot = true` affects only the read surface and not the required authorization claims. Deployments that require operator-only access to snapshot-backed passes should implement that restriction as an operator policy (for example by restricting which API clients may send `Use-Snapshot = true`) and document it in instance configuration guidance.

## Query Parameter Rules

## `minChangeVersion`

- optional on changed-resource collection GET, `/deletes`, and `/keyChanges`
- when absent and `maxChangeVersion` is also absent, the collection GET route remains an ordinary non-Change-Query request
- when absent and `maxChangeVersion` is also absent on `/deletes` or `/keyChanges`, the route returns all retained tracked rows for the routed resource
- must be a non-negative signed 64-bit integer
- when `minChangeVersion` exceeds the current synchronization ceiling returned by `availableChangeVersions`, all three surfaces return `200 OK` with an empty result set and `totalCount = 0` when requested; this matches ODS behavior

## `keyValues`, `oldKeyValues`, and `newKeyValues` — Extension Resource Identity

When an extension adds identity-component fields to a base resource (extended identity), those additional fields participate in `/keyChanges`, `oldKeyValues`/`newKeyValues`, and the `IdentityVersion` advancement decision on exactly the same footing as base-resource identity fields.

Required rule:
- the source of truth for all identity fields — base and extension — is always `ResourceSchema.IdentityJsonPaths` for the deployed effective schema
- the shortest-unique-suffix aliasing algorithm operates across the full merged set of identity paths, including extension-contributed paths
- extension deployments that add new identity paths to an existing resource may generate new or changed alias names for existing identity fields if the extension introduces a duplicate leaf name; such alias collisions must be detected and rejected at schema compilation time as described in the fail-fast alias-derivation validation rule in `06-Validation-Rollout-and-Operations.md`

## `maxChangeVersion`

- optional
- when supplied, must be a non-negative signed 64-bit integer
- when `minChangeVersion` is also supplied, `maxChangeVersion` must be greater than or equal to `minChangeVersion`

## `limit`, `offset`, `totalCount`

- retain the existing validation rules already enforced by DMS
- remain valid in changed-resource mode and on `/deletes`
- remain valid on `/keyChanges`
- in changed-resource mode, all three apply to the final authorized one-row-per-resource result set after any internal candidate verification or elimination required by the execution strategy
- on `/keyChanges`, all three apply after authorization filtering, over the final one-row-per-key-change-event result set
- when `totalCount=true` is requested, the server returns the result count as the `Total-Count` HTTP response header, preserving ODS/API header contract parity; `totalCount` is not returned as a body field

## Window Semantics

When both bounds are supplied, the feature uses the Ed-Fi lower-bound-inclusive rule:

```text
minChangeVersion <= ChangeVersion <= maxChangeVersion
```

This rule applies equally to:

- changed-resource queries
- delete queries
- key-change queries

When only `minChangeVersion` is supplied, the effective rule is:

```text
minChangeVersion <= ChangeVersion
```

When only `maxChangeVersion` is supplied, the effective rule is:

```text
ChangeVersion <= maxChangeVersion
```

When neither bound is supplied:

- the collection GET route remains an ordinary non-Change-Query request
- `/deletes` and `/keyChanges` return all retained tracked rows for the routed resource

`ChangeVersion` values are globally ordered and monotonic, but not guaranteed to be gap-free.

## Synchronization Algorithms

DMS-843 defines two synchronization flows that share the same routes, payloads, and tracking artifacts and are selected only by `Use-Snapshot`.

## Non-snapshot synchronization

When `Use-Snapshot` is absent or `false`, all requests execute against the ordinary current committed state visible to the resolved DMS instance.

The normative non-snapshot client flow is:

1. Identify the resources to be synchronized and their dependency order using Ed-Fi model dependencies, resource dependency metadata, or an equivalent dependency model.
2. For initial synchronization, call `availableChangeVersions`, capture `newestChangeVersion` as the initial synchronization version, load data with ordinary collection GETs in dependency order, and persist the captured synchronization version only after the initial load succeeds.
3. For repeating change processing, let `lastSuccessfulChangeVersion` be the saved synchronization version from the previous successful pass.
4. Compute `startChangeVersion = lastSuccessfulChangeVersion + 1`.
5. Call `availableChangeVersions` and capture its `newestChangeVersion` as `synchronizationVersion`.
6. Query `keyChanges` for applicable resources in dependency order using `minChangeVersion = startChangeVersion` and `maxChangeVersion = synchronizationVersion`.
7. Query changed resources in dependency order using the same inclusive bounded window.
8. Query `/deletes` in reverse-dependency order using the same inclusive bounded window.
9. Apply key changes first, then live-resource changes, then deletes locally, preserving the same dependency order rules.
10. Persist `synchronizationVersion` only after the full pass succeeds.

Implications of this algorithm:

- supplying both bounds caps the pass at the watermark captured in step 5; rows written after that point are not included in the current pass and will be picked up in the next pass
- this is the single normative non-snapshot algorithm for DMS-843; the Ed-Fi client guidance discusses both bounded and open-ended non-snapshot alternatives, but DMS-843 standardizes the bounded-window form for consistency
- open-ended lower-bound-only windows remain supported by the public API for ODS compatibility, caller-managed partitioning, diagnostics, or specialized workflows, but they are not the normative DMS-843 non-snapshot synchronization contract
- rows with `ChangeVersion > synchronizationVersion` that arrive during the pass are excluded by the `maxChangeVersion` bound and will appear on the next pass; callers do not need duplicate-delivery tolerance for that case
- non-snapshot processing remains best-effort under concurrent writes for other paging-drift reasons (see Paging and Completeness by Mode); the bounded window eliminates one entire class of drift by fixing the upper watermark at pass start

## Snapshot-backed synchronization

When `Use-Snapshot = true`, the client chooses the same API routes and payloads but asks DMS to execute the requests against the configured read-only snapshot source for the resolved instance.

The normative snapshot-backed client flow is:

1. Identify the resources to be synchronized and their dependency order using Ed-Fi model dependencies, resource dependency metadata, or an equivalent dependency model.
2. For initial synchronization, call `availableChangeVersions` with `Use-Snapshot = true`, capture the returned `newestChangeVersion` as the initial synchronization version, load data with ordinary collection GETs in dependency order while continuing to send `Use-Snapshot = true`, and persist the captured synchronization version only after the initial load succeeds.
3. For repeating change processing, let `lastSuccessfulChangeVersion` be the saved synchronization version from the previous successful snapshot-backed pass.
4. Compute `startChangeVersion = lastSuccessfulChangeVersion + 1`.
5. Call `availableChangeVersions` with `Use-Snapshot = true` and capture its `newestChangeVersion` as `synchronizationVersion`.
6. Query `keyChanges` for applicable resources in dependency order using `minChangeVersion = startChangeVersion`, `maxChangeVersion = synchronizationVersion`, and `Use-Snapshot = true`.
7. Query changed resources in dependency order using the same inclusive bounded window and `Use-Snapshot = true`.
8. Query `/deletes` in reverse-dependency order using the same inclusive bounded window and `Use-Snapshot = true`.
9. Apply key changes first, then live-resource changes, then deletes locally, preserving the same dependency order rules.
10. Persist `synchronizationVersion` only after the full pass succeeds.

Implications of this algorithm:

- each `Use-Snapshot = true` request reads a frozen snapshot surface for the currently active binding selected for that request
- bounded windows using `maxChangeVersion = synchronizationVersion` are correctness-safe for a multi-request pass only while those requests continue to hit the same snapshot binding; DMS-843 does not provide a public binding token or server-side pass pinning to enforce that across requests
- offset paging is stable within a snapshot-backed pass only while the pass continues to read the same snapshot binding and page membership therefore does not drift under concurrent writes to the live primary
- writes committed after the selected snapshot point are invisible to requests served by that binding and will not be observed until a later snapshot refresh or a later non-snapshot run
- if no usable snapshot binding exists for a later request in a pass, the request fails with the documented snapshot-unavailable contract and the client must discard the partial pass and retry once operations restore a stable binding or rerun in non-snapshot mode according to its tolerance for best-effort behavior
- if operations repoint the binding between requests, DMS resolves the later request against the newly active binding because the public contract exposes only `Use-Snapshot`; keeping one binding stable across a pass is therefore an operational prerequisite rather than a server-enforced API guarantee

Open-ended lower-bound-only windows and max-only windows remain supported by the public API for ODS compatibility, caller-managed partitioning, diagnostics, or specialized workflows in either mode, but they are not the normative DMS-843 synchronization algorithm.

The ordering and algorithms above keep the snapshot-backed flow aligned to the Ed-Fi client guidance and define one bounded-window non-snapshot flow as the normative DMS-843 contract while still allowing the broader ODS-compatible request shapes.

## Response Semantics by Scenario

## Multiple updates before synchronization

Expected behavior:

- the changed-resource query returns the resource once
- the representation is the current representation at query time
- earlier intermediate updates are not returned separately

## Insert followed by delete before synchronization

Expected behavior:

- the resource does not appear in the changed-resource query
- the delete appears in `/deletes`

## Delete followed by reinsert in the same window

Expected behavior:

- `/deletes` suppresses the earlier delete tombstone for that window
- the changed-resource query may return the later live row if its current `ChangeVersion` falls in the requested window
- clients observe the net current-state result for that window rather than delete/recreate churn

## Delete followed by reinsert in a later window

Expected behavior:

- if the later live row is already visible on the selected source when `/deletes` is evaluated, the earlier tombstone is suppressed even when the re-add falls outside the requested delete window
- if `/deletes` is evaluated before the re-add becomes visible on the selected source, the earlier tombstone may still appear
- the changed-resource query may return the later live row in a later window
- clients must treat this as delete plus later create across windows

This is intended legacy ODS parity. DMS-843 does not define a narrower same-window-only suppression rule.

## Multiple key changes before synchronization

Expected behavior:

- `/keyChanges` returns one collapsed row per affected resource item in the requested window
- that row's `oldKeyValues` are the initial key values from the earliest surviving authorized transition in the window
- that row's `newKeyValues` are the final key values from the latest surviving authorized transition in the window
- that row's `changeVersion` is the final surviving tracked key-change token for that resource item in the window

## Key change followed by delete before synchronization

Expected behavior:

- `/keyChanges` returns the key transition if the key-change `ChangeVersion` falls in the window
- `/deletes` returns the later delete tombstone if the delete `ChangeVersion` falls in the window
- both rows may legitimately appear because they answer different synchronization questions and are not duplicates of one another

## Explicit DMS Error Contract for Invalid or Unusable Requests

The public Ed-Fi documentation describes the change-query workflow and parameters, but it does not publish a normative response-body contract for malformed windows or retention-floor misses. DMS therefore defines those edge cases explicitly rather than leaving them ambiguous.

Required status behavior:

- when Change Queries is disabled and a collection GET request supplies `minChangeVersion`, `maxChangeVersion`, or `Use-Snapshot = true`, return `400 Bad Request`
- when Change Queries is disabled and `/deletes`, `/keyChanges`, or `availableChangeVersions` is requested, return `404 Not Found` through the reserved dedicated route rather than by falling through to generic route parsing
- invalid `Use-Snapshot` header returns `400 Bad Request`
- if `Use-Snapshot = true` and the resolved snapshot source is not configured, not reachable, or no longer available, return `404 Not Found`
- invalid or negative `minChangeVersion` returns `400 Bad Request`
- invalid or negative `maxChangeVersion` returns `400 Bad Request`
- `maxChangeVersion < minChangeVersion` returns `400 Bad Request`
- if a later retention phase introduces replay-floor advancement, `minChangeVersion < oldestChangeVersion` returns `409 Conflict`

Required body behavior:

- error responses use `application/problem+json`
- all responses include RFC 9457 core members: `type`, `title`, `status`, and `detail`
- all responses include `correlationId`
- all responses include `validationErrors`
- all responses include an `errors` array
- for the change-query request-validation and replay-floor failures defined below, `validationErrors` is present as an empty object, `{}`, because these failures are request-level contract errors rather than path-keyed document-validation failures

`400 Bad Request` for change-query parameters or `Use-Snapshot = true` on the collection GET route when the feature is explicitly disabled:

- `type`: `urn:ed-fi:api:change-queries:feature-disabled`
- `title`: `Change Queries Feature Disabled`
- `status`: `400`
- `detail`: `Change Queries is not enabled for this DMS deployment.`
- `validationErrors`: `{}`
- `errors`: exactly one entry, `Change query parameters or snapshot-backed reads cannot be used because the Change Queries feature is disabled.`

`400 Bad Request` for invalid `Use-Snapshot`:

- `type`: `urn:ed-fi:api:change-queries:validation:use-snapshot`
- `title`: `Invalid Change Query Request`
- `status`: `400`
- `detail`: `The change query parameters are invalid.`
- `validationErrors`: `{}`
- `errors`: exactly one entry, `The 'Use-Snapshot' header must be a boolean value.`

Compatibility note:

- this is an explicit DMS contract choice; legacy ODS commonly treats non-true values as snapshot-off rather than as malformed, but DMS-843 intentionally validates `Use-Snapshot` as a strict boolean

`404 Not Found` for requested snapshot mode when no usable snapshot source is available:

- `type`: `urn:ed-fi:api:change-queries:snapshot:not-found`
- `title`: `Snapshot Not Found`
- `status`: `404`
- `detail`: `The requested snapshot-backed change-query view is not available for this DMS instance. Retry without 'Use-Snapshot' or after the snapshot is restored.`
- `validationErrors`: `{}`
- `errors`: exactly one entry, `The requested snapshot-backed read source is not available.`

Compatibility note:

- legacy ODS snapshot-missing flows surface `404 Not Found`; DMS-843 keeps that status while still defining an explicit DMS problem-details body for this case

`400 Bad Request` for invalid `minChangeVersion`:

- `type`: `urn:ed-fi:api:change-queries:validation:min-change-version`
- `title`: `Invalid Change Query Request`
- `status`: `400`
- `detail`: `The change query parameters are invalid.`
- `validationErrors`: `{}`
- `errors`: exactly one entry, `The 'minChangeVersion' parameter must be a non-negative 64-bit integer.`

`400 Bad Request` for invalid `maxChangeVersion`:

- `type`: `urn:ed-fi:api:change-queries:validation:max-change-version`
- `title`: `Invalid Change Query Request`
- `status`: `400`
- `detail`: `The change query parameters are invalid.`
- `validationErrors`: `{}`
- `errors`: exactly one entry, `The 'maxChangeVersion' parameter must be a non-negative 64-bit integer.`

`400 Bad Request` for invalid window relationship:

- `type`: `urn:ed-fi:api:change-queries:validation:window`
- `title`: `Invalid Change Query Request`
- `status`: `400`
- `detail`: `The change query parameters are invalid.`
- `validationErrors`: `{}`
- `errors`: exactly one entry, `The 'maxChangeVersion' parameter must be greater than or equal to 'minChangeVersion'.`

`409 Conflict` for replay-floor miss in a later retention phase:

- `type`: `urn:ed-fi:api:change-queries:sync:window-unavailable`
- `title`: `Change Query Window No Longer Available`
- `status`: `409`
- `detail`: `The requested change query window is older than the oldest available change version for complete synchronization. Reinitialize or restart from the advertised replay floor.`
- `validationErrors`: `{}`
- `errors`: exactly one entry, `The supplied 'minChangeVersion' is older than the current replay floor.`
- extension members `requestedMinChangeVersion`, `oldestChangeVersion`, and `newestChangeVersion` are required

`500 Internal Server Error` for tracked-change authorization contract failure during write-side capture:

- `type`: `urn:ed-fi:api:system-configuration:security`
- `title`: `Security Configuration Error`
- `status`: `500`
- `detail`: `The tracked-change authorization contract for the routed resource is invalid or incomplete for this deployment.`
- `validationErrors`: `{}`
- `errors`: exactly one entry, `The request could not capture the tracked-change authorization data required for this resource.`

Write-side failure semantics:

- this applies when a delete or identity-changing update cannot resolve the required tracked-change authorization inputs to the declared `AuthorizationBasis` contract for the routed resource
- no tombstone or key-change row is committed in this failure path
- DMS must not silently downgrade to weaker tracked-change authorization behavior

## Paging and Completeness by Mode

The feature still avoids snapshot history tables, but it now supports two consistency modes.

Without snapshots:

- offset paging can drift while a resource is being enumerated
- if an already-processed item is deleted, later items can shift upward and an item on an upcoming page can be missed completely
- the same missed-item condition can occur when a previously processed item is updated so it no longer matches a bounded `maxChangeVersion` filter
- bounded windows eliminate one class of overrun drift, but they do not eliminate paging drift caused by concurrent deletes or updates
- DMS therefore does not claim that `offset` plus `limit` yields a complete synchronization result under concurrent writes
- the normative non-snapshot synchronization algorithm uses `maxChangeVersion = synchronizationVersion`
- there is no separate normative open-ended `minChangeVersion`-only non-snapshot pass contract in DMS-843; open-ended windows remain supported request shapes, but they are non-normative for synchronization
- non-snapshot processing remains a best-effort current-state enumeration mode, and clients must still tolerate duplicates or gaps caused by concurrent paging drift and apply compensating controls such as overlap or periodic reinitialization when stronger assurance is required

With snapshots:

- the page membership and ordering are frozen for the snapshot-backed pass
- offset paging and bounded windows using `maxChangeVersion = synchronizationVersion` are correctness-safe for that pass because the later live writes that cause drift are not visible
- the returned representations, tombstones, and key-change rows are the ones current inside the snapshot, not the live-primary results that may exist after the snapshot point
- the pass completeness guarantee is limited to the chosen snapshot source; if that source lags the primary, later primary commits are intentionally outside the pass and are picked up only after a later snapshot refresh or a later live read

## Contract Invariants

The public API contract must preserve these invariants:

- existing collection GET callers are unaffected when no change-query parameters or `Use-Snapshot` header are supplied
- changed-resource mode activates on collection GET when either `minChangeVersion` or `maxChangeVersion` is supplied
- `/deletes` and `/keyChanges` support independently optional lower and upper bounds, including max-only windows
- change-query requests fail explicitly when the feature is disabled; DMS never silently downgrades them to ordinary non-change-query requests
- `Use-Snapshot` is optional; when absent or `false`, the API preserves the current live best-effort behavior
- the only normative non-snapshot synchronization algorithm is the bounded-window form that captures `synchronizationVersion` from `availableChangeVersions` and uses it as `maxChangeVersion`; open-ended lower-bound-only windows remain supported but non-normative
- when `Use-Snapshot = true`, `availableChangeVersions` and the subsequent synchronization reads are all resolved against the configured snapshot source rather than against the live primary
- DMS never silently downgrades a `Use-Snapshot = true` request to a live read
- snapshot-backed synchronization resolves the currently active configured derivative binding per resolved instance on each request; if no usable binding exists, `Use-Snapshot = true` requests fail explicitly, and multi-request pass stability depends on operations keeping the binding unchanged because DMS-843 exposes no client-visible binding token
- changed-resource results are deterministically ordered by `ChangeVersion` plus a stable backend-local document tie-breaker (`DocumentPartitionKey`, `DocumentId` on the current backend)
- delete results are deterministically ordered by `ChangeVersion` plus a stable backend-local document tie-breaker (`DocumentPartitionKey`, `DocumentId` on the current backend)
- key-change results are deterministically ordered by `ChangeVersion` plus a stable backend-local document tie-breaker (`DocumentPartitionKey`, `DocumentId` on the current backend)
- changed-resource paging semantics are based on the final authorized result set rather than on raw internal candidates
- delete visibility is supported independently from the live row
- delete suppression is based on whether the same natural-key identity is currently visible on the selected source when `/deletes` is evaluated, not on whether the live row falls inside the requested delete window
- key-change visibility is supported independently from the current live key state
- key-change visibility is one public row per affected resource item in the requested window; when multiple retained authorized key-change rows exist for that resource item in one window, the API collapses them into one row with initial `oldKeyValues`, final `newKeyValues`, and the final surviving tracked key-change token
- `/deletes` and `/keyChanges` return the canonical public resource `id` sourced from `DocumentUuid`
- key payload field names and ordering are deterministic for the routed resource
- `/keyChanges` remains a valid route for resources that do not support identity updates and returns an empty array for them
- profiled changed-resource eligibility is resource-level, with profiles affecting only the returned representation
- multi-resource synchronization uses dependency order for `keyChanges` and changed resources, and reverse-dependency order for deletes
- the feature continues to avoid snapshot history tables even when `Use-Snapshot` is enabled
