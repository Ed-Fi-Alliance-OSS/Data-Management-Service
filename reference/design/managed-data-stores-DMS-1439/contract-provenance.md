# DMS-1439 selected contract and fixture provenance

This records revision 9's approved contract for step **1.1 only**. The accompanying
`conformance-fixtures.json` is reusable expected data, not an implementation. Its
unit tests check fixture integrity and selected source-contract assertions. No
HTTP route, DTO, formatter, comparer, migration, repository or worker is introduced
by this step. Passing these tests does not establish product conformance, provider
parity or completion of any lifecycle acceptance criterion.

The JSON `models` are independent state snapshots, not a sequential transition
trace: their order does not authorize transitions from CreateError to Created or
DeleteError to Deleted. `timestampEvents` records event rules separately. Query
`expectedIds` refer only to the adjacent ordinary `rows` dataset. Naming and
canonical-name vectors are expected outputs for future production tests; no
formatter, normalizer or query evaluator is recreated in fixture tests.

## Approval and source pins

The architectural reviewer's revision-9 approval, relayed into the implementation session, was:

> Revision 9 is approved as an implementation spec, including the proposed ordinary catalog compatibility changes. Proceed one step at a time and stop after each local commit for review, as the spec requires.

That relayed architectural approval supersedes the external document's retained preapproval
status. The user limited the next work to step 1.1; it does not authorize automatic
progression through later steps or a push.

* Approved document: `C:/dev/simpat/specs/DMS-1439/spec.md`, revision 9, active
  sections 1–10; SHA-256
  `0deda56ca66aecffb4e184cb7acc1c4d770ee7dfc3248e07ab47bfb2792098ac`.
  Earlier historical proposals are not governing contracts.
