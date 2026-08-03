---
jira: DMS-1348
jira_url: https://edfi.atlassian.net/browse/DMS-1348
source_spike: DMS-1349
status: planning
related:
  - DMS-993
  - DMS-1019
  - DMS-1023
  - DMS-1043
  - DMS-1055
---

# Epic: Partitioned Cursor Paging

## Status

This planning draft is the initial design output of `DMS-1349`. `DMS-1348` has no
implementation children yet, so the work-package names below are provisional and have no
Jira keys or story files. Create those Jira children before turning the packages into
implementation-owned story documents. The source spike is currently targeted to Ed-Fi API
v8.1.

## Outcome

Add the ODS-compatible partitioned cursor-paging surface to regular resource and descriptor
GET-many endpoints without replacing the existing `limit`/`offset` surface or making deep-page
latency grow with page depth.

Cursor page requests use indexed `DocumentId` range seeks. The `/partitions` endpoint performs
one intentionally linear, non-hydrating query to calculate balanced ranges over the filtered,
authorized candidate set. No new persisted state, schema migration, or index is expected.

## Compatibility Baseline

The public contract follows the Ed-Fi client guidance:

- [Improve Paging Performance with Partitioned Cursor Paging](https://docs.ed-fi.org/reference/ed-fi-api/client-developers-guide/improve-paging-performance-cursor-paging/)

The ODS 7.3.2 implementation is a behavioral reference where the guide is not precise:

- [`PagingHelpers`](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/v7.3.2/Application/EdFi.Ods.Common/Infrastructure/Repositories/PagingHelpers.cs)
  defines the token syntax.
- [`KeySetPagingStrategy`](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/v7.3.2/Application/EdFi.Ods.Common/Providers/Queries/Paging/KeySetPagingStrategy.cs)
  applies inclusive range bounds and a zero offset.
- [`PartitionsController`](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/v7.3.2/Application/EdFi.Ods.Api/Controllers/Partitions/Controllers/PartitionsController.cs)
  and its provider-specific applicators define partition validation, sizing, and token
  generation.

The documented API contract is normative when the guide/OpenAPI and incidental ODS
implementation permissiveness disagree. In particular, DMS will reject `limit` or `offset` in a
cursor request even though ODS 7.3.2 accidentally accepts `limit` when `pageSize` is also present.

## Public API Contract

### GET-many cursor paging

| Input/output | Contract |
| --- | --- |
| `Next-Page-Token` response header | Included on every non-empty HTTP 200 regular or descriptor GET-many response, including a `limit`/`offset` response that can begin a cursor walk. Absent on an empty response. |
| `pageToken` | Selects the next inclusive `DocumentId` range. It is opaque to clients and is normally copied from `Next-Page-Token` or a `/partitions` response. |
| `pageSize` | Optional only when `pageToken` is present; integer `0..MaximumPageSize`. When omitted, use the configured `MaximumPageSize`, matching the existing default GET-many size. |
| `limit`, `offset` | Remain supported for traditional paging. Neither may be combined with `pageToken` or `pageSize`, including when its value is zero. |
| `totalCount` | Remains supported for traditional paging. `totalCount=true` is invalid in cursor mode; an explicitly supplied `totalCount=false` is allowed. Clients may issue `?totalCount=true&limit=0` separately before a cursor walk. |
| filters | Resource-property filters and `minChangeVersion`/`maxChangeVersion` compose with the cursor range. Clients must repeat the same filters on each request; the token does not store or validate them. |

The first cursor page is an ordinary GET-many request, optionally using `limit`. Its
`Next-Page-Token` starts the seek-based walk after that returned page. A traditional page that
uses a non-zero offset therefore starts its cursor continuation after the offset page, not at the
beginning of the collection. A token obtained from `/partitions` starts at that partition's first
accessible candidate.

`pageSize=0` returns HTTP 200 with an empty array and no `Next-Page-Token`; it intentionally
cannot advance a cursor walk. The implementation does not fetch one extra row to predict the
terminal page. It emits a token for every non-empty response and normally discovers completion
through the next empty response.

Query-parameter names are case-insensitive at the HTTP boundary and are canonicalized before
Core validation. Preserve the frontend's existing last-value-wins behavior for repeated query
parameters. Case variants such as `pageToken` and `PAGETOKEN` must collapse to one canonical key
without a dictionary collision or HTTP 500; the last value in request order wins.

#### Cursor validation and ProblemDetails

The presence of either `pageToken` or `pageSize`, including a blank or malformed value, selects
the new cursor validation path and its parameter-validation ProblemDetails shell. Traditional-only
`limit`/`offset` failures retain the existing generic bad-request response and messages.

Cursor validation is phase-gated. Accumulate errors in canonical parameter order within a phase,
but stop after the first phase containing errors:

1. syntax and range validation;
2. required parameter relationships; and
3. mixed-mode rules.

New cursor and partition failures use HTTP 400 with this JSON shape and the current DMS
`application/json` response media type:

```json
{
  "detail": "Parameters supplied to the request were invalid.",
  "type": "urn:ed-fi:api:bad-request:parameter-validation-failed",
  "title": "Parameter Validation Failed",
  "status": 400,
  "correlationId": "<request correlation id>",
  "validationErrors": {},
  "errors": ["<one or more messages>"]
}
```

Use these exact messages:

- malformed `pageToken`: `The page token provided was invalid.`
- invalid `pageSize`: `PageSize must be a value between 0 and {MaximumPageSize}.`
- invalid `limit` while the cursor shell applies: `Limit must be omitted or set to a numeric value between 0 and {MaximumPageSize}.`
- invalid `offset` while the cursor shell applies: `Offset must be a numeric value greater than or equal to 0.`
- invalid `totalCount` while the cursor shell applies: `TotalCount must be a boolean value.`
- `pageSize` without `pageToken`: `PageToken is required when pageSize is specified.`
- cursor parameters with `limit`: `Use pageSize instead of limit when using cursor paging with pageToken.`
- cursor parameters with `offset`: `Both offset and pageToken parameters were provided, but they support alternative paging approaches and cannot be used together.`
- `totalCount=true` in cursor mode: `The totalCount parameter cannot be set to true when using cursor paging with pageToken.`

Within a phase, report errors in canonical order: `pageToken`, `pageSize`, `limit`, `offset`,
`totalCount`. A syntax/range failure suppresses relationship and mixed-mode errors; a required
relationship failure suppresses mixed-mode errors.

### `/partitions`

Each regular resource and descriptor collection exposes:

```http
GET /data/{projectEndpoint}/{resourceEndpoint}/partitions?number=10
```

Route qualifiers, tenant segments, authentication, resource authorization, and profile routing
continue to compose through their existing DMS boundaries.

| Input/output | Contract |
| --- | --- |
| `number` | Optional desired partition count. Valid range is `1..200`; the default is configurable and initially `10`. |
| response | HTTP 200 with `{ "pageTokens": ["...", "..."] }`. No accessible candidates produces an empty array. |
| fewer partitions | The response may contain fewer tokens than requested because every partition is at least five maximum-sized pages. |
| filters | Supports the same resource-property and live-resource change-version filters as GET-many. Boundaries are calculated after filters and authorization. |
| excluded parameters | `limit`, `offset`, `pageToken`, `pageSize`, and `totalCount` are not part of the partition operation. |

Partition validation is also phase-gated. A malformed or out-of-range `number` produces the
exact error `Number of partitions must be between 1 and 200.` Partition-reserved parameters are
reported as unsupported without first parsing their values, using the exact error
`The '{parameter}' parameter is not supported by the partitions endpoint.` If several reserved
parameters are present, report them in canonical order `pageToken`, `pageSize`, `limit`, `offset`,
`totalCount`. The syntax/range phase for `number` takes precedence over the unsupported parameter
phase.

The ODS-compatible partition calculation is:

```text
requested count = number ?? DefaultPartitionCount
computed size   = ceiling(accessible candidate count / requested count)
minimum size    = MaximumPageSize * 5
partition size  = max(computed size, minimum size)
```

Select the actual `DocumentId` at candidate row numbers `1`, `1 + partition size`,
`1 + 2 * partition size`, and so on. Each token covers its starting id through one less than
the next starting id; the last token is unbounded above. This produces at most the requested
count without assuming that identity values are contiguous.

`/partitions` applies all filters and authorization before row numbering and counting. The
candidate relation must contain one row per `DocumentId`, even when an authorization strategy
uses joins internally.

## Cursor Token Contract

Clients must treat tokens as opaque. DMS uses the ODS token syntax so contract tests and SDKs see
the expected shape:

1. Format the inclusive minimum and maximum `DocumentId` values as invariant-culture signed
   decimal `Int64` values separated by a comma.
2. UTF-8 encode that text.
3. Base64url encode it by replacing `+` with `-`, `/` with `_`, and removing `=` padding.

The decoder accepts correctly padded or unpadded base64url input. It rejects `+`, `/`, internal
padding, more padding than required, an impossible base64url length, invalid UTF-8, and anything
other than exactly two comma-separated fields. A decimal field must match `-?[0-9]+`: whitespace
and a leading `+` are invalid, and the parsed value must fit `Int64`. The minimum is required. An
empty maximum decodes as `Int64.MaxValue` for ODS compatibility. The normal encoder always
includes both values and always emits canonical unpadded base64url.

Negative bounds and a minimum greater than the maximum are safe match-nothing ranges rather than
authorization bypasses; an inverted range is also how a bounded partition reaches its terminal
empty page after returning the item at the upper bound.

After a non-empty page, the next token uses `last selected DocumentId + 1` and retains the
request's maximum bound. If the last selected id is `Int64.MaxValue`, omit the header instead of
overflowing. Tokens are not signed, encrypted, or bound to a resource, filter set, client,
tenant, or database. Changing a range cannot bypass the independently compiled filters and
authorization predicates, and tokens are not promised to be portable between data stores.

The codec belongs to Core's HTTP-contract boundary. Frontend code only canonicalizes parameter
names, while backend contracts, planners, and SQL compilers receive the typed
`CursorRange(InclusiveMinimum, InclusiveMaximum)` and never parse or emit token text.

## Relational Design

### Cursor page selection

Both PostgreSQL and SQL Server already page regular resources and descriptors in ascending
`DocumentId` order, and every page root has a `DocumentId` primary key. Extend page selection
with a paging-mode choice:

- traditional: existing `ORDER BY DocumentId` plus `OFFSET`/`LIMIT` or `FETCH`;
- cursor: add `DocumentId >= @cursorMin AND DocumentId <= @cursorMax`, order by `DocumentId`,
  and take `@pageSize` with no offset operation;
- partition planning: reuse the same unpaged, filtered, authorized candidate relation.

Represent live collection paging as a discriminated choice rather than nullable combinations:

```text
CollectionPaging
|- Traditional(PaginationParameters)
`- Cursor(CursorRange, PageSize)
```

Retain the existing `PaginationParameters` model for traditional and tracked-change paging so
`/deletes` and `/keyChanges` do not acquire cursor behavior. Add explicit query-parameter roles
for cursor bounds and size and for partition count/minimum size; do not overload the existing
offset/limit roles.

Factor a reusable `CandidateDocumentIdQuerySpec` containing the root relation, value predicates,
live change-version predicates, unified-alias rewrites, row-level authorization specification,
and deterministic parameter metadata. Traditional page, cursor page, and partition compilers
must consume that same spec. The regular-resource builder continues to root on the resource
table, while the descriptor builder continues to root on `dms.Descriptor` with its mandatory
`ResourceKeyId` predicate. Core resource/change-version filter parsing is also shared between
GET-many and `/partitions` so candidate behavior cannot drift before it reaches SQL.

The candidate relation must produce exactly one row per `DocumentId`. Authorization strategies
should preserve uniqueness by construction, normally through `EXISTS`; do not add unconditional
`DISTINCT` merely to conceal a duplicate-producing authorization plan.

PostgreSQL cursor selection has this shape:

```sql
SELECT r."DocumentId"
FROM <shared candidate FROM/JOIN clauses>
WHERE <resource, change-version, and authorization predicates>
  AND r."DocumentId" >= @cursorMin
  AND r."DocumentId" <= @cursorMax
ORDER BY r."DocumentId"
LIMIT @pageSize;
```

SQL Server uses `TOP`, not `OFFSET 0`, so the no-offset invariant is literal:

```sql
SELECT TOP (@pageSize) r.[DocumentId]
FROM <shared candidate FROM/JOIN clauses>
WHERE <resource, change-version, and authorization predicates>
  AND r.[DocumentId] >= @cursorMin
  AND r.[DocumentId] <= @cursorMax
ORDER BY r.[DocumentId];
```

Cursor mode never compiles or runs total-count SQL. Existing traditional provider SQL must remain
behaviorally and textually unchanged except for unavoidable factoring of the shared candidate
plan.

Materialize a regular-resource page keyset once, as the current hydration batch does. Surface the
inserted ids as the first batch result set with PostgreSQL `RETURNING "DocumentId"` and SQL Server
`OUTPUT INSERTED.[DocumentId]`; `HydrationExecutor` calculates their maximum. Carry that value
through `HydratedPage` and `QuerySuccess` so Core can create `Next-Page-Token`. This returns at
most `MaximumPageSize` bigint values and adds no second candidate selection, database command, or
roundtrip. It is more robust than deriving the boundary only from hydrated document metadata
because a selected last row could be concurrently deleted before hydration. Descriptor query
rows already carry `DocumentId`; the descriptor handler takes their maximum.

Core emits the token only when the final response array is non-empty. The returned maximum is the
highest selected keyset id, not necessarily the highest document that survived later hydration.

### Partition planning

Compile the already filtered and authorized candidate relation into one provider-specific SQL
statement that:

1. orders unique candidates by `DocumentId`;
2. derives row number and candidate count;
3. computes the partition size; and
4. returns only the starting `DocumentId` values.

Use quotient/remainder ceiling arithmetic rather than `candidate_count + number - 1` so the
calculation cannot overflow. PostgreSQL has this shape:

```sql
WITH candidates AS (
    SELECT r."DocumentId"
    FROM <shared candidate relation>
    WHERE <all predicates>
),
ranked AS (
    SELECT
        "DocumentId",
        ROW_NUMBER() OVER (ORDER BY "DocumentId") AS row_number,
        COUNT(*) OVER () AS candidate_count
    FROM candidates
),
sized AS (
    SELECT *,
        GREATEST(
            candidate_count / @number
              + CASE WHEN candidate_count % @number = 0 THEN 0 ELSE 1 END,
            @minimumPartitionSize
        ) AS partition_size
    FROM ranked
)
SELECT "DocumentId"
FROM sized
WHERE (row_number - 1) % partition_size = 0
ORDER BY "DocumentId";
```

SQL Server uses the equivalent CTE with `COUNT_BIG`, `ROW_NUMBER`, `%`, and `CASE`. The database
returns starting ids only. Backend code converts each non-final start to the inclusive range
`start..nextStart-1` and the final start to `start..Int64.MaxValue`; Core token-encodes those typed
ranges.

The endpoint performs one database command and does not hydrate documents, project profiles,
resolve descriptors, inject links, or return total count. Provider SQL may differ, but the
candidate set, boundaries, and returned tokens must be identical for the same seeded data and
authorization context.

The partition query is intentionally `O(n)` over accessible candidates. That cost is paid once
to enable independent, depth-insensitive range scans. Adding secondary indexes is out of scope
unless measured provider plans demonstrate a specific regression that existing root, filter,
and authorization indexes cannot serve.

## Application Boundaries

- **Frontend/path routing:** replace the implicit "optional third segment means UUID" model with
  `ResourcePathOperation.Collection`, `.ById(DocumentUuid)`, and `.Partitions`. This recognizes
  `/{project}/{resource}/partitions` before UUID parsing. Unknown third segments retain the
  existing invalid-UUID response, and additional segments remain unmatched. Canonicalize
  `pageToken`, `pageSize`, and partition `number` while preserving last-value-wins semantics.
- **Core model:** use the explicit traditional/cursor choice and typed `Int64` range described
  above. Keep token text encoding/decoding at the HTTP contract boundary.
- **Core pipelines:** keep the existing GET-many pipeline for cursor pages. Add a dedicated
  partition pipeline with request logging/error handling, tenant and datastore resolution, typed
  path parsing, database fingerprint/resource seed/mapping resolution, endpoint and profile
  resolution, resource-info construction, shared filter and change-version validation,
  partition validation, resource-action authorization, row-level authorization filter
  construction, and then a partition handler. Partitions do not hydrate or profile-project
  documents.
- **Backend contracts:** add a dedicated `IPartitionQueryHandler`; do not route partition work
  through `QueryDocuments`. Query success carries the selected keyset maximum, and partition
  success carries typed ranges. SQL planners and executors never parse token strings.
- **Candidate planning:** factor the current root predicates, change-version filters, and
  authorization specification so traditional pages, cursor pages, and partition boundaries
  cannot drift.

### OpenAPI assembly

The current ApiSchema base documents already contain `pageToken`, `pageSize`, and
`numberOfPartitions` components, while resource fragments omit the cursor parameter references,
`Next-Page-Token` header, and partition paths. Add these platform-wide operations during DMS
OpenAPI assembly after all core, abstract, and extension fragments are merged but before domain
and profile filtering.

For every eligible core-resource, extension-resource, and descriptor collection:

- append `pageToken` and `pageSize` parameter references to the collection GET;
- document `Next-Page-Token` as a string header on its HTTP 200 response;
- add a sibling `/partitions` GET operation;
- copy resource filters, live change-version filters, security, tags, and domain metadata from
  the collection operation, but do not copy traditional/cursor paging or `totalCount` parameters;
- use a reusable HTTP 200 `application/json` schema containing `pageTokens: string[]`;
- generate the `operationId` by appending `Partitions` to the collection GET `operationId`, for
  example `getStudentsPartitions` and `get_TPDMCandidatesPartitions`; and
- provide a partition-specific summary and description rather than copying the collection GET
  text.

Do not augment item-by-id, composite, change-query, discovery, or management paths. Publish the
runtime `MaximumPageSize` as both the `pageSize` default and maximum, replacing the authoritative
fixture's current default of 25 for assembled DMS documents. Publish `DefaultPartitionCount` as
the `numberOfPartitions` default.

Profile OpenAPI filtering must explicitly associate `/partitions` with its base collection
because the partition response has no resource schema from which to infer the relationship.
Retain the operation only when the profile exposes readable content for that resource. The
partition success response stays `application/json` in profile documents and is not rewritten to
a resource-profile media type. Descriptor partition operations belong to descriptor OpenAPI;
this feature does not introduce descriptors into the existing resource-derived profile document.

### Configuration

Add `AppSettings:DefaultPartitionCount` with a property default and configured default of `10`.
Startup option validation requires `DefaultPartitionCount` in `1..200` and
`MaximumPageSize > 0`. Calculate the minimum partition size with checked `long` arithmetic as
`(long)MaximumPageSize * 5`. Pass both values into OpenAPI assembly. The environment override is
`AppSettings__DefaultPartitionCount`. Cursor paging is part of the API contract and is not
feature-toggled.

## Consistency Under Writes

Cursor paging is not a snapshot protocol, matching ODS behavior:

- deletes create harmless identity gaps;
- updates retain `DocumentId` and do not move between ranges, but can change filter,
  change-version, ownership, namespace, or relationship membership;
- an item that becomes eligible behind the current lower bound can be missed, while an item that
  becomes ineligible before its page disappears;
- new documents receive larger identity values and may appear in the final unbounded partition;
- a deleted and recreated document receives a new `DocumentId` and may appear later in the walk;
- changing filters, claims, ownership, or relationship authorization during a walk may change
  later results;
- retries with the same token may observe committed changes.

Non-final partition upper bounds prevent a later insert from moving into a completed partition.
Routing, resource authorization, supplied filters, and row-level authorization are independently
reapplied on every request, so moving a token between resources, clients, tenants, or databases
does not confer access and is not promised to produce meaningful results.
The feature does not add long-running transactions, server-side cursor state, snapshot handles,
or repeatable-read guarantees.

## Performance Invariants and Evidence

Implementation is incomplete without reproducible PostgreSQL and real SQL Server evidence.
Capture the traditional-paging baseline before changing planner code. The DMS repository does not
currently contain an implemented cross-provider cursor benchmark harness, so DMS-1348 must add a
repeatable script/configuration/result format or explicitly integrate and pin the external
Suite-3 performance runner.

Use these pinned data sets:

- 10,000 candidates for smoke and setup validation;
- 1,000,000 accessible regular-resource candidates with at least 10% `DocumentId` gaps;
- 2,000,000 total regular-resource candidates with 1,000,000 accessible under representative
  row-level authorization;
- filtered candidate sets at approximately 1% and 10% selectivity; and
- at least 250,000 descriptors split across accessible and inaccessible namespaces.

Measure page sizes 25 and 500 at the first, middle, and last cursor ranges. Compare offset 0, a
one-page shallow offset, and a recorded deep offset. Measure partition counts 1, 10, and 200 for
unfiltered, filtered, and representative authorized candidates. Each scenario has at least five
warmups and 30 measured warm-cache iterations on a pinned environment. Record p50, p95, command
count, returned rows/tokens, logical reads or buffers, database CPU/time, and the execution plan.

Acceptance gates are:

- cursor SQL contains no `OFFSET`, row-number skip, or count query and uses the root
  `DocumentId` key as a range predicate;
- existing `limit`/`offset` SQL and behavior remain unchanged;
- cursor hydration performs one database command, uses the existing single-command page-keyset
  architecture, and adds no roundtrip;
- `/partitions` performs one database command and returns identifiers only;
- middle/last cursor p50 is at most `1.20x` first-page cursor p50, and p95 is at most `1.30x`;
- first-page cursor p50/p95 is at most `1.20x`/`1.30x` the offset-0 baseline;
- existing shallow-offset p50/p95 is at most `1.20x`/`1.30x` its pre-change baseline;
- partition `number=200` p50 is at most `1.25x` `number=1` on the same candidate set, proving the
  requested count does not cause repeated scans; and
- deep-offset results are recorded for comparison but are not a cursor acceptance gate.

Capture PostgreSQL `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` and SQL Server actual XML plans with
`SET STATISTICS IO, TIME ON`. Unfiltered cursor plans should use primary-key range access.
Filtered and authorized plans should retain applicable existing indexes without repeated full
candidate scans. A partition plan may scan and sort the candidate set once. Add DDL or indexes
only after reviewed provider evidence demonstrates a repeatable deficiency.

Bounded telemetry records paging mode, requested/returned page size, requested/returned partition
count, duration, provider, command category, and success/failure. It never records raw token text,
filter names or values, decoded bounds, client identity, or candidate identifiers.

## Test Expectations

- Unit tests cover token round trips, omitted maximum, padded/unpadded input, forbidden alphabet
  and padding forms, invalid UTF-8, extra fields, decimal grammar, `Int64` bounds, terminal
  inverted ranges, and overflow handling.
- Validation tests cover every query-parameter combination, phase gating, canonical error order,
  exact messages and ProblemDetails shells, repeated-parameter last-value-wins behavior, and
  case-variant canonicalization without an exception.
- Routing and handler tests cover typed collection/by-id/partition classification, the dedicated
  pipeline order, ordinary/empty/zero-size/`Int64.MaxValue` header behavior, and startup
  configuration validation.
- SQL compiler/golden tests cover traditional, cursor, and partition SQL for PostgreSQL and SQL
  Server, including explicit parameter roles, SQL Server `TOP`, absence of offset/count SQL in
  cursor mode, overflow-safe partition sizing, and identifiers-only output.
- Hydration tests cover PostgreSQL `RETURNING` and SQL Server `OUTPUT`, result-set ordering, the
  selected-keyset maximum, and a selected final row deleted before hydration.
- Backend integration tests cover regular resources and descriptors, page sizes 0/1/max,
  multiple pages, partition boundaries, sparse ids, empty sets, filtered queries, change-version
  ranges, concurrent insert/delete behavior, and identical boundaries for equivalently seeded
  PostgreSQL and real SQL Server 2025 databases.
- Authorization integration tests prove that partition boundaries and cursor pages use the same
  accessible candidate set for no-further-authorization, relationship, namespace, ownership, and
  view-based strategies where supported. Descriptor coverage includes its supported no-further
  and namespace strategies. Forged ranges cannot expose inaccessible identifiers.
- OpenAPI tests cover core, extension, descriptor, excluded-domain, and readable/write-only
  profile documents; `operationId` values, summaries/descriptions, parameters/defaults, response
  headers/media types, tags, security, and excluded endpoint families are asserted exactly.
- E2E tests cover the public headers/body, the terminal empty request, malformed/mixed parameters,
  default and requested partition counts, parallel consumption without overlap, route qualifiers,
  multi-tenancy, extension resources, descriptors, and OpenAPI/profile metadata.
- A parity fixture runs the same contract cases against an ODS 7.3 reference API and DMS. Any
  additional difference must be recorded here before implementation is accepted.

The approved intentional ODS differences are:

- reject `limit` whenever cursor parameters are present, including when `pageSize` is also present;
- reject `totalCount=true` in cursor mode;
- use DMS `Int64 DocumentId` bounds rather than ODS `Int32 AggregateId` bounds;
- omit the next header rather than overflowing at `Int64.MaxValue`; and
- use the stricter approved base64url and decimal decoder contract.

## Likely Affected Areas

- `Core.External`: paging/range, query-result, and partition repository contracts.
- `Core`: `UtilityService`, `PathComponents`, `RequestInfo`, path/query validation middleware,
  `ApiService`, query/partition handlers, token codec, configuration, and response headers.
- `Frontend.AspNetCore`: query-parameter canonicalization, option registration, and default
  configuration.
- `Backend.Plans` and plan contracts: shared candidate spec, page/partition compilers, parameter
  roles, hydration batch output, and executor result contracts.
- `Backend`: regular and descriptor candidate planners, `RelationalDocumentStoreRepository`, and
  `DescriptorReadHandler`.
- `Core/OpenApi`, OpenAPI generator, and authoritative-fixture-based tests: platform augmentation
  and profile filtering.
- Core unit, backend plan/unit, PostgreSQL integration, SQL Server integration, API-level
  integration, DMS E2E, ODS-parity, and performance harness projects.

## Risks and Guardrails

- Authorization joins that duplicate a root id would corrupt partition counts and boundaries;
  assert the one-row-per-`DocumentId` candidate invariant for every supported strategy.
- Adding the selected-id result set changes hydration batch result ordering; update both provider
  executors and all normalized plan contracts atomically.
- Parameterized SQL Server `TOP` and large window queries can produce provider-specific plan or
  memory-grant behavior; do not infer parity from PostgreSQL tests.
- The intentionally linear partition query may sort or spill on large filtered/authorized sets;
  preserve the one-command/one-scan shape and use measured evidence before proposing DDL.
- The authoritative OpenAPI `pageSize` default currently conflicts with approved runtime behavior;
  assembled documents must expose the runtime value consistently across resource and descriptor
  specifications.
- `/partitions` has no resource-schema response from which the current profile filter can infer
  ownership; explicit base-path association is mandatory.
- `pageSize=0`, inverted ranges, sparse identifiers, an empty candidate set, and
  `Int64.MaxValue` are valid edge conditions rather than server errors.
- No implementation Jira children or story documents exist. This spike may refine the epic and
  package boundaries but does not authorize production code or Jira creation.

## Non-Goals

- Removing or transparently rewriting `limit`/`offset` paging.
- Cursor paging for composites, `/deletes`, `/keyChanges`, discovery, or management endpoints.
- Snapshot-consistent export or a guarantee against data/authorization changes during a walk.
- Server-side cursor storage, token signing/encryption, token expiration, or cross-database token
  portability.
- New DDL or indexes without benchmark evidence.

## Proposed Work Packages

Jira keys and final filenames are intentionally deferred until `DMS-1348` is decomposed.

1. **Cursor contract foundation** — typed paging/range models, token codec, phase-gated validation,
   ProblemDetails, configuration, and focused unit tests.
2. **Typed resource path operations** — collection/by-id/partition routing, canonicalization, and
   regression tests without exposing an incomplete partition handler.
3. **Shared candidate planning** — reusable regular/descriptor candidate specs, shared filter
   validation, deterministic parameters, and uniqueness contracts.
4. **Provider cursor SQL** — PostgreSQL and SQL Server compilers, explicit parameter roles, and
   SQL/golden tests preserving traditional SQL.
5. **Regular-resource cursor execution** — hydration keyset `RETURNING`/`OUTPUT`, selected maximum,
   `QuerySuccess`, response header, and both-provider integration tests.
6. **Descriptor cursor execution** — descriptor boundary propagation, headers, and provider tests.
7. **Partition pipeline and SQL** — route exposure, dedicated Core/backend contracts, regular and
   descriptor boundary planning, both provider compilers, validation, and integration tests.
8. **OpenAPI and client contract** — platform-wide resource/extension/descriptor augmentation,
   profile association, `operationId` values, summaries/descriptions, runtime defaults,
   snapshots, and client-facing documentation.
9. **Authorization, parity, and E2E suite** — cross-strategy accessible-set tests, ODS comparison,
   route/tenant/profile coverage, terminal walks, and parallel partition consumption.
10. **Performance and observability gate** — pre-change baselines, reproducible cross-provider
    harness, pinned large-data fixtures, provider-plan evidence, bounded telemetry, thresholds,
    and regression reporting.

Capture package 10's traditional-paging baselines before package 4 changes planner code. Packages
2 through 4 follow package 1. Packages 5 through 7 consume the shared candidate plan. Package 8
may proceed once package 1 fixes the public contract. Package 9 consumes packages 5 through 8,
and package 10 completes after provider, authorization, and E2E behavior is stable.

## Completion Evidence

- All eligible GET-many OpenAPI operations expose the cursor parameters and response header, and
  all eligible collections expose `/partitions`.
- Sequential and parallel cursor walks return every member of a stable filtered/authorized fixture
  exactly once across both providers, including descriptors.
- Invalid combinations and tokens return the approved ODS-compatible 400 contract.
- Cursor pages satisfy the SQL-shape, roundtrip, plan, and latency gates above without regressing
  traditional paging.
- The ODS/DMS parity fixture and the supported unit, provider integration, and E2E suites pass.
