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

## Design References

- [Partitioned cursor paging](../../design-docs/partitioned-cursor-paging.md)

The linked design document owns the normative cursor-paging, token, partition, relational, OpenAPI,
and configuration contracts. This epic partitions implementation work, and its stories own the
executable acceptance evidence for those contracts.

## Status

This epic is the design output of `DMS-1349`. Its eleven work packages were approved and then filed
as `DMS-1348` children `DMS-1383` through `DMS-1393`; each work-package file now carries its own
Jira key in frontmatter, and `JIRA-INDEX.md` records the mapping. Each story's issue description is
the work-package file converted to Jira wiki markup, so the file remains the editable source and any
change to it must be pushed to the corresponding issue. `DMS-1348` itself carries no description,
because this file is longer than Jira's 32,767-character description limit. The source spike is
currently targeted to Ed-Fi API v8.1.

The eleven packages are the result of consolidation during design: typed path operations, provider
cursor SQL, and descriptor cursor execution were each folded into a sibling package, and a separate
ODS reference-deployment package was dropped in favor of the static comparison cases owned by
`DMS-1390`. The file-name number prefixes are left at their pre-consolidation values and are
therefore not contiguous; they carry no meaning beyond ordering.

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

Emitting `Next-Page-Token` on an ordinary `limit`/`offset` response extends the published Ed-Fi
surface, because the client guide does not describe the header for traditional responses and the
authoritative collection fixtures do not define it as a response header. It nevertheless matches
ODS 7.3.2 runtime behavior, whose own generated OpenAPI also publishes the header as a described
HTTP 200 response header on the non-composite collection GET, and gives clients a cursor-walk entry
point.

## Public API Contract

