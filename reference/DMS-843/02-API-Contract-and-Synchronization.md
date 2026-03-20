# API Contract and Synchronization

## Public Endpoints

## 1. Available change versions

Route:

```text
GET /{routePrefix}changeQueries/v1/availableChangeVersions
```

Example response:

```json
{
  "oldestChangeVersion": 101,
  "newestChangeVersion": 24822
}
```

Semantics:

- instance-scoped to the resolved DMS instance identified by route prefix and instance-resolution rules
- authenticated like other DMS endpoints
- not filtered by per-resource authorization rules
- provides the watermarks used to define a bounded synchronization window

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
- delete rows are ordered deterministically
- delete rows preserve the natural-key values needed by downstream synchronization clients
- delete rows are authorization-filtered

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
- results are returned only for resources that support identity updates
- key-change rows are ordered deterministically
- if multiple key changes occur for the same resource inside one window, the API returns one collapsed row with the earliest `oldKeyValues`, the latest `newKeyValues`, and the latest `changeVersion`
- key-change rows are authorization-filtered

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

- required for changed-resource mode
- required for `/deletes`
- required for `/keyChanges`
- must be a positive signed 64-bit integer

## `maxChangeVersion`

- optional
- when supplied, must be a positive signed 64-bit integer
- must be strictly greater than `minChangeVersion`

## `limit`, `offset`, `totalCount`

- retain the existing validation rules already enforced by DMS
- remain valid in changed-resource mode and on `/deletes`
- remain valid on `/keyChanges`

ODS-compatibility note:

- the design must not introduce a new `400 Bad Request` contract for invalid or inconsistent change-query windows on these routes
- the HTTP behavior for those cases must remain ODS-compatible `200 OK`
- exact response-body parity for those cases must be verified during implementation testing

## Window Semantics

The feature uses the Ed-Fi bounded-window rule:

```text
minChangeVersion < ChangeVersion <= maxChangeVersion
```

This rule applies equally to:

- changed-resource queries
- delete queries
- key-change queries

`ChangeVersion` values are globally ordered and monotonic, but not guaranteed to be gap-free.

## Synchronization Algorithm

The intended client synchronization flow is:

1. Call `availableChangeVersions`.
2. Capture `newestChangeVersion` as the upper bound for the synchronization pass.
3. Query changed resources using the client watermark as `minChangeVersion` and the captured `newestChangeVersion` as `maxChangeVersion`.
4. Query deletes using the same bounds.
5. Query key changes using the same bounds for resources that support identity updates.
6. Apply live-resource changes, deletes, and key changes locally.
7. Persist the new watermark only after the full bounded window succeeds.

This algorithm is compatible with client crash recovery because the same bounded window can be replayed safely.

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

## ODS-Compatible Parameter Behavior

For this feature, the package must follow observed ODS-compatible status behavior rather than introducing new request-failure semantics.

Required status behavior:

- malformed `minChangeVersion` requests still return `200 OK`
- malformed `maxChangeVersion` requests still return `200 OK`
- `maxChangeVersion <= minChangeVersion` requests still return `200 OK`
- other out-of-contract change-query window combinations on these routes should remain `200 OK` unless ODS parity testing proves otherwise
- when retention is enforced later, requests older than the retained window should also remain `200 OK` rather than adding a new ProblemDetails contract in this design package

Implications:

- the parameter rules above remain semantic requirements for correct Change Query usage
- status-code behavior must nevertheless remain ODS-compatible
- exact response-body behavior for invalid or unusable windows must be captured from ODS and matched in parity tests during implementation

## Snapshot-Free Paging Behavior

The feature intentionally avoids snapshot tables.

Accepted consequence:

- if a row later receives a new `ChangeVersion` that moves it outside the captured window, that row may disappear from a later offset page within the older window

Why this is acceptable:

- the client synchronizes using a bounded window and advances its watermark only after completing the window
- the row reappears in a later window using its newer `ChangeVersion`
- the model matches current-state Ed-Fi semantics rather than historical snapshot semantics

This behavior must be documented in implementation notes and tested explicitly.

## Contract Invariants

The public API contract must preserve these invariants:

- existing collection GET callers are unaffected when no change-query parameters are supplied
- changed-resource results are deterministically ordered
- delete results are deterministically ordered
- key-change results are deterministically ordered
- delete visibility is supported independently from the live row
- key-change visibility is supported independently from the current live key state
- key payload field names and ordering are deterministic for the routed resource
- the same bounded window can be replayed without corruption of client state
