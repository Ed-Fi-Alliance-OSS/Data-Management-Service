# Education organization projection

The Ed-Fi API serves a service-only endpoint that lists the core education
organizations in one data store, with each one's parent. The DMS Configuration
Service reads it over HTTP to learn which education organizations a data store
holds, without ever connecting to that data store's database.

This page documents the service contract, how DMS serves it and what it costs,
how to provision the Configuration Service's credential, how the Configuration
Service reads and classifies the projection, the upgrade order, and what each
service logs.

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

## Request pipeline

DMS runs a projection request through these steps in order. A step that fails
answers the request, and no later step runs.

1. Request logging and the unexpected-exception handler.
2. Tenant validation (multi-tenant mode): a malformed tenant segment is
   `400 urn:ed-fi:api:bad-request`.
3. Authentication: `401`.
4. Tenant existence: an unknown tenant is `404 urn:ed-fi:api:not-found`; a
   tenant list that cannot be loaded is `503`.
5. Client-to-tenant binding: a client whose application belongs to another
   tenant is `401`; a binding that cannot be checked is `503`. The client's data
   store assignments are not consulted.
6. Service-claim authorization: `403` when the claim set does not grant `Read`
   on the projection claim, `500 urn:ed-fi:api:system:configuration:security`
   when it grants it with another authorization strategy.