The design doc owns the normative public surface:
[Public API Contract](../../design-docs/partitioned-cursor-paging.md#public-api-contract), including
[GET-many cursor paging](../../design-docs/partitioned-cursor-paging.md#get-many-cursor-paging),
[query-parameter name canonicalization](../../design-docs/partitioned-cursor-paging.md#query-parameter-name-canonicalization),
[cursor validation and ProblemDetails](../../design-docs/partitioned-cursor-paging.md#cursor-validation-and-problemdetails),
[operation scoping](../../design-docs/partitioned-cursor-paging.md#operation-scoping), and
[`/partitions`](../../design-docs/partitioned-cursor-paging.md#partitions) with its
[partition validation](../../design-docs/partitioned-cursor-paging.md#partition-validation) and
[partition sizing](../../design-docs/partitioned-cursor-paging.md#partition-sizing) rules.

## ODS Precedence Comparison

The expected DMS messages below are the design doc's
[worked precedence examples](../../design-docs/partitioned-cursor-paging.md#worked-precedence-examples).
The third column is unique to this epic: it records how ODS 7.3.2 answers the same request, as
established by reading the pinned sources cited in `Compatibility Baseline`. `DMS-1390` turns this
column into static comparison cases.

DMS returns exactly one error per rejected cursor request, which matches ODS's one-element
validation-error response rather than accumulating every applicable cursor message. Partition
validation deliberately differs: it reports every unsupported reserved parameter in deterministic
order once the higher-priority `number` phase passes. Giving `number` the highest priority matches
ODS, which range-checks `number` before it constructs its query parameters and therefore before it
decodes a supplied `pageToken`.

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
| `?pageToken=X&totalCount=true` | `The totalCount parameter cannot be set to true when using cursor paging with pageToken.` | Both reject with HTTP 400, but ODS rejects later and with different text. Its cursor validator allows the combination; its repository builds the count template, finds a `MinAggregateId` parameter in it, and throws `Total count cannot be determined while using cursor paging (when pageToken is specified).` DMS rejects during parameter validation. |

## Cursor Token Contract

The design doc owns the token encoding, decoder strictness, range semantics, security properties,
and layering rules:
[Cursor Token Contract](../../design-docs/partitioned-cursor-paging.md#cursor-token-contract).

## Relational Design

The design doc owns the paging-mode choice, the shared candidate relation and its
one-row-per-`DocumentId` invariant, both providers' cursor SQL, the selected-keyset boundary, and
the partition-planning statement:
[Relational Design](../../design-docs/partitioned-cursor-paging.md#relational-design), covering
[paging-mode choice](../../design-docs/partitioned-cursor-paging.md#paging-mode-choice),
[shared candidate relation](../../design-docs/partitioned-cursor-paging.md#shared-candidate-relation),
[provider cursor SQL](../../design-docs/partitioned-cursor-paging.md#provider-cursor-sql),
[carrying the selected-keyset boundary](../../design-docs/partitioned-cursor-paging.md#carrying-the-selected-keyset-boundary),
and [partition planning](../../design-docs/partitioned-cursor-paging.md#partition-planning).

## Application Boundaries

The design doc owns the frontend/routing, Core model, Core pipeline, backend-contract, and
candidate-planning boundaries, and the configuration settings and their startup validation:
[Application Boundaries](../../design-docs/partitioned-cursor-paging.md#application-boundaries) and
[Configuration](../../design-docs/partitioned-cursor-paging.md#configuration).

## OpenAPI Publication Gating

The design doc owns the augmentation rules, published runtime defaults, and profile filtering:
[OpenAPI Assembly](../../design-docs/partitioned-cursor-paging.md#openapi-assembly). It states
"Publish only what runs" story-free. This epic supplies the corresponding delivery sequencing:

- Append `pageToken` and `pageSize` parameter references to the collection GET, and document
  `Next-Page-Token` as a string header on its HTTP 200 response, only after DMS-1386 has activated
  both regular-resource and descriptor cursor execution.
- Add the sibling `/partitions` GET operation only when the DMS-1387 runtime partition pipeline is
  activated. The path must not be published ahead of the implementation, and there is no interim
  feature toggle.
- Because regular-resource and descriptor cursor execution land together in DMS-1386, the cursor
  parameter and response-header gate is a single predecessor rather than two.

## Consistency Under Writes

Cursor paging is not a snapshot protocol, matching ODS behavior. The design doc owns the enumerated
write-interaction behavior, the non-final partition upper bound rationale, and the relationship to
change-query extraction:
[Consistency Under Writes](../../design-docs/partitioned-cursor-paging.md#consistency-under-writes).

## Performance Invariants and Evidence

Implementation is incomplete without reproducible PostgreSQL and real SQL Server evidence.
DMS-1391 must add a repeatable script/configuration/result format or explicitly integrate and pin
the external Suite-3 performance runner, then capture the traditional-paging baseline before
DMS-1385 modifies the shared page-selection compiler. DMS-1385 preserves traditional page-selection
SQL behaviorally and textually, so the baseline is regression insurance over that shared compiler
rather than a record of an expected change: it is the evidence that traditional SQL and latency did
not move. Because DMS-1386 depends on DMS-1385, the baseline also lands before the first change that
does alter shared traditional runtime execution, DMS-1386's selected-id result set in the collection
hydration batch. DMS-1391 has no predecessor in this epic and can be delivered while DMS-1383 and
DMS-1384 are in progress.

The pre-change DMS-1391 baseline is deliberately limited to the three traditional offset
scenarios used by the gates: offset 0, a one-page shallow offset, and a recorded deep offset, for
page sizes 25 and 500 on both providers. It records commit/environment identity, p50/p95, command
count, returned rows, reads or buffers, database CPU/time, and plans using the same primary fixture
that DMS-1392 reuses, so baseline and final-gate results are directly comparable. It does not
provision the authorized, filtered, or descriptor variants and does not run cursor or partition
scenarios.

After DMS-1385 through DMS-1390 are complete, DMS-1392 uses the DMS-1391 harness and baseline to run
the final matrix and evaluate the acceptance gates. The fixture set is deliberately narrow: one
primary regular-resource fixture, reused for the authorized and filtered variants rather than
provisioned a second time, plus a small descriptor set and a smoke set.

- 10,000 candidates for smoke and setup validation;
- one primary fixture of 500,000 accessible regular-resource candidates with at least 10%
  `DocumentId` gaps. That count is where a requested `number=200` and the five-maximum-page minimum
  partition size coincide exactly: the minimum is `500 * 5 = 2500`, and `ceiling(500000 / 200)` is
  also exactly `2500`. It therefore exercises `number=200` at the boundary where the minimum stops
  binding, with no headroom to hide a sizing error;
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

DMS-1393 adds production telemetry independently of the measurement matrix. The recorded dimensions
and the never-record list are owned by
[Bounded Telemetry](../../design-docs/partitioned-cursor-paging.md#bounded-telemetry).

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
- DMS-1390 owns the ODS-comparison case definitions as static expected values derived from the
  `ODS Precedence Comparison` table above and the approved-difference list below, both of which were
  established by reading the pinned ODS 7.3.2 sources cited in `Compatibility Baseline`. No live ODS
  reference deployment is in scope. Any additional difference must be recorded here before
  implementation is accepted.

### Approved Intentional ODS Differences

The approved intentional ODS differences are:

- reject `limit` whenever cursor parameters are present, including when `pageSize` is also present;
- reject `totalCount=true` when a valid `pageToken` is present during parameter validation, with
  DMS's `The totalCount parameter cannot be set to true when using cursor paging with pageToken.`
  text. ODS 7.3.2 also rejects that combination with HTTP 400, but only after its repository builds
  the count template and finds a `MinAggregateId` parameter in it, and it reports
  `Total count cannot be determined while using cursor paging (when pageToken is specified).`;
- treat query-key presence as significant for cursor parameters and apply the DMS validator to
  blank and non-numeric values, where ODS's `int?` model binding reads a blank value as absent and
  returns HTTP 200, and fails binding on a non-numeric value before its validator can emit a range
  message;
- return `Number of partitions must be between 1 and 200.` for a non-numeric
  `/partitions?number=abc`, where ODS's `[FromQuery] int? number` binding fails before its
  controller body runs. `PartitionsController` is an `[ApiController]`, and
  `ApiBehaviorOptionsConfigurator`
  supplies an `InvalidModelStateResponseFactory` without setting `SuppressModelStateInvalidFilter`,
  so automatic model-state validation answers with an `ErrorTranslator` ProblemDetails shell built
  from `ModelState`, and the controller's own range check — which is what produces ODS's otherwise
  identical `Number of partitions must be between 1 and 200.` text — never executes. This is the
  partition-side form of the cursor `int?` binding difference above, with a different endpoint and a
  different message;
- return that same range error for a present-but-blank `/partitions?number=`, treating the present
  query key as a malformed partition count, where ODS's nullable-int model binding reads an empty
  value as absent, so `number` arrives null, its range check does not trigger for null, and the
  request succeeds with the configured default partition count. The empty-value-binds-to-null step
  rests on ASP.NET Core's standard binding of an empty value to a nullable simple type, the same
  basis as the recorded blank-`pageSize` behavior above;
- reject `pageToken` and `pageSize` on `/deletes` and `/keyChanges` under DMS's
  unknown-query-field rule, where ODS binds the same request model on those endpoints, accepts a
  valid token, and answers a malformed one with `The page token provided was invalid.`;
- reject all five of `limit`, `offset`, `pageToken`, `pageSize`, and `totalCount` on `/partitions`
  as unsupported, where ODS 7.3.2 binds those reserved names into the request model its partitions
  action declares, range-checks only `number`, and ignores `limit`, `offset`, `pageSize`, and
  `totalCount`: they never reach its partition query, which carries no paging clause. ODS decodes a
  supplied `pageToken`, answering a malformed one with `The page token provided was invalid.`, then
  discards the decoded range, so a well-formed token does not move its partition boundaries;
- reject ODS's undocumented `allowSmallPartitions` and `useJoinAuth` partition parameters under
  DMS's unknown-query-field rule, where ODS reads them from the separate additional-parameters
  dictionary that collects query parameters its request model does not define;
- return evenly sized partitions, and at most the requested count of them, by computing a true
  ceiling. ODS 7.3.2 spells the size as `CEILING(CountOfRows / @numberOfPartitions)` over two
  integer operands, so its `CEILING` receives an already-truncated integer quotient and is a no-op:
  ODS effectively floors the partition size and can compute one more starting id than the count it
  sized for. When `number` is supplied, ODS still returns at most `number` tokens, because its token
  loop emits a final token spanning the second-to-last starting id through `int.MaxValue` and stops;
  the surplus partition is absorbed into a final range that can be roughly twice as wide as the
  others rather than handed out as an extra token. When `number` is omitted, both of that loop's cap
  expressions read the nullable request value and no cap applies, while the sizing SQL used the
  configured default partition count, so only in that default case can ODS return one more token
  than its configured `DefaultPartitionCount`. DMS honors its at-most-count promise for the default
  count as well, and that promise matches the client guide, which only ever promises fewer
  partitions than requested;
- route `/partitions` through the same profile content-type outcome as GET-many, including the
  HTTP 405 profile method-usage response, where ODS 7.3.2's partitions controller carries no
  assigned-profile-usage enforcement filter and so does not enforce assigned profile usage on that
  endpoint, unlike its data-management controller base, whose GET-many, GET-by-id, PUT, and POST
  actions each apply that filter;
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
- omit the `/partitions` path from a profile document for a resource the profile includes as
  write-only, where ODS 7.3.2 publishes it: its paths factory keeps every resource that is readable
  or writable, then adds the partition path for each non-composite resource through a path method
  that has no `Readable` guard, unlike the sibling collection and by-id path methods, which gate
  their read operations on it;
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

The design-property risks are owned by
[Risks and Guardrails](../../design-docs/partitioned-cursor-paging.md#risks-and-guardrails):
duplicate-producing authorization joins, hydration batch ordering, SQL Server plan behavior,
partition query spill, conflicting OpenAPI defaults, profile association, and the edge conditions
that are valid rather than server errors.

This epic adds one delivery guardrail:

- The eleven children exist as filed stories only. Filing them authorized no production code; each
  story is picked up on its own, and the design doc remains the normative contract it implements.

## Non-Goals

The non-goals are owned by
[Requirements and Non-Goals](../../design-docs/partitioned-cursor-paging.md#requirements-and-non-goals).

## Proposed Work Packages

These files allocate implementation ownership and acceptance evidence for the contracts the design
doc owns. Each is filed as the Jira story whose key heads its entry below, and that key is how the
package is referred to everywhere else in this epic.

1. **[DMS-1383: Cursor contract primitives](00a-cursor-contract-primitives.md)** — typed
   paging/range models, token codec, nullable-boundary and partition-result contract shapes,
   configuration and startup validation, and focused unit tests.
2. **[DMS-1384: Request validation and typed paths](00b-cursor-and-partition-validation.md)** —
   ODS-precedence single-error cursor validation, phase-gated partition validation, the
   ProblemDetails shell, operation-scoped rejection on `/deletes` and `/keyChanges`, typed
   collection/by-id/partition path operations, and parameter canonicalization.
3. **[DMS-1385: Candidate planning and provider cursor SQL](02-shared-candidate-planning.md)** —
   extend the shared page-document-id spec/compiler, share Core filter validation, add parameter
   roles, assert candidate uniqueness, and compile PostgreSQL and SQL Server cursor SQL with
   goldens preserving traditional page-selection SQL.
4. **[DMS-1386: Cursor execution](04-regular-resource-cursor-execution.md)** — regular-resource
   hydration keyset `RETURNING`/`OUTPUT`, descriptor boundary propagation, nullable selected
   maximum, `QuerySuccess`, the response header, and both-provider integration tests.
5. **[DMS-1387: Partition pipeline and SQL](06-partition-pipeline-and-sql.md)** — route exposure,
   dedicated Core/backend contracts, regular and descriptor boundary planning, both provider
   compilers, validation, and integration tests.
6. **[DMS-1388: OpenAPI and client contract](07-openapi-and-client-contract.md)** — platform-wide
   resource/extension/descriptor augmentation, profile association, `operationId` values,
   summaries/descriptions, runtime defaults, and snapshots, plus the client-facing documentation
   update, which is a separable delivery slice within this story.
7. **[DMS-1389: Cursor and partition authorization matrix](08a-authorization-matrix.md)** —
   cross-strategy accessible-set agreement between cursor walks and partition boundaries plus
   forged-range negative cases.
8. **[DMS-1390: Public contract, parity, and E2E suite](08b-public-contract-parity-and-e2e.md)** —
   public parameter/header/body coverage, route/tenant/profile/extension/descriptor coverage,
   terminal and parallel walks, concurrency scenarios, and the static ODS-comparison cases.
9. **[DMS-1391: Performance harness and traditional baseline](09-performance-harness-and-baseline.md)** —
   reproducible cross-provider harness and the three pre-change offset baseline scenarios.
10. **[DMS-1392: Performance final gate](10-performance-and-observability-final-gate.md)** — the
    narrow reused fixture set, full provider-plan evidence, thresholds, and regression reporting.
11. **[DMS-1393: Bounded cursor and partition telemetry](12-bounded-cursor-and-partition-telemetry.md)** —
    production paging metrics with bounded dimensions and explicit privacy constraints.

DMS-1384 follows DMS-1383 because its validators consume the typed contracts and token codec, and it
owns the request boundary end to end: the query-key presence its phase selection depends on is
produced by its own parameter canonicalization. DMS-1391 has no predecessor in this epic and must
complete before DMS-1385 modifies the shared page-selection compiler, so the baseline stands as
regression insurance that traditional page-selection SQL and latency did not move; that dependency
also places the baseline before the downstream DMS-1386 shared execution change. DMS-1386 and
DMS-1387 consume the shared page-document-id plan and the compiled cursor SQL. DMS-1387 boundary
compilation may proceed from DMS-1383 through DMS-1385, but route activation additionally requires
DMS-1386, so `/partitions` cannot hand out tokens before both regular-resource and descriptor
GET-many can consume them. DMS-1388 publishes no cursor parameter, response header, or partition
path until DMS-1386 and DMS-1387 provide the corresponding runtime behavior, as recorded in
`OpenAPI Publication Gating`. DMS-1389 and DMS-1390 both consume DMS-1386 and DMS-1387, DMS-1390
additionally consumes DMS-1388, and the two may proceed in parallel. DMS-1392 runs after DMS-1385
through DMS-1391, while DMS-1393 may proceed independently after DMS-1386 and DMS-1387.

## Completion Evidence

- All eligible GET-many OpenAPI operations expose the cursor parameters and response header, and
  all eligible collections expose `/partitions`.
- Sequential and parallel cursor walks return every member of a stable filtered/authorized fixture
  exactly once across both providers, including descriptors.
- Invalid combinations and tokens return the approved ODS-compatible 400 contract.
- Cursor pages satisfy the SQL-shape, roundtrip, plan, and latency gates above without regressing
  traditional paging.
- The ODS/DMS parity fixture and the supported unit, provider integration, and E2E suites pass.