* Inspected DMS baseline: `cc5704e004068251ed03483de8c76adc86513688`.
* [DMS-1439](https://edfi.atlassian.net/browse/DMS-1439) and its checked-in
  [original story](../../spikes/DMS-1334/DMS-1439-managed-data-store-lifecycle.md).
  The original issue and story are unchanged, including their original AC9 wording.
* [Pinned OpenAPI](../jobs-DMS-1437/admin-api-v3-ed115fd8.yaml) and
  [generation provenance](../jobs-DMS-1437/admin-api-v3-provenance.md): upstream
  `Ed-Fi-Alliance-OSS/ODS-Admin-API` commit
  `ed115fd8c0cb3e17dcfefc86966582c74e39f277`; YAML SHA-256
  `f0735ae1c7517b3534f31635fdc387b0665ff28679a98b2008103c7557b9466b`.
  Relevant anchors: `/v3/dataStores/manage` (line 1118), by-ID route (1235),
  `addDataStoreManageRequest` (3612), `dataStoreManageModel` (3982).
  This existing evidence is reused without editing or regenerating it.

The approved spec records inspection of the following upstream source at that
same pin. These links preserve its evidence trail; the generated YAML alone does
not prove runtime timestamps, sorting or tombstone behavior:

| Evidence | Pinned source under `Application/EdFi.Ods.AdminApi.V3` |
| --- | --- |
| Naming | [DataStoreManageDatabaseNameFormatter.cs](https://github.com/Ed-Fi-Alliance-OSS/ODS-Admin-API/blob/ed115fd8c0cb3e17dcfefc86966582c74e39f277/Application/EdFi.Ods.AdminApi.V3/Features/DataStores/Manage/DataStoreManageDatabaseNameFormatter.cs) |
| Projection | [DataStoreManageMapper.cs](https://github.com/Ed-Fi-Alliance-OSS/ODS-Admin-API/blob/ed115fd8c0cb3e17dcfefc86966582c74e39f277/Application/EdFi.Ods.AdminApi.V3/Features/DataStores/Manage/DataStoreManageMapper.cs) |
| List runtime | [ReadDataStoreManage.cs](https://github.com/Ed-Fi-Alliance-OSS/ODS-Admin-API/blob/ed115fd8c0cb3e17dcfefc86966582c74e39f277/Application/EdFi.Ods.AdminApi.V3/Features/DataStores/Manage/ReadDataStoreManage.cs), [GetDataStoreManagesQuery.cs](https://github.com/Ed-Fi-Alliance-OSS/ODS-Admin-API/blob/ed115fd8c0cb3e17dcfefc86966582c74e39f277/Application/EdFi.Ods.AdminApi.V3/Infrastructure/Database/Queries/GetDataStoreManagesQuery.cs) |
| Acceptance timestamps/nulls | [AddDataStoreManageCommand.cs](https://github.com/Ed-Fi-Alliance-OSS/ODS-Admin-API/blob/ed115fd8c0cb3e17dcfefc86966582c74e39f277/Application/EdFi.Ods.AdminApi.V3/Infrastructure/Database/Commands/AddDataStoreManageCommand.cs), [DeleteDataStoreManageCommand.cs](https://github.com/Ed-Fi-Alliance-OSS/ODS-Admin-API/blob/ed115fd8c0cb3e17dcfefc86966582c74e39f277/Application/EdFi.Ods.AdminApi.V3/Infrastructure/Database/Commands/DeleteDataStoreManageCommand.cs) |
| Worker timestamps and retained values | [CreateInstanceJob.cs](https://github.com/Ed-Fi-Alliance-OSS/ODS-Admin-API/blob/ed115fd8c0cb3e17dcfefc86966582c74e39f277/Application/EdFi.Ods.AdminApi.V3/Infrastructure/Services/Jobs/CreateInstanceJob.cs), [DeleteInstanceJob.cs](https://github.com/Ed-Fi-Alliance-OSS/ODS-Admin-API/blob/ed115fd8c0cb3e17dcfefc86966582c74e39f277/Application/EdFi.Ods.AdminApi.V3/Infrastructure/Services/Jobs/DeleteInstanceJob.cs) |
| Deleted remains readable | [GetDataStoreManageByIdQuery.cs](https://github.com/Ed-Fi-Alliance-OSS/ODS-Admin-API/blob/ed115fd8c0cb3e17dcfefc86966582c74e39f277/Application/EdFi.Ods.AdminApi.V3/Infrastructure/Database/Queries/GetDataStoreManageByIdQuery.cs) |

## Q1 — acceptance responses

Keep written AC4/AC8: POST returns **202 with zero body bytes** and an absolute
managed-resource `Location`; DELETE returns **204 with zero body bytes and no
Location**. Build the managed URL from scheme, host/port, PathBase and canonical
route. Acceptance follows the committed aggregate/job transaction. Neither body
contains `jobId` or `message`.

Jira comment 99313 and [ADMINAPI-1523](https://edfi.atlassian.net/browse/ADMINAPI-1523)
at upstream commit `bc4199b50409cbc4b71b5271af24bc1dfbf97518` are conflicting later
evidence, not a replacement pin or permission to change the written contract.

## Q2 — managed query contract

Default offset 0, limit 25, orderBy `id`, direction `Descending`. Pagination binds
as int32; offset must be nonnegative, limit positive, with no smaller maximum.
Omitted or exactly empty orderBy selects `id`; otherwise only the exact nine
camelCase public field names are accepted. Direction is exactly `Ascending` or
`Descending`; supplied empty values and abbreviations are invalid. ID is optional
int32 equality (nonpositive unmatched IDs produce `[]`). Name uses Q5 equality;
empty/whitespace matches no valid managed name. Combine filters and tenant before
paging; invalid values return validation 400.

NULLS LAST in either direction, then requested value order, then Id in the same
direction. Strings use ASCII-folded ordinal comparisons; numbers and UTC timestamps
use their respective orders. No snapshot guarantee across requests. The pinned
runtime's Id-ascending behavior is intentionally not copied: AC5's advertised
sorting governs. Real provider/pipeline tests remain future work.

## Q4 — authorized AC9 clarification

The user's explicit instruction after revision 6 was:

> ok, keep snapshot persistence and cleanup in DMS-1441. update the spec to remove that dependency from 1439 and make the AC9 clarification explicit, then send it back for review. don't implement yet.

The selected wording is:

> Delete execution transitions through DeleteInProgress, verifies and drops the DMS-1438-owned physical target, then atomically removes the linked ordinary catalog row, retains the management tombstone, and marks Deleted in a single CMS transaction. Education-organization snapshot persistence and cleanup on ordinary or managed catalog deletion are delivered and verified by DMS-1441.

Original AC9 said “atomically removes its snapshot and linked ordinary catalog
row.” Its snapshot portion is explicitly assigned to DMS-1441. DMS-1439 owns
physical-delete reconciliation, current-link clearing, ordinary catalog removal
with existing assignment/context/derivative-registration cascades, retained history
and Deleted in one CMS transaction. It does not physically delete derivative DBs.
The [sequencing material](../../spikes/DMS-1334/candidate-implementation-stories.md)
assigns G13 to DMS-1441 and G22 to DMS-1441 integrated with DMS-1439.

DMS-1441 owns real snapshot persistence and integration tests for atomic cleanup,
rollback and refresh/deletion races. Its schema, owner, delivery or tests are not a
DMS-1439 prerequisite. Add no speculative snapshot table, cleanup interface or no-op
hook. No snapshot criterion is claimed implemented or tested by this step.

## Q5 — strict parity and approved compatibility impact

AC17 remains strict: PostgreSQL and SQL Server must exhibit identical observable
API/state behavior. The user selected “Keep strict parity; revise the design.”
The architectural reviewer's revision-9 approval explicitly includes the following compatibility changes;
they are **recorded here, not implemented in step 1.1**.

Canonical name equality trims only U+0009–000D, U+0020, U+0085, U+00A0, U+1680,
U+2000–200A, U+2028, U+2029, U+202F, U+205F, U+3000 at the edges, then folds ASCII
A–Z to a–z. All other code points are preserved; ordinal equality uses no accent
removal, Unicode normalization, general case folding or locale-dependent LOWER.
Managed display names are trimmed before the 1–46 ASCII validation; ordinary
display strings are not rewritten. `Store`/`store` conflict on both providers;
`café`/`cafe` coexist on both, including an accent-insensitive display Name column.

The later schema work replaces ordinary `(TenantId, Name)` uniqueness with a
database-generated common-key constraint and null-equal tenants. No residual
linguistic Name uniqueness veto is allowed. PostgreSQL uses generated text with
deterministic C equality; SQL Server uses a persisted computed bounded binary key
with a four-byte length prefix and normalized NVARCHAR bytes. These encodings need
equivalent logical equality, not identical bytes. Direct SQL writes recompute keys.

Approved consequences and rollout requirements:

* Existing PostgreSQL ASCII-case/edge-space duplicates can block preflight. SQL
  Server linguistic equivalents outside this policy can become distinct. Preserve
  names, IDs, connections and tenants; never automatically rename/delete/deduplicate.
  Fail with bounded row-ID diagnostics and leave legacy uniqueness intact on failure.
* Ordinary name filtering uses canonical equality. Accent aliases cease matching
  on SQL Server; ASCII-case/edge-space aliases start matching on PostgreSQL. Type
  filtering is exact, case-sensitive and untrimmed on both providers.
* Ordinary name sorting uses unsigned UTF-8 lexical order of canonical text; type
  sorting uses exact UTF-8 text. Both use numeric Id ties in the requested direction.
  Do not sort by the length-prefixed equality key. Paging occurs after SQL filtering
  and sorting. Page positions can change; external clients are not certified.
* Preserve ordinary whitelist `id`/`name`/`dataStoreType`, case-insensitive validation,
  existing asc/ascending/desc/descending parsing, default Id ascending and no implicit
  limit (including offset-only). These differ intentionally from managed defaults.
* Fix bootstrap registration-name suffix selection to trim the fixed whitespace
  set; retain target-identity selection/refusal. DMS unfiltered ID-based caching and
  operational offset/limit callers keep their existing contracts.
* SQL Server query expressions require SQL Server 2019+ and the selected UTF-8
  collation. Preflight this even with management disabled. Drain serving binaries
  and writers, deploy corrected reads/caller and schema together, qualify constraint
  inventory and HTTP behavior before restart. No old CMS read binary may serve the
  new schema. Rollback requires legacy-collision preflight or planned restore.
* Management false does not restore old uniqueness/filter/sort semantics. Both
  providers still need real migration, direct-write, API, Unicode/paging, race and
  publication evidence; fixture tests cannot provide it.

## Q8 — request, output and lifecycle observations

POST permits only `name` and `databaseTemplate`; reject unknown properties locally.
Trim using Q5, require 1–46 ASCII characters matching `^[A-Za-z0-9 _]+$`, and exact
`Minimal` or `Sample`. Sample remains public even though provisioning maps it to
Populated. Naming follows the pinned formatter: spaces to underscores, trim edge
underscores, remove repeated leading `^(?:(?:edfi_+ods)(?:_+|$))+` matches ignoring
case/culture, trim underscores again, emit `EdFi_Ods_{remaining}_{template}` or
`EdFi_Ods_{template}`. Validate final names with DMS-1438, including its 63-character
ASCII/protected-name checks. Do not truncate or add suffixes. Different names that
produce one physical target collide before provisioning.

Public models have exactly nine fields: `id`, `name`, `dataStoreId`, `dataStoreName`,
`status`, `databaseTemplate`, `databaseName`, `lastRefreshed`, `lastModifiedDate`.
Emit null explicitly. All three linked fields are null in every unlinked
PendingCreate/CreateInProgress/CreateFailed/CreateError response. This intentionally
differs from upstream CreateInProgress databaseName exposure. Publish all three
together; Deleted retains their historical values while the current internal FK is
cleared. Collection and by-ID reads include tombstones; history does not authorize
a current-link operation.

Acceptance initializes both timestamps to the same UTC instant. Delete acceptance
changes only lastModifiedDate. In-progress/success/failure observations and terminal
exhaustion advance both timestamps. lastRefreshed is a lifecycle observation, not
an education-organization refresh. Fixture timestamps are fixed illustrative
instants; future database tests must prove this mapping using database UTC.

## Selected status and readiness expectations

Only Created admits DELETE; Deleted/absent is 404, every other state gets a distinct
safe validation 400. Created with closed readiness yields 503. Closed readiness
also refuses POST before acceptance with zero aggregate/reservation/job changes,
no lifecycle/timestamp/link changes and no physical call. The early closed POST path
does not resolve secrets. The fixed 503 problem has type
`urn:ed-fi:management-api:service-unavailable`, title `Service Unavailable`, detail
`Managed data store provisioning is unavailable.`, current correlationId, empty
validationErrors and errors. No Location, Retry-After, job ID or readiness details.

Authentication/authorization and tenant middleware precede feature validation.
Disabled authorized managed routes return existing validation 400; reads remain
available under closed readiness when enabled. Tenant selection is partitioning,
not an authorization boundary. Linked ordinary PUT/DELETE will return 409 with an
absolute managed URL, including when management is disabled. Exact error mappings,
pipeline ordering, physical effects and durable no-write guarantees are future
production tests, not behavior simulated by these fixtures.
