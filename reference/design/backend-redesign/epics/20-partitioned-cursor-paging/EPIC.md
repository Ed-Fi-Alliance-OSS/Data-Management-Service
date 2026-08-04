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

This planning draft is the design output of `DMS-1349`. `DMS-1348` has no implementation
children yet. The eleven linked work-package files below are provisional planning placeholders that
were approved before Jira creation; they use `jira: TBD` and are not implementation-owned story
documents. Create Jira children only after separate authorization, then replace each placeholder's
frontmatter and update `JIRA-INDEX.md` in a mapping-only change. The source spike is currently
targeted to Ed-Fi API v8.1.

The planning identifiers are not contiguous. Typed path operations, provider cursor SQL, and
descriptor cursor execution were consolidated into `E20-S00b`, `E20-S02`, and `E20-S04`
respectively, and the separate ODS reference-deployment package was removed in favor of static
comparison cases owned by `E20-S08b`. The retired `E20-S01`, `E20-S03`, `E20-S05`, and `E20-S11`
identifiers are not reused.

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
- [`QueryParameters`](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/v7.3.2/Application/EdFi.Ods.Common/Models/Queries/QueryParameters.cs#L31-L37)
  decodes a supplied token before
  [`QueryParametersValidator`](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/v7.3.2/Application/EdFi.Ods.Common/Models/Queries/QueryParametersValidator.cs#L16-L45)
  runs from
  [`DataManagementControllerBase`](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/v7.3.2/Application/EdFi.Ods.Api/Controllers/DataManagementControllerBase.cs#L171-L180).
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
| `Next-Page-Token` response header | Included whenever regular-resource or descriptor GET-many page selection produces a non-null `HighestSelectedDocumentId`, including on a `limit`/`offset` response that can begin a cursor walk and when concurrent deletes leave the hydrated response body empty. Absent when page selection is skipped or selects no keys, and at `Int64.MaxValue` where advancing would overflow. |
| `pageToken` | Selects the next inclusive `DocumentId` range. It is opaque to clients and is normally copied from `Next-Page-Token` or a `/partitions` response. |
| `pageSize` | Optional only when `pageToken` is present; integer `0..MaximumPageSize`. When omitted, use the configured `MaximumPageSize`, initially `500`, matching the existing default GET-many size. |
| `limit`, `offset` | Remain supported for traditional paging. When `limit` is omitted, use the configured `MaximumPageSize`, initially `500`. Neither parameter may be combined with `pageToken` or `pageSize`, including when its value is zero. |
| `totalCount` | Remains supported for traditional paging. When `pageToken` is present and valid, `totalCount=true` is invalid and an explicitly supplied `totalCount=false` is allowed. Clients may issue `?totalCount=true&limit=0` separately before a cursor walk. |
| filters | Resource-property filters and `minChangeVersion`/`maxChangeVersion` compose with the cursor range. Clients must repeat the same filters on each request; the token does not store or validate them. |

The first cursor page is an ordinary GET-many request, optionally using `limit`. Its
`Next-Page-Token` starts the seek-based walk after that returned page. A traditional page that
uses a non-zero offset therefore starts its cursor continuation after the offset page, not at the
beginning of the collection. A token obtained from `/partitions` starts at that partition's first
accessible candidate.

`pageSize=0` returns HTTP 200 with an empty array and no `Next-Page-Token` because its selected
keyset is empty and `HighestSelectedDocumentId` is null; it intentionally cannot advance a cursor
walk.
The implementation does not fetch one extra row to predict the terminal page. It emits a token
whenever page selection returns a non-empty keyset and normally discovers completion through the
next keyset-empty response. If all selected rows are concurrently deleted before hydration, the
current body can be empty while the header still advances past those selected keys.

Emitting `Next-Page-Token` on an ordinary `limit`/`offset` response extends the published Ed-Fi
surface because the client guide does not describe it for traditional responses and authoritative
collection fixtures do not define it as a response header. It nevertheless matches ODS 7.3.2
runtime behavior and gives clients a cursor-walk entry point.

The new `pageToken`, `pageSize`, and partition `number` parameter names are case-insensitive at the
HTTP boundary and are canonicalized before Core validation. Canonicalization is scoped to those
three names only. The existing case-sensitive matching of `limit`, `offset`, and `totalCount` is
unchanged, so `?LIMIT=5` continues to return
`The query field 'LIMIT' is not valid for this resource.` rather than becoming a working limit.
Preserve the frontend's existing last-value-wins behavior for repeated query parameters. Case
variants such as `pageToken` and `PAGETOKEN` must collapse to one canonical key and retain only the
last value in request order.

#### Cursor validation and ProblemDetails

The presence of either `pageToken` or `pageSize`, including a blank or malformed value, selects
the new cursor validation path and its parameter-validation ProblemDetails shell. Traditional-only
`limit`/`offset` failures retain the existing generic bad-request response and messages.

Cursor validation returns exactly one error. Evaluate the following four phases in order, use the
exact message shown for each rule, and stop at the first match. Query-key presence, including a
blank, malformed, or zero value, controls phase selection and relationship/conflict ordering
through phase 2 before general syntax/range parsing.

**Phase 0 — token decode**

- `pageToken` present and not decodable: `The page token provided was invalid.`

**Phase 1 — mixed-mode conflicts**

All rules in this phase require `pageToken` to be present and valid:

1. `offset` present: `Both offset and pageToken parameters were provided, but they support alternative paging approaches and cannot be used together.`
2. `limit` present: `Use pageSize instead of limit when using cursor paging with pageToken.`
3. `totalCount=true`: `The totalCount parameter cannot be set to true when using cursor paging with pageToken.`

**Phase 2 — required relationships**

These rules apply when `pageToken` is absent:

4. `pageSize` and `offset` present, with no `limit`: `Use limit instead of pageSize when using limit/offset paging.`
5. `pageSize` present: `PageToken is required when pageSize is specified.`

**Phase 3 — syntax and range**

Evaluate this phase in canonical order `pageSize`, `limit`, `offset`, `totalCount`:

6. invalid `pageSize`: `PageSize must be a value between 0 and {MaximumPageSize}.`
7. invalid `limit`: `Limit must be omitted or set to a numeric value between 0 and {MaximumPageSize}.`
8. invalid `offset`: `Offset must be a numeric value greater than or equal to 0.`
9. invalid `totalCount`: `TotalCount must be a boolean value.`

The numbered rules define the within-phase tie-breakers. A phase-0 failure suppresses every other
rule, a mixed-mode conflict suppresses relationship and syntax/range rules, and a required-
relationship failure suppresses syntax/range rules. This matches ODS's one-element validation-
error response rather than accumulating every applicable cursor message.

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
  "errors": ["<message>"]
}
```

For a cursor failure, `errors` contains exactly the one message selected above. A partition
failure uses the same shell but may contain several ordered messages.

##### Worked precedence examples

`X` denotes any successfully decoded page token. Every expected DMS failure below returns HTTP 400
with exactly the listed message.

| Request | Expected DMS message | Matches ODS 7.3.2? |
| --- | --- | --- |
| `?pageToken=X&offset=-1` | `Both offset and pageToken parameters were provided, but they support alternative paging approaches and cannot be used together.` | Yes. |
| `?pageToken=X&limit=99999` | `Use pageSize instead of limit when using cursor paging with pageToken.` | Yes. |
| `?pageSize=99999` | `PageToken is required when pageSize is specified.` | Yes. |
| `?pageSize=` | `PageToken is required when pageSize is specified.` | No. ODS model-binds the blank value to `null`, treats the parameter as absent, and returns HTTP 200; this is an approved stricter DMS rejection. |
| `?pageToken=!!!&offset=5` | `The page token provided was invalid.` | Yes. |
| `?pageToken=!!!&limit=10` | `The page token provided was invalid.` | Yes. |
| `?pageSize=5&limit=10` | `PageToken is required when pageSize is specified.` | No. ODS returns HTTP 200; this is an approved stricter DMS rejection. |
| `?pageSize=5&totalCount=true` | `PageToken is required when pageSize is specified.` | Yes. ODS returns the same message from its limit/offset branch. |
| `?pageSize=5&offset=3&totalCount=true` | `Use limit instead of pageSize when using limit/offset paging.` | Yes. |
| `?pageToken=X&pageSize=-1` | `PageSize must be a value between 0 and {MaximumPageSize}.` | Yes. |
| `?pageToken=X&pageSize=abc` | `PageSize must be a value between 0 and {MaximumPageSize}.` | No. ODS's `int?` model binding fails before its validator runs, so ODS returns a model-binding response shell instead of this range message. |
| `?pageToken=X&limit=10&pageSize=5` | `Use pageSize instead of limit when using cursor paging with pageToken.` | No. ODS returns HTTP 200; this is an approved stricter DMS rejection. |
| `?pageToken=X&totalCount=true` | `The totalCount parameter cannot be set to true when using cursor paging with pageToken.` | No. ODS returns HTTP 200; this is an approved DMS rejection. |

Cursor parameter recognition is operation-scoped. Supplying `pageToken` or `pageSize` to
`/deletes` or `/keyChanges` returns the existing HTTP 400 bad-request shell with
`The query field '{parameter}' is not valid for this Change Query endpoint.` These names must not
become globally reserved parameters that unsupported endpoint families silently ignore.

### `/partitions`

Each regular resource and descriptor collection exposes:

```http
GET /data/{projectEndpoint}/{resourceEndpoint}/partitions?number=10
```

Route qualifiers, tenant segments, authentication, resource authorization, and profile routing
continue to compose through their existing DMS boundaries. Profile content-type handling follows
the existing GET-many outcome: a `/partitions` request that explicitly requests a profile whose
resource exposes no readable content type returns the existing HTTP 405 profile method-usage
response, and a request for which no readable profile applies implicitly proceeds unfiltered. The
partition response is never profile-projected, so runtime enforcement and the OpenAPI omission of
`/partitions` from write-only profile documents agree.

| Input/output | Contract |
| --- | --- |
| `number` | Optional desired partition count. Valid range is `1..200`; the default is configurable and initially `10`. |
| response | HTTP 200 with `{ "pageTokens": ["...", "..."] }`. No accessible candidates produces an empty array. |
| fewer partitions | The response may contain fewer tokens than requested because every partition is at least five maximum-sized pages. |
| filters | Supports the same resource-property and live-resource change-version filters as GET-many. Boundaries are calculated after filters and authorization. |
| excluded parameters | `limit`, `offset`, `pageToken`, `pageSize`, and `totalCount` are not part of the partition operation. |

Partition validation uses its own ordered phases. A malformed or out-of-range `number` produces the
exact error `Number of partitions must be between 1 and 200.` Partition-reserved parameters are
reported as unsupported without first parsing their values, using the exact error
`The '{parameter}' parameter is not supported by the partitions endpoint.` If several reserved
parameters are present, report them in canonical order `pageToken`, `pageSize`, `limit`, `offset`,
`totalCount`. The syntax/range phase for `number` takes precedence over the unsupported parameter
phase. This distinction is intentional: cursor validation reproduces ODS's short-circuiting
single-error control flow, while partition validation reports every unsupported reserved parameter
in deterministic order after the higher-priority `number` phase passes.

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

After a non-empty selected keyset, the next token uses `HighestSelectedDocumentId + 1` and retains
the request's maximum bound. If the highest selected id is `Int64.MaxValue`, omit the header
instead of overflowing. Tokens are not signed, encrypted, or bound to a resource, filter set,
client, tenant, or database. Changing a range cannot bypass the independently compiled filters
and authorization predicates, and tokens are not promised to be portable between data stores.

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
`/deletes` and `/keyChanges` do not acquire cursor behavior or reserve cursor parameter names.
Add explicit query-parameter roles for cursor bounds and size and for partition count/minimum
size; do not overload the existing offset/limit roles.

Extend the existing `PageDocumentIdQuerySpec` and shared `PageDocumentIdSqlCompiler` rather than
introducing a parallel candidate abstraction. The spec already carries the root relation, value
and live change-version predicates, unified-alias rewrites, row-level authorization
specification, paging parameter names, and deterministic compiler inputs, and both the
regular-resource and descriptor planners already construct it. Add explicit cursor-bound/page-size
and partition-count/minimum-size parameter roles and expose the same unpaged candidate relation
to the partition compiler. The regular-resource builder continues to root on the resource table,
while the descriptor builder continues to root on `dms.Descriptor` with its mandatory
`ResourceKeyId` predicate.

The residual Core-side work is to share resource-filter and live change-version parsing between
GET-many and `/partitions` so candidate behavior cannot drift before it reaches SQL. Add an
explicit one-row-per-`DocumentId` assertion for each consumer and supported authorization
strategy.

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

Cursor mode never compiles or runs total-count SQL. Existing traditional page-selection SQL must
remain behaviorally and textually unchanged. This textual gate does not cover the collection
hydration-batch change required to expose selected keys; traditional response behavior remains
unchanged.

Materialize a regular-resource collection page keyset once, as the current hydration batch does.
For `PageKeysetSpec.Query`, surface the inserted ids as the first batch result set with PostgreSQL
`RETURNING "DocumentId"` and SQL Server `OUTPUT INSERTED.[DocumentId]`; `HydrationExecutor`
calculates their maximum without depending on row order. `PageKeysetSpec.Single` GET-by-id
hydration retains its existing batch shape and does not gain this result set. Carry the nullable
`HighestSelectedDocumentId` through `HydratedPage` and `QuerySuccess` so Core can create
`Next-Page-Token`. This returns at most `MaximumPageSize` bigint values and adds no second
candidate selection, database command, transaction, or roundtrip. It is more robust than deriving
the boundary only from hydrated document metadata because any or all selected rows could be
concurrently deleted before hydration. Descriptor query rows already carry `DocumentId`; the
descriptor handler takes their maximum.

`HighestSelectedDocumentId` is null when page selection is skipped or the selected keyset is
empty, including authorization/preprocessing/planner early-empty paths and zero-size pages. An
empty response array alone cannot distinguish those cases from concurrent deletion after
selection. Core emits the token whenever `HighestSelectedDocumentId` is present, regardless of
the final response-body count, except for the `Int64.MaxValue` overflow case.

### Partition planning

Compile the already filtered and authorized candidate relation into one provider-specific SQL
statement that:

1. orders unique candidates by `DocumentId`;
2. derives row number and candidate count;
3. computes the partition size; and
4. returns only the starting `DocumentId` values.

Compute the mathematical ceiling with provider-appropriate arithmetic; its algebraic spelling is
not contractual. ODS 7.3.2 spells the size as `CEILING(CountOfRows / @numberOfPartitions)` where
both operands are integers, so its `CEILING` receives an already-truncated integer quotient and is
a no-op: ODS effectively floors the partition size. When the division is inexact and the computed
size exceeds the minimum, ODS therefore returns one more token than `number`. DMS keeps the true
ceiling and returns at most `number` tokens, which matches the client guide, which only ever
promises fewer partitions than requested, and the epic's policy that the documented contract is
normative over incidental ODS implementation behavior. PostgreSQL has this illustrative logical
shape:

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
            CEIL(candidate_count::numeric / @number),
            @minimumPartitionSize
        ) AS partition_size
    FROM ranked
)
SELECT "DocumentId"
FROM sized
WHERE (row_number - 1) % partition_size = 0
ORDER BY "DocumentId";
```

SQL Server uses the equivalent CTE with `COUNT_BIG`, `ROW_NUMBER`, and a provider-appropriate
ceiling expression. The database returns starting ids only. Backend code converts each non-final
start to the inclusive range `start..nextStart-1` and the final start to
`start..Int64.MaxValue`; Core token-encodes those typed ranges.

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
  through `QueryDocuments`. Query success carries the nullable selected keyset maximum, and
  partition success carries typed ranges. SQL planners and executors never parse token strings.
- **Candidate planning:** extend `PageDocumentIdQuerySpec` and `PageDocumentIdSqlCompiler` with
  cursor and partition roles, share Core filter/change-version parsing, and assert that traditional
  pages, cursor pages, and partition boundaries consume one unique candidate row per document.

### OpenAPI assembly

The current ApiSchema base documents already contain `pageToken`, `pageSize`, and
`numberOfPartitions` components, while resource fragments omit the cursor parameter references,
`Next-Page-Token` header, and partition paths. Add these platform-wide operations during DMS
OpenAPI assembly after all core, abstract, and extension fragments are merged but before domain
and profile filtering.

For every eligible core-resource, extension-resource, and descriptor collection, publish the
following metadata only with its corresponding completed runtime execution:

- append `pageToken` and `pageSize` parameter references to the collection GET only after E20-S04
  has activated both regular-resource and descriptor cursor execution;
- document `Next-Page-Token` as a string header on its HTTP 200 response under the same cursor
  execution gate;
- add a sibling `/partitions` GET operation only when the E20-S06 runtime partition pipeline is
  activated; the path must not be published ahead of the implementation and there is no interim
  feature toggle;
- copy resource filters, live change-version filters, security, tags, and domain metadata from
  the collection operation, but do not copy traditional/cursor paging or `totalCount` parameters;
- use a reusable HTTP 200 `application/json` schema containing `pageTokens: string[]`;
- generate the `operationId` by appending `Partitions` to the collection GET `operationId`, for
  example `getStudentsPartitions` and `get_TPDMCandidatesPartitions`; and
- provide a partition-specific summary and description rather than copying the collection GET
  text.

There is no advance-publication interval or interim feature toggle: cursor parameters and the
response header must not be published before clients can use them, just as `/partitions` paths
must not be published before the runtime route is active. Because regular-resource and descriptor
cursor execution now land together in E20-S04, the cursor parameter and response-header gate is a
single predecessor rather than two.

Do not augment item-by-id, change-query, discovery, or management paths, and do not introduce
composite paths. Publish the runtime `MaximumPageSize`, initially `500`, as both the default and
maximum for the existing `limit` parameter and the new `pageSize` parameter. This replaces the
authoritative fixture's current published default of `25` and fixed `limit` maximum of `500` with
the runtime value consistently in assembled DMS documents. Publish `DefaultPartitionCount` as the
`numberOfPartitions` default.

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
E20-S09 must add a repeatable script/configuration/result format or explicitly integrate and pin
the external Suite-3 performance runner, then capture the traditional-paging baseline before
E20-S02 modifies the shared page-selection compiler. E20-S02 preserves traditional page-selection
SQL behaviorally and textually, so the baseline is regression insurance over that shared compiler
rather than a record of an expected change: it is the evidence that traditional SQL and latency did
not move. Because E20-S04 depends on E20-S02, the baseline also lands before the first change that
does alter shared traditional runtime execution, E20-S04's selected-id result set in the collection
hydration batch. E20-S09 has no E20 predecessor and can be delivered while E20-S00a and E20-S00b
are in progress.

The pre-change E20-S09 baseline is deliberately limited to the three traditional offset
scenarios used by the gates: offset 0, a one-page shallow offset, and a recorded deep offset, for
page sizes 25 and 500 on both providers. It records commit/environment identity, p50/p95, command
count, returned rows, reads or buffers, database CPU/time, and plans using the same primary fixture
that E20-S10 reuses, so baseline and final-gate results are directly comparable. It does not
provision the authorized, filtered, or descriptor variants and does not run cursor or partition
scenarios.

After E20-S02 through E20-S08b are complete, E20-S10 uses the E20-S09 harness and baseline to run
the final matrix and evaluate the acceptance gates. The fixture set is deliberately narrow: one
primary regular-resource fixture, reused for the authorized and filtered variants rather than
provisioned a second time, plus a small descriptor set and a smoke set.

- 10,000 candidates for smoke and setup validation;
- one primary fixture of 500,000 accessible regular-resource candidates with at least 10%
  `DocumentId` gaps. That count is the smallest at which `number=200` still yields all 200 tokens,
  because the five-maximum-page minimum partition size is `500 * 5 = 2500` and
  `ceiling(500000 / 200)` is exactly `2500`;
- the same primary fixture read by a second principal that can access approximately half of it,
  giving the representative row-level authorization variant without a second data load;
- one filtered variant of the primary fixture at approximately 10% selectivity; and
- 25,000 descriptors split across accessible and inaccessible namespaces.

Measure page sizes 25 and 500 at the first, middle, and last cursor ranges. Compare offset 0, a
one-page shallow offset, and a recorded deep offset. Measure partition counts 1, 10, and 200 on the
unfiltered primary fixture, and `number=10` on the filtered and authorized variants. Iteration
counts are not reduced, because iterations cost seconds while fixture provisioning dominates the
run and a stable p95 depends on them: each scenario has at least five warmups and 30 measured
warm-cache iterations on a pinned environment. Record p50, p95, command count, returned
rows/tokens, logical reads or buffers, database CPU/time, and the execution plan.

Acceptance gates are:

- cursor SQL contains no `OFFSET`, row-number skip, or count query and uses the root
  `DocumentId` key as a range predicate;
- existing `limit`/`offset` page-selection SQL remains behaviorally and textually unchanged; the
  expected selected-id result set in collection hydration batches is outside this textual SQL
  gate;
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

## Bounded Telemetry

E20-S12 adds production telemetry independently of the measurement matrix. Record paging mode,
requested/returned page size, requested/returned partition count, duration, provider, command
category, and success/failure with bounded dimensions. Never record raw token text, decoded
bounds, filter names or values, client identity, or candidate identifiers.

## Test Expectations

- Unit tests cover token round trips, omitted maximum, padded/unpadded input, forbidden alphabet
  and padding forms, invalid UTF-8, extra fields, decimal grammar, `Int64` bounds, terminal
  inverted ranges, and overflow handling.
- Validation tests cover every query-parameter combination, ODS-compatible cursor precedence,
  exactly one cursor error, partition phase gating and unsupported-parameter ordering, exact
  messages and ProblemDetails shells, repeated-parameter last-value-wins behavior, and case-variant
  canonicalization without an exception.
- Routing and handler tests cover typed collection/by-id/partition classification, the dedicated
  pipeline order, selected-keyset-empty/body-empty-after-selection/zero-size/`Int64.MaxValue`
  header behavior, cursor parameters on `/deletes` and `/keyChanges`, and startup configuration
  validation.
- SQL compiler/golden tests cover traditional, cursor, and partition SQL for PostgreSQL and SQL
  Server, including explicit parameter roles, SQL Server `TOP`, absence of offset/count SQL in
  cursor mode, partition-sizing semantics, and identifiers-only output.
- Hydration tests cover PostgreSQL `RETURNING` and SQL Server `OUTPUT`, the batch result-set
  sequence without assuming selected-id row order, the nullable selected-keyset maximum, unchanged
  GET-by-id result sets, and all selected rows deleted before hydration.
- Backend integration tests cover regular resources and descriptors, page sizes 0/1/max,
  multiple pages, partition boundaries, a returned token count that never exceeds the requested
  `number`, sparse ids, empty sets, filtered queries, change-version ranges, concurrent
  insert/delete behavior, and identical boundaries for equivalently seeded PostgreSQL and real
  SQL Server 2025 databases.
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
- E20-S08b owns the ODS-comparison case definitions as static expected values derived from the
  worked precedence table above and the approved-difference list below, both of which were
  established by reading the pinned ODS 7.3.2 sources cited in `Compatibility Baseline`. No live ODS
  reference deployment is in scope. Any additional difference must be recorded here before
  implementation is accepted.

### Approved Intentional ODS Differences

The approved intentional ODS differences are:

- reject `limit` whenever cursor parameters are present, including when `pageSize` is also present;
- reject `totalCount=true` when a valid `pageToken` is present;
- treat query-key presence as significant for cursor parameters and apply the DMS validator to
  blank and non-numeric values, where ODS's `int?` model binding reads a blank value as absent and
  returns HTTP 200, and fails binding on a non-numeric value before its validator can emit a range
  message;
- reject `pageToken` and `pageSize` on `/deletes` and `/keyChanges` under DMS's
  unknown-query-field rule, where ODS binds the same request model on those endpoints, accepts a
  valid token, and answers a malformed one with `The page token provided was invalid.`;
- reject `limit`, `offset`, `pageToken`, `pageSize`, and `totalCount` on `/partitions`, where ODS
  7.3.2 validates only `number` and otherwise passes these through as additional parameters;
- reject ODS's undocumented `allowSmallPartitions` and `useJoinAuth` partition pass-through
  parameters under DMS's unknown-query-field rule;
- return at most the requested `number` of partition tokens by computing a true ceiling, where ODS
  divides two integer operands and then applies `CEILING` to the already-truncated quotient, so ODS
  effectively floors the partition size and returns one more token than `number` whenever the
  division is inexact and the computed size exceeds the minimum;
- gate `Next-Page-Token` on a non-null `HighestSelectedDocumentId`, including when concurrent
  deletion leaves an empty hydrated body, while ODS gates the header on hydrated body count;
- retain DMS's existing `Offset must be a numeric value greater than or equal to 0.` text rather
  than ODS's `Offset cannot be a negative value.`;
- retain DMS's existing `Limit must be omitted or set to a numeric value between 0 and {N}.` text
  rather than ODS's `Limit must be a value between 0 and {N}.`;
- use DMS's configured `MaximumPageSize`, initially `500`, as the value applied when `limit` or
  `pageSize` is omitted, where Ed-Fi's published default is `25`;
- publish that same runtime `MaximumPageSize` as both the default and maximum for `limit` and
  `pageSize`, replacing the authoritative fixture's hardcoded `limit` default of `25` with maximum
  `500` and its `pageSize` default of `25` with no maximum. ODS's runtime maximum for both
  parameters is likewise a configurable setting already defaulting to `500`, so this difference is
  confined to published metadata rather than runtime enforcement;
- publish the runtime `DefaultPartitionCount` as the OpenAPI `numberOfPartitions` default, which
  Ed-Fi's published metadata omits entirely. ODS's default partition count is likewise a
  configurable setting defaulting to `10`, so DMS matches ODS runtime behavior here and differs only
  in the published metadata;
- use DMS `Int64 DocumentId` bounds rather than ODS `Int32 AggregateId` bounds;
- omit the next header rather than overflowing at `Int64.MaxValue`; and
- use the stricter approved base64url and decimal decoder contract.

## Likely Affected Areas

- `Core.External`: paging/range, query-result, and partition repository contracts.
- `Core`: `UtilityService`, `PathComponents`, `RequestInfo`, path/query validation middleware,
  `ApiService`, query/partition handlers, token codec, configuration, and response headers.
- `Frontend.AspNetCore`: query-parameter canonicalization, option registration, and default
  configuration.
- `Backend.Plans` and plan contracts: the existing `PageDocumentIdQuerySpec` and
  `PageDocumentIdSqlCompiler`, partition compilation, parameter roles, hydration batch output, and
  executor result contracts.
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
- The authoritative OpenAPI paging defaults and fixed `limit` maximum can conflict with approved
  runtime behavior; assembled documents must expose `MaximumPageSize` consistently as both the
  default and maximum across resource and descriptor specifications.
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

These provisional allocation files are approved for decomposition before Jira creation. They
allocate ownership and evidence while this epic remains authoritative for every shared contract.
Their `E20-S00a` through `E20-S12` identifiers are stable planning identifiers, not Jira keys, and
they are intentionally non-contiguous as described in `Status`.

1. **[E20-S00a: Cursor contract primitives](00a-cursor-contract-primitives.md)** — typed
   paging/range models, token codec, nullable-boundary and partition-result contract shapes,
   configuration and startup validation, and focused unit tests.
2. **[E20-S00b: Request validation and typed paths](00b-cursor-and-partition-validation.md)** —
   ODS-precedence single-error cursor validation, phase-gated partition validation, the
   ProblemDetails shell, operation-scoped rejection on `/deletes` and `/keyChanges`, typed
   collection/by-id/partition path operations, and parameter canonicalization.
3. **[E20-S02: Candidate planning and provider cursor SQL](02-shared-candidate-planning.md)** —
   extend the shared page-document-id spec/compiler, share Core filter validation, add parameter
   roles, assert candidate uniqueness, and compile PostgreSQL and SQL Server cursor SQL with
   goldens preserving traditional page-selection SQL.
4. **[E20-S04: Cursor execution](04-regular-resource-cursor-execution.md)** — regular-resource
   hydration keyset `RETURNING`/`OUTPUT`, descriptor boundary propagation, nullable selected
   maximum, `QuerySuccess`, the response header, and both-provider integration tests.
5. **[E20-S06: Partition pipeline and SQL](06-partition-pipeline-and-sql.md)** — route exposure,
   dedicated Core/backend contracts, regular and descriptor boundary planning, both provider
   compilers, validation, and integration tests.
6. **[E20-S07: OpenAPI and client contract](07-openapi-and-client-contract.md)** — platform-wide
   resource/extension/descriptor augmentation, profile association, `operationId` values,
   summaries/descriptions, runtime defaults, and snapshots, plus the client-facing documentation
   update, which is a separable delivery slice within this story.
7. **[E20-S08a: Cursor and partition authorization matrix](08a-authorization-matrix.md)** —
   cross-strategy accessible-set agreement between cursor walks and partition boundaries plus
   forged-range negative cases.
8. **[E20-S08b: Public contract, parity, and E2E suite](08b-public-contract-parity-and-e2e.md)** —
   public parameter/header/body coverage, route/tenant/profile/extension/descriptor coverage,
   terminal and parallel walks, concurrency scenarios, and the static ODS-comparison cases.
9. **[E20-S09: Performance harness and traditional baseline](09-performance-harness-and-baseline.md)** —
   reproducible cross-provider harness and the three pre-change offset baseline scenarios.
10. **[E20-S10: Performance final gate](10-performance-and-observability-final-gate.md)** — the
    narrow reused fixture set, full provider-plan evidence, thresholds, and regression reporting.
11. **[E20-S12: Bounded cursor and partition telemetry](12-bounded-cursor-and-partition-telemetry.md)** —
    production paging metrics with bounded dimensions and explicit privacy constraints.

E20-S00b follows E20-S00a because its validators consume the typed contracts and token codec, and it
owns the request boundary end to end: the query-key presence its phase selection depends on is
produced by its own parameter canonicalization. E20-S09 has no E20 predecessor and must complete
before E20-S02 modifies the shared page-selection compiler, so the baseline stands as regression
insurance that traditional page-selection SQL and latency did not move; that dependency also places
the baseline before the downstream E20-S04 shared execution change. E20-S04 and E20-S06 consume the
shared page-document-id plan and the compiled cursor SQL. E20-S06 boundary compilation may proceed
from E20-S00a through E20-S02, but route activation additionally requires E20-S04, so `/partitions`
cannot hand out tokens before both regular-resource and descriptor GET-many can consume them.
E20-S07 publishes no cursor parameter, response header, or partition path until E20-S04 and E20-S06
provide the corresponding runtime behavior. E20-S08a and E20-S08b both consume E20-S04 and E20-S06,
E20-S08b additionally consumes E20-S07, and the two may proceed in parallel. E20-S10 runs after
E20-S02 through E20-S09, while E20-S12 may proceed independently after E20-S04 and E20-S06.

## Completion Evidence

- All eligible GET-many OpenAPI operations expose the cursor parameters and response header, and
  all eligible collections expose `/partitions`.
- Sequential and parallel cursor walks return every member of a stable filtered/authorized fixture
  exactly once across both providers, including descriptors.
- Invalid combinations and tokens return the approved ODS-compatible 400 contract.
- Cursor pages satisfy the SQL-shape, roundtrip, plan, and latency gates above without regressing
  traditional paging.
- The ODS/DMS parity fixture and the supported unit, provider integration, and E2E suites pass.
