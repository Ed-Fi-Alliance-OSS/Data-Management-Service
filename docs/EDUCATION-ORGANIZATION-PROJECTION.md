# Education organization projection

The Ed-Fi API serves a service-only endpoint that lists the core education
organizations in one data store, with each one's parent. The DMS Configuration
Service reads it over HTTP to learn which education organizations a data store
holds, without ever connecting to that data store's database.

This page currently documents the service contract. Deployment, provisioning,
Configuration Service settings and measured cost are added as the feature is
implemented (DMS-1440).

The machine-readable contract is
[education-organization-projection.v1.openapi.yaml](../reference/design/edorg-projection-DMS-1440/education-organization-projection.v1.openapi.yaml),
with response examples under
[contract/examples](../reference/design/edorg-projection-DMS-1440/contract/examples/).
Where this page and the OpenAPI document differ, treat it as a defect in one of
them.

## What the projection contains

Exactly four core types, identified by these `discriminator` values:

- `edfi.StateEducationAgency`
- `edfi.EducationServiceCenter`
- `edfi.LocalEducationAgency`
- `edfi.School`

Extension-defined types and the other core education organization types are not
included. Each item has the same five members, types and nullability as the
Admin API `educationOrganizationModel`.

## Finding the endpoint

Clients do not compose the URL. They read it from the Discovery document, served
at `GET {base}/` in single-tenant mode and `GET {base}/{tenant}` in multi-tenant
mode. When the deployment enables the endpoint
(`AppSettings:EnableEducationOrganizationProjection`, default `true`), Discovery
carries:

```json
{
  "urls": {
    "oauth": "http://localhost:8080/Tenant_255901/{districtId}/{schoolYear}/oauth/token",
    "educationOrganizationProjection": "http://localhost:8080/Tenant_255901/{districtId}/{schoolYear}/management/education-organizations"
  },
  "educationOrganizationProjection": {
    "contractVersions": ["educationOrganizationProjection.v1"]
  }
}
```

Route-qualifier segments the Discovery request did not supply appear as
`{name}` placeholders. A client replaces each with the data store's context
value of the same name, and must not send a request while any placeholder is
unresolved. Both URLs include any path base and carry no query string.

`urls.oauth` is the existing Discovery member and is not affected by this
feature. When the endpoint is disabled, Discovery omits exactly
`urls.educationOrganizationProjection` and the top-level
`educationOrganizationProjection` object, keeps every other member including
`urls.oauth`, and the route is not mapped.

The route forms are:

| Mode | Path |
| --- | --- |
| Single-tenant | `/management/education-organizations` |
| Multi-tenant | `/{tenant}/management/education-organizations` |
| Multi-tenant with route qualifiers | `/{tenant}/{districtId}/{schoolYear}/management/education-organizations` |

The legacy `/management/{tenant}/...` form is not used, and the prefix-less path
is not served in multi-tenant mode.

## Authorization

Requests carry a bearer token obtained with the client-credentials grant at
`urls.oauth`: HTTP Basic `client_id:client_secret` and the form body
`grant_type=client_credentials`.

The client's claim set must grant `Read` on the service claim
`http://ed-fi.org/identity/claims/services/educationOrganizationProjection`,
with exactly the `NoFurtherAuthorizationRequired` authorization strategy. No
shipped claim set grants this claim. The credential is an application API
client with no data store assignments. In multi-tenant mode the client must
belong to the tenant in the route, so each tenant needs its own credential.

Authorization is checked before any query parameter is parsed or any data store
is looked up.

## Request

`GET {endpoint}?dataStoreId=&limit=&cursor=&contractVersion=`

| Parameter | Required | Rules | Rejected with |
| --- | --- | --- | --- |
| `dataStoreId` | yes | The CMS data store id; integer from 1 to 2147483647 | `400 parameter-validation-failed` |
| `limit` | no | Integer from 1 to `MaximumPageSize` (default 2000, configurable up to 10000); defaults to `MaximumPageSize` | `400 parameter-validation-failed` |
| `cursor` | no | The previous page's `nextCursor`, verbatim | `400 invalid-cursor` |
| `contractVersion` | no | A version listed in Discovery; defaults to `educationOrganizationProjection.v1` | `400 unsupported-contract-version` |

