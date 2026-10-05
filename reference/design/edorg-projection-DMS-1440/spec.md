# DMS-1440 Implementation Spec — DMS education-organization projection endpoint and CMS HTTP reader

Status: **APPROVED v4 (architect review, 2026-10-02), amended by the Phase 0.1 contract review (§0.0) as contract corrections, not a redesign.** Implementation proceeds one step at a time under the §9 checkpoints. The measurement gates of steps 2.5 and 2.6 are closed (step 2.6 approval, 2026-10-04), and the `MaxProjectionRows` (50 000) and `MaximumPageSize` (2 000) defaults are approved. This document is self-contained; it does not depend on earlier drafts.

Branch: `DMS-1440` (created from `main` at `5c964676f`). Before approval only this document was edited.

## 0. Review history and dispositions

### 0.0 Phase 0.1 contract review (post-approval), 2026-10-02

| # | Finding or decision | Disposition |
| --- | --- | --- |
| C1 (P2) | Cursor replay promised the same page whenever unexpired. | §4.2, §4.6 item 3 and the contract: replay with unchanged request parameters returns the same projected items **while the projected content remains unchanged and the request remains authorized**; a committed projected change → 409 `projection-changed`. |
| C2 (P2) | "`errors` empty" contradicted inherited responses. | §3.6: empty `errors` is guaranteed only for projection-owned fixed problems; inherited responses keep their bodies; shared middleware is not changed. |
| C3 (P2) | "Members absent when disabled" read as covering `urls.oauth`. | §3.2, D-4 and the contract: the toggle removes only `urls.educationOrganizationProjection` and the top-level `educationOrganizationProjection` object; `urls.oauth` is existing Discovery behavior and stays. |
| Security-configuration type | The spec named `urn:ed-fi:api:security-configuration`. | Corrected to the existing `urn:ed-fi:api:system:configuration:security` (§3.4, §3.6); CMS matches it exactly (§5.5). |
| Unexpected-exception 500 | `CoreExceptionLoggingMiddleware` returns `{message, traceId}` as `application/json`. | Accepted as the one exception to the problem-document rule; Transient `ServiceUnavailable` (§3.6, §5.5). |
| Malformed tenant | Inherited 400 `urn:ed-fi:api:bad-request` was missing. | Added to §3.6; Permanent `InvalidRequest`. |
| Fingerprint-read failure | No body was specified for a transient fingerprint read failure. | 503 `…:target-unavailable` with its fixed body; `service-configuration-error` is reserved for a missing connection string (§3.5, step 2.3); an undecryptable one fails the catalog load and is 503 `service-unavailable` (step 2.3 review). |
| Digest id encoding | "Unsigned decimal" conflicted with int64 ids. | Signed int64, invariant-culture decimal, no `+`, no unnecessary leading zeroes, for ids, parent ids and cursor positions; counts and lengths nonnegative; no negative-id rejection; no zero sentinel (§4.2, §4.4, step 2.2 vectors). |
| Single-tenant binding | Tenant component unspecified. | Empty string (§4.2). |
| Service-claim wording | The misconfiguration message names the identity claim. | Step 1.2: requirement-specific wording; identity responses and their pinned fixtures unchanged. |
| Fixed literals | Titles and details for projection-owned types were proposed in the contract. | Approved; inherited responses keep their existing variability. |
| Step 1.1 (deviation, approved in principle) | A `ResourceClaim` row alone leaves the claim ungrantable on a populated catalog; the initial load skips populated catalogs and reload/upload replace the hierarchy and non-reserved claim sets. | `0035` also appends the claim to the stored hierarchy (§8, step 1.1); files named `0035_Add_EducationOrganizationProjection_Claim.sql`. |
| Step 1.1 (P2) | Existing-claim detection used exact spelling; CMS resolves claim names exactly and then case-insensitively, so a case variant would gain a second equivalent node. | Detection on both providers follows CMS's matching at every depth, collation-independent (§8); existing claims are never renamed, replaced or duplicated. |
| Step 1.1 (P2) | SQL Server's `=` pads trailing spaces even under `BIN2`, so a trailing-space name was treated as the claim, and such a metadata row would collide with `UX_ResourceClaim_ClaimName`. | Both SQL Server predicates add a `DATALENGTH` equality; a colliding metadata row fails the deployment before any change with an actionable diagnostic (§8); no uniqueness-constraint change. |
| Step 2.2 (P2) | Parse-step failures used the `FrontendResponse` default `application/json`, so CMS (§5.5 reads types only from `application/problem+json`) would miss the invalid-cursor restart and misclassify unsupported versions. | Every parse-step failure is served as `application/problem+json`; bodies unchanged. |
| Step 2.2 (P2) | Replacement-fallback UTF-8 let different malformed names share canonical bytes. | The digest encodes strictly (byte counts and hashed bytes); §6 rejects malformed UTF-16 as 409 `projection-data-invalid` before hashing (step 2.6). |
| Step 2.2 decision | Padded projection cursors were accepted. | One canonical representation: only the unpadded form is accepted (§4.2, contract). The existing `PageTokenCodec` throw on non-zero unused bits is a separate bug ticket. |
| Step 2.3 (P2) | Reused providers on the projection path logged before the projection steps could redact: the catalog provider passed `HttpRequestException`/`JsonException` objects to the logger, `IdentityTenantSnapshot` passed the wrapped catalog failure, and the mapping provider and its cache logged the effective schema hash. | Narrow logging corrections only: those records now carry the exception type (and HTTP status) instead of the exception object, and the mapping logs name dialect and mapping version without the hash; behavior, exception messages and diagnostics are unchanged. Trace-level tests drive the projection steps over the real catalog provider (HTTP faked beneath it), the real tenant snapshot, and the real mapping provider and cache (compilation faked beneath them), capturing every logger category (step 2.3). |
| Step 2.3 decision | An undecryptable primary connection string fails the whole tenant catalog load inside the shared provider, with the same exception type as an outage. | Approved: 503 `service-unavailable` (§3.5); the shared provider's failure model is not changed to separate two transient responses. `service-configuration-error` means a missing connection string only. |
| Step 2.3 decision | Primary fingerprint verdicts (not provisioned, mismatch, malformed) are retained for the process lifetime by the shared cache. | Accepted as existing behavior, cache policy unchanged; documented: repairing or provisioning a database under the same connection string requires restarting every affected DMS replica, and CMS retries alone cannot clear a cached negative verdict (§3.5). |
| Step 2.3 decision | Composition defects (no registered compiler; a mapping-provider exception other than `MappingSetUnavailableException`) reach the inherited unexpected-exception 500. | Approved; expected mapping incompatibility stays the typed `MappingSetUnavailableException` path. The projection response helper in its own file is approved. |
| Step 2.4 decisions | Plan records in their own file; the row carries the stored discriminator literal for the handler's allowlist; a private exception carries the first incompatibility inside `Compile`. | Approved. SQL snapshots cover both dialects; the incompatibility mutations run against the PostgreSQL mapping, whose resolution logic both dialects share. |
| Step 2.5 (found) | SQL Server's pool reset restores `LOCK_TIMEOUT` but not the isolation level, so a pooled connection kept `SERIALIZABLE` after the read. | The SQL Server reader restores `READ COMMITTED` after the transaction ends (approved as necessary). |
| Step 2.5 (P2) | A failed session restore was swallowed as "the connection is broken", which is not guaranteed; the connection could return to the pool still `SERIALIZABLE`. | Whenever the read cannot confirm cleanup - ending the transaction, restoring the session (including a connection no longer open), or releasing the connection - it excludes the connection from reuse before release, through a provider-specific discard. SQL Server: `SqlConnection.ClearPool`, which closes the checked-out connection on release. PostgreSQL (review 3): `NpgsqlConnection.ClearPool` does nothing for connections from an explicitly built `NpgsqlDataSource` (the production path; Npgsql 8.0.4's pool registry excludes them, and 8.0.4 has no public `NpgsqlDataSource.Clear`), so the read ends its own server session with `pg_terminate_backend(pg_backend_pid())`; Npgsql breaks the connector on the resulting `57P01` and destroys it on release, without touching the shared, leased data source. Faked-object tests; a SQL Server pooled-reuse regression with a restore that fails on an open connection; and a PostgreSQL regression through the production data-source cache and provider (pool of one) proving the failed session's backend is never rented again, with a clean-read control proving the same session is otherwise reused. |
| Step 2.5 (P2) | Owner disposal ran outside the protected cleanup, so a disposal failure could replace cancellation or a typed result and reach the outer logger with provider diagnostics. | Cleanup never throws: the cancellation, typed result or defect already in flight is what the caller receives; only the failure's description is logged (Warning). **Defined outcome:** a read that committed keeps its result when cleanup afterwards fails, because its rows were read and committed in one consistent transaction; the connection is excluded from reuse. |
| Step 2.5 (P2) | The SQL Server counterexample committed C before T1 began, which shows blocking but not §4.6's commit reordering. | Independent rows: T1 updates A and B and stays open while T2 updates C and commits; the read then waits, and the test asserts its `blocking_session_id` is T1's session before T1 commits. HTTP digest and replay assertions stay with the handler tests (2.6, 2.8). |
| Step 2.5 (P2) | The measurement writers updated only `School` and reported end-to-end latency as if it were lock wait; the documentation said a writer's added wait is bounded by one read. | The opt-in workload writes all four tables (single-row updates and two-table transactions, several concurrent writers) and reports lock waits (SQL Server `LCK_M_*` wait totals; sampled waiting sessions on both engines) and deadlock and error counts. The bound claim is removed (blocking chains and concurrent readers invalidate it); PostgreSQL's non-blocking statement is qualified as ordinary DML, since conflicting table locks can block the read. |
| Step 2.5 measurement (round 3, production writes) | With the production write pipeline (API `PUT` of all four types: renames, School to Local Education Agency moves and Local Education Agency parent changes; SQL Server with the RCSI and snapshot settings provisioning enables), four writers and one client reading back-to-back at the cap: no deadlocks and no write failures on either engine, every read succeeds (PostgreSQL 93 of 93, SQL Server 167 of 167). SQL Server writers wait behind the reads: write p95 140 to 213 ms, 75 s of lock wait over 2,000 operations, writers waiting in 83% of samples (state agency, service center and local agency writes most, School writes least). PostgreSQL write latency is unchanged. The synthetic stress case (direct SQL, no RCSI, a School-then-LEA transaction) still makes the read the victim in 65 of 75 reads; the production pipeline did not reproduce that shape. | Production writes did not reproduce substantial read failures, so no isolation amendment was proposed on that ground. The writer blocking on SQL Server is the cost of `SERIALIZABLE`. Decided at the step 2.5 approval: see "Isolation decision" below. |
| Isolation decision (step 2.5 approval, 2026-10-04) | Snapshot isolation is available on databases DMS provisioning creates, but not guaranteed on every existing database DMS serves. Supporting both modes would add capability detection, fallback behavior and another concurrency test matrix. | **SQL Server keeps `SERIALIZABLE`** for DMS-1440, with the existing isolation, lock-timeout, command-timeout and transient-failure behavior. The measured writer latency under production writes is accepted. A snapshot-isolation optimization needs its own focused design review; snapshot isolation gives transaction-level consistency, but each page is a new transaction, so the digest comparison stays. |
| Step 2.5 approval (2026-10-04) | `f37558e20` (PostgreSQL session termination) and `e230183b8` (production-write measurement). | Approved; the provider gate is closed. Ending the PostgreSQL connection's own session excludes it from reuse without disposing the shared data source, and keeping the committed rows of a read whose later cleanup fails stays approved. Defaults remain provisional until step 2.6. |
| Step 2.5 decisions | Shared staged-read file and internal test hook; deferring handler-owned 400/409 assertions of §4.6 items 2-4 to 2.6/2.8; the documented transient fallback (Npgsql's client command timeout has no SQLSTATE; SQL Server socket error numbers vary by host); measurements opt-in (`[Explicit]`, `ProjectionMeasurement`). | Approved. `MaxProjectionRows` 50,000 and `MaximumPageSize` 2,000 stay **provisional** pending the completed provider gate and the step 2.6 measurements. |
| Step 2.6 (review requirement) | The set reader opens the request's effective target, which the step 2.3 target middleware does not set. | `SelectEffectiveDataStoreTargetMiddleware(ReadOnly, NotApplicable, NotApplicable)` runs immediately after target resolution and before the schema step (§3.4 step 9), recording the resolved store's primary. Pinned by a test through the real host's container (`WebApplicationFactory<Program>`, external boundaries replaced) in which the store also configures a snapshot and a read replica and the request sends `Use-Snapshot: true`: the reader in the request scope sees the primary target and the PostgreSQL data source leased for the primary database, and the fingerprint read targets only the primary. Omitting the step, or allowing either derivative, fails it. |
| Step 2.6 decisions | (1) A cursor positioned at or after the last item of an unchanged set (never issued by DMS, since a cursor is issued only when an item follows it) is answered 400 `invalid-cursor`, not an empty continuation page. (2) Validation runs as separate whole-set passes in a fixed order: discriminators first (an unknown literal is 409 `projection-unsupported` wherever it sits), then identifiers and names, references, cycles. A self-link is reported as a cycle. (3) A reader that returns rows out of identifier order, or a reference its type cannot carry, has broken its contract: the handler throws (inherited 500), it does not report data. (4) The pipeline test lives in `Frontend.AspNetCore.Tests.Unit`, the only project that boots the production host; Core keeps the exact step-order and policy tests. (5) The step 2.3 middlewares are registered as Core singletons; the parse step and the handler are built by the pipeline from the projection settings. (6) The walk measurement is a separate file (`Scenarios/EducationOrganizationProjectionWalkMeasurement.cs`) run from the step 2.5 measurement fixtures; `PROJECTION_MEASURE_PHASES=provider,walks` selects phases. | Approved at the step 2.6 approval (2026-10-04). Observation for step 2.7: the frontend serializes bodies with `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`, not the default encoder §3.3 assumes. The 2,048-byte item bound still holds, because unescaped UTF-8 is never longer than the escaped form. |
| Step 2.6 measurement | Handler alone at the cap (50,000 rows, 2,000 per page, set already read): 32 ms per page median (58 ms p95), of which the digest is 28 ms; 30 MB allocated per page; 322 KB response body; 0.8 s of handler time per 25-page read. Complete reads through the production reader: PostgreSQL 3.7-4.2 s, SQL Server 5.9-8.1 s; non-read time per page varied between runs (PostgreSQL 25-46 ms median, SQL Server 39-209 ms) and rose with full garbage collections in the test process. One committed change between pages: the next page is refused and the restarted read completes. With four concurrent API writers almost no logical read completes within 3 restarts (PostgreSQL 1 of 7 in each of two runs; SQL Server 0-1 of 11-12 in three runs); with one rename every 10 s every logical read completes; with one every 2 s, 2-5 of 7-11. Every provider read succeeded. A server-GC comparison was not run: the run was stopped by the host for low memory. Tables: `docs/EDUCATION-ORGANIZATION-PROJECTION.md`, "Handler cost and complete reads". | Superseded by the step 2.6 review 1 rows below: these complete-read figures predate the digest change, and counted logical reads that finished after the writers stopped among reads during writes. |
| Step 2.6 review 1 (P2) | The cycle check took a checkpoint only per starting row; one walk along a long parent chain, and marking that path finished, ran unchecked. | Both loops take a checkpoint per agency. A handler test cancels a third of the way along a 5,000-agency chain ending in a cycle (checkpoint 17,000 of the walk's 15,002-20,001): `OperationCanceledException`, hashing never entered, no response; without the walk's checkpoints the cycle is reached and 409 answered. A validator test pins the exact checkpoint count of a long chain (30 = three passes, starting rows, walk, marking), which drops if either loop loses its checkpoint. |
| Step 2.6 review 1 (P2) | The walk measurement stopped only the outer read loop, so a logical read in progress when the writers finished, restarts included, was counted among reads during writes; the writer adapter discarded the writers' failure counts. | The phase records when the writers finished; logical reads finishing while writers were active and after are reported separately, with the writers' operations and failures (by status and problem type). Corrected result: with four concurrent writers no logical read completed while they were active on either engine (PostgreSQL 0 of 9, SQL Server 0 of 17, every attempt refused on page 2); the one completion in each run came after the writers stopped. No write failures. |
| Step 2.6 review 1 decision (allocations) | 30 MB allocated per page, 23.4 MB of it the digest's per-row strings and byte arrays (about 750 MB per complete read). | The digest formats its canonical bytes into one pooled buffer and appends them to the hash: 0.00 MB for the digest, 6.5 MB per page in all. Handler time per page 31-32 ms → 11-18 ms median; digest 26 ms → 3.7-8.2 ms; 25-page handler time 0.78-0.80 s → 0.35-0.56 s (two runs each, Release). Every golden vector and the contract cursor bytes are unchanged. Row order is an explicit contract: ascending id, equal ids in supplied order (stable); a sequence already in id order is hashed as supplied, any other is sorted; tests pin both paths and a row-count-independent allocation bound. Complete reads after the change (one run per engine; .NET 10.0.12 Release, workstation concurrent GC, 16 processors, at least 3.2 GB free throughout, test host at most 0.9 GB): PostgreSQL 3.0 s with 16 ms non-read time per page median, SQL Server 4.4 s with 10 ms. Tables: `docs/EDUCATION-ORGANIZATION-PROJECTION.md`, "Handler cost and complete reads". `MaxProjectionRows` and `MaximumPageSize` stay provisional until this checkpoint is accepted. |
| Step 2.6 review 1 decisions | The four step 2.6 decisions. | Accepted: an end-position cursor answers 400; unknown discriminators take precedence over data failures; impossible reader ordering or slot shapes are defects (500); the production-DI test belongs in the frontend project. A populated supported reference slot resolving to the wrong type stays 409. |
| Step 2.6 approval (2026-10-04) | `2e32b7801` and the review 1 corrections `2c71d66d6` (parent-chain cancellation checkpoints), `6fad64c84` (pooled-buffer digest) and `7f898ab67` (completions during and after writes, writer failures). | Approved; no blocking findings. **The measurement gate is closed: `MaxProjectionRows` 50,000 and `MaximumPageSize` 2,000 are approved as defaults** (D-17). **Documented limitation:** sustained changes to projected content can exhaust the bounded restarts, so a complete read is not guaranteed while writes continue; the measurements do not establish completion under continuous writes. CMS discards an incomplete attempt and keeps the previous snapshot: the reader exposes no items unless every page of one logical read succeeded (AC 10, §5.4). Go-ahead for step 2.7 only. |
| Step 2.7 (found) | The documented envelope bound (256 bytes) is below the envelope the frontend serializes for the largest values: 270 bytes measured, a 162-character cursor with a 10-digit `dataStoreId`, a 20-character negative position and a 10-digit walk timestamp, plus 108 fixed bytes. The item arithmetic also assumed 19-character ids, while negative int64 ids have 20. | **Approved (step 2.7 review, 2026-10-04):** envelope allowance 512 bytes (§3.3); the 2,048-byte item allowance and the CMS validator's 1,024-byte envelope allowance (§5.2) remain sufficient. 270 bytes is a measurement, not a timeless maximum: the walk timestamp's decimal width grows (11 digits from the year 2286). Measured over HTTP through the frontend serializer (`JavaScriptEncoder.UnsafeRelaxedJsonEscaping`, which writes no UTF-16 unit in more than 6 bytes) on a full default-size page of the largest items for each fixture. |
| Step 2.7 decisions | (1) `ProjectionContractVersions` becomes public so Discovery advertises exactly the list the parse step accepts, rather than a frontend copy. (2) `Cache-Control: no-store` is assigned in the frontend entry point after Core answers, which covers every status Core returns; responses produced before the endpoint (rate limiting, fallback) keep the existing `SecurityHeadersMiddleware` value for non-success statuses. Not the identity endpoint-metadata middleware, which exists so a 2xx short-circuited by the rate limiter is still marked; the projection endpoint has no such 2xx. (3) The repeated-parameter names live in the frontend, pinned to Core's parse-step constants by a test (the `PartitionsPathSegment` precedent). (4) `FrontendRequest.Path` is the escaped request path, tenant and qualifiers included, without the query string, so cursors never reach Core or frontend request logs; the framework's hosting events are covered by the review row below. (5) The step 2.6 production-pipeline host setup moved into a shared test helper reused by the over-HTTP tests; the step 2.6 assertions are unchanged. (6) The DMS E2E and Instance Management E2E Discovery expectations (exact member sets) gain the two projection members, since the toggle defaults to `true` in every compose file. | Accepted at the step 2.7 review (2026-10-04): shared public contract versions, entry-point `no-store`, frontend parameter names pinned to Core, the shared test host; route and Discovery wiring sound. |
| Step 2.7 review (P2) | Removing the query from `FrontendRequest.Path` protected Core logging only. The framework's `Microsoft.AspNetCore.Hosting.Diagnostics` request-starting and request-finished events (Information, the shipped level) carry the raw query string, and so the cursor, in their `QueryString` property and rendered message, for every request including rejected ones; the production filter covered identity routes only. | The production logging stage (`LoggingConfigurator.ApplyLogContextAndIdentityRedaction`, applied by `ConfigureLogging`) adds `EducationOrganizationProjectionQueryStringRedactingEnricher`: on an event whose `Path` or `RequestPath` ends in `/management/education-organizations` after any tenant, qualifier or path-base segments (case-insensitive, one trailing slash), `QueryString` becomes `?[redacted]`. Messages render from properties, so the rendered message changes with it. The events are kept (method, path, status, elapsed time), as are the DMS frontend and Core completion events, which never carried the query. Logging-level, so it applies before endpoint execution: to rejected requests, requests the endpoint never reaches and requests while the endpoint is disabled. Other routes keep their query strings. Tests: real requests through the production pipeline with an issued cursor (200) and refused ones (invalid, unauthenticated, repeated); every path form (tenant and qualifier prefixes, path base, mixed case, trailing slash, refused, not reached); `ConfigureLogging` over the shipped settings; an unrelated route as control. Removing the enricher fails 25 of the 39 tests. |

### 0.1 Round 3 findings (v3 → v4), 2026-10-02

| # | Finding | Disposition in v4 |
| --- | --- | --- |
| F1 (P1) | The acquisition classifiers classify exception **types** (`DbException` / `NpgsqlException`), so a single `try` applying them first would turn missing-column/table failures into transient `TargetUnavailable`. | **Fixed.** §3.9 defines four explicit stages with separate boundaries — Acquire, Prepare (begin transaction, session settings), Execute+Materialize, Commit — each with its own classifier and result; the backend result contract distinguishes `SchemaIncompatible(reason, providerCode)` from `TargetUnavailable(stage, describe)`. Tests must drive real execution exceptions (dropped column, changed column type) through the readers and assert the permanent result, and a real acquisition failure (unresolvable host) and assert the transient result, on both providers. |
| F2 (P1) | `AppSettings:Datastore` accepts `mssql`, while `RelationalProviderToken.TryNormalize` accepts `sqlserver`; the v3 comparison would reject every valid SQL Server deployment. | **Fixed.** §3.5 compares the catalog token with the **registered dialect** (`SqlDialect` from the runtime mapping-set compiler / command executor): `postgresql` ↔ `SqlDialect.Pgsql`, `sqlserver` ↔ `SqlDialect.Mssql`. Catalog vocabulary unchanged. Tests: matching PostgreSQL, matching SQL Server, both mismatches, `Missing`, `Unknown`. |
| F3 (P2) | Incompatible physical types (PostgreSQL `42804`, `42846`) and typed-reader materialization failures fell into the transient default. | **Fixed.** §3.9 adds deterministic type/conversion codes for both providers (PostgreSQL `42804`, `42846`, `42P18`, `22P02`, `22003`; SQL Server 206, 235, 241, 242, 245, 257, 402, 529, 8114, 8115) and classifies materialization exceptions (`InvalidCastException`, `FormatException`, `OverflowException`, `SqlNullValueException`, unexpected `DBNull`) as permanent `SchemaIncompatible`. Q19: the transient default is a documented fallback for genuinely unknown failures only; known schema/data incompatibilities never enter it. |
| F4 (P2) | v3 referenced "as v2" content that no longer exists. | **Fixed.** Every section is restored in full: reader contracts (§5.1), configuration (§5.2), Discovery resolution (§5.3), token and loop rules (§5.4), total classification matrix (§5.5), diagnostics (§5.6), edge cases (§7), every step's files and tests (§9), validation commands (§11). |
| Q18 | Accept defaults provisionally; describe 25 pages as the full-cap walk at the default page size. | §4.5 reworded. |
| Q20 | Accept API seeding with a separate resource-authorized client; keep setup failures distinct. | §9 steps 4.1/4.2: seeding uses the fixture's resource client; setup failures fail the fixture with a distinct message before any projection assertion. |
| Unicode fixture | `nvarchar(75)` cannot hold 75 supplementary characters. | §3.3 and step 2.5: PostgreSQL fixture = 75 supplementary characters; SQL Server fixture = 37 supplementary characters plus one BMP character (75 UTF-16 units). Shared bound unchanged. |
| Measurement sequence | The handler does not exist at step 2.5. | Split: step 2.5 measures provider behavior (statement elapsed, bytes transferred, writer impact); step 2.6 measures handler allocations and full-walk timing; both are approval gates before later steps. |
| Cancellation test | Needs a deterministic in-processing checkpoint. | §4.4: an internal `IProjectionProcessingObserver` seam reports stage transitions and 1,000-row checkpoints; the test cancels at a named checkpoint during validation and asserts `OperationCanceledException`, that hashing never started, and that no response was written. |

### 0.2 Round 2 findings (v2 → v3) — still in force

Digest conditions normative (§4.3); internal rows carry every reference slot and all populated references are validated before precedence (§6); projection-owned database boundary (§3.9); provider comparison (§3.5); 2,048-byte item bound (§3.3); genuinely empty provisioned store in E2E (§9 4.2); cancellation during processing (§4.4); `MaxProjectionRows`/`MaximumPageSize` defaults with measurement (D-17); pinned digest encoding (§4.2); normalized URL containment and removal of `AllowedTokenEndpointOrigins` (§5.3); explicit process-context exports, shard selection, TRX guard (§9 4.2); case-insensitive repeated-parameter handling (§3.1).

### 0.3 Round 1 findings (v1 → v2) — still in force

Version-based fence replaced by consistent full read per page + digest (D-5); projection-owned mapping middleware (D-9); whole-set rejection of contradictions (D-7); production reader under maintained coverage (D-15); no exception messages at any log level (D-16); Q1–Q13 accepted as recorded: service claim with per-tenant credentials and binding, toggle default `true`, qualifier templates including OAuth, 409 `projection-changed` transient by exact type, not-provisioned transient / fingerprint mismatch permanent, cycles fail closed, configuration secrets, claims migration, strict projection JSON with tolerant Discovery/OAuth parsing.

## 1. Scope, non-goals, sources, decisions, assumptions, open questions

### 1.1 Scope (Jira DMS-1440, Story, fixVersion "Ed-Fi API v8.1", parent epic DMS-1072, blocks DMS-1441, relates to DMS-1438 and DMS-1334, sprint "API Platform Sprint 69")

DMS, under `src/dms`:

- A service-only fixed-route endpoint `GET {prefix}/management/education-organizations?dataStoreId=&limit=&cursor=&contractVersion=` where `{prefix}` is `FixedRoutePattern.Build(routeQualifierSegments, multiTenancy)`.
- Discovery advertisement of the endpoint URL template and the supported projection contract versions.
- Service authorization through a dedicated CMS-seeded service claim, with client-to-tenant binding, reusing the DMS-1515 identity pipeline steps.
- Tenant- and route-context-bound resolution of the CMS ordinary `dataStoreId` through `IDataStoreProvider`, with an explicit provider-dialect check.
- A mapping-set-driven projection of exactly the four core education-organization types, with every core reference validated for existence and type, parent precedence, duplicate and cycle rejection, stable ordering, digest-verified consistent pagination, cancellation throughout, and a stable ProblemDetails taxonomy.
- Projection-owned, stage-separated translation of fingerprint, mapping-set, connection-acquisition, execution/materialization and commit failures so permanent incompatibility is never reported as transient and no internal diagnostic reaches a response or a log.

CMS, under `src/config`:

- The shipped claims hierarchy gains the new service claim (both Data Standard claim documents and the `ResourceClaim` seed).
- A provider-neutral HTTP reader in `EdFi.DmsConfigurationService.Backend`: Discovery lookup, URL-template resolution for both the projection and the OAuth endpoints, client-credentials token acquisition and cache, paged complete read, strict response validation, timeout and cancellation handling, total failure classification into the DMS-1437 model with registered sanitized error codes.
- A maintained integration test project exercising the production reader against the real DMS+CMS stack on both engines.
- Operational documentation.

### 1.2 Non-goals

Snapshot persistence, refresh endpoints and jobs, schedules, tenant aggregates (DMS-1441); administrative database lifecycle (DMS-1438/DMS-1439); extension-defined education-organization types; historical or deleted records; application assignment validation; any CMS reference to a DMS project or package; any CMS read of DMS domain tables, generated DDL or `dms.EffectiveSchema`; a public Management API v3 route; serving the projection OpenAPI document from `/metadata`; new identity-provider surface in CMS; a production CMS test endpoint; changes to `ResolveMappingSetMiddleware`, `ValidateDatabaseFingerprintMiddleware`, `CoreExceptionLoggingMiddleware`, `ConnectionAcquisition`, `IRelationalCommandExecutor` or any DDL; normalization of existing claim-cache keys; renaming `IdentityTenantSnapshot`. `SchemaHashConstants.RelationalMappingVersion` stays `v3`.

### 1.3 Sources read

- **Jira DMS-1440**: all fields; 15 ACs; no comments; links (blocks DMS-1441, relates to DMS-1438 and DMS-1334); stale `customfield_10429` (direct-SQL) disregarded.
- **Spike documents** under `reference/spikes/DMS-1334/` (five story files, findings, index).
- **DMS-1437 design**: `reference/design/jobs-DMS-1437/{spec.md,admin-api-v3-ed115fd8.yaml,admin-api-v3-provenance.md}`; `docs/CMS-BACKGROUND-JOBS.md`.
- **DMS-1413/DMS-1515 identity**: design docs; `Core/Middleware/{ServiceClaimAuthorizationMiddleware,ValidateClientTenantBindingMiddleware,ValidateTenantExistsMiddleware,JwtRoleAuthenticationMiddleware,ResolveDataStoreMiddleware,ValidateDatabaseFingerprintMiddleware,ResolveMappingSetMiddleware}.cs`; `ApiService.cs`; `Security/{JwtValidationService,Conventions,EndpointRoleAuthorization}.cs`; `Paging/PageTokenCodec.cs`; `Configuration/{IDataStoreProvider,ConfigurationServiceDataStoreProvider,DataStore,RelationalProviderToken}.cs`; `Utilities/RouteContextMatcher.cs`; `Response/FailureResponse.cs`.
- **DMS frontend**: endpoint modules, `FixedRoutePattern.cs`, `AspNetCoreFrontend.cs`, `Content/ContentProvider.cs`, `Configuration/AppSettings.cs` (`Datastore` ∈ {`postgresql`, `mssql`}), `appsettings.json`.
- **DMS backend**: `Backend.External/{IRelationalTokenInfoEducationOrganizationLookup,RelationalModelTypes,AuthEdOrgHierarchyContracts,IMappingSetProvider,EffectiveSchemaContracts}.cs` (`SqlDialect { Pgsql, Mssql }`); `Backend.Plans/TokenInfoEducationOrganizationSqlCompiler.cs`; `Backend/{RelationalCommandAccess,ConnectionAcquisition}.cs`; `Backend.Postgresql/{PostgresqlRelationalCommandExecutor,PostgresqlSeamConnection,PostgresqlConnectionAcquisitionFailure,NpgsqlDataSourceProvider,NpgsqlDataSourceCache}.cs`; `Backend.Mssql/{MssqlRelationalCommandExecutor,MssqlSeamConnection,MssqlConnectionAcquisitionFailure,MssqlConnectionAcquisition,MssqlDocumentCacheWriter}.cs`; `Backend.Ddl/CdcSqlServerHeartbeatDatabaseProvider.cs`; fixture `src/dms/backend/Fixtures/authoritative/ds-5.2/expected/pgsql.sql`.
- **DMS tests**: `tests/EdFi.DataManagementService.Tests.Integration`; `tests/EdFi.InstanceManagement.Tests.E2E` (README, `setup-local-dms.ps1`, `Management/{ConfigServiceClient,TestConfiguration,TokenHelper}.cs`, `Hooks/SetupHooks.cs`, features); `eng/docker-compose/provision-e2e-database.ps1`; `build-dms.ps1`.
- **CMS code**: `Backend/Jobs/*`, `Backend/Claims/Standards/ds52|ds61/Claims.json`, `Backend/Repositories/ResourceClaimMetadataResolver.cs`, deploy scripts `0009`, `0025`, `0031`, `Backend.OpenIddict/Services/OpenIddictTokenManager.cs`, `Backend.Keycloak/*TokenManager*`, `Frontend.AspNetCore/Modules/IdentityModule.cs`, `Infrastructure/WebApplicationBuilderExtensions.cs`, `DataModel/Model/DataStore/DataStoreResponse.cs`, `DataModel/LoggingUtility.cs`.
- **Docs and CI**: `docs/{CONFIGURATION,CURSOR-PAGING,RELATIONAL-BACKEND,ROLES-SCOPES,CLAIMS-LOADING-GUIDE,API-CLIENT-AND-INSTANCE-CONFIGURATION,MULTI-TENANCY-GETTING-STARTED,CMS-BACKGROUND-JOBS}.md`; `.github/workflows/on-dms-pullrequest.yml`, `on-config-pullrequest.yml`; `build-config.ps1`; `eng/docker-compose/{.env.routeContext.e2e,setup-openiddict.ps1,setup-keycloak.ps1}`.
- **Upstream ODS-Admin-API** (`gh api`, main head `8df5376ec4` on 2026-10-02): `EducationOrganizationService.cs`, `docs/design/Education-organization-Endpoints.md`.
- **External references**: SQL Server `SET TRANSACTION ISOLATION LEVEL` and `nchar/nvarchar` storage; PostgreSQL transaction isolation, character types and error-code appendix; System.Text.Json character encoding.

### 1.4 Admin API parity evidence (AC 6, AC 7)

Pinned OpenAPI `educationOrganizationModel`: `educationOrganizationId` int64, `nameOfInstitution` string, `shortNameOfInstitution` string nullable, `discriminator` string, `parentId` int64 nullable, `additionalProperties: false`.

Upstream reader SQL: `COALESCE(scl.localeducationagencyid, lea.parentlocaleducationagencyid, lea.educationservicecenterid, lea.stateeducationagencyid, esc.stateeducationagencyid) AS parentid` over `edfi.educationorganization` filtered to the four `edfi.*` discriminators.

| Element | Upstream | Pinned here |
| --- | --- | --- |
| Discriminators | `edfi.<ResourceName>` | Exactly the four `edfi.*` values; DMS literal `Ed-Fi:<ResourceName>` mapped through a fixed allowlist. |
| Parent | COALESCE in that order | Same precedence, selected per arm from the row's own reference fields after every populated reference has been validated (§6). |
| Self/transitive ancestors | Stored value returned; no substitution | No substitution; self-link, cycles, unresolved or wrong-type references fail closed (stricter by AC 8). |
| Nulls/types | `DBNull` → null; `long` | Same. |
| Error handling | Rows dropped; store failures swallowed | DMS fails the request closed; CMS fails the target. |

### 1.5 Evidence from current code that shapes the design

1. DMS has no ASP.NET auth stack; `ClaimSetName` is the first `scope` claim; service-endpoint precedents are the exact-role gate and the claim-set service claim.
2. CMS cannot mint a custom-scope or custom-role client through its API; a claim set granting a service claim to an application client with an empty data-store assignment is the only dedicated least-privilege permission in both identity-provider modes.
3. Discovery is tenant/qualifier aware with real-value-or-`{placeholder}` URLs; `urls.oauth` is the DMS `{prefix}/oauth/token` proxy; unit tests pin `urls.Count == 7`; the CI-validated Discovery OpenAPI document is an ApiSchema package asset.
4. `IDataStoreProvider.GetById(long, string?)` is tenant-scoped; `ResolveDataStoreMiddleware` reloads once on a miss and uses `RouteContextMatcher.IsMatch`. `DataStore.RelationalProviderToken` normalizes to `postgresql` or `sqlserver`; `RelationalProviderMetadataStatus.Supported` means recognized, not matching. The deployment setting `AppSettings:Datastore` is `postgresql` or `mssql`; the runtime dialect is `SqlDialect { Pgsql, Mssql }` exposed by the runtime mapping-set compiler and the command executor. No enabled/disabled flag exists in either catalog.
5. Fingerprint verdicts are cached per `(TargetKind, connection string)`; `ValidateDatabaseFingerprintMiddleware` answers 503 for not-provisioned, mismatch and malformed. `ResolveMappingSetMiddleware` returns `ex.Message`/`ex.Diagnostics` in a 503.
6. Relational shape: `School.LocalEducationAgency_LocalEducationAgencyId`; `LocalEducationAgency.{ParentLocalEducationAgency_LocalEducationAgencyId, EducationServiceCenter_EducationServiceCenterId, StateEducationAgency_StateEducationAgencyId}`; `EducationServiceCenter.StateEducationAgency_StateEducationAgencyId`; `NameOfInstitution varchar(75) NOT NULL`, `ShortNameOfInstitution varchar(75) NULL`; composite FKs; nine-arm `EducationOrganization_View`; mapping-set exposure through `AbstractUnionViewsInNameOrder`, root columns with `SourceJsonPath`, `DocumentReferenceBindings`.
7. `IRelationalCommandExecutor` begins no transaction. `ConnectionAcquisition.GuardAsync` rethrows caller cancellation and `ObjectDisposedException`, wraps expected failures **only for Snapshot targets**, otherwise propagates raw. `PostgresqlConnectionAcquisitionFailure.IsExpected` accepts `NpgsqlException`, `ArgumentException` (not `ArgumentNullException`), `SocketException` and certificate-loading exceptions; `MssqlConnectionAcquisitionFailure.IsExpected` accepts `DbException` and `ArgumentException` (not null); both `Describe` methods return the type name plus `SqlState`/`SqlException.Number`. **They classify types, not stages**, so they are applied only to the Acquire stage (§3.9). Acquisition-path log statements carry only target kind, counts and exception type names. `MssqlDocumentCacheWriter` is the precedent for `BeginTransactionAsync(IsolationLevel…)`. **Corrected at the step 2.5 review (round 3):** DMS SQL Server provisioning (`MssqlDatabaseProvisioner`, `docs/RELATIONAL-BACKEND.md`) enables `READ_COMMITTED_SNAPSHOT` and `ALLOW_SNAPSHOT_ISOLATION` on databases it creates, and for an existing database only warns when RCSI is off; snapshot isolation is therefore available on newly provisioned databases but not guaranteed on every database DMS serves. Earlier versions of this item said provisioning enables neither.
8. Page tokens are unsigned canonical base64url CSV decoded strictly.
9. DMS-1437: only `JobPermanentException(JobErrorCode)` is permanent; codes `^[A-Za-z][A-Za-z0-9]{0,63}\z`; handler token linked to host stop and lease uncertainty.
10. CMS has no DMS client, no DMS base URL setting, default `IHttpClientFactory` loggers active; `BearerTokenPerClientLimit` default 5; CMS `scope` comma-joined; `expires_in = TokenExpirationMinutes*60`.
11. A new hierarchy claim requires a `ResourceClaim` row (`ResourceClaimMetadataResolver`).
12. Instance Management E2E: DMS + CMS, multi-tenant, `districtId,schoolYear`, identity on; three route databases provisioned **DDL-only** (stores start empty; features create schools through the API); claim sets/applications/clients provisioned through real CMS endpoints; CI runs it `--no-build --no-restore` against the prebuilt DMS artifact; `src/config` changes trigger the DMS workflow; the test process reads `DMS_API_URL` and `CONFIG_SERVICE_URL` and obtains a CMS system-admin token in `SetupHooks`; `Invoke-WithInstanceE2ETestProcessContext` exports engine, three database names and connection strings, the route manifest, two tenants' vendor/application/client key and secret, and data-store ids.

### 1.6 Architectural decisions

| ID | Decision | Status |
| --- | --- | --- |
| D-1 | **Authorization = dedicated service claim** `http://ed-fi.org/identity/claims/services/educationOrganizationProjection`, action `Read`, strategies exactly `[NoFurtherAuthorizationRequired]`; granted to no shipped claim set; credential = CMS application API client with empty data-store assignment. | Accepted. |
| D-2 | **Client-to-tenant binding on**; per-tenant credentials in multi-tenant mode. | Accepted. |
| D-3 | **Route** `{FixedRoutePattern}/management/education-organizations`; qualifiers must match the store's `RouteContext`; CMS fills placeholders of both templates from the store's contexts. | Accepted. |
| D-4 | **Discovery**: `urls.educationOrganizationProjection` + top-level `educationOrganizationProjection.contractVersions`, gated by `AppSettings:EnableEducationOrganizationProjection` (default `true`); the toggle removes only these two members, and the existing `urls.oauth` is unaffected. | Accepted; clarified (§0.0 C3). |
| D-5 | **Pagination = consistent full read per page + projection digest** under four normative implementation conditions (§4.3). | Accepted. |
| D-6 | **Cursor** = unsigned (not cryptographically signed) canonical base64url CSV with format version, `dataStoreId`, `lastId`, digest, walk issued-at, binding hash; 60-minute lifetime from walk start; future-dated beyond 60 s invalid. | Accepted. |
| D-7 | **Fail closed on data**, whole set, every page: duplicate ids; any populated core reference not resolving to a row of its expected type; any cycle (length ≥ 1) over LEA parent links. | Accepted. |
| D-8 | **Fail closed on mapping**: typed incompatibility reasons from a mapping-set-driven compiler; extension arms ignored. | Accepted. |
| D-9 | **Projection-owned, stage-separated translation** of fingerprint, mapping-set, acquisition, execution/materialization and commit failures (§3.9). | Revised (round 3). |
| D-10 | **Explicit target states** including the provider-dialect comparison against the registered `SqlDialect` (§3.5). | Revised (round 3). |
| D-11 | **Primary only.** | Accepted. |
| D-12 | **CMS reader returns typed results**; only caller cancellation propagates as an exception. | Accepted. |
| D-13 | **CMS credentials and settings from configuration**; secrets via environment or plugin contribution. | Accepted. |
| D-14 | **Discovered URLs trusted only when, after normalization, scheme/host/port equal `DmsBaseUrl`'s and the path begins with `DmsBaseUrl`'s path at a segment boundary**; redirects disabled; 3xx permanent. | Accepted. |
| D-15 | **Cross-component verification** = Instance Management E2E (DMS) + CMS-owned production-reader test project in the same lanes + contract examples. | Accepted. |
| D-16 | **Redaction is total** at every log level, through reused infrastructure. | Accepted. |
| D-17 | **Operational cap**: `MaxProjectionRows` (default 50 000) with its own problem type `projection-too-large`; `MaximumPageSize` default 2 000; measurement is a gated deliverable (steps 2.5 and 2.6). | Accepted; defaults approved at the step 2.6 approval (2026-10-04). |

### 1.7 Assumptions

- A-1 `edfi.EducationOrganizationIdentity` enforces uniqueness of `EducationOrganizationId` across member types; the duplicate check is defense in depth; verified in step 2.5.
- A-2 `NameOfInstitution` and `ShortNameOfInstitution` are at most 75 characters (PostgreSQL `varchar(75)`, code points) or 75 UTF-16 units (SQL Server `nvarchar(75)`) in every supported Data Standard; the body bound (§3.3) derives from this and from the frontend encoder writing no UTF-16 code unit in more than 6 bytes (corrected at step 2.7: the frontend uses `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`, not the default encoder).
- A-3 CMS deployments without DMS-1441 refresh never configure the reader; it reports `NotConfigured` and registers nothing that can fail startup.
- A-4 The Instance E2E stores are empty at suite start and after `@InstanceCleanup`; the reader test project seeds its own hierarchy through the DMS API and removes it.
- A-5 The Instance E2E CI lanes can restore and build one additional test project under `src/config/tests`. Fallback: add it to the DMS integration-assemblies artifact; recorded as a deviation and reviewed before use.

### 1.8 Open questions requiring approval

None new. Q18–Q20 are accepted as dispositioned in §0.1 (defaults, approved once the step 2.5/2.6 measurement gates closed on 2026-10-04; API seeding). The implementer records in the step reports any deviation from the error-code tables in §3.9 discovered during testing, and such a change requires approval before the next step.

## 2. Acceptance-criteria traceability matrix

| AC | Jira text (abridged) | Proposed behavior | Steps | Verification (would fail if reverted) |
| --- | --- | --- | --- | --- |
| 1 | Authenticated service endpoint; Discovery advertises template and version; CMS discovers, never hard-codes layout | §3.1; §3.2; §5.3 resolves projection and OAuth templates with normalized containment | 2.7, 3.2, 4.1, 4.2 | Discovery unit tests per mode and toggle; reader tests: substitution for both templates, unresolved placeholder → `TargetNotRoutable` with no request, `/api-other` under `/api` rejected; production-reader E2E reads via live Discovery |
| 2 | Client-credentials bearer, dedicated least-privilege permission; others denied | §3.4; credential has no resource claims and no data stores | 1.1, 1.2, 2.6, 2.8, 4.1 | API denial matrix; E2E four credential shapes; no shipped claim set grants the claim |
| 3 | CMS provider-neutral reader; no DMS references; no target DB connections | §5; architecture test | 3.1–3.5, 4.2 | Unit tests per concern; architecture test; Trace-level redaction tests; production-reader E2E |
| 4 | Resolve tenant + `dataStoreId` through DMS routing; missing, cross-tenant, disabled, not-yet-routable → classified, no rows | §3.5 states incl. dialect comparison | 2.3, 2.8, 4.1, 4.2 | API tests per state incl. matching PG, matching MSSQL, both mismatches, `Missing`, `Unknown`; E2E unknown/cross-tenant/qualifier-mismatch 404; reader E2E unknown store and non-existent database |
| 5 | Exactly SEA, ESC, LEA, School | D-8 | 2.4, 2.5 | Compiler selects four of nine arms; integration excludes `PostSecondaryInstitution` |
| 6 | `edfi.*` discriminators; `Ed-Fi:*` never exposed | Allowlist | 2.6, 2.8 | Handler and API tests |
| 7 | Parent precedence; no self or transitive-only ancestors | §6 per-arm selection after reference validation; slot retained internally | 2.4–2.6, 2.8 | Integration exact-value tests per precedence row; API response has no slot fields |
| 8 | Duplicates or contradictory relationships fail with stable non-transient type | D-7 over the whole set every page | 2.5, 2.6, 2.8 | Duplicate id; self-parent; two- and three-node cycle; parent-LEA slot holding an ESC id (FK dropped) → 409; valid parent LEA plus dangling ESC → 409; contradiction outside the requested page → 409; validation precedes slicing |
| 9 | Deterministic pages by numeric id; int64; empty store; opaque cursor | §3.3, D-5, D-6, §4.2 golden vectors | 2.2, 2.5, 2.6, 2.8, 4.2 | Codec and digest vectors; §4.6 concurrency suite on both engines; delete/identity/content change → 409; genuinely empty provisioned store through the production reader → `Success`, zero items |
| 10 | CMS reads all pages before success; any failure prevents replacement | §5.4 loop | 3.4, 4.2 | Unit tests per §7; reader E2E injected 503 on page 3 → `Transient`, no items; body-bound regression with engine-appropriate Unicode fixtures |
| 11 | DMS distinguishes transient from non-transient; CMS maps without leaks | §3.6; §3.9 stage-separated classification; §5.5 total matrix | 2.2, 2.3, 2.5, 3.1, 3.4 | Both providers: dropped column → 409; changed column type → 409; typed-reader cast failure → 409; deadlock/lock timeout/command timeout/login failure → 503; unresolvable host → 503 via the Acquire stage; unknown code → 503 (documented fallback); every failure logs only `Describe`-level detail; diagnostic-bearing mapping failure → 409 fixed body |
| 12 | Cancellation-aware both sides | DB wait, materialization, validation/hash checkpoints, pre-publish; CMS every stage | 2.5, 2.6, 2.8, 3.2–3.4 | Provider tests under exclusive lock; handler test cancels at a named validation checkpoint via the observer seam (hashing never starts, no response); API abort; CMS TCS-gated handlers |
| 13 | Extension types excluded | D-8 | 2.4, 2.5 | As AC 5 |
| 14 | No CMS code touches DMS tables/DDL/`EffectiveSchema` | HTTP only | 3.5 | Architecture test |
| 15 | Administrative DB access remains DMS-1438/1439 | Nothing opens a database from CMS | all | Architecture test |

## 3. DMS contract (**[Jira]** existing requirement, **[D]** proposed decision)

### 3.1 Route and parameters

**[Jira]** `GET {prefix}/management/education-organizations` (single-tenant `/management/…`; multi-tenant `/{tenant}/management/…`; with qualifiers `/{tenant}/{districtId}/{schoolYear}/management/…`). **[D]** Legacy `/management/{tenant}/…` untouched; the prefix-less path is not mapped in multi-tenant mode.

| Name | Required | Type / rules | Failure |
| --- | --- | --- | --- |
| `dataStoreId` | yes | int32 `> 0` | 400 `parameter-validation-failed` |
| `limit` | no | int32 in `1..MaximumPageSize` (default `MaximumPageSize`) | 400 `parameter-validation-failed` |
| `cursor` | no | opaque; must decode, bind, and be unexpired | 400 `invalid-cursor` |
| `contractVersion` | no | supported version when present | 400 `unsupported-contract-version` |

**[D] Repeated parameters are always rejected.** The projection frontend handler evaluates `HttpContext.Request.Query[name].Count > 1` for each of the four names (`IQueryCollection` lookup is case-insensitive) and passes repeated names into Core through a new optional `FrontendRequest.RepeatedQueryParameterNames` (default empty; populated only by this handler). The Core parse step, after authorization, compares with `OrdinalIgnoreCase` and returns 400 `parameter-validation-failed` naming the parameter. Unknown parameters are ignored.

Headers: `Authorization: Bearer <token>` required; responses `application/json` or `application/problem+json`; always `Cache-Control: no-store`.

### 3.2 Discovery

```json
{
  "urls": {
    "oauth": "http://host/api/Tenant_255901/{districtId}/{schoolYear}/oauth/token",
    "educationOrganizationProjection": "http://host/api/Tenant_255901/{districtId}/{schoolYear}/management/education-organizations"
  },
  "educationOrganizationProjection": { "contractVersions": ["educationOrganizationProjection.v1"] }
}
```

`urls.oauth` is the existing Discovery member and is unaffected by the toggle. `urls.educationOrganizationProjection` and the top-level `educationOrganizationProjection` object are present only when the toggle is on; turning it off removes exactly those two members. Real-value-or-`{placeholder}` convention; includes `PathBase`; no query string. The ApiSchema-package Discovery OpenAPI document is not modified.

### 3.3 Success response and size bound

```json
{
  "contractVersion": "educationOrganizationProjection.v1",
  "dataStoreId": 3788,
  "nextCursor": "MSwzNzg4LDI1NTkwMTAwMSx…",
  "items": [
    { "educationOrganizationId": 255901, "nameOfInstitution": "Grand Bend ISD", "shortNameOfInstitution": "GBISD", "discriminator": "edfi.LocalEducationAgency", "parentId": 25 },
    { "educationOrganizationId": 255901001, "nameOfInstitution": "Grand Bend High School", "shortNameOfInstitution": null, "discriminator": "edfi.School", "parentId": 255901 }
  ]
}
```

- `nextCursor` is `null` exactly when the slice reached the end of the digest-verified set. A page is empty only when the whole set is empty (first page, `nextCursor: null`). `items.Count == limit` whenever `nextCursor` is non-null. Items strictly ascending by id; exactly five members each; `shortNameOfInstitution` and `parentId` always present.
- **Body bound (A-2; corrected at step 2.7).** The frontend serializes with `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`, not the default encoder. Under either encoder no UTF-16 code unit serializes to more than 6 bytes (`\uXXXX`): both escape each half of a supplementary code point (12 bytes per code point) and control characters; the relaxed encoder writes other non-ASCII BMP characters as raw UTF-8 (at most 3 bytes). PostgreSQL `varchar(75)` admits 75 code points → at most 75 × 12 = **900 bytes** per name; SQL Server `nvarchar(75)` admits 75 code units → 450 bytes; 900 bounds both. Largest item (negative int64 id and parent id of 20 characters, the longest discriminator `edfi.EducationServiceCenter`): 27 + 20 + 21 + 902 + 26 + 902 + 17 + 29 + 12 + 20 + 1 = 1,977 → **2,048 bytes per item**. **Envelope ≤ 512 bytes** (approved at the step 2.7 review): measured at 270 bytes for the largest values (108 fixed bytes and a 162-character cursor with a 10-digit `dataStoreId`, a 20-character position and a 10-digit walk timestamp), a measurement rather than a timeless maximum, since the timestamp's decimal width can grow; the earlier estimate of 256 bytes was below it. Default `MaximumPageSize = 2000` ⇒ ≤ 2000 × 2048 + 512 bytes (3.91 MiB); ceiling `10000` ⇒ ≤ 19.6 MiB. CMS validator: `MaxResponseBodyBytes ≥ PageSize × 2048 + 1024`, whose 1,024-byte allowance covers the envelope.
- **Unicode fixtures**: PostgreSQL — both names of 75 supplementary characters (U+1F600); SQL Server — both names of 37 supplementary characters plus one BMP non-ASCII character (75 UTF-16 units); plus 75 control characters (escaped by the relaxed encoder). Each is served over HTTP through the frontend serializer as a full default-size page of the largest items (step 2.7): item, envelope and page sizes are asserted against the bounds, and each name against 6 bytes per UTF-16 unit. Measured: largest item 1,977 bytes (PostgreSQL fixture), 1,077 (control characters), 1,069 (SQL Server fixture); envelope 270 bytes (measured); full page 3,956,251 bytes.

### 3.4 Authentication and authorization pipeline

1. `RequestResponseLoggingMiddleware`, `CoreExceptionLoggingMiddleware`
2. `TenantValidationMiddleware` (400)
3. `JwtAuthenticationMiddleware` (401)
4. `ValidateTenantExistsMiddleware` (`IdentityTenantSnapshot`; 404 / 503)
5. `ValidateClientTenantBindingMiddleware` (401 / 503; ignores `DataStoreIds`)
6. `ServiceClaimAuthorizationMiddleware` with `ServiceClaimRequirement(…/services/educationOrganizationProjection, "Read")` (403 / 500 `urn:ed-fi:api:system:configuration:security`)
7. `ParseEducationOrganizationProjectionRequestMiddleware` (400s)
8. `ResolveEducationOrganizationProjectionTargetMiddleware` (§3.5)
9. `SelectEffectiveDataStoreTargetMiddleware(ReadOnly, NotApplicable, NotApplicable)`
10. `ValidateEducationOrganizationProjectionTargetSchemaMiddleware` (fingerprint re-mapping, D-9)
11. `ResolveEducationOrganizationProjectionMappingSetMiddleware` (409 `projection-unsupported`, fixed detail)
12. `EducationOrganizationProjectionHandler`

Authorization precedes parsing and target resolution; steps 4–6 are the identity ordering and inherit its documented tenant-existence disclosure and cache revocation windows.

### 3.5 Target resolution and target states (AC 4; D-10)

```
IDataStoreProvider.RefreshInstancesIfExpiredAsync(tenant, ct)
store := GetById(dataStoreId, tenant); if null → LoadDataStores(tenant, ct) once; store := GetById(…)
registeredDialect := the SqlDialect of the registered runtime mapping-set compiler (Pgsql | Mssql)
```

Catalog-token ↔ dialect mapping (catalog vocabulary preserved): `postgresql` ↔ `SqlDialect.Pgsql`; `sqlserver` ↔ `SqlDialect.Mssql`. The deployment string `AppSettings:Datastore` (`postgresql`/`mssql`) is **not** parsed here; the registered dialect is the source of truth.

| State | Status / type | Class |
| --- | --- | --- |
| tenant catalog load fails during the miss reload (CMS unreachable, error status, malformed response, or an undecryptable primary connection string anywhere in the tenant's catalog) | 503 `service-unavailable` | Transient |
| `store is null` (unknown or other tenant) | 404 `…:target-not-found` | Permanent |
| route context mismatch | 404 `…:target-not-found` (same body) | Permanent |
| `RelationalProviderMetadataStatus == Unknown` | 409 `…:target-provider-unsupported` | Permanent |
| `Supported` and token maps to a dialect ≠ `registeredDialect` | 409 `…:target-provider-unsupported` | Permanent |
| `Missing` (legacy row without a token) | proceed, assuming `registeredDialect` (matches current routing) | — |
| `ConnectionString` null, empty or whitespace | 503 `service-configuration-error` (missing connection string only) | Transient |
| fingerprint read fails transiently (connection, timeout, other non-verdict failure) | 503 `…:target-unavailable` (fixed body; not `service-configuration-error`) | Transient |
| Acquire-stage failure (§3.9) | 503 `…:target-unavailable` | Transient |
| Prepare/Execute-stage transient failure (lock timeout, deadlock, command timeout, connection drop) | 503 `…:target-unavailable` | Transient |
| Execute/Materialize-stage schema or type incompatibility (§3.9) | 409 `…:target-schema-incompatible` | Permanent |
| Commit-stage failure | 503 `…:target-unavailable` | Transient |
| no `dms.EffectiveSchema` row | 503 `database-not-provisioned` | Transient |
| fingerprint mismatch or malformed row | 409 `…:target-schema-incompatible` | Permanent |
| mapping set unavailable / required element missing | 409 `…:projection-unsupported` | Permanent |
| set larger than `MaxProjectionRows` | 409 `…:projection-too-large` | Permanent |

Fingerprint verdicts come from the cache every endpoint shares, which keeps a Primary target's verdict (not provisioned, mismatch, malformed) for the life of the process. Repairing or provisioning a database under the same connection string therefore requires restarting every affected DMS replica; CMS retries alone cannot clear a cached negative verdict. The cache policy is not changed by this endpoint.

No catalog flag named "disabled" exists; the Jira word maps onto the unsupported-provider and unusable-connection states. Tests (step 2.3): matching PostgreSQL, matching SQL Server, both mismatches, `Missing`, `Unknown`.

### 3.6 Error taxonomy

| Status | Type | When | CMS class |
| --- | --- | --- | --- |
| 400 | `urn:ed-fi:api:bad-request:parameter-validation-failed` | bad `dataStoreId`/`limit`, repeated parameter | Permanent `InvalidRequest` |
| 400 | `…:invalid-cursor` | undecodable, mismatched, expired, future-dated | Permanent `InvalidRequest` (one restart allowed) |
| 400 | `…:unsupported-contract-version` | | Permanent `UnsupportedContract` |
| 400 | `urn:ed-fi:api:bad-request` | malformed tenant segment (inherited `TenantValidationMiddleware`) | Permanent `InvalidRequest` |
| 401 | `urn:ed-fi:api:security:authentication` | token or binding failure | Permanent `Unauthorized` after one refresh |
| 403 | `urn:ed-fi:api:security:authorization:` | claim set lacks `Read` | Permanent `Forbidden` |
| 404 | `urn:ed-fi:api:not-found` / `…:target-not-found` | tenant / store | Permanent `TargetNotFound` |
| 409 | `…:target-provider-unsupported` | | Permanent `TargetSchemaIncompatible` |
| 409 | `…:target-schema-incompatible` | fingerprint or execution/materialization schema failure | Permanent `TargetSchemaIncompatible` |
| 409 | `…:projection-unsupported` | mapping | Permanent `Unsupported` |
| 409 | `…:projection-too-large` | cap | Permanent `LimitExceeded` |
| 409 | `…:projection-data-invalid` | §6 contradictions | Permanent `DataInvalid` |
| 409 | `…:projection-changed` | digest differs | **Transient** `ProjectionChanged` (exact type only) |
| 429 | `urn:ed-fi:api:too-many-requests` | | Transient `RateLimited` |
| 500 | `urn:ed-fi:api:system:configuration:security` | strategy misconfiguration | Permanent `Forbidden` |
| 500 | `urn:ed-fi:api:system` | invalid deployment configuration (inherited) | Transient `ServiceUnavailable` |
| 500 | none: `application/json` `{message, traceId}` | unexpected exception (inherited `CoreExceptionLoggingMiddleware`) | Transient `ServiceUnavailable` |
| 503 | `database-not-provisioned` / `service-configuration-error` / `service-unavailable` / `…:target-unavailable` | §3.5 | Transient |

All problem types are under `urn:ed-fi:api:education-organization-projection:` unless shown with their existing full prefix. Every failure is `application/problem+json` except the inherited unexpected-exception 500, which keeps its existing `application/json` `{message, traceId}` body. Projection-owned problems have fixed `title`/`detail` literals (pinned in the contract) and an empty `errors` array; `parameter-validation-failed` names the offending parameters in `errors`. Inherited responses (authentication, malformed tenant, authorization, security configuration, tenant not found, rate limiting, service unavailable, system) keep their existing bodies, including any `errors` entries; shared middleware is not changed to alter them. No SQL, object names, connection strings, hashes or diagnostics in any body.

### 3.7 Configuration (DMS)

| Setting | Default | Validation |
| --- | --- | --- |
| `AppSettings:EnableEducationOrganizationProjection` | `true` | bool |
| `AppSettings:EducationOrganizationProjection:MaximumPageSize` | `2000` | `1..10000` |
| `AppSettings:EducationOrganizationProjection:MaxProjectionRows` | `50000` | `1000..1000000` |
| `AppSettings:EducationOrganizationProjection:CursorLifetimeMinutes` | `60` | `1..1440` |
| `AppSettings:EducationOrganizationProjection:ReadLockTimeoutSeconds` | `5` | `1..60` |
| `AppSettings:EducationOrganizationProjection:ReadCommandTimeoutSeconds` | `60` | `5..600` |

Compose/env mapping `DMS_ENABLE_EDUCATION_ORGANIZATION_PROJECTION` in `eng/docker-compose/local-dms.yml`, `published-dms.yml`, `eng/azure-vm/compose/docker-compose.yml`. Invalid values fail startup through the Core `AppSettingsValidator` (`Core/Configuration/AppSettingsValidator.cs`), which the frontend binds with `ValidateOnStart`; limits are validated whether or not the toggle is on.

### 3.8 Compatibility and upgrade

Additive endpoint, Discovery fields and claim; `urls.Count` tests become toggle-aware. Contract versioning: advertised and echoed; a future `v2` is advertised alongside `v1` for at least one release; CMS sends the highest common version explicitly; no common version is permanent `UnsupportedContract`. Coordinated order: CMS claims migration → operator creates claim set and per-tenant application/client → DMS with toggle on → CMS reader settings. Hybrid/Filesystem claims deployments add the claim themselves (documented). `RelationalMappingVersion` stays `v3`.

### 3.9 Database boundary: stages, classification, logging (D-9, D-16)

The provider readers execute four **explicit stages**, each in its own boundary. The backend result union is:

```csharp
Set(rows) | TooLarge | MappingIncompatible(reasonCode, resourceName?)
| SchemaIncompatible(reasonCode, providerCode?)          // permanent: 409 target-schema-incompatible
| TargetUnavailable(stage, describe)                     // transient: 503 target-unavailable
```

| Stage | Covers | Classification |
| --- | --- | --- |
| **Acquire** | opening the connection through the existing seam | caller cancellation → rethrow; `ObjectDisposedException` → rethrow; provider `IsExpected(ex)` → `TargetUnavailable(Acquire, Describe(ex))`; any other → rethrow (defect) |
| **Prepare** | `BeginTransactionAsync`, `SET … lock_timeout` / `SET LOCK_TIMEOUT`, `CommandTimeout` | caller cancellation → rethrow; `DbException` → `TargetUnavailable(Prepare, Describe(ex))`; other → rethrow |
| **Execute + Materialize** | executing the projection statement and reading every row into the internal row contract | caller cancellation → rethrow; `DbException` → **execution code table** below; materialization exceptions (`InvalidCastException`, `FormatException`, `OverflowException`, `System.Data.SqlTypes.SqlNullValueException`, `DBNull` where a non-null column is required) → `SchemaIncompatible(MaterializationTypeMismatch)`; other → rethrow |
| **Commit** | `CommitAsync` | caller cancellation → rethrow; any `DbException` or indeterminate outcome → `TargetUnavailable(Commit, Describe(ex))`; rows are discarded |

The type-based acquisition classifiers are applied **only** in the Acquire stage; they are never consulted for execution failures, so a missing-column `NpgsqlException`/`SqlException` raised by the statement cannot be misread as unavailability.

**Execution code table** (`DbException` in the Execute+Materialize stage):

| Provider | Permanent → `SchemaIncompatible` | Transient → `TargetUnavailable(Execute)` |
| --- | --- | --- |
| PostgreSQL `SqlState` | `42P01` undefined_table, `42703` undefined_column, `3F000` invalid_schema_name, `42704` undefined_object, `42809` wrong_object_type, `42883` undefined_function, **`42804` datatype_mismatch, `42846` cannot_coerce, `42P18` indeterminate_datatype, `22P02` invalid_text_representation, `22003` numeric_value_out_of_range** | `40P01` deadlock_detected, `55P03` lock_not_available, `57014` query_canceled, class `08` connection, class `28` authorization, class `53` insufficient resources, class `57` operator intervention, **any other or missing code (documented fallback)** |
| SQL Server `Number` | 207 invalid column, 208 invalid object, 4104 multi-part identifier, 2812 procedure not found, 1088 object not found, **206 operand type clash, 235/245/241/242 conversion failures, 257 implicit conversion not allowed, 402 incompatible data types, 529 explicit conversion not allowed, 8114 error converting data type, 8115 arithmetic overflow** | 1205 deadlock, 1222 lock request timeout, −2 timeout, 4060/18456 open/login failures, 10053/10054/10060/40613 connection, **any other (documented fallback)** |

The transient fallback is a documented bound on genuinely unknown failures (the job layer retries under DMS-1437 `MaxAttempts`), not a claim that every permanent failure is recognized. Known incompatible-schema and incompatible-type cases are listed above and must not reach the fallback; the implementer records any additional deterministic code met during step 2.5 testing and proposes it for the permanent list.

**Mapping**: the compiler returns typed `MappingIncompatibility(reasonCode, resourceName?)`; `MappingSetUnavailableException` → 409 `projection-unsupported`, reason logged as `MappingSetUnavailable`.

**Logging rules**: never `Exception.Message`/`ToString()`, SQL, object names, connection strings, hashes. Allowed: stage, state/reason enum names, `dataStoreId`, sanitized tenant and correlation id, `Describe(ex)` output (type name and provider code), row count, elapsed ms. Reused acquisition-path components were audited and log only target kind, counts and exception type names; the audit is repeated and recorded in step 2.5. The reused catalog, tenant and mapping components were corrected and are covered by the step 2.3 upstream redaction tests.

**Tests** (step 2.5, both providers, real databases): dropped column → `SchemaIncompatible`; column type changed (`ALTER TABLE … ALTER COLUMN ShortNameOfInstitution TYPE integer` / `ALTER COLUMN … int`) → `SchemaIncompatible` (via code table or materialization); unresolvable host in the connection string → `TargetUnavailable(Acquire)`; malformed connection option → `TargetUnavailable(Acquire)`; lock timeout, deadlock, command timeout → `TargetUnavailable(Execute)`; logger capture at `Trace` with hostile values proves only `Describe`-level detail is logged. Full-pipeline API tests (step 2.8) repeat the dropped-column and unresolvable-host cases with the fingerprint verdict pre-cached.

## 4. Pagination consistency, cursor integrity, replicas

### 4.1 Guarantee and argument

For one logical read, every returned item reflects one committed state S of the four core tables, with no duplicates and no skipped rows; otherwise some page fails and CMS discards the walk.

Page *k* reads the complete core set inside one explicit transaction. PostgreSQL `RepeatableRead` is snapshot isolation. SQL Server `Serializable` prevents dirty reads and holds shared and range locks until transaction completion, so a fully consumed statement inside that transaction reads a set consistent with some serial order. The handler computes `digest(S_k)` over every row (§4.2). Page *k* is answered only if `digest(S_k) == digest(S_1)`, so the projected content of S_k equals that of S_1; each page returns the slice `id > lastId` of its own set, so the concatenation equals the ascending enumeration of one content set. Uncommitted transactions are invisible regardless of version allocation; deletes, identity changes, parent changes, and a different database behind the same catalog id change the digest unless the projected content is identical, which is unobservable by construction.

### 4.2 Cursor and canonical digest encoding

Cursor: `1,<dataStoreId>,<lastEducationOrganizationId>,<digestBase64url>,<walkIssuedAtUnixSeconds>,<bindingHash>`; `lastEducationOrganizationId` is a signed int64 encoded as in the digest below; `bindingHash` = first 32 hex chars of SHA-256 over `tenantLower|contractVersion|k1=v1;k2=v2…` (`tenantLower` is the empty string in single-tenant mode) (pairs sorted ordinally by lower-cased key, lower-cased values). Strict decode: only the unpadded base64url text the encoder emits is accepted — any `=`, even correct padding, is rejected (unlike `PageTokenCodec`, which keeps accepting padded page tokens for compatibility) — with strict UTF-8 and every field in its canonical form; any failure, `dataStoreId` ≠ query, binding mismatch, `now − walkIssuedAt > CursorLifetimeMinutes`, or `walkIssuedAt > now + 60 s` → 400 `invalid-cursor`. `walkIssuedAt` is set on the first page and copied unchanged into later cursors. Replay of an unexpired cursor with unchanged request parameters returns the same projected items while the projected content remains unchanged and the request remains authorized; after a committed projected change it is answered 409 `projection-changed`, and after an authorization change with the corresponding 401/403/404. Strict decode rejects non-canonical integers (`+`, unnecessary leading zeroes, `-0`). No server state; a forged cursor can only move the position or carry a different digest for a store the caller is already authorized to read in full.

**Digest** = SHA-256 over the UTF-8 bytes of (LF line endings, no BOM, invariant culture, ids and parent ids as **signed int64** invariant-culture decimal with a leading `-` for negative values, no `+` and no unnecessary leading zeroes (zero is `0`); the row count and lengths, as UTF-8 **byte** counts, are nonnegative decimal):

```
edorg-projection-digest:v1\n
<rowCount>\n
<id>;<tag>;<S(nameOfInstitution)>;<S(shortNameOfInstitution)>;<P(parentId)>\n      (one line per row, ascending id)
```

`tag` ∈ `SEA`, `ESC`, `LEA`, `SCH`. `S(x)` = `-` when null, otherwise `<byteCount>:<x>` (`0:` is the empty string). `P(parentId)` = `-` when null, otherwise the signed decimal as for ids (a lone `-` is unambiguous because a negative id always has digits). Length prefixes make `;`, `:` and `\n` inside names unambiguous. The digest covers the five projected fields only.

Golden vectors (step 2.2 fixtures with exact text and expected hex digest): empty set (`edorg-projection-digest:v1\n0\n`); one SEA with null short name and null parent; a name containing `;`, `:`, `\n`, `"`; empty-string short name versus null; a name of 75 supplementary characters (byte count 300); ids `1`, `2147483648`, `9007199254740993`; two rows ordered by id regardless of insertion order; **signed boundaries** `-9223372036854775808`, `-1`, `0`, `9223372036854775807` as ids and as parent ids; a mixed negative/positive set ordered numerically (not lexically); the Phase 0.1 worked example (`docs/EDUCATION-ORGANIZATION-PROJECTION.md`, digest `19ac413c…bffa`).

### 4.3 Per-page read (provider readers) — normative conditions

1. **Acquire** the Primary connection through the existing seam. **Prepare**: `BeginTransactionAsync(IsolationLevel.RepeatableRead)` (PostgreSQL) / `BeginTransactionAsync(IsolationLevel.Serializable)` (SQL Server); `SET LOCAL lock_timeout` / `SET LOCK_TIMEOUT` from `ReadLockTimeoutSeconds`; `CommandTimeout = ReadCommandTimeoutSeconds`. **Every command is attached to the transaction. No isolation-weakening hints or statements.**
2. **Execute + Materialize**: one compiled statement selecting per arm the internal row `(id, tag, nameOfInstitution, shortNameOfInstitution, localEducationAgencyRef, parentLocalEducationAgencyRef, educationServiceCenterRef, stateEducationAgencyRef)` (irrelevant slots `NULL`), `ORDER BY id`, at most `MaxProjectionRows + 1` rows. **Consume the reader fully and close it before committing.**
3. **Commit.** On failure or indeterminate outcome → `TargetUnavailable(Commit)`; **rows are never returned after a failed commit.** On any failure, roll back and dispose.
4. Return `Set(rows)` or `TooLarge`. Processing (§4.4) happens after transaction completion.

### 4.4 Handler algorithm and processing checkpoints

```
set := reader.ReadSetAsync(...)                                     // §4.3
observer.Stage(Validation); ct.ThrowIfCancellationRequested()
validate(set)   → DataInvalid(kind)?                               // §6; iterative; checkpoint every 1,000 rows
observer.Stage(Precedence); select parents per arm
observer.Stage(Hashing); digest := SHA256(canonical(set))           // checkpoint every 1,000 rows
if cursor is null: walkIssuedAt := now; fence := digest
else if cursor.digest != digest → 409 projection-changed
observer.Stage(Slicing); slice := (cursor is null ? set : set.Where(id > cursor.lastId)).Take(limit + 1)   // no sentinel: negative and zero ids are included on the first page
items := slice.Take(limit) mapped through the discriminator allowlist (five wire fields only)
nextCursor := slice.Count > limit ? Encode(1, dataStoreId, items.Last.id, fence, walkIssuedAt, bindingHash) : null
observer.Stage(Publishing); ct.ThrowIfCancellationRequested()
```

- Each checkpoint calls `ct.ThrowIfCancellationRequested()` and `observer.Checkpoint(stage, rowsProcessed)`. `IProjectionProcessingObserver` is an **internal** seam with a no-op production implementation; tests install a recording observer whose `Checkpoint` callback cancels the `CancellationTokenSource` at a named point (for example `Validation`, 2,000 rows) and then assert `OperationCanceledException`, that `Hashing` was never entered, and that no response was written.
- Cycle detection: iterative three-color walk over LEA → parent-LEA links with an explicit stack; linear in the number of LEAs. Existence/type checks use dictionaries built in one pass.

### 4.5 Cost, measurement, alternatives (D-17)

Every page re-reads and re-hashes the whole set, so a walk costs `pages × rows`. **At the default page size** (2,000) a full-cap walk (50,000 rows) is 25 pages × 50,000 rows = 1.25 M rows transferred and hashed, before restarts; smaller `limit` values increase the page count proportionally (the ceiling is `MaxProjectionRows / 1` pages at `limit = 1`, bounded on the CMS side by `MaxPages`). At an estimated 150–250 bytes per internal row that is roughly 190–310 MB per full-cap walk at the default page size. Typical stores complete in one to eight pages. These are estimates, not promises.

**Measurement gates**: step 2.5 measures provider behavior at the cap on both engines — statement elapsed time, bytes transferred, concurrent-writer impact (a writer loop on the four tables during repeated reads; on SQL Server, blocking time and deadlock victims counted). Step 2.6 measures handler behavior — managed allocations per page (`GC.GetTotalAllocatedBytes` around the handler), validation/hash time, and full 25-page walk time. Each step's report records the numbers; if either exceeds the operational envelope agreed at its checkpoint, defaults are revisited before the next step.

Alternatives weighed: shared server-side snapshot per walk — rejected for cost (cleanup, cross-replica storage, write access, hides database replacement); SQL-side digest — rejected (dialect-specific aggregation and recursive CTEs duplicating validation); single-response projection — contradicts the pinned contract.

### 4.6 Required deterministic concurrency tests (both providers, step 2.5)

1. **Round-1 counterexample**: T1 updates A and B (uncommitted); T2 updates C and commits; page 1 (A on page 1, B later); T1 commits; page 2. Invariant: never A-old with B-new. PostgreSQL: page 1 returns A-old, page 2 → 409. SQL Server: page 1 blocks on T1's locks; the test commits T1 once `sys.dm_exec_requests` shows the reader session blocked by T1's session (state signal); page 1 then returns A-new and B-new and page 2 succeeds.
2. Delete, identity change, name change, parent change between pages → 409.
3. Replay of the same cursor with unchanged parameters and unchanged projected content → identical page; replay after a committed projected change → 409 `projection-changed`.
4. Expired and future-dated cursors → 400; walk timestamp preserved.
5. Cancellation while blocked by an exclusive lock → `OperationCanceledException`, no response, transaction rolled back, connection disposed.
6. Lock timeout and command timeout → `TargetUnavailable(Execute)`; only `Describe` output logged.
7. Dropped column and changed column type → `SchemaIncompatible`; unresolvable host → `TargetUnavailable(Acquire)`.

## 5. CMS reader

### 5.1 Contracts (`src/config/backend/EdFi.DmsConfigurationService.Backend/EducationOrganizationProjection/`)

```csharp
public interface IEducationOrganizationProjectionReader
{
    Task<EducationOrganizationProjectionReadResult> ReadAllAsync(
        EducationOrganizationProjectionReadRequest request, CancellationToken cancellationToken);
}

public sealed record EducationOrganizationProjectionReadRequest(
    string? TenantName,                                              // null in single-tenant mode
    int DataStoreId,
    IReadOnlyDictionary<string, string> DataStoreContexts);          // ContextKey → ContextValue from the CMS catalog

public abstract record EducationOrganizationProjectionReadResult
{
    public sealed record Success(IReadOnlyList<EducationOrganizationProjectionItem> Items, string ContractVersion, int PageCount, int Restarts)
        : EducationOrganizationProjectionReadResult;
    public sealed record Failure(EducationOrganizationProjectionFailure Detail) : EducationOrganizationProjectionReadResult;
}

public sealed record EducationOrganizationProjectionItem(
    long EducationOrganizationId, string NameOfInstitution, string? ShortNameOfInstitution, string Discriminator, long? ParentId);

public enum EducationOrganizationProjectionFailureCategory { Transient, Permanent }

public enum EducationOrganizationProjectionFailureCode
{
    NotConfigured, DiscoveryUnavailable, DiscoveryInvalid, UnsupportedContract, TokenUnavailable, TokenRejected,
    Unauthorized, Forbidden, TargetNotFound, TargetNotRoutable, TargetSchemaIncompatible, Unsupported, DataInvalid,
    ProjectionChanged, RateLimited, ServiceUnavailable, NetworkError, Timeout, MalformedResponse, UnexpectedResponse,
    InvalidRequest, LimitExceeded
}

public enum EducationOrganizationProjectionStage { Discovery, Token, Page }

public sealed record EducationOrganizationProjectionFailure(
    EducationOrganizationProjectionFailureCategory Category, EducationOrganizationProjectionFailureCode Code,
    EducationOrganizationProjectionStage Stage, int? HttpStatus,
    string? ProblemType /* sanitized [A-Za-z0-9:._-], ≤ 256 */, string? CorrelationId /* sanitized, ≤ 128 */,
    int PagesRead, int Restarts);
```

Registration `services.AddDmsEducationOrganizationProjectionReader(IConfiguration)`: binds `DmsEducationOrganizationProjectionSettings` with `IValidateOptions` + `ValidateOnStart`; registers the named `HttpClient` `DmsEducationOrganizationProjectionHttpClient.Name` (`AllowAutoRedirect = false`, `UseCookies = false`, `Timeout = Timeout.InfiniteTimeSpan`, `.RemoveAllLoggers()`, `.AddLogger<ProjectionHttpClientLogger>()`), `IDmsDiscoveryClient` (singleton cache, `TimeProvider`), `IProjectionServiceTokenProvider` (singleton, `TimeProvider`), `IEducationOrganizationProjectionReader` (transient), and the job error codes via `AddJobErrorCode` (§5.5). Extension `EducationOrganizationProjectionFailure.ToJobErrorCode()` maps permanent codes to `JobErrorCode`.

### 5.2 Configuration (`DmsEducationOrganizationProjectionSettings`, section of the same name)

| Key | Default | Notes |
| --- | --- | --- |
| `DmsBaseUrl` | unset → `NotConfigured` | absolute http(s), may include a base path, no query/fragment |
| `Credentials:ClientId` / `Credentials:ClientSecret` | — | single-tenant credential and multi-tenant fallback |
| `TenantCredentials:<tenant>:ClientId` / `ClientSecret` | — | case-insensitive tenant key |
| `ContractVersions` | `["educationOrganizationProjection.v1"]` | versions this CMS understands |
| `PageSize` | `2000` | `1..10000`, sent as `limit` |
| `DiscoveryTimeoutSeconds` / `TokenRequestTimeoutSeconds` / `PageRequestTimeoutSeconds` | `10` / `30` / `60` | per request |
| `TotalReadTimeoutSeconds` | `600` | whole logical read incl. restarts |
| `MaxPages` / `MaxItems` / `MaxResponseBodyBytes` / `MaxWalkRestarts` | `2000` / `500000` / `8388608` / `3` | `MaxResponseBodyBytes ≥ PageSize × 2048 + 1024` |
| `TokenExpirySafetyMarginSeconds` | `60` | |
| `DiscoveryCacheSeconds` | `300` | |

Validation (when `DmsBaseUrl` is set): every referenced credential pair non-empty; numeric bounds in range; the body-size rule. Secrets are never logged and never included in `ToString`.

### 5.3 Discovery lookup and template resolution (both templates)

1. `GET {DmsBaseUrl}/{tenant}` (multi-tenant) or `GET {DmsBaseUrl}/` (single-tenant), `Accept: application/json`, no credentials, timeout `DiscoveryTimeoutSeconds`; tolerant JSON (unknown members ignored). Classification per §5.5.
2. Require `urls.oauth` (string), `urls.educationOrganizationProjection` (string), `educationOrganizationProjection.contractVersions` (non-empty string array); missing projection entries → Permanent `Unsupported`; wrong types → Permanent `DiscoveryInvalid`.
3. Version: highest common with `ContractVersions`; none → Permanent `UnsupportedContract`.
4. **Containment**, both templates, after substitution: parse as absolute `Uri`; normalize (lower-case scheme/host, default port elided, percent-decoding normalized, dot segments rejected); scheme/host/port equal `DmsBaseUrl`'s; `DmsBaseUrl`'s path segments are a prefix of the template's path segments **at segment boundaries** (`/api` admits `/api/x`, not `/api-other`); otherwise Permanent `DiscoveryInvalid`.
5. Placeholders `{name}` in both paths → the store context value whose key equals `name` case-insensitively, `Uri.EscapeDataString`-encoded per segment; unresolved placeholder (including residual `{tenant}`) → Permanent `TargetNotRoutable`, no request.
6. Cached per tenant for `DiscoveryCacheSeconds`; invalidated on `TargetNotFound`, `Unauthorized`, `DiscoveryInvalid`.

### 5.4 Token acquisition and complete-read loop

**Token**: `POST <resolved oauth URL>`, `Authorization: Basic base64(clientId:secret)`, form `grant_type=client_credentials`; tolerant JSON; requires `access_token` (non-empty string), `expires_in` (positive integer), `token_type` equal to `bearer` ignoring case. Cache key `(tenantKey, clientId)` independent of path; valid until `expires_in − TokenExpirySafetyMarginSeconds`; single-flight per key (`SemaphoreSlim`); honours the caller token. A page 401 invalidates the cached token and retries that page once; a second 401 → Permanent `Unauthorized`.

**Loop**:

```
deadline := now + TotalReadTimeoutSeconds; restarts := 0
attempt:
  items := []; cursor := null; seen := {}; pages := 0; previousId := null
  loop:
    if now ≥ deadline → Transient Timeout
    GET url?dataStoreId=&limit=PageSize&contractVersion=v[&cursor=]   (Bearer; per-page timeout linked to deadline and caller ct)
    classify (§5.5)
      409 projection-changed → restarts < MaxWalkRestarts ? (restarts++; goto attempt) : Transient ProjectionChanged
      400 invalid-cursor      → cursor != null && restarts < MaxWalkRestarts ? (restarts++; goto attempt) : Permanent InvalidRequest
    read body with hard cap MaxResponseBodyBytes → exceeded ⇒ Permanent LimitExceeded
    parse strictly: required contractVersion, dataStoreId, nextCursor (nullable, must be present), items;
      items' five members required (shortNameOfInstitution, parentId nullable but present);
      unknown members, duplicate properties, wrong types ⇒ Permanent MalformedResponse
    contractVersion == requested && dataStoreId == requested          else Permanent MalformedResponse
    pages++ > MaxPages ⇒ Permanent LimitExceeded
    pages > 1 && items is empty ⇒ Permanent MalformedResponse            // no empty continuation page
    nextCursor != null && items is empty ⇒ Permanent MalformedResponse
    each item: discriminator ∈ four; name non-empty; id > previousId; parentId != id   else Permanent DataInvalid
    items.Count > MaxItems ⇒ Permanent LimitExceeded
    nextCursor == null → return Success(items, version, pages, restarts)
    nextCursor == cursor || nextCursor ∈ seen ⇒ Permanent MalformedResponse; seen += nextCursor; cursor := nextCursor
```

Limits `MaxPages`, `MaxItems`, `seen` are per attempt; `MaxWalkRestarts` bounds attempts; `TotalReadTimeoutSeconds` spans everything including Discovery and token calls. Caller cancellation is checked before classifying any timeout and propagates as `OperationCanceledException`. No partial item list is ever returned.

### 5.5 Total classification matrix

Evaluation order per stage: caller cancellation → transport exception → HTTP status class → parsed problem `type` (from `application/problem+json` bodies ≤ 64 KB; unparsable ⇒ "no type").

| Stage | Observation | Category | Code |
| --- | --- | --- | --- |
| any | caller token cancelled | — | `OperationCanceledException` |
| any | per-call or total deadline | Transient | `Timeout` |
| any | `HttpRequestException`, `IOException`, socket errors | Transient | `NetworkError` |
| any | 3xx | Permanent | `DiscoveryInvalid` |
| any | 429 | Transient | `RateLimited` |
| any | 500 with type exactly `urn:ed-fi:api:system:configuration:security` (ordinal match) | Permanent | `Forbidden` |
| any | other 5xx (any or no type, including the non-problem `{message, traceId}` 500 body) | Transient | `ServiceUnavailable` |
| Discovery | 200 valid | — | continue |
| Discovery | 200 missing projection entries | Permanent | `Unsupported` |
| Discovery | 200 malformed / wrong types / containment failure | Permanent | `DiscoveryInvalid` |
| Discovery | 404 | Permanent | `TargetNotFound` |
| Discovery | other 4xx / 2xx≠200 | Permanent | `UnexpectedResponse` |
| Token | 200 valid | — | continue |
| Token | 200 malformed | Permanent | `MalformedResponse` |
| Token | 400, 401 | Permanent | `TokenRejected` |
| Token | other 4xx / 2xx≠200 | Permanent | `UnexpectedResponse` |
| Page | 200 (validated per §5.4) | — | continue |
| Page | 2xx≠200 | Permanent | `UnexpectedResponse` |
| Page | 400 `parameter-validation-failed` / other / no type | Permanent | `InvalidRequest` |
| Page | 400 `invalid-cursor` | restart rule, then Permanent | `InvalidRequest` |
| Page | 400 `unsupported-contract-version` | Permanent | `UnsupportedContract` |
| Page | 401 (after one refresh) | Permanent | `Unauthorized` |
| Page | 403 | Permanent | `Forbidden` |
| Page | 404 (any type) | Permanent | `TargetNotFound` |
| Page | 409 `projection-changed` (exact) | restart rule, then Transient | `ProjectionChanged` |
| Page | 409 `target-schema-incompatible`, `target-provider-unsupported` | Permanent | `TargetSchemaIncompatible` |
| Page | 409 `projection-unsupported` | Permanent | `Unsupported` |
| Page | 409 `projection-too-large` | Permanent | `LimitExceeded` |
| Page | 409 `projection-data-invalid` | Permanent | `DataInvalid` |
| Page | 409 other / no type | Permanent | `UnexpectedResponse` |
| Page | other 4xx | Permanent | `UnexpectedResponse` |

Registered job error codes (grammar `^[A-Za-z][A-Za-z0-9]{0,63}$`, fixed messages without `{}` or control characters): `EdOrgProjectionNotConfigured`, `EdOrgProjectionDiscoveryInvalid`, `EdOrgProjectionUnsupported` (also for `UnsupportedContract`), `EdOrgProjectionUnauthorized` (also for `TokenRejected`), `EdOrgProjectionForbidden`, `EdOrgProjectionTargetNotFound`, `EdOrgProjectionTargetNotRoutable`, `EdOrgProjectionTargetSchemaIncompatible`, `EdOrgProjectionDataInvalid`, `EdOrgProjectionInvalidRequest`, `EdOrgProjectionMalformedResponse`, `EdOrgProjectionUnexpectedResponse`, `EdOrgProjectionLimitExceeded`. Transient codes map to no job code; the job layer retries and ends in `AttemptsExhausted` if never successful.

### 5.6 Diagnostics and redaction

Logged (Information/Warning): sanitized tenant (`LoggingUtility.SanitizeForLog`), `dataStoreId`, stage, code, category, HTTP status, sanitized problem `type`, sanitized DMS `correlationId`, pages read, restarts, elapsed ms, resolved URL **path without query**. Never logged at any level: `Authorization` headers, client secrets, access tokens, request or response bodies, cursor values, exception messages (type chain only). `RemoveAllLoggers()` on the named client; `ProjectionHttpClientLogger` logs method, path-without-query, status, elapsed. The sanitized values are what the `Failure` record carries. Tests at `LogLevel.Trace` with hostile values in problem bodies, headers, discovery documents and exception messages.

## 6. Projection validation rules (whole set, every page)

Internal row per arm: School `(id, SCH, name, short, localEducationAgencyRef)`; LEA `(id, LEA, name, short, parentLocalEducationAgencyRef, educationServiceCenterRef, stateEducationAgencyRef)`; ESC `(id, ESC, name, short, stateEducationAgencyRef)`; SEA `(id, SEA, name, short)`.

**Step 1 — structural**: ids unique across the set (else `DuplicateIdentifier`); non-core arms absent (compiler guarantee).

**Step 2 — every populated reference resolves to a row of its expected type**:

| Reference | Expected target |
| --- | --- |
| School.localEducationAgencyRef | LEA |
| LEA.parentLocalEducationAgencyRef | LEA, ≠ own id |
| LEA.educationServiceCenterRef | ESC |
| LEA.stateEducationAgencyRef | SEA |
| ESC.stateEducationAgencyRef | SEA |

Absent or wrong-type → `UnresolvedReference`, including **non-selected** references. Multiple valid references are legitimate; no chain-agreement rule.

**Step 3 — cycles**: LEA → parentLocalEducationAgencyRef must be acyclic (self-link = length 1) → `ParentCycle`.

**Step 4 — precedence** (after steps 1–3): School → `localEducationAgencyRef`; LEA → `parentLocalEducationAgencyRef ?? educationServiceCenterRef ?? stateEducationAgencyRef`; ESC → `stateEducationAgencyRef`; SEA → `null`.

| Situation | Outcome |
| --- | --- |
| LEA with valid parent LEA (any valid ESC/SEA too) | parent = parent LEA |
| LEA without parent LEA, valid ESC | parent = ESC (legitimate fallback) |
| LEA with only valid SEA | parent = SEA (legitimate fallback) |
| LEA with none | `null` |
| School with/without valid LEA | LEA / `null` |
| ESC with/without valid SEA | SEA / `null` |
| SEA | `null` |
| Duplicate id | 409 `projection-data-invalid` |
| Any populated reference unresolved or wrong type (selected or not) | 409 `projection-data-invalid` |
| Self-link or longer cycle over parent-LEA links | 409 `projection-data-invalid` |
| `nameOfInstitution` or `shortNameOfInstitution` not well-formed UTF-16 (a lone high or low surrogate; SQL Server `nvarchar` can store one) anywhere in the set | 409 `projection-data-invalid`, detected in validation before hashing; never replaced by U+FFFD and never an unexpected 500 (the digest's strict encoder throws only as a backstop) |
| Non-core arm | excluded |
| Mapping element missing | 409 `projection-unsupported` |
| Set larger than `MaxProjectionRows` | 409 `projection-too-large` |

All checks run over the complete set before the digest is compared and before slicing.

## 7. CMS complete-read edge cases

| Case | Outcome |
| --- | --- |
| Body larger than `MaxResponseBodyBytes` | Permanent `LimitExceeded` |
| Unsupported or missing version | Permanent `UnsupportedContract` / `Unsupported` |
| `dataStoreId` echo differs | Permanent `MalformedResponse` |
| Malformed JSON, unknown members, missing `nextCursor`/`shortNameOfInstitution`/`parentId`, null `nameOfInstitution` | Permanent `MalformedResponse` |
| Empty continuation page; non-progressing or previously seen cursor | Permanent `MalformedResponse` |
| Later page fails | classified per §5.5; items discarded |
| Per-call or total timeout | Transient `Timeout` |
| Caller cancellation | `OperationCanceledException` |
| Items out of order or duplicated across pages | Permanent `DataInvalid` |
| `projection-changed` beyond `MaxWalkRestarts` | Transient `ProjectionChanged` |
| DMS 409 `projection-too-large` | Permanent `LimitExceeded` |

## 8. PostgreSQL and SQL Server behavior, schema validation, persistence

- Provider readers own connection and transaction (§4.3, §3.9 stages). `Serializable` on SQL Server may block concurrent writers to the four tables for the duration of the statement and commit and may make the reader a deadlock victim (1205) or hit `LOCK_TIMEOUT` (1222); both are transient and roll back. Lock timeout bounds waiting; `ReadCommandTimeoutSeconds` bounds execution; on either the transaction is rolled back and the connection disposed. No duration promised; steps 2.5/2.6 measure.
- Snapshot isolation is not used. DMS provisioning enables it (and RCSI) on newly created SQL Server databases but not on every existing one (§1.5 item 7, corrected), so the provider read stays `SERIALIZABLE` (decided at the step 2.5 approval, §0.0 "Isolation decision"). Supporting both modes would need capability detection, a fallback and another concurrency test matrix; a snapshot optimization needs a separate focused design review, and would still need the per-page digest comparison, because every page is its own transaction.
- No new DMS tables, columns or DDL; `RelationalMappingVersion` stays `v3`; `IRelationalCommandExecutor` unchanged.
- CMS persistence (revised at the step 1.1 checkpoint, §0.0 "Step 1.1"): one DbUp script per provider, `Backend.Postgresql/Deploy/Scripts/0035_Add_EducationOrganizationProjection_Claim.sql` and `Backend.Mssql/Deploy/Scripts/0035_Add_EducationOrganizationProjection_Claim.sql`. A catalog loads the embedded claims only while its claims tables are empty, and a claim absent from the stored hierarchy cannot be granted, so each script (1) inserts the `ResourceClaim` row and (2) appends the claim, with no claim-set grants, to the stored `ClaimsHierarchy` document. Both steps are additive and run only when no equivalent claim exists. Equivalence follows CMS's claim lookup (exact, then `StringComparison.OrdinalIgnoreCase`); for the all-ASCII claim name that is an ASCII-only name of the same length equal after ASCII upper-casing, evaluated with `COLLATE "C"` (PostgreSQL) and `Latin1_General_100_BIN2` plus a `DATALENGTH` equality (SQL Server, whose `=` ignores trailing spaces even under a binary collation) so it does not depend on the database collation. On SQL Server, a `ResourceClaim` row that `UX_ResourceClaim_ClaimName` treats as equal to the claim name but CMS does not (for example one with trailing spaces) fails the deployment before any change with a diagnostic naming the row's `Id`; the script is not journaled and the operator's row is kept for them to rename or remove. The hierarchy is searched at every depth. An existing equivalent claim, in any letter case and at any depth, is kept as is: not renamed, replaced or duplicated, its authorization and grants unchanged, and the hierarchy row, including `LastModifiedDate`, `LastModifiedAt` and `ModifiedBy`, untouched. When the claim is appended, `LastModifiedDate` (the hierarchy's concurrency token) advances, so a writer holding the pre-upgrade hierarchy gets a conflict instead of overwriting it. A catalog with no stored hierarchy yet is not changed by the append and receives the claim from the embedded claims on first load. Rerunning either script changes nothing. The initial-load and `reload-claims`/`upload-claims` paths are not used for upgrade: the first skips populated catalogs and the others replace the hierarchy and the non-reserved claim sets. Upgrade tests cover pre-populated catalogs on both providers (§9 step 1.1).
- Execution/materialization classification is tested on both providers by dropping a column and by changing a column type in a test database.

## 9. Phases and steps

Every step: narrowly bounded edits, tests that fail if the step is reverted, `dotnet csharpier format` on touched files, a self-review against its ACs, a **local** commit (no push), a report (SHA, file-by-file summary, ACs, test commands/results, residual risks, next step), and an **approval checkpoint** before the next step. Setup or fixture failures in any test are reported as such, distinct from projection outcomes.

### Phase 0 — Contract artifact (after spec approval, before any code)

**0.1 Check in the service contract.** Files: `reference/design/edorg-projection-DMS-1440/education-organization-projection.v1.openapi.yaml` (paths, parameters incl. the repeated-parameter rule, envelope and item schemas with `required` members, every problem type with examples, Discovery fragment, cursor and digest semantics as prose); `reference/design/edorg-projection-DMS-1440/contract/examples/{success-page,success-last-page,empty,problem-*.json}`; `docs/EDUCATION-ORGANIZATION-PROJECTION.md` (contract section). Risks: none. Tests: none. Checkpoint.

### Phase 1 — Permission

**1.1 CMS: seed the service claim.** Files: `Backend/Claims/Standards/ds52/Claims.json`, `ds61/Claims.json` (claim appended as the last root claim); new `Backend.Postgresql/Deploy/Scripts/0035_Add_EducationOrganizationProjection_Claim.sql` and its `Backend.Mssql` twin (`ResourceClaim` row plus stored-hierarchy append, §8); `Backend.Postgresql.Tests.Integration/ClaimsHierarchyMetadata.json`, `Backend.Mssql.Tests.Integration/ClaimsHierarchyMetadata.json`; `Tests.E2E/TestData/Claims/authoritative-composition.json`; `eng/CmsHierarchy/ClaimSetFiles/AuthorizationHierarchy.json`; `Given_Embedded_Claims_Json`; `Tests.E2E/ResourceClaimSeedDataTests.cs` (reads every resource-claim deploy script); `Backend.Mssql.Tests.Integration/DeployTests.cs` (deployed count 430); new `EducationOrganizationProjectionClaimTests.cs` in both integration projects and `EducationOrganizationProjectionClaimCollationTests.cs` (SQL Server). Risks: claim-set mutation integrity check; E2E composition fixture drift; the stored-hierarchy append. Tests: embedded claims contain the claim with exactly `Read` and no claim-set grants; on both providers, fresh, upgraded (pre-populated, with a custom grant), existing-nested-exact and existing-nested-case-variant catalogs: one metadata row and one hierarchy claim, unchanged pre-existing claims, grants and claim sets, the hierarchy's audit and concurrency values unchanged when the claim already exists, grant/revoke through the repository, no-op rerun; SQL Server case-sensitive-collation database for the absent and case-variant cases; CMS E2E claims composition. Checkpoint.

**1.2 DMS: generalize service-claim authorization.** Files: `Core/Middleware/ServiceClaimAuthorizationMiddleware.cs` (`ServiceClaimRequirement(string ClaimUri, Func<RequestInfo,string> RequiredAction, string ClaimDescription)`, where `ClaimDescription` is the phrase naming the claim in that requirement's security-configuration response), `Core/Security/Conventions.cs` (projection claim constant), `Core/ApiService.cs` (identity wiring only), `Core.Tests.Unit/Middleware/ServiceClaimAuthorizationMiddlewareTests.cs`. Each requirement carries its own denial/misconfiguration wording: the projection requirement uses generic service-claim wording, and identity responses keep their current wording and pinned contract fixtures unchanged (no global message change). Risks: identity behavior change, pinned by existing tests that must pass unchanged. Checkpoint.

### Phase 2 — DMS endpoint

**2.1 Options and toggle.** Files: `Core/Configuration/AppSettings.cs` (toggle), `Core/Configuration/EducationOrganizationProjectionSettings.cs` (options record), Core `AppSettingsValidator` (validation), `appsettings.json`, compose/env files, `docs/CONFIGURATION.md`. Tests: binding/validation; startup failure via `WebApplicationFactory` on out-of-range values. Checkpoint.

**2.2 Request model, cursor codec, digest, problem types, repeated-parameter plumbing.** Files: `Core/EducationOrganizationProjection/{EducationOrganizationProjectionRequest,ProjectionCursor,ProjectionCursorCodec,ProjectionDigest,ProjectionContractVersions}.cs`, `ParseEducationOrganizationProjectionRequestMiddleware.cs`, `Core/External/Frontend/FrontendRequest.cs` (`RepeatedQueryParameterNames`, default empty), `Response/FailureResponse.cs` factories. Tests: codec round-trip and strict-decode rejection table; binding mismatch; expiry, future-dated, preserved walk timestamp (fake clock); digest golden vectors (§4.2) incl. the signed-boundary vectors; codec round-trip of negative, zero, `Int64.MinValue` and `Int64.MaxValue` positions and rejection of `+`, leading zeroes and `-0`; a set with negative ids returns them on the first page (no zero sentinel); parameter table incl. repeats with mixed case; order-of-operations: cursor binding uses the request's tenant/qualifiers. Checkpoint.

**2.3 Target resolution, schema and mapping translation.** Files: `Core/Middleware/{ResolveEducationOrganizationProjectionTargetMiddleware,ValidateEducationOrganizationProjectionTargetSchemaMiddleware,ResolveEducationOrganizationProjectionMappingSetMiddleware}.cs`. Tests with fakes: every §3.5 state including matching PostgreSQL, matching SQL Server, both mismatches, `Missing`, `Unknown`; exactly one `LoadDataStores` on a miss; CMS outage → 503 not 404; transient fingerprint read failure → 503 `target-unavailable`, never `service-configuration-error`; missing connection string → 503 `service-configuration-error`; a catalog with an undecryptable primary connection string → 503 `service-unavailable` with its exact body and media type; `MappingSetUnavailableException("hostile", ["Server=…;Password=…"])` → 409 fixed body with neither string in body or Trace logs; cancelling one request during an active shared fingerprint read ends only its wait while another waiter receives the result (signal-controlled). Upstream redaction (review): Trace-level capture of every logger category with the real catalog provider (connection, error status, malformed JSON, token and undecryptable failures), the real tenant snapshot (connection and malformed JSON), and the real mapping provider and cache (success, failure, missing required pack, cache hit), with fakes only for HTTP and compilation: no exception objects, hostile text or hashes in any record. Checkpoint.

**2.4 Backend contract and SQL compiler.** Files: `Backend.External/{IEducationOrganizationProjectionSetReader,EducationOrganizationProjectionContracts}.cs` (internal row with reference slots; result union `Set | TooLarge | MappingIncompatible | SchemaIncompatible | TargetUnavailable`); `Backend.Plans/EducationOrganizationProjectionSqlCompiler.cs`. Tests (`Backend.Plans.Tests.Unit`): SQL snapshots per dialect from the ds-5.2 model; incompatibility reasons (missing view, arm, column, binding); extension arm ignored; per-arm slot columns; row cap clause. Checkpoint.

**2.5 Provider readers, stage boundary, provider measurement.** Files: `Backend.Postgresql/PostgresqlEducationOrganizationProjectionSetReader.cs`, `Backend.Mssql/MssqlEducationOrganizationProjectionSetReader.cs`, shared `Backend/EducationOrganizationProjectionStagedRead.cs` (the four stages, protected cleanup, internal test observer; approved at review), `Backend/EducationOrganizationProjectionRowReader.cs` and `EducationOrganizationProjectionExecutionClassifier.cs` (code tables), DI registrations. Integration tests on both engines: §6 table with exact values incl. slot-corruption and non-selected dangling cases (FK dropped in test); engine-appropriate Unicode fixtures; nulls; int64; empty store; cap; A-1; §4.6 suite (seven items); dropped column and changed column type → `SchemaIncompatible`; unresolvable host and malformed connection option → `TargetUnavailable(Acquire)`; lock/command timeout and deadlock → `TargetUnavailable(Execute)`; Trace-level log capture proves only `Describe` output; acquisition-path logging audit recorded. **Measurement deliverable (provider)**: statement elapsed, bytes transferred, writer impact at the cap on both engines, written into the step report and docs. Checkpoint (includes acceptance of the measurement).

**2.6 Handler, pipeline, facade, handler measurement.** Files: `Core/Handler/EducationOrganizationProjectionHandler.cs`, `Core/EducationOrganizationProjection/{ProjectionSetValidator,IProjectionProcessingObserver}.cs`, `ProjectionDigest.cs` (hashing checkpoints), `ApiService.CreateEducationOrganizationProjectionPipeline`, `IApiService.GetEducationOrganizationProjection(FrontendRequest, CancellationToken)`, `DmsCoreServiceExtensions` (step 2.3 middlewares); tests `Core.Tests.Unit/{EducationOrganizationProjection/ProjectionSetValidatorTests,Handler/EducationOrganizationProjectionHandlerTests,Handler/EducationOrganizationProjectionHandlerMeasurementTests,Pipeline/PipelineOrderingTests}.cs`, `Frontend.AspNetCore.Tests.Unit/EducationOrganizationProjectionPipelineTests.cs`, `Tests.Integration/Scenarios/EducationOrganizationProjectionWalkMeasurement.cs`. The pipeline records the resolved store's primary as the effective target before any database step (§0.0 "Step 2.6"). Tests with a fake set reader: validation table (duplicate, self, two- and three-node cycle, slot corruption, non-selected dangling, contradiction outside the requested page, a lone high or low surrogate in either name → 409 before `Hashing` is entered); digest compare → 409; slicing rules (never an empty continuation page); unknown literal → 409 `projection-unsupported`; result-union mapping to §3.6; cancellation at a named validation checkpoint via the observer (hashing never entered, no response); 50,000-LEA valid chain without stack growth. **Measurement deliverable (handler)**: allocations per page, validation/hash time, full 25-page walk time at the cap. Checkpoint (includes acceptance of the measurement).

**2.7 Frontend module and Discovery.** Files: `Frontend/Modules/EducationOrganizationProjectionEndpointModule.cs` (toggle, `FixedRoutePattern`, `no-store`, repeated-parameter detection), `AspNetCoreFrontend.GetEducationOrganizationProjection`, `DiscoveryEndpointModule.cs`, `urls.Count` test updates. Tests: module mapping per mode and toggle; Discovery content per mode and toggle; `RequestAborted` passed through; repeated names (mixed case) reach Core. Checkpoint.

**2.8 API integration tests (both engines).** Files: `Tests.Integration/Scenarios/EducationOrganizationProjectionScenario.cs`, dialect wrappers, parity rows, a claim-set double granting the new claim (the smoke claim set must not). Coverage: AC 2 denial matrix; §3.5 states incl. provider mismatch and `Missing`; precedence through HTTP; paging and int64; fail-closed cases; abort; no `Ed-Fi:` substring; `no-store`; every problem type's exact `type`; diagnostic-bearing mapping failure; dropped column and unresolvable host with cached fingerprint verdict; body-bound regression with engine-appropriate fixtures. Checkpoint.

### Phase 3 — CMS reader

**3.1 Settings, DI, error codes, HttpClient.** Files: `Backend/EducationOrganizationProjection/{DmsEducationOrganizationProjectionSettings,DmsEducationOrganizationProjectionSettingsValidator,EducationOrganizationProjectionJobErrorCodes,DmsEducationOrganizationProjectionHttpClient,ProjectionHttpClientLogger,ServiceCollectionExtensions}.cs`; `WebApplicationBuilderExtensions` call; `appsettings.json` empty section; `docs/CONFIGURATION.md`. Tests: validator table incl. `MaxResponseBodyBytes ≥ PageSize × 2048 + 1024`; startup failure via `WebApplicationFactory`; code grammar and registration; logger emits path only. Checkpoint.

**3.2 Discovery client and template resolution.** Files: `IDmsDiscoveryClient`, `DmsDiscoveryClient`, `ProjectionUrlTemplateResolver`. Tests with a fake `HttpMessageHandler` and fake clock: single/multi-tenant document URL; placeholder substitution for both templates (case-insensitive key, encoding); unresolved placeholder → no request; containment (`/api` vs `/api-other`, dot segments, percent-encoding, default ports, case); 3xx; 404; malformed; cache and invalidation. Checkpoint.

**3.3 Token provider and cache.** Files: `IProjectionServiceTokenProvider`, `ProjectionServiceTokenProvider`. Tests: Basic header never logged; caching until expiry minus margin (fake clock); single-flight under concurrency; classification rows; per-tenant credential selection; cancellation propagates. Checkpoint.

**3.4 Page reader and complete-read loop.** Files: `EducationOrganizationProjectionReader`, `ProjectionResponseParser` (strict options), `ProjectionFailureClassifier`. Tests: contract example replay; §5.4 loop rules (empty continuation, seen cursor, limits per attempt, restart bound, deadline across restarts); every §5.5 row incl. "otherwise" rows and malformed problem bodies; strict parsing cases incl. missing `nextCursor`; 401 refresh-once; `projection-too-large`; supplementary-character body case; timeout vs caller cancellation with TCS-gated handlers and fake clock (no `Task.Delay`). Checkpoint.

**3.5 Boundary guards and redaction.** Files: `Backend.Tests.Unit/EducationOrganizationProjection/BackendProjectBoundaryTests.cs` (scans `EdFi.DmsConfigurationService.Backend.csproj` for `EdFi.DataManagementService.*`, `Npgsql`, `Microsoft.Data.SqlClient`), `RedactionTests.cs` (Trace-level capture at every stage with hostile values); `docs/CMS-BACKGROUND-JOBS.md` cross-reference. Checkpoint.

### Phase 4 — Cross-component and documentation

**4.1 Instance Management E2E (DMS side).** Files: `Features/EducationOrganizationProjection/EducationOrganizationProjection.feature`, step definitions, `Management/{ConfigServiceClient,DmsApiClient}.cs` additions. Fixture: seed a known hierarchy into `Tenant_255901` / `255901/2025` through the DMS API using the fixture's **resource-authorized** client (SEA `1`; ESC `10` → SEA; LEA `100` → ESC `10` and SEA `1` (parent = ESC); LEA `101` → parent LEA `100`; Schools `100001` → LEA `100`, `101001` → LEA `101`, and `900001` with no LEA); scenario-owned cleanup in reverse dependency order; seeding failure fails the scenario with a setup message before any projection assertion. Scenarios: claim set import granting the projection claim; four credential shapes (projection-only, resource-only, identity-only, cross-tenant); Discovery advertises both templates; `limit=2` walk with exact items and parents (`100` → `10`, `101` → `100`, `100001` → `100`, `900001` → `null`, `10` → `1`, `1` → `null`); qualifier mismatch 404; cross-tenant 404; 401/403 cases; projection token denied on `/data` GET and POST. Both engines via existing lanes. Checkpoint.

**4.2 Production-reader integration tests (CMS side, maintained).** Project `src/config/tests/EdFi.DmsConfigurationService.Tests.DmsProjectionE2E` (NUnit; references `EdFi.DmsConfigurationService.Backend` only; in the CMS solution; excluded from `build-config.ps1 UnitTest` by the `*.Tests.Unit` filter).
- **Inputs**: `Invoke-WithInstanceE2ETestProcessContext` additionally exports `INSTANCE_E2E_DMS_BASE_URL`, `INSTANCE_E2E_CONFIG_SERVICE_URL`, `INSTANCE_E2E_CONFIG_ADMIN_CLIENT_ID`, `INSTANCE_E2E_CONFIG_ADMIN_CLIENT_SECRET` (the same values the Reqnroll suite uses as `DMS_API_URL`, `CONFIG_SERVICE_URL` and the `SetupHooks` system-admin credential), beside the existing route manifest and tenant fixture variables. The project fails fast with a distinct setup message when any is missing; it never skips.
- **Provisioning** through real CMS endpoints: claim set granting the projection claim; one application per tenant with `dataStoreIds: []`; one client each; cleanup in `OneTimeTearDown`. Hierarchy seeding into `255901/2025` through the DMS API with the fixture tenant's resource client (same shape as 4.1), removed afterwards; seeding failures are reported as setup failures.
- **Hosting**: `ServiceCollection` with `AddDmsEducationOrganizationProjectionReader(configuration)` (per-tenant credentials, `DmsBaseUrl`), plus `services.AddHttpClient(DmsEducationOrganizationProjectionHttpClient.Name).AddHttpMessageHandler(() => faultHandler)` for fault injection and request recording.
- **Scenarios**: (a) multi-page read (`PageSize=2`) of the seeded hierarchy, exact items and parents; (b) **empty store** `Tenant_255902` / `255902/2024` — precondition: a direct probe returns zero items, otherwise the test **fails**; then `Success` with zero items and `PageCount == 1`; (c) unknown store id → `TargetNotFound`; (d) store registered with a connection string naming a non-existent database → `Transient` (DMS reload-on-miss makes the first call deterministic), then deleted; (e) store registered without a `schoolYear` context → `TargetNotRoutable` with no page request recorded; (f) injected 503 on page 3 → `Transient`, zero items exposed; (g) injected `projection-changed` 409 on page 2 twice → success after restarts; four times → `Transient ProjectionChanged`; (h) caller cancellation during page 2 → `OperationCanceledException`.
- **Execution**: `RunInstanceE2E` runs this project once per engine when `TestFilter` is empty or names `instance-management-ci-shard-1`: `dotnet test <project> -c $Configuration --logger "trx;LogFileName=$testResults/EdFi.DmsConfigurationService.Tests.DmsProjectionE2E.<engine>.trx"` (with build and restore; not `--no-build`), inside the same process context, under `Invoke-Execute` so a non-zero exit fails the target. A post-run guard parses the TRX `Counters` and fails the target when `total < 8` or `passed != total`. Both CI lanes therefore execute it. A-5 risk and fallback recorded. Checkpoint.

**4.3 Documentation.** `docs/EDUCATION-ORGANIZATION-PROJECTION.md` complete (contract, Discovery, pipeline, digest/cursor semantics, measured cost tables from 2.5/2.6, stage classification tables, provider-dialect semantics, provisioning in self-contained and Keycloak modes incl. rotation and per-tenant credentials, CMS settings, failure taxonomy, upgrade order, redaction rules); `docs/CONFIGURATION.md`, `docs/ROLES-SCOPES.md`, `docs/MULTI-TENANCY-GETTING-STARTED.md`, `docs/CLAIMS-LOADING-GUIDE.md`, `src/dms/tests/EdFi.InstanceManagement.Tests.E2E/README.md`. Checkpoint.

### Phase 5 — Final validation (§11), cumulative diff review, push only after approval.

## 10. Alternatives and operational limits

Alternatives rejected: configured-role authorization (not dedicated in self-contained mode); no tenant binding (cross-tenant exposure); version-based fence (unsound); shared server-side snapshot (cost, replica storage, hides database replacement); SQL-side digest (dialect duplication); single-response projection (contradicts the pinned contract); signed cursor (no security gain); classifying every 409 as permanent (kills `projection-changed` restarts); Debug-level exception messages (redaction).

Operational limits: page ≤ 2 000 by default (≤ 4 MiB); set ≤ 50 000 rows by default (409 `projection-too-large` beyond, distinct from schema incompatibility); full-cap walk at the default page size = 25 pages, each re-reading the set; cursor lifetime 60 min from walk start; CMS read ≤ 10 min, ≤ 2 000 pages per attempt, ≤ 500 000 items, ≤ 8 MiB per body, ≤ 3 restarts; one token per CMS replica per credential (size `BearerTokenPerClientLimit` ≥ 2 × replicas + headroom).

## 11. Final validation plan

Prerequisites: Docker; PostgreSQL and SQL Server integration containers per the DMS and CMS local-setup notes (DMS integration lanes need the tuned containers and `127.0.0.1`; CMS integration needs trust-auth Postgres 5432 plus `ConnectionStrings__MssqlAdmin` for the MSSQL half); `pwsh`; .NET 10 SDK. Known pre-existing Windows failures to diff against a merge-base run: `Backend.Cdc` and `SchemaTools` unit tests. Format gate scoped to touched directories.

```powershell
# Formatting
dotnet csharpier check src/dms/core src/dms/backend src/dms/frontend src/dms/tests src/config

# DMS unit
./build-dms.ps1 UnitTest -Configuration Release

# DMS backend integration, both engines (env per local setup notes)
./build-dms.ps1 IntegrationTest -Configuration Release -DatabaseEngine postgresql
./build-dms.ps1 IntegrationTest -Configuration Release -DatabaseEngine mssql

# DMS API-level integration, both engines
dotnet test src/dms/tests/EdFi.DataManagementService.Tests.Integration/EdFi.DataManagementService.Tests.Integration.csproj -c Release --filter "Category=PostgresqlIntegration"
dotnet test src/dms/tests/EdFi.DataManagementService.Tests.Integration/EdFi.DataManagementService.Tests.Integration.csproj -c Release --filter "Category=MssqlIntegration"

# CMS unit; CMS integration (PostgreSQL always; MSSQL when ConnectionStrings__MssqlAdmin is set)
./build-config.ps1 Build -Configuration Release
./build-config.ps1 UnitTest -Configuration Release
$env:ConnectionStrings__MssqlAdmin = "<per local setup notes>"
./build-config.ps1 IntegrationTest -Configuration Release

# CMS E2E — the three CI lanes
./build-config.ps1 E2ETest -Configuration Release -IdentityProvider self-contained
./build-config.ps1 E2ETest -Configuration Release -IdentityProvider self-contained -EnvironmentFile "./.env.config.mssql.e2e" -E2ETestFilter "TestCategory=MssqlRepresentative"
./build-config.ps1 E2ETest -Configuration Release -IdentityProvider self-contained -EnvironmentFile "./.env.config.mssql.multitenant.e2e" -E2ETestFilter "TestCategory=MssqlMultitenantRepresentative"

# Cross-component: Instance Management E2E (DMS features + production-reader project), both engines
pwsh ./build-dms.ps1 InstanceE2ETest -Configuration Release
pwsh ./build-dms.ps1 InstanceE2ETest -Configuration Release -DatabaseEngine mssql

# Discovery regression in the standard DMS E2E shard carrying DiscoveryAPI.feature
./build-dms.ps1 E2ETest -Configuration Release -IdentityProvider self-contained -EnvironmentFile './.env.e2e' -TestFilter 'Category=@e2e-ci-shard-1'
```

Deliverables attached to the final validation report: the step 2.5 and 2.6 measurement tables; TRX count-guard output for the reader project on both engines. Review checklist before push: authentication denial matrix with four credential shapes; tenant isolation; exact hierarchy values incl. slot-corruption and non-selected-reference cases; nullable fields; large ids; empty store precondition; §4.6 concurrency suite on both engines; stage-separated classification (dropped column and changed type permanent; unresolvable host transient) on both providers; engine-appropriate Unicode body bound; cache lag (store registered after DMS start → reload on miss); failed later pages through the production reader; Trace-level redaction in both services; `RelationalMappingVersion == "v3"`; no CMS→DMS reference; lock files unchanged.
