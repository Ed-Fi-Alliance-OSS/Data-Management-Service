# API Contract and Synchronization

## Feature Availability

The Change Queries API surface is controlled by `AppSettings.EnableChangeQueries`.

Required contract:

- DMS follows the Ed-Fi ODS/API default-on posture for this feature, so if the flag is absent DMS treats it as `true`
- when the flag is off, the dedicated Change Query routes are not exposed and requests to them return `404 Not Found`
- feature-off handling must reserve the `/deletes`, `/keyChanges`, and `availableChangeVersions` paths so those requests cannot fall through to the generic collection or item-by-id routes
- the feature-off `404 Not Found` behavior for those dedicated routes intentionally treats the endpoints as not exposed; DMS-843 does not define a bespoke Change-Query-specific response-body contract for that hidden-route case
- when the flag is off, collection GET requests that include `minChangeVersion` or `maxChangeVersion` return `400 Bad Request` with the feature-disabled problem details defined below
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

- instance-scoped to the resolved DMS instance identified by route prefix and instance-resolution rules
- authenticated like other DMS endpoints
- not filtered by per-resource authorization rules
- readable profile headers do not apply to this endpoint; any profile media type is ignored and the response remains ordinary `application/json`
- aligned to the Ed-Fi ODS/API single synchronization surface used across `keyChanges`, changed-resource queries, and `deletes`; see the [platform guide](https://docs.ed-fi.org/reference/ods-api/platform-dev-guide/features/changed-record-queries/) and [client guide](https://docs.ed-fi.org/reference/ods-api/client-developers-guide/using-the-changed-record-queries/)
- provides the watermarks used to define a bounded synchronization window
- for the initial DMS-843 scope, no purge-driven replay-floor advancement is assumed, so `oldestChangeVersion` is `0`
- `newestChangeVersion` is the current synchronization ceiling for that same surface
- if retained participating rows exist, `newestChangeVersion` is the greatest retained `ChangeVersion` across them
- when the synchronization surface is empty and no replay-floor metadata exists, the endpoint returns `0` for both values; in that bootstrap case, `0` is a starting watermark sentinel rather than a retained change row
- if a later retention phase introduces replay-floor metadata, `oldestChangeVersion` becomes the lowest valid inclusive `minChangeVersion` that can still return complete results, and `newestChangeVersion = oldestChangeVersion =` the greatest participating replay floor when all participating sources are empty after purge

## 2. Changed-resource collection query

Route:

```text
GET /{routePrefix}data/{projectNamespace}/{endpointName}?minChangeVersion={min}&maxChangeVersion={max}&limit={limit}&offset={offset}&totalCount={totalCount}
```

Examples:

```text
GET /data/ed-fi/students?minChangeVersion=100&maxChangeVersion=250
GET /tenant-a/data/ed-fi/students?minChangeVersion=100&maxChangeVersion=250&limit=25&offset=0
GET /district-100/2026/data/ed-fi/students?minChangeVersion=100
```

Semantics:

- when `minChangeVersion` is absent, the route behaves exactly like the current collection GET route
- when `minChangeVersion` is present, the route switches into changed-resource mode
- the payload shape remains the normal resource array returned by the current collection GET route
- the API returns the latest current representation of each qualifying resource once
- the API does not return every intermediate mutation
- `limit`, `offset`, and `totalCount` in changed-resource mode apply to the final authorized one-row-per-resource result set; internal journal candidates eliminated by verification or authorization do not consume page space or count toward `totalCount`
- the final changed-resource result set is ordered by `ChangeVersion`, `DocumentPartitionKey`, and `DocumentId`
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
- `id` is the canonical public resource identifier for the deleted item and is sourced from the stored `DocumentUuid`
- delete rows are ordered deterministically
- delete rows preserve the natural-key values needed by downstream synchronization clients
- delete rows are authorization-filtered
- readable profile headers do not apply; the endpoint must ignore profile media types and must not alter the payload shape or media type based on profile rules

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
- the route is valid for all change-query-enabled resources
- resources that support identity updates return key-change rows when qualifying transitions exist
- resources that do not support identity updates return `200 OK` with an empty array and `totalCount = 0` when `totalCount` is requested
- `id` is the canonical public resource identifier for the affected item and is sourced from the stored `DocumentUuid`
- the row `changeVersion` is the public live representation-change stamp from the same committed identity-changing update that produced the key transition; it is not the internal `IdentityVersion`
- key-change rows are ordered deterministically
- key-change-query authorization is evaluated before collapse against each tracking row's stored pre-update authorization projection
- if multiple key changes occur for the same resource inside one window, the API returns one collapsed row with the earliest `oldKeyValues`, the latest `newKeyValues`, and the latest `changeVersion` from the rows that remain after authorization filtering
- `limit`, `offset`, and `totalCount` on `/keyChanges` apply to that collapsed authorized result set rather than to raw tracking rows
- key-change rows are authorization-filtered
- readable profile headers do not apply; the endpoint must ignore profile media types and must not alter the payload shape or media type based on profile rules

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

## Query Parameter Rules

## `minChangeVersion`

- required to activate changed-resource mode on the collection GET route
- required for `/deletes`
- required for `/keyChanges`
- when absent and `maxChangeVersion` is also absent, the collection GET route remains an ordinary non-Change-Query request
- must be a non-negative signed 64-bit integer

## `maxChangeVersion`

- optional
- valid only when `minChangeVersion` is also supplied
- when supplied, must be a non-negative signed 64-bit integer
- must be greater than or equal to `minChangeVersion`

## `limit`, `offset`, `totalCount`

- retain the existing validation rules already enforced by DMS
- remain valid in changed-resource mode and on `/deletes`
- remain valid on `/keyChanges`
- in changed-resource mode, all three apply to the final authorized one-row-per-resource result set after any internal candidate verification or elimination required by the execution strategy
- on `/keyChanges`, all three apply after authorization filtering and collapse, over the final one-row-per-resource result set

## Window Semantics

The feature uses the Ed-Fi lower-bound-inclusive rule:

```text
minChangeVersion <= ChangeVersion <= maxChangeVersion
```

This rule applies equally to:

- changed-resource queries
- delete queries
- key-change queries

When `maxChangeVersion` is omitted, the effective rule is:

```text
minChangeVersion <= ChangeVersion
```

`ChangeVersion` values are globally ordered and monotonic, but not guaranteed to be gap-free.

## Synchronization Algorithm

DMS-843 does not define a client-selectable snapshot or other consistent-read request mode. All requests execute against current committed state.

The normative DMS-843 client synchronization flow is:

1. Identify the resources to be synchronized and their dependency order using Ed-Fi model dependencies, resource dependency metadata, or an equivalent dependency model.
2. For initial synchronization, call `availableChangeVersions`, capture `newestChangeVersion` as the initial synchronization version, load data with ordinary collection GETs in dependency order, and persist the captured synchronization version only after the initial load succeeds.
3. For repeating change processing, let `lastSuccessfulChangeVersion` be the saved synchronization version from the previous successful pass.
4. Compute `startChangeVersion = lastSuccessfulChangeVersion + 1`.
5. Call `availableChangeVersions` and capture its `newestChangeVersion` as `synchronizationVersion`.
6. Query `keyChanges` for applicable resources in dependency order using `minChangeVersion = startChangeVersion` and omitting `maxChangeVersion`.
7. Query changed resources in dependency order using the same `minChangeVersion` and omitting `maxChangeVersion`.
8. Query `/deletes` in reverse-dependency order using the same `minChangeVersion` and omitting `maxChangeVersion`.
9. Apply key changes first, then live-resource changes, then deletes locally, preserving the same dependency order rules.
10. Persist `synchronizationVersion` only after the full pass succeeds.

Implications of this algorithm:

- because DMS-843 v1 has no client-visible snapshot mode, both initial synchronization and repeating change processing are subject to ordinary current-state read timing under concurrent writes
- rows with `ChangeVersion > synchronizationVersion` may appear during the pass
- those rows may legitimately be returned again on the next pass because the saved watermark is `synchronizationVersion`, not the highest `ChangeVersion` observed during retrieval
- callers must treat such repeated rows as expected duplicate delivery rather than as a server bug

Bounded windows using `maxChangeVersion` remain supported by the public API for caller-managed partitioning, diagnostics, or specialized workflows, but they are not the normative synchronization algorithm in DMS-843 v1.

The ordering and algorithm above follow the Ed-Fi ODS/API client guidance for change-query synchronization without snapshots.

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

## Delete followed by reinsert

Expected behavior:

- `/deletes` returns the delete event for the old row
- the changed-resource query may return the later live row in a newer window
- clients must treat this as delete plus later create

## Multiple key changes before synchronization

Expected behavior:

- `/keyChanges` returns one collapsed row for the resource in the requested window
- `oldKeyValues` come from the earliest key state in the window
- `newKeyValues` come from the latest key state in the window
- `changeVersion` is the latest key-change `ChangeVersion` in the window

## Key change followed by delete before synchronization

Expected behavior:

- `/keyChanges` returns the key transition if the key-change `ChangeVersion` falls in the window
- `/deletes` returns the later delete tombstone if the delete `ChangeVersion` falls in the window
- both rows may legitimately appear because they answer different synchronization questions and are not duplicates of one another

## Explicit DMS Error Contract for Invalid or Unusable Windows

The public Ed-Fi documentation describes the change-query workflow and parameters, but it does not publish a normative response-body contract for malformed windows or retention-floor misses. DMS therefore defines those edge cases explicitly rather than leaving them ambiguous.

Required status behavior:

- when Change Queries is disabled and a collection GET request supplies `minChangeVersion` or `maxChangeVersion`, return `400 Bad Request`
- when Change Queries is disabled and `/deletes`, `/keyChanges`, or `availableChangeVersions` is requested, return `404 Not Found` through the reserved dedicated route rather than by falling through to generic route parsing
- when `/deletes` or `/keyChanges` is called without `minChangeVersion`, return `400 Bad Request`
- when `maxChangeVersion` is supplied without `minChangeVersion`, return `400 Bad Request`
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

`400 Bad Request` for change-query parameters on the collection GET route when the feature is explicitly disabled:

- `type`: `urn:ed-fi:api:change-queries:feature-disabled`
- `title`: `Change Queries Feature Disabled`
- `status`: `400`
- `detail`: `Change Queries is not enabled for this DMS deployment.`
- `validationErrors`: `{}`
- `errors`: exactly one entry, `Change query parameters cannot be used because the Change Queries feature is disabled.`

`400 Bad Request` for missing required `minChangeVersion`:

- `type`: `urn:ed-fi:api:change-queries:validation:min-change-version-required`
- `title`: `Invalid Change Query Request`
- `status`: `400`
- `detail`: `The change query parameters are invalid.`
- `validationErrors`: `{}`
- `errors`: exactly one entry, `The 'minChangeVersion' parameter is required for this Change Query request.`

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

`400 Bad Request` for `maxChangeVersion` without `minChangeVersion`:

- `type`: `urn:ed-fi:api:change-queries:validation:window`
- `title`: `Invalid Change Query Request`
- `status`: `400`
- `detail`: `The change query parameters are invalid.`
- `validationErrors`: `{}`
- `errors`: exactly one entry, `The 'maxChangeVersion' parameter cannot be supplied without 'minChangeVersion'.`

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

## Non-Snapshot Paging Limitations

The feature intentionally avoids snapshot history tables and does not expose a client-selectable snapshot mode, so offset-paged synchronization is not correctness-safe under concurrent writes.

Without snapshots:

- offset paging can drift while a resource is being enumerated
- if an already-processed item is deleted, later items can shift upward and an item on an upcoming page can be missed completely
- the same missed-item condition can occur when a previously processed item is updated so it no longer matches a bounded `maxChangeVersion` filter
- open-ended `minChangeVersion` processing reduces one class of skip risk, but it does not eliminate paging drift caused by concurrent deletes or updates

Therefore:

- DMS does not claim that `offset` plus `limit` yields a complete synchronization result under concurrent writes
- the normative synchronization algorithm omits `maxChangeVersion` and persists the pass-start `synchronizationVersion`
- non-snapshot processing is supported as a best-effort current-state enumeration mode, and clients must tolerate duplicates or gaps and apply compensating controls such as overlap or periodic reinitialization when stronger assurance is required

## Contract Invariants

The public API contract must preserve these invariants:

- existing collection GET callers are unaffected when no change-query parameters are supplied
- change-query requests fail explicitly when the feature is disabled; DMS never silently downgrades them to ordinary non-change-query requests
- changed-resource results are deterministically ordered by `ChangeVersion`, `DocumentPartitionKey`, and `DocumentId`
- delete results are deterministically ordered by `ChangeVersion`, `DocumentPartitionKey`, and `DocumentId`
- key-change results are deterministically ordered by `ChangeVersion`, `DocumentPartitionKey`, and `DocumentId`
- changed-resource paging semantics are based on the final authorized result set rather than on raw internal candidates
- delete visibility is supported independently from the live row
- key-change visibility is supported independently from the current live key state
- `/deletes` and `/keyChanges` return the canonical public resource `id` sourced from `DocumentUuid`
- key payload field names and ordering are deterministic for the routed resource
- `/keyChanges` remains a valid route for resources that do not support identity updates and returns an empty array for them
- profiled changed-resource eligibility is resource-level, with profiles affecting only the returned representation
- multi-resource synchronization uses dependency order for `keyChanges` and changed resources, and reverse-dependency order for deletes
- DMS-843 v1 does not expose a client-selectable snapshot or consistent-read mode