Supplying any of the four parameters more than once, in any letter case, is
rejected with `400 parameter-validation-failed` naming the parameter. Unknown
parameters are ignored.

## Response

```json
{
  "contractVersion": "educationOrganizationProjection.v1",
  "dataStoreId": 3788,
  "nextCursor": "MSwzNzg4LDEwLEdheEJQTk5KeEhyQm1fU0g1eGRfTDl0M2FMb3ZORi1NUXBDN19jX1l2X28sMTc5MDk1MzIwMCw0NGNlM2RiYTc0ZWM4ZjhiYTE0ZGFjN2U5NDNmYWRmNw",
  "items": [
    {
      "educationOrganizationId": 1,
      "nameOfInstitution": "Example State Department of Education",
      "shortNameOfInstitution": "ESDE",
      "discriminator": "edfi.StateEducationAgency",
      "parentId": null
    },
    {
      "educationOrganizationId": 10,
      "nameOfInstitution": "Region 10 Education Service Center",
      "shortNameOfInstitution": "ESC 10",
      "discriminator": "edfi.EducationServiceCenter",
      "parentId": 1
    }
  ]
}
```

| Member | Type | Notes |
| --- | --- | --- |
| `contractVersion` | string | Echo of the version the page conforms to |
| `dataStoreId` | int32 | Echo of the request |
| `nextCursor` | string or `null` | Always present; `null` when this page reaches the end of the set |
| `items[].educationOrganizationId` | int64 | Unique across the set |
| `items[].nameOfInstitution` | string | Never empty |
| `items[].shortNameOfInstitution` | string or `null` | Always present; may be the empty string |
| `items[].discriminator` | string | One of the four values above |
| `items[].parentId` | int64 or `null` | Always present; see [parent selection](#validation-and-parent-selection) |

Neither the envelope nor an item has any other member. Every response carries
`Cache-Control: no-store`.

An item serializes to at most 2,048 bytes and the envelope to at most 512, so a
page is at most `limit` × 2,048 + 512 bytes: about 3.9 MiB at the default
`MaximumPageSize` of 2,000.

## Reading the whole set

A read starts without `cursor` and follows `nextCursor` until it is `null`,
repeating `dataStoreId`, `limit` and `contractVersion` unchanged on every
request.

- Items are strictly ascending by `educationOrganizationId`, in numeric order;
  ids are signed int64 values, and zero and negative ids are included.
- When `nextCursor` is not `null`, the page has exactly `limit` items.
- A page has no items only when the whole set is empty. That page is the first
  page and has `nextCursor: null`. No continuation page is ever empty.
- Replaying an unexpired cursor with unchanged request parameters returns the
  same projected items while the projected content remains unchanged and the
  request remains authorized. After a committed change to the projected content
  the replay is answered `409 projection-changed`; after an authorization change
  it is answered with the corresponding 401, 403 or 404.

For example, a read of the seven education organizations in the
[worked example](#worked-example) with `limit=2` takes four requests:

| Request | `cursor` | Items | `nextCursor` |
| --- | --- | --- | --- |
| 1 | omitted | 1, 10 | after 10 |
| 2 | after 10 | 100, 101 | after 101 |
| 3 | after 101 | 100001, 101001 | after 101001 |
| 4 | after 101001 | 900001 | `null` |

## How DMS keeps a read consistent

The pages of one read all come from one projected content set: no item is
duplicated or skipped, even when the data store is written to during the read.

Every request reads the complete set of the four types inside one database
transaction (PostgreSQL `REPEATABLE READ`, SQL Server `SERIALIZABLE`), validates
it, and computes a SHA-256 digest of its projected content. The first page
records that digest in its cursor. A later page is answered only when the set it
read has the same digest, and then returns the items after the cursor's
position. When the set has changed, DMS answers `409 projection-changed` and the
client restarts the read without a cursor.

A change that leaves every projected member identical, such as an update to a
member the projection does not return, does not change the digest and does not
interrupt a read.

### Effect on writers and measured cost

Because every page reads the whole set, a complete read of `N` items in pages
of `limit` reads the set `ceil(N / limit)` times. At the default cap (50,000
items) and default page size (2,000), that is 25 reads.

- **PostgreSQL.** `REPEATABLE READ` reads a snapshot, so ordinary row writes
  (`INSERT`, `UPDATE`, `DELETE`) neither block the read nor are blocked by it.
  A conflicting table-level lock, such as one taken by DDL or `LOCK TABLE`,
  does block the read, up to the read's lock timeout.
- **SQL Server.** `SERIALIZABLE` holds shared and range locks on the rows of
  the four education organization tables until the read's transaction ends.
  A writer to one of those tables can wait for the read in progress. When other
  readers or writers are queued too, waits form blocking chains, so no general
  bound on a writer's wait follows from the duration of one read. A writer
  transaction that holds a row lock the read needs and then needs one the read
  holds deadlocks with it, and SQL Server aborts one of the two. When the read is
  aborted, DMS answers `503 target-unavailable` for the client to retry. DMS
  provisioning enables read committed snapshot and snapshot isolation on the
  databases it creates, but the read uses `SERIALIZABLE`, which takes these
  locks either way.
  After the read, DMS returns the session to `READ COMMITTED`. A connection
  whose cleanup cannot be confirmed is not reused.

  The following were observed in the measurements below, and the plan or the
  victim choice could differ elsewhere. The read took the four tables in the
  order State Education Agency, Education Service Center, Local Education
  Agency, then School. A transaction that updated a School and then a Local
  Education Agency deadlocked with it, and SQL Server chose the read, which had
  written nothing, as the victim every time.

A provider read that succeeds is not the same as a complete read of the set. A
complete read also needs every page's digest to match the first page's, so any
committed change to projected content between pages restarts it with `409
projection-changed`. Reducing lock conflicts, for example with snapshot
isolation per page, would not prevent those restarts.

Provider measurements at the cap (step 2.5) follow. Read times cover the
provider reader alone, without validation, hashing or serialization. Each writer
phase runs four concurrent writers for 2,000 operations, first alone and then
while one client reads the set back-to-back, which is how a full walk reads.
Every number counts provider reads only: a full walk can still restart, as
described above.

**With production writes.** The hierarchy is created through the API, and the
writers change it through the API with `PUT`. They rename the state agency,
service centers, local agencies and schools, and they change relationships:
moving a School to another Local Education Agency and changing a Local Education
Agency's parent. SQL Server runs with the isolation settings DMS provisioning
enables on new databases (read committed snapshot and snapshot isolation).

| | PostgreSQL 16 | SQL Server 2025 |
| --- | --- | --- |
| Read time, median (min-max of 10) | 125 ms (76-187) | 148 ms (113-272) |
| Writes alone: p50 / p95 / max | 13 / 56 / 104 ms | 12 / 140 / 484 ms |
| Writes during reads: p50 / p95 / max | 21 / 51 / 94 ms | 19 / 213 / 466 ms |
| Writes during reads, p95 by kind | 47-56 ms, every kind | School renames and moves 37-46 ms; state agency, service center and local agency writes 208-227 ms |
| Lock waits during reads | writers waiting in 9 of 378 samples | 828 waits, 75.2 s total; writers waiting in 766 of 919 samples, the read in 20 |
| Write failures; deadlocks | none; 0 | none; 0 |
| Reads that succeeded | 93 of 93 | 167 of 167 |

**Stress case, without production writes.** Direct SQL writers on a database
without read committed snapshot. The writers include a transaction that updates a
School and then a Local Education Agency, the reverse of the order the read took
the tables in.

| | PostgreSQL 16 | SQL Server 2025 |
| --- | --- | --- |
| Bytes received per read | 5.25 MB (105 bytes per row) | 6.24 MB (125 bytes per row) |
| Single-row writers during reads: p95; deadlocks; reads that succeeded | not run separately | 101 ms; 0; 123 of 123 |
| With the reverse-order transaction, during reads: p95; deadlocks; reads that succeeded | 6.3 ms; 0; 13 of 13 | 98 ms; 65; **10 of 75** |

Every one of the 65 SQL Server deadlocks had the same shape. The read held
key-range locks on Local Education Agency rows and waited for a School row,
while the writer held that School row and waited for a Local Education Agency
row. The read was the victim each time. The production write pipeline did not
reproduce this shape.

Notes:

- **Lock waits** on SQL Server are the server's `LCK_*` totals over the phase,
  for readers and writers together. On both engines, waiting sessions are also
  sampled every 20 ms and split into writers and the read by statement text.
- **Test conditions:** one workstation; local containers with their data on
  tmpfs; 1 state education agency, 10 service centers, about 1,000 local
  education agencies and about 49,000 schools; names of about 30 characters.
- **Limits:** these are indications, not guarantees. Bytes scale with name
  length. Handler cost (validation, digest and response) is measured
  separately, below.

### Handler cost and complete reads

Every page also validates and hashes the whole set before it returns its
items. Measured at the cap (50,000 items) and the default page size (2,000),
so a complete read is 25 pages.

**Handler alone** (step 2.6), with the set already read, so no database time.
Two runs, median per page:

| Per page | Median | 95th percentile |
| --- | --- | --- |
| Handler time | 11-18 ms | 29-34 ms |
| Validation / parent selection / digest | 2.0-4.1 / 1.3 / 3.7-8.2 ms | 6.7 / 16 / 5.4-9.9 ms |
| Managed memory allocated | 6.5 MB | 6.5-6.6 MB |
| Of which: validation / parent selection / digest / response items | 1.6 / 3.4 / 0.0 / 1.5 MB | same |
| Response body (2,000 items) | 322 KB | 322 KB |

A 25-page read spends 0.35-0.56 s in the handler (median of five). The digest
formats its canonical bytes into one reusable buffer; before it did, it
allocated 23.4 MB a page and the handler 30 MB, about 750 MB over a complete
read.

**Complete reads.** These measurements run the request parsing and the handler
over the production provider reader against the databases described above, one
page after another, in the order a client reads. Runtime: .NET 10.0.12, Release
build, workstation garbage collection with concurrent collection on, 16
processors; the test process, which also hosts the API and the writers, peaked
at 0.9 GB, and at least 3.2 GB of physical memory stayed free.

| | PostgreSQL 16 | SQL Server 2025 |
| --- | --- | --- |
| Complete read, no writes (median of 3) | 3.0 s | 4.4 s |
| Provider reads per complete read | 2.3 s | 3.8 s |
| Non-read time per page, median (95th percentile) | 16 ms (70 ms) | 10 ms (72 ms) |
| One change committed after page 5 | page 6 refused; restarted read completes; 3.8 s in all | page 6 refused; restarted read completes; 5.5 s in all |

**Reads during writes.** A logical read here follows the Configuration
Service's rule: on `projection-changed` it restarts without a cursor, at most 3
times (4 attempts). Logical reads run back-to-back while the writers are
active; a read in progress when the writers stop runs to its end and is counted
separately.

| Writes to projected content | PostgreSQL 16 | SQL Server 2025 |
| --- | --- | --- |
| Four concurrent writers, 2,000 `PUT`s (writers active 10 s / 28 s, no write failures) | 0 of 9 completed while writers were active, all 9 used up their restarts; every attempt refused on page 2; 1 completed after the writers stopped | 0 of 17 completed while writers were active, all 17 used up their restarts; every attempt refused on page 2; 1 completed after the writers stopped |
| One rename every 10 s, for 60 s (no write failures) | 25 of 25 completed while writing, 1-2 attempts each; 1 more after | 12 of 12 completed while writing, 1-2 attempts each; 1 more after |
| One rename every 2 s, for 60 s (no write failures) | 5 of 11 completed while writing; 6 used up their restarts; 1 more after | 2 of 7 completed while writing; 5 used up their restarts; 1 more after |

A read completes only when no change to projected content commits while it is
in progress, so whether reads complete depends on how often projected content
changes, not on provider reads succeeding. Every provider read in these runs
succeeded. Changes less frequent than one complete read (seconds at the cap)
cost at most a restart or two. A steady stream of changes prevents a complete
read at the cap, and the read reports `projection-changed` once its restarts
are used up.

These measurements do not show that a read can complete while projected
content keeps changing. A read that
runs out of restarts fails as a whole. The Configuration Service discards the
incomplete attempt and keeps its previous snapshot.

### Canonical digest form

The digest is SHA-256 over the UTF-8 bytes (no byte order mark, LF line endings)
of:

```
edorg-projection-digest:v1
<rowCount>
<id>;<tag>;<S(nameOfInstitution)>;<S(shortNameOfInstitution)>;<P(parentId)>
```

with one row line per item in ascending id order, each line ending in LF.

- `<id>` is the signed int64 id in invariant-culture decimal: a leading `-` for
  negative values, no `+`, no unnecessary leading zeroes (zero is `0`).
  `<rowCount>` is a nonnegative decimal count.
- `<tag>` is `SEA`, `ESC`, `LEA` or `SCH`.
- `S(x)` is `-` when `x` is `null`; otherwise `<n>:<x>`, where `<n>` is the
  UTF-8 byte count of `x` in decimal (`0:` is the empty string). The length
  prefix keeps `;`, `:` and line breaks inside names unambiguous.
- `P(x)` is `-` when `x` is `null`; otherwise the parent id, encoded like
  `<id>`. A lone `-` is unambiguous because a negative id always has digits.

The digest covers the five projected members only. It is a server-side
consistency check, not part of what clients parse.

The empty set's canonical text is `edorg-projection-digest:v1`, LF, `0`, LF,
with digest
`59132bba34f2b858390ede6a95ca6c40dd16413b5a5a3661687bc46dde839c23`.

### Worked example

The example responses in the contract come from this set:

```text
edorg-projection-digest:v1
7
1;SEA;37:Example State Department of Education;4:ESDE;-
10;ESC;34:Region 10 Education Service Center;6:ESC 10;1
100;LEA;14:Grand Bend ISD;5:GBISD;10
101;LEA;20:Grand Bend North ISD;-;100
100001;SCH;22:Grand Bend High School;4:GBHS;100
101001;SCH;34:Grand Bend North Elementary School;-;101
900001;SCH;19:Independent Academy;-;-
```

Its SHA-256 is
`19ac413cd349c47ac19bf487e7177f2fdb7768ba2f345f8c4290bbfdcfd8bffa`, which is
`GaxBPNNJxHrBm_SH5xd_L9t3aLovNF-MQpC7_c_Yv_o` in unpadded base64url. LEA `100`
has both an education service center and a state education agency and takes
the education service center as its parent; LEA `101` takes its parent LEA;
school `900001` has no LEA.

## Cursor

`cursor` is opaque: clients never construct, parse or change it. Clients must
return `nextCursor` verbatim. The decoder accepts only canonical, unpadded
base64url with canonical payload fields; padded or otherwise noncanonical
representations receive `400 invalid-cursor`. A cursor is bound to the data store, tenant, route
qualifiers and contract version of the read that produced it, and expires
`CursorLifetimeMinutes` (default 60) after the read's first page, however many
pages follow. A cursor that does not decode, does not match the request, has
expired, or is dated more than 60 seconds in the future is rejected with
`400 invalid-cursor`, and the client may restart the read without a cursor.

DMS keeps no state for a read. For reference, format version `1` is the
unpadded base64url encoding of the comma-separated values
`1,<dataStoreId>,<lastEducationOrganizationId>,<digest>,<walkIssuedAtUnixSeconds>,<bindingHash>`,
where `<lastEducationOrganizationId>` is the signed id of the last item
returned, encoded like `<id>` in the digest, `<digest>` is the unpadded
base64url digest of the read's first page,
`<walkIssuedAtUnixSeconds>` is the first page's time, carried unchanged into
later cursors, and `<bindingHash>` is the first 32 lower-case hexadecimal
characters of SHA-256 over
`<tenant>|<contractVersion>|<key1>=<value1>;<key2>=<value2>...`. In that
binding text the tenant is lower-cased and is the empty string in
single-tenant mode, and the
route-qualifier pairs are lower-cased and sorted by key. The cursor is not
signed. Changing it can only move the position, or fail the digest check, within
a set the caller is already authorized to read in full.

DMS does not write cursors to its logs. Its request logs record the path without
the query string, and the framework's request-starting and request-finished
log events for this route record the query string as `?[redacted]`.

## Validation and parent selection

Every page validates the whole set before it compares the digest and before it
selects the page's items, so a problem anywhere in the set fails every page.
The set is rejected with `409 projection-data-invalid` when:

- two items have the same `educationOrganizationId`;
- any populated reference does not resolve to an education organization of the
  expected type, whether or not it would become the parent:

  | Reference | Must resolve to |
  | --- | --- |
  | School's local education agency | `edfi.LocalEducationAgency` |
  | Local education agency's parent local education agency | another `edfi.LocalEducationAgency` |
  | Local education agency's education service center | `edfi.EducationServiceCenter` |
  | Local education agency's state education agency | `edfi.StateEducationAgency` |
  | Education service center's state education agency | `edfi.StateEducationAgency` |

- the parent local education agency links form a cycle, including a local
  education agency that names itself;
- a `nameOfInstitution` or `shortNameOfInstitution` is not well-formed text
  (it contains an unpaired UTF-16 surrogate).

`parentId` is then chosen as follows. It is never the item's own id and, when
not `null`, is always the id of another item in the set.

| Type | `parentId` |
| --- | --- |
| `edfi.School` | Its local education agency, otherwise `null` |
| `edfi.LocalEducationAgency` | Its parent local education agency, else its education service center, else its state education agency, else `null` |
| `edfi.EducationServiceCenter` | Its state education agency, otherwise `null` |
| `edfi.StateEducationAgency` | `null` |

## Failures

Failures are `application/problem+json` documents with the usual DMS members:
`type`, `title`, `status`, `detail`, `correlationId`, `errors`, and (except on
401) `validationErrors`. The one exception is the unexpected-exception 500
described below. Classify a failure by its HTTP status and `type` only.

The problem documents this endpoint owns, listed in the
[fixed-literal table](#fixed-titles-and-details), have an empty `errors` array.
`parameter-validation-failed` names the offending parameters in `errors`.
Responses produced by existing DMS pipeline steps, such as authentication,
malformed-tenant and security-configuration failures, keep their existing
bodies, including any `errors` entries. No body carries SQL, database object
names, connection strings, hashes or other internal diagnostics.

Types under `urn:ed-fi:api:education-organization-projection:` are written below
by their last segment.

| Status | `type` | Meaning | Client action |
| --- | --- | --- | --- |
| 400 | `urn:ed-fi:api:bad-request:parameter-validation-failed` | `dataStoreId` missing or out of range, `limit` out of range, or a parameter repeated | Fix the request |
| 400 | `invalid-cursor` | The cursor does not decode, does not match the request, has expired, or is dated in the future | Restart the read once without a cursor |
| 400 | `unsupported-contract-version` | `contractVersion` is not served | Choose a version from Discovery |
| 400 | `urn:ed-fi:api:bad-request` | Multi-tenant mode: the tenant segment is malformed | Fix the request |
| 401 | `urn:ed-fi:api:security:authentication` | Token missing or invalid, or the client does not belong to the route tenant | Refresh the token once, then stop |
| 403 | `urn:ed-fi:api:security:authorization:` | The claim set does not grant `Read` on the service claim | Fix the claim set |
| 404 | `urn:ed-fi:api:not-found` | Multi-tenant mode: the tenant does not exist | Stop |
| 404 | `target-not-found` | No data store with this id for this tenant and route; the body does not say which | Stop |
| 409 | `target-provider-unsupported` | The data store's provider is unrecognized or not the one this deployment serves | Stop |
| 409 | `target-schema-incompatible` | The database's schema does not match this deployment | Stop |
| 409 | `projection-unsupported` | The data model lacks an element the projection needs | Stop |
| 409 | `projection-too-large` | The set exceeds `MaxProjectionRows` (default 50000) | Stop |
| 409 | `projection-data-invalid` | Duplicate identifiers or contradictory relationships ([validation](#validation-and-parent-selection)) | Stop |
| 409 | `projection-changed` | The set changed during the read | Restart the read without a cursor |
| 429 | `urn:ed-fi:api:too-many-requests` | Rate limited | Retry later |
| 500 | `urn:ed-fi:api:system:configuration:security` | The service claim is granted with the wrong authorization strategy | Fix the claim set |
| 500 | `urn:ed-fi:api:system` | The deployment's configuration is invalid | Retry later |
| 503 | `urn:ed-fi:api:service-unavailable` | The tenant or data store catalog could not be loaded, including when the tenant's catalog holds a connection string that cannot be decrypted | Retry later |
| 503 | `urn:ed-fi:api:service-configuration-error` | The data store has no connection string (connection configuration only) | Retry later |
| 503 | `urn:ed-fi:api:database-not-provisioned` | The data store's database has not been provisioned | Retry later |
| 503 | `target-unavailable` | The database could not be reached, its schema fingerprint could not be read, or it did not complete the read | Retry later |

`projection-changed` is the only 409 worth retrying, and only by restarting the
read. Every other 409 is permanent for the data store as it stands. A 500
caused by an unexpected exception keeps the existing DMS body
`{"message": "...", "traceId": "..."}`, served as `application/json`; treat it
like any other 500. A 429 or 503 may carry `Retry-After`.

DMS caches a database's schema fingerprint verdict per connection string for the
life of the process, including the negative verdicts behind
`database-not-provisioned` and `target-schema-incompatible`. After a database is
provisioned or repaired under the same connection string, restart every affected
DMS replica: retrying from the Configuration Service alone cannot clear a cached
negative verdict.

### Fixed titles and details

The problem types this endpoint owns have fixed titles and details:

| `type` | `title` | `detail` |
| --- | --- | --- |
| `invalid-cursor` | Invalid Cursor | The cursor is not valid for this request. Restart the read without a cursor. |
| `unsupported-contract-version` | Unsupported Contract Version | The requested contract version is not supported. |
| `target-not-found` | Target Not Found | The data store could not be found. |
| `target-provider-unsupported` | Target Provider Unsupported | The data store uses a database provider that this Ed-Fi API deployment does not serve. |
| `target-schema-incompatible` | Target Schema Incompatible | The data store's database schema is not compatible with this Ed-Fi API deployment. |
| `projection-unsupported` | Projection Unsupported | The education organization projection is not supported by the data model in use. |
| `projection-too-large` | Projection Too Large | The education organization set is larger than this deployment is configured to project. |
| `projection-data-invalid` | Projection Data Invalid | The education organization data contains duplicate identifiers or contradictory relationships. |
| `projection-changed` | Projection Changed | The education organization set changed during the read. Restart the read without a cursor. |
| `target-unavailable` | Target Unavailable | The data store's database is temporarily unavailable. Retry the request later. |
| `urn:ed-fi:api:service-configuration-error` | Service Configuration Error | The data store's database connection is not configured. |
| `urn:ed-fi:api:database-not-provisioned` | Database Not Provisioned | The data store's database has not been provisioned. |

The other types are produced by existing DMS pipeline steps and keep their
existing bodies.

## Contract versions

Every response echoes `contractVersion`. Discovery lists the versions the
deployment serves; a client sends the highest version it shares with that list,
and stops with no request when there is none. A later version is advertised
alongside `educationOrganizationProjection.v1` for at least one release.
