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

- **PostgreSQL.** `REPEATABLE READ` reads a snapshot. It does not block writers,
  and writers do not block it.
- **SQL Server.** `SERIALIZABLE` holds shared and range locks on the four
  education organization tables until the read's transaction ends. A writer to
  one of those tables waits for the read in progress to finish. A writer that
  already holds a lock the read needs, and then needs a lock the read holds,
  deadlocks with it. SQL Server then aborts one of the two, and when the read
  is the victim, DMS answers `503 target-unavailable` and the client retries.
  DMS provisioning does not enable snapshot isolation, and the read does not
  use it. After the read, DMS returns the session to `READ COMMITTED` before
  releasing the connection to the pool.

Provider measurements at the cap (step 2.5), one read of the full set by the
provider reader alone, without validation, hashing or serialization:

| | PostgreSQL 16 | SQL Server 2025 |
| --- | --- | --- |
| Read time, median (min-max of 10) | 102 ms (68-123) | 101 ms (83-127) |
| Bytes received per read | 5.25 MB (105 bytes per row) | 6.24 MB (125 bytes per row) |
| Single-row writer, alone: p50 / p95 / max | 1.0 / 1.3 / 17 ms | 1.3 / 1.6 / 360 ms (first connection) |
| Same writer during back-to-back reads: p50 / p95 / max | 1.1 / 2.7 / 18 ms | 1.4 / 87 / 156 ms |
| Deadlocks or errors (writer / read) | none / none (43 reads) | none / none (243 reads) |

These were measured on one workstation, against local containers with their
data on tmpfs: 1 state education agency, 10 service centers, 989 local
education agencies, 49,000 schools, names of about 30 characters, and 2,000
sequential single-row `School` name updates. They are indications, not
guarantees. Bytes scale with name length. On SQL Server, a writer's added wait is
bounded by the duration of the read it waits for. Handler cost (validation,
digest and response) is measured separately.

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