7. Parameter parsing: the `400` responses of [Request](#request) and
   [Cursor](#cursor).
8. [Target resolution](#target-resolution).
9. Effective target: the data store's primary database. A snapshot or read
   replica the data store publishes, and the `Use-Snapshot` header, are ignored.
10. Schema fingerprint validation.
11. Mapping-set resolution: a data model that lacks a view, column or reference
    the projection needs is `409 projection-unsupported`.
12. The [read](#database-read-stages),
    [validation](#validation-and-parent-selection), digest comparison and page.

Steps 4 to 6 are the identity endpoints' steps, in the same order, and use the
same caches. A revoked grant or a moved application stops working when DMS next
refreshes its claim sets and application context (`CacheSettings` in
[Configuration](./CONFIGURATION.md#cachesettings), 600 seconds by default).

## Target resolution

`dataStoreId` is the Configuration Service data store id. DMS looks it up in the
route tenant's catalog. When it is not there, DMS reloads that tenant's catalog
once and looks again, so a data store registered after DMS started is found by
its first request.

| Situation | Response |
| --- | --- |
| The tenant's catalog cannot be reloaded: the Configuration Service is unreachable, answers an error or a malformed document, or holds a primary connection string that cannot be decrypted | `503 urn:ed-fi:api:service-unavailable` |
| No data store with this id in the route tenant | `404 target-not-found` |
| The data store's contexts do not match the route qualifiers | `404 target-not-found`, the same body |
| The data store's provider is not recognized | `409 target-provider-unsupported` |
| The data store's provider is recognized but is not the one this deployment serves | `409 target-provider-unsupported` |
| The data store has no recorded provider (a legacy row) | Read with this deployment's provider, as ordinary routing does |
| The data store has no connection string | `503 urn:ed-fi:api:service-configuration-error` |
| The database's schema fingerprint cannot be read | `503 target-unavailable` |
| The database has no `dms.EffectiveSchema` row | `503 urn:ed-fi:api:database-not-provisioned` |
| The fingerprint does not match this deployment, or is malformed | `409 target-schema-incompatible` |

The Configuration Service catalog has no flag that enables or disables a data
store. A data store DMS cannot serve is reported by one of the states above.

### Provider dialect

The Configuration Service records a data store's provider as `postgresql` or
`sqlserver`. DMS compares that value with the SQL dialect of the mapping
compiler it has registered: `postgresql` matches `Pgsql` and `sqlserver` matches
`Mssql`. The deployment setting `AppSettings:Datastore` (`postgresql` or
`mssql`) is not consulted; the registered dialect decides.

The two providers read the set differently:

| | PostgreSQL | SQL Server |
| --- | --- | --- |
| Isolation | `REPEATABLE READ` | `SERIALIZABLE`; the session is restored to `READ COMMITTED` afterwards |
| Lock wait bound | `SET LOCAL lock_timeout` | `SET LOCK_TIMEOUT` |
| Name length | `varchar(75)`: 75 code points | `nvarchar(75)`: 75 UTF-16 code units |
| Names | Read as text; PostgreSQL cannot store an unpaired surrogate | Read as the stored UTF-16 code units, so an unpaired surrogate reaches validation and is answered `409 projection-data-invalid` instead of being replaced by U+FFFD |
| Connection whose cleanup cannot be confirmed | The read ends its own server session, so the connection is never reused | The connection's pool is cleared, so the connection is never reused |

The lock wait and the statement are bounded by
`EducationOrganizationProjection:ReadLockTimeoutSeconds` and
`ReadCommandTimeoutSeconds` ([Configuration](./CONFIGURATION.md#appsettings)).
The effect of each isolation level on writers is described under
[effect on writers](#effect-on-writers-and-measured-cost).

## Database read stages

Each read runs four stages, each with its own failure handling, so a missing
column is never reported as an outage and an outage is never reported as an
incompatible schema.

| Stage | Covers | A failure becomes |
| --- | --- | --- |
| Acquire | Opening the connection | An expected connection failure: `503 target-unavailable` |
| Prepare | Beginning the transaction; setting the lock and command timeouts | Any database exception: `503 target-unavailable` |
| Execute and materialize | Running the statement and reading every row | A database exception: by its code, in the table below. A value that cannot be read as its expected type: `409 target-schema-incompatible` |
| Commit | Ending the transaction | Any database exception or unknown outcome: `503 target-unavailable`, and the rows are discarded |

The classifier that recognizes expected connection failures is used only in the
Acquire stage, so a missing column raised by the statement is never mistaken
for an unreachable database. Caller cancellation ends the read in any stage,
with no response. Any other exception is a defect and reaches the
unexpected-exception `500`.

Database exceptions in the Execute and materialize stage:

| Provider | `409 target-schema-incompatible` | `503 target-unavailable` |
| --- | --- | --- |
| PostgreSQL `SQLSTATE` | `42P01`, `42703`, `3F000`, `42704`, `42809`, `42883` (missing objects); `42804`, `42846`, `42P18`, `22P02`, `22003` (types and conversions) | `40P01` deadlock, `55P03` lock not available, `57014` query cancelled, classes `08`, `28`, `53` and `57`, **and any other or missing code** |
| SQL Server error number | `207`, `208`, `4104`, `2812`, `1088` (missing objects); `206`, `235`, `241`, `242`, `245`, `257`, `402`, `529`, `8114`, `8115` (types and conversions); `447` (a name column that is no longer character data) | `1205` deadlock, `1222` lock timeout, `-2` timeout, `4060` and `18456` (open and login), `10053`, `10054`, `10060`, `40613` (connection), **and any other number** |

An unrecognized code is treated as transient. The Configuration Service retries
it a bounded number of times and then fails the job, so a deterministic failure
missing from the permanent list shows up as a job that keeps failing with
`target-unavailable`, not as a `409`. Report such a code: it belongs in the
permanent list.

## Provisioning the Configuration Service credential

The Configuration Service reads the projection as an ordinary DMS API client.
Its credential is an application whose claim set grants only `Read` on the
projection claim and which has no data stores and no education organizations.
No shipped claim set grants the claim.

In multi-tenant mode a client works only in its own tenant, so provision one
credential per tenant, and send every request below with that tenant's `Tenant`
header. In single-tenant mode, provision one credential.

1. **Import the claim set.**

   ```http
   POST /v3/claimSets/import
   Tenant: Tenant_255901
   Content-Type: application/json

   {
     "claimSetName": "EdOrgProjectionReader-Tenant_255901",
     "resourceClaims": [
       {
         "name": "educationOrganizationProjection",
         "claimName": "http://ed-fi.org/identity/claims/services/educationOrganizationProjection",
         "actions": [{ "name": "Read", "enabled": true }]
       }
     ]
   }
   ```

   Claim set names are unique across tenants: a name another tenant already
   holds is refused with `409`, so give each tenant's claim set its own name.
   Keep the claim's default `NoFurtherAuthorizationRequired` strategy; a claim
   set that overrides it is answered
   `500 urn:ed-fi:api:system:configuration:security`.

2. **Verify the grant.** `GET /v3/claimSets/{id}/export` must show `Read`, and
   nothing else, on the projection claim. An import skips a resource claim that
   is missing from the stored claims hierarchy instead of refusing it, so an
   export without the claim means the hierarchy lacks it; see the
   [Claims Loading Guide](./CLAIMS-LOADING-GUIDE.md#upgrading-an-existing-deployment-education-organization-projection-claim).

3. **Create the application.** `POST /v3/applications` with the claim set's
   name, `"dataStoreIds": []` and `"educationOrganizationIds": []`, under a
   vendor of the tenant. The response's `key` and `secret` are the credential.
   See
   [API Client and Data Store Configuration](./API-CLIENT-AND-INSTANCE-CONFIGURATION.md#provisioning-a-client-with-no-data-store-assignment)
   for the client lifecycle.

4. **Configure the Configuration Service.** Set
   `DmsEducationOrganizationProjectionSettings:TenantCredentials:<tenant>:ClientId`
   and `ClientSecret` to the `key` and `secret` (single-tenant mode:
   `Credentials:ClientId` and `ClientSecret`), together with `DmsBaseUrl`
   ([settings](./CONFIGURATION.md#dmseducationorganizationprojectionsettings)).
   Supply the secret through the environment or a secret store, never a
   committed file. The shared `Credentials` pair is used for every tenant
   without its own entry, but a client authenticates in its own tenant only, so
   in multi-tenant mode give every tenant its own entry.

DMS sees the new claim set when it next refreshes its claim sets
(`ClaimSetsCacheExpirationSeconds`, 600 seconds by default), or at once through
`POST /management/reload-claimsets` (`/management/{tenant}/reload-claimsets` in
multi-tenant mode) where the management endpoints are enabled.

Do not grant the projection claim to a vendor application's claim set. A
credential that holds it can list the education organizations of every data
store in its tenant.

### Identity providers

The reader takes the token URL from Discovery (`urls.oauth`, the DMS token
endpoint), sends the credential as HTTP Basic with each value percent-encoded,
and sends `grant_type=client_credentials` with no `scope`. DMS forwards the
request to the configured identity provider, as it does for every client.

- **Self-contained.** The Configuration Service issues the token. Each token
  counts toward `IdentitySettings:BearerTokenPerClientLimit` (default 5) until
  it expires or is revoked; see [token budget](#token-budget).
- **Keycloak.** Keycloak issues the token. The Configuration Service creates
  each application's Keycloak client with its claim set as a default client
  scope, so a request without `scope` receives it.
  `BearerTokenPerClientLimit` does not apply. **The reader's Keycloak path has
  not been validated end to end:** the mechanism described here follows the
  code, but the Instance Management E2E suite, which exercises the reader
  against a running stack, uses the self-contained provider only.

Provisioning is the same in both modes: the claim set and application are
created through the Configuration Service API, which registers the client with
the identity provider. DMS checks the client-to-tenant binding against the
Configuration Service, whichever provider issued the token.

### Credential rotation

The reader caches one token per tenant and client id, and uses it until
`TokenExpirySafetyMarginSeconds` (default 60) before it expires. Its settings
are read at startup, so a changed credential takes effect when the
Configuration Service restarts.

To rotate without a failed read, use a second client:

1. `POST /v3/apiClients` with the application's `applicationId`, a `name`,
   `"isApproved": true` and `"dataStoreIds": []`. The response carries the new
   client's `key` and `secret`.
2. Set the tenant's `ClientId` and `ClientSecret` to the new pair and restart
   the Configuration Service instances.
3. Delete the old client with `DELETE /v3/apiClients/{numeric id}`.

`PUT /v3/apiClients/{numeric id}/reset-credential` also rotates the secret,
keeping the key. From the reset until the Configuration Service runs with the
new secret, a read that needs a new token fails with `TokenRejected`, which the
job layer records as a permanent failure.

### Token budget

With the self-contained provider, every token issued counts toward
`BearerTokenPerClientLimit` until it expires or is revoked, and the reader never
revokes a token. In steady state a Configuration Service process holds one
token per credential, and two for up to `TokenExpirySafetyMarginSeconds` while a
new token overlaps the one it replaces. About two tokens per replica is
therefore a sizing estimate, not a bound. More tokens count when:

- **A process restarts.** Its cache starts empty, and the previous process's
  token still counts until it expires.
- **A page is answered `401`.** The reader discards that token and requests
  another; the discarded token still counts until it expires.
- **The token lifetime does not exceed the safety margin.** A token whose
  `expires_in` is at most `TokenExpirySafetyMarginSeconds` is used for one page
  request and never reused, so every page requests a new token: a multi-page
  read requests several, and frequent reads can reach the limit even with a
  single replica.

Keep `TokenExpirySafetyMarginSeconds` comfortably below the token lifetime (by
default 60 seconds against the Configuration Service's 30-minute lifetime), and
size `BearerTokenPerClientLimit` with headroom for restarts and refreshes; see
[Configuration](./CONFIGURATION.md#relevant-parameters-in-appsettingsjson-configuration-service).
A grant beyond the limit is `429`, which the reader treats as transient
(`RateLimited`).

## Configuration Service reader

The reader is a library in the Configuration Service backend
(`AddDmsEducationOrganizationProjectionReader`) for the background jobs that use
it; see [Configuration Service Background Jobs](./CMS-BACKGROUND-JOBS.md). It
talks to DMS over HTTP only: it opens no DMS database connection, and the
backend references no DMS assembly and no database provider. Its settings are
in
[`DmsEducationOrganizationProjectionSettings`](./CONFIGURATION.md#dmseducationorganizationprojectionsettings);
with no `DmsBaseUrl` it is not configured and every read fails with
`NotConfigured`.

A read of one data store:

1. Reads Discovery at `{DmsBaseUrl}/{tenant}` (single-tenant mode
   `{DmsBaseUrl}/`), without credentials, and caches it per tenant for
   `DiscoveryCacheSeconds`. A failed read is never cached.
2. Chooses the highest contract version that both Discovery and
   `ContractVersions` list.
3. Fills the placeholders of both URL templates from the data store's contexts
   in the Configuration Service catalog. A placeholder no context fills is
   `TargetNotRoutable`, and no request is sent. Each filled URL must have the
   scheme, host and port of `DmsBaseUrl` and lie under its path at a segment
   boundary (`/api` admits `/api/x`, not `/api-other`); otherwise the read is
   `DiscoveryInvalid`. Redirects are never followed.
4. Obtains a token, then requests pages of `PageSize` items until `nextCursor`
   is `null`, checking every page against the contract: members, echoes, page
   size, ascending ids, no repeated cursor, no empty continuation page. When the
   set ends, every `parentId` must name an item of the set.
5. Restarts without a cursor after `409 projection-changed`, or after
   `400 invalid-cursor` when it sent a cursor, at most `MaxWalkRestarts` times
   in all.

The result is either every item of one complete read or a failure with no
items: a read never returns part of a set. `TotalReadTimeoutSeconds` bounds the
whole read, Discovery, tokens and restarts included. Caller cancellation is
thrown as `OperationCanceledException` with the caller's token and is never
reported as a timeout.

A page answered `401` is retried once with a new token. A read that ends in
`DiscoveryInvalid`, `TargetNotFound` or `Unauthorized` drops the tenant's cached
Discovery document, so the next read fetches it again.

### Failure classification

A failure has a code, a category (`Transient` or `Permanent`) and the stage it
happened in: `Discovery`, `Token` or `Page`. The same HTTP status can mean
different things at different stages, so the rules below are per stage.

**Without a request.** These are found before the request they concern is
sent, and that request is never made: a missing `DmsBaseUrl` or an unusable
tenant name before Discovery, an unfilled placeholder after it, a missing credential before
the token request.

| Observation | Code |
| --- | --- |
| No `DmsBaseUrl`; no credential for the tenant | `NotConfigured` |
| A tenant name that cannot form a URL segment; a URL placeholder no data store context fills | `TargetNotRoutable` |

**Every stage**, checked first, in this order:

| Observation | Code |
| --- | --- |
| Caller cancellation | none: `OperationCanceledException` is thrown |
| The request's timeout, or `TotalReadTimeoutSeconds`, reached | `Timeout` |
| A transport failure | `NetworkError` |
| Any `3xx` (redirects are never followed) | `DiscoveryInvalid` |
| `429` | `RateLimited` |
| `500 urn:ed-fi:api:system:configuration:security` | `Forbidden` |
| Any other `5xx`, including the `{message, traceId}` body | `ServiceUnavailable` |

**Discovery request**, then:

| Observation | Code |
| --- | --- |
| `200` without the projection members (the endpoint is disabled) | `Unsupported` |
| `200` that is malformed, over 1 MiB, or has a template outside `DmsBaseUrl` | `DiscoveryInvalid` |
| `200` with no contract version in common with `ContractVersions` | `UnsupportedContract` |
| `404` | `TargetNotFound` |
| Any other status, including `400`, `401`, `403` and a `2xx` other than `200` | `UnexpectedResponse` |

**Token request**, then:

| Observation | Code |
| --- | --- |
| `200` that is malformed or over 1 MiB | `MalformedResponse` |
| `400` or `401` | `TokenRejected` |
| Any other status, including `403`, `404` and a `2xx` other than `200` | `UnexpectedResponse` |

**Page request**, then. The projection's own problem types are recognized only
here.

| Observation | Code |
| --- | --- |
| `400 unsupported-contract-version` | `UnsupportedContract` |
| `400 invalid-cursor` | Restart, when a cursor was sent and a restart remains; otherwise `InvalidRequest` |
| Any other `400` | `InvalidRequest` |
| `401` | Retry the page once with a new token; a second `401` is `Unauthorized` |
| `403` | `Forbidden` |
| `404`, whatever its type | `TargetNotFound` |
| `409 projection-changed` | Restart, while a restart remains; then `ProjectionChanged` |
| `409 target-schema-incompatible` or `target-provider-unsupported` | `TargetSchemaIncompatible` |
| `409 projection-unsupported` | `Unsupported` |
| `409 projection-too-large` | `LimitExceeded` |
| `409 projection-data-invalid` | `DataInvalid` |
| Any other `409` or `4xx`; a `2xx` other than `200` | `UnexpectedResponse` |
| `200` with a body over `MaxResponseBodyBytes`, or beyond `MaxPages` pages or `MaxItems` items | `LimitExceeded` |
| `200` that breaks the contract: malformed JSON, a missing or unknown member, a wrong echo, a wrong page size, a repeated cursor, an empty continuation page | `MalformedResponse` |
| `200` whose items are out of order or repeated, or have an unknown discriminator, an empty name, or a `parentId` equal to the item's own id; at the end of the set, a `parentId` naming no item | `DataInvalid` |

A problem `type` is read only from an `application/problem+json` body of at most
64 KB; `projection-changed`, `invalid-cursor` and the security-configuration
type are matched exactly.

Each code has one category and, when permanent, one job error code. A job
handler throws `JobPermanentException` with the job error code for a permanent
failure, and any other exception for a transient one, so the job layer retries a
transient failure and ends in `AttemptsExhausted` if no attempt succeeds. The
job error codes' public messages are fixed; see
[failure classification](./CMS-BACKGROUND-JOBS.md#failure-classification-and-public-error-text).

| Code | Category | Job error code |
| --- | --- | --- |
| `NotConfigured` | Permanent | `EdOrgProjectionNotConfigured` |
| `DiscoveryInvalid` | Permanent | `EdOrgProjectionDiscoveryInvalid` |
| `Unsupported`, `UnsupportedContract` | Permanent | `EdOrgProjectionUnsupported` |
| `TokenRejected`, `Unauthorized` | Permanent | `EdOrgProjectionUnauthorized` |
| `Forbidden` | Permanent | `EdOrgProjectionForbidden` |
| `TargetNotFound` | Permanent | `EdOrgProjectionTargetNotFound` |
| `TargetNotRoutable` | Permanent | `EdOrgProjectionTargetNotRoutable` |
| `TargetSchemaIncompatible` | Permanent | `EdOrgProjectionTargetSchemaIncompatible` |
| `DataInvalid` | Permanent | `EdOrgProjectionDataInvalid` |
| `LimitExceeded` | Permanent | `EdOrgProjectionLimitExceeded` |
| `InvalidRequest` | Permanent | `EdOrgProjectionInvalidRequest` |
| `MalformedResponse` | Permanent | `EdOrgProjectionMalformedResponse` |
| `UnexpectedResponse` | Permanent | `EdOrgProjectionUnexpectedResponse` |
| `ProjectionChanged`, `RateLimited`, `ServiceUnavailable`, `NetworkError`, `Timeout` | Transient | none |

## Upgrade order

The endpoint, its Discovery members and its claim are additive. DMS databases
need no change: the projection reads existing tables, and the effective schema
hash is unchanged.

1. **Upgrade the Configuration Service.** Its database upgrade adds the
   projection claim, with no grants, to the resource claims and to an existing
   catalog's stored claims hierarchy, and leaves existing claims, claim sets
   and grants unchanged; a new catalog receives the claim from the embedded
   claims. Deployments that load claims from the filesystem add the claim to
   their claims files; see the
   [Claims Loading Guide](./CLAIMS-LOADING-GUIDE.md#upgrading-an-existing-deployment-education-organization-projection-claim).
2. **Provision** the claim set, application and credential for each tenant
   ([above](#provisioning-the-configuration-service-credential)).
3. **Deploy DMS** with `AppSettings:EnableEducationOrganizationProjection`
   `true`, its default.
4. **Configure the reader** (`DmsBaseUrl` and the credentials) and restart the
   Configuration Service. Until a background job uses the reader, its section
   can stay empty.

Turning the toggle off later removes the route and the two Discovery members.
A read that fetches Discovery then fails as `Unsupported`. A read that still
holds the tenant's cached Discovery document (`DiscoveryCacheSeconds`, 300 by
default) first requests a page, which DMS answers `404` because the route is
gone: that read fails as `TargetNotFound` and drops the cached document, so the
next read fetches Discovery and fails as `Unsupported`. Both failures are
permanent, and reads keep failing until the toggle is turned back on.

## Logging and redaction

Neither service logs a cursor, token, client secret, `Authorization` header,
request or response body, or exception message, at any level.

**DMS.**

- A served page is logged at `Debug` with the data store id, the sanitized
  tenant, status, row count, read and total time, and the trace id. A refused
  request is logged with the same identifiers and a reason or stage name; a
  database failure adds only the exception type name and the provider's code
  (`SQLSTATE` or error number). SQL, object names, connection strings, digests,
  schema hashes and education organization names are never logged.
- Request logs record the path without the query string, and the framework's
  request events for this route record the query string as `?[redacted]`
  ([Cursor](#cursor)).
- The shared components on this path (the data store catalog, the tenant list
  and the mapping set) log exception type names and HTTP status codes instead of
  exception objects, and log the mapping set by dialect and mapping version,
  without its hash.

**Configuration Service.**

- One record per read: `Information` on success, `Warning` on failure, with the
  sanitized tenant, data store id, stage, code, category, HTTP status, the
  sanitized problem `type` and DMS `correlationId`, pages read, restarts and
  elapsed time. A cancelled read writes no record. Match a failure to the DMS
  logs through its `correlationId`.
- The reader's `HttpClient` replaces the default request logging with one
  record per request: method, path without the query string, status and elapsed
  time, or the exception type names of a failure.
- A `Failure` result carries the same sanitized values, so a job that stores or
  logs it adds nothing the record above leaves out.
