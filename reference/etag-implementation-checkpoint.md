# ETag → ContentVersion implementation — checkpoint

**Purpose:** Resume point so context can be cleared. Captures what's done, what's
left, and how to continue implementing the ADR
(`reference/adr-etag-from-content-version.md`) + companion design-doc edits
(`reference/adr-etag-from-content-version-designdoc-edits.md`).

**Date:** 2026-07-01 · **Author:** Stephen Fuqua, with AI assistance (Claude Opus 4.8).
Draft work on a branch; nothing merged. ADR still `Proposed — DRAFT` pending team acceptance.

## Where the work lives
- **Worktree:** `D:\tanager\DMS-etag-content-version`
- **Branch:** `etag-content-version` (tracks `origin/main`), currently **ahead 14 commits**, working tree clean.
- **Plan:** `docs/superpowers/plans/2026-07-01-etag-from-content-version.md`
- Execution is staged for review (superpowers:executing-plans). Stages 1–3 done; integration layer in progress.

## The decision being implemented
`_etag` changes from a SHA-256 content hash to a composed strong validator:
```
_etag = "{ContentVersion}-{variantKey}"
variantKey = schemaEpoch "." format "." profileCode "." linkFlag
```
- `ContentVersion` = `dms.Document.ContentVersion` (opaque string, never parsed as a number).
- `schemaEpoch` = first 8 lowercase hex of `MappingSet.Key.EffectiveSchemaHash`.
- `format` = `j` (JSON; registry in `ResponseFormat`).
- `profileCode` = `_` (no profile) or first 8 hex of `SHA-256(profileName)` — **decision:** hash of name (MappingSet has no enumerable profile catalog, so the ordinal registry was infeasible).
- `linkFlag` = `l`/`n` from `ResourceLinksOptions.Enabled`.
- **If-Match** compares the **state-significant projection** only: `ContentVersion`, `schemaEpoch`, `profileCode` (drop `format`, `linkFlag`). So link/format differences do NOT `412`; a different profile or ContentVersion DOES `412`. Profile **is** significant (decided).
- Served `ETag` stays representation-complete (all four components) for conditional-GET/`If-None-Match` correctness; only the write-time comparison is projected.

## New building blocks (all committed, unit-tested)
Under `src/dms/backend/EdFi.DataManagementService.Backend/Etag/`:
- `EtagValue` (Core `Utilities/`) — Compose / ToHeaderValue (quote) / TryParseHeaderValue (unquote, reject `W/`).
- `VariantKey` + `VariantKeyFactory` + `ResponseFormat` enum + `EtagVariantInputs(ProfileName, Format)`.
- `ProfileVariantCode.Of(profileName)` — `_` or 8-hex SHA-256 prefix.
- `EtagComposer` / `IEtagComposer` — `Compose(long contentVersion, VariantKey)`; DI singleton (registered in `ReferenceResolverServiceCollectionExtensions.cs`).
- `EtagMatchProjection.Of(etag)` — parses `{cv}-{epoch}.{fmt}.{profile}.{link}` → `{cv}-{epoch}.{profile}` (drops fmt+link); `"!malformed"` sentinel for bad tags.

## Production changes committed (Stages 1–3)
- **Read path** (`RelationalReadMaterializer.cs`): `InjectApiMetadata` composes `_etag` from `documentMetadata.ContentVersion` + `EtagVariantInputs` (on the materialization request) when present; else falls back to the legacy hash. `MappingSet`/`EtagVariant` threaded via `Materialize`→`MaterializePage`. Read call sites (`RelationalDocumentStoreRepository` GET-by-id ~line 2507, query-page ~3894) set `EtagVariant`. Profile name threaded from Core via `ReadableProfileProjectionContext.ProfileName` (init-only, default `""`, set in Core `Handler/Utility.cs`).
- **Write path** (`RelationalCommittedRepresentationReader.cs`): **no longer hydrates/materializes**; reads only `dms.Document.ContentVersion` via `RelationalDocumentLockCommandBuilder.BuildContentVersionCommand` (scalar) and composes the etag, returning `{_etag}`. Executor/result-builders unchanged (still extract `_etag`).
- **Write If-Match** (`RelationalWriteExecutionStateResolver.cs`): deferred path composes + projection-compares; standard path uses `RelationalCurrentEtagPreconditionChecker` (see below). Both pass the **write profile name** (`request.ProfileWriteContext?.ProfileName`).
- **Precondition checker** (`RelationalCurrentEtagPreconditionChecker.cs`, serves standard write If-Match AND DELETE): composes current etag + projection compare. Request now carries `ProfileName` (null for DELETE). **This was the source of a real bug** — it previously hard-coded no-profile, 412-ing profiled updates; fixed in `b02b883e`.
- `DefaultRelationalWriteExecutor` ctor gained `IEtagComposer` (passed to the resolver; still injects `IRelationalReadMaterializer` for the merge orchestrator).

## Test status
- **Unit — all green:** backend `1844`, core `2558`, frontend `193` (Phase 5 added a header-quoting test; backend not re-run after Phase 5 but unchanged by it).
- **PostgreSQL integration: PG-side etag caller updates DONE** (commit `0312d17e`). `RelationalGetIntegrationTestHelper` is now etag-agnostic (`AssertComposedEtag` shape check; `_etag` dropped from canonical body comparison). Verified passing against the running container: the 2 `..._with_readable_profile_projection` smoke tests, 14 affected fixtures (PostAsUpdate/ResourceLinksFlag/ProfileIfMatch/IfMatchCascade/propagated-identity), and 7 descriptor read tests. A partial full-suite run showed 488 passes / 0 assertion failures (only `DROP DATABASE` teardown timeouts from container saturation — infra, not test logic). **Do not run the whole suite at once — target affected tests via `--filter`.**
- **MSSQL integration: mirror DONE** (commit `ebe5e3b6`). Same composed-etag updates applied to the 4 MSSQL test files (SSA smoke, DS52 survey cascade smoke, ProfileIfMatch, ResourceLinksFlag); SSA link-bearing If-Match now uses `FlipLinkFlag` (dead `CreateLinkBearingResponse`/`AddReferenceLink` removed). Verified passing against a local SQL Server 2022 container (`dms-mssql-etag`) with targeted `--filter` runs: profile (20), SSA smoke (4), DS52 + ResourceLinks (5), and unchanged-but-etag-consuming cascade/caller-agnostic/descriptor tests (10). Descriptor path still hashes (Phase 6). **Target affected tests via `--filter`; do not run the whole suite.**

### Note on the readable-profile-projection smoke tests (fixed in `0312d17e`)
The two `..._with_readable_profile_projection` smoke tests previously asserted the profiled read's
`_etag` **equals** the unprojected read's. That is now wrong by design: profile is state-significant,
so a profiled representation carries a **different** strong validator. Fix threaded a real `ProfileName`
constant into `CreateReadableProfileProjectionContext` (the default `""` degenerately hashed to
`e3b0c442` = `SHA-256("")`) and flipped the assertion to `.NotBe`. `ProfileVariantCode.Of` returns `_`
only for a **null** name; empty string is hashed — so backend-direct tests must set a real `ProfileName`.

## Remaining work (ordered)
1. ~~**Fix `RelationalGetIntegrationTestHelper`** etag handling; re-run PG suite to catch all callers.~~ **DONE** (`0312d17e`).
2. ~~**Mirror all integration-test updates to MSSQL** (`*.Mssql.Tests.Integration`).~~ **DONE** (`ebe5e3b6`).
3. ~~**Phase 5 — HTTP quoting:** serve `ETag` header quoted; make `WritePreconditionFactory` quote-tolerant.~~ **DONE** (`da2046bb`). `AspNetCoreFrontend.ToResult` quotes the `ETag` header at the HTTP boundary (body `_etag` and `FrontendResponse.Headers["etag"]` stay unquoted); `WritePreconditionFactory` unquotes inbound `If-Match` via `EtagValue.TryParseHeaderValue` and keeps weak `W/` tags verbatim (→ can't match → 412). These two ship together: a quoted served `ETag` means clients echo a quoted `If-Match`. No server-side `If-None-Match`/conditional-GET exists, so nothing else needed. Updated tests: `AspNetCoreFrontendResponseHeaderTests`, `RelationalWriteSmokeTests` (pipeline), `WritePreconditionFactoryTests`, `DeleteByIdHandlerTests`. Full Core (2558) + Frontend (193) unit suites green.
4. **Phase 6 — descriptors + dead code: IN PROGRESS** (ADR accepted 2026-07-02; executing subagent-driven per `docs/superpowers/plans/2026-07-02-descriptor-composed-etag.md`). **Done:** T1 `DescriptorVariantKey` (`aa7d0e4f`); T2 descriptor read composed etag (`ac52efc3`). **Resume at T3** (descriptor write + If-Match). **Critical caveat captured in the plan's "Task 3 investigation notes":** descriptor `ContentVersion` is trigger-managed — INSERT copies the insert-time value (so `RETURNING`/`OUTPUT` on the Document INSERT is correct), but UPDATE bumps it via an AFTER trigger (so the write UPDATE must do a follow-up `SELECT Document.ContentVersion`, NOT `OUTPUT`). Remaining after T3: T4 remove read-materializer hash fallback, T5 remove version-middleware `_etag` stamp + handler fallback, T6 delete the two formatters, T7 verify + apply design-doc edits + flip ADR status. Original scope note: descriptors are a **self-consistent hash island** (`DescriptorDocumentMaterializer` read, `DescriptorWriteHandler` 4 sites incl. the If-Match precondition loader at ~2267). Converting requires adding `ContentVersion` to the descriptor read query + `DescriptorReadRow`, threading a descriptor `variantKey`, composing at all sites, reworking the Core `InjectVersionMetadataToEdFiDocumentMiddleware` (+ handler `?? ParsedBody["_etag"]` fallback in Upsert/UpdateById), and removing the materializer hash fallback (`RelationalReadMaterializer:282`) — then delete `ResourceEtagFormatter` + `RelationalApiMetadataFormatter`. **Keep `CanonicalJsonSerializer`** (non-etag caller: `ProjectSchemaMetadataExtractor`). `RelationalCommittedRepresentationReader` is still used (light ContentVersion reader) — do NOT remove.
5. **Phase 7 — E2E (Task 7.3): DONE** (`03df4f3c`). `etag.feature` is format-agnostic (placeholders + status assertions) and needed no change; the step defs now strip the quoted `ETag` header on capture so it matches the unquoted body `_etag` and round-trips as `If-Match`. `ProfileFiltering.feature` scenarios 07–08 updated for the reversal (profiled etag ≠ full-resource etag). **E2E NOT executed** (needs full DMS+PG+Keycloak stack) — compile-verified only. **Task 7.4 (design-doc edits + flip ADR to Accepted): BLOCKED** — companion file + plan gate it on team acceptance of the ADR (still `Proposed — DRAFT`); a human decision.
6. Full solution build + all suites; open PR; human review before merge.

## Known follow-ups / notes
- **DELETE + profile:** the DELETE precondition composes with no profile (`_`); an If-Match captured under a readable profile will `412` a profile-less DELETE. Edge case, acceptable per ADR discussion.
- **Precondition `CurrentEtag` field** is composed with `linksEnabled: true` (constant, since linkFlag is projected out of matching). It is informational; if a test asserts exact `CurrentEtag` against a links-off served etag it could differ. Consider injecting real link mode only if that becomes a problem.
- **`ReadableProfileProjectionContext.ProfileName`** defaults to `""`; only Core populates it. Backend-direct integration tests must set it (done for the PG profile test support: constant `ReadableProfileName = "root-only-profile"`).
- The redesign's earlier profile/link-**insensitive** `_etag` requirement is deliberately **reversed** — reviewers should resolve any disagreement at the ADR level.

## Environment / how to resume
- .NET 10; tests are **NUnit + FluentAssertions**, `Given_<X>` / `It_<behavior>`, `[Parallelizable]`.
- **SonarAnalyzer runs warnings-as-errors** — no unused usings/members, no nested ternaries, etc.
- Husky pre-commit runs `dotnet husky run` (formats staged files). In a fresh worktree the `.husky/_` bootstrap is git-ignored; if commits fail with `.husky/_/husky.sh: No such file`, copy it from the main worktree: `cp -r D:/tanager/DMS/.husky/_ D:/tanager/DMS-etag-content-version/.husky/_`.
- End commit messages with `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
- **PG integration container** (throwaway, trust auth) currently running as `dms-pg-etag`:
  `docker run -d --name dms-pg-etag -p 5432:5432 -e POSTGRES_HOST_AUTH_METHOD=trust -e POSTGRES_USER=postgres postgres:16-alpine -c max_locks_per_transaction=256`
  The integration `DatabaseSetupFixture` auto-provisions the DB/schema; the appsettings connection string uses no password (trust).
- **MSSQL integration container** (throwaway) running as `dms-mssql-etag`, matching the test appsettings connection (`Server=localhost,1433;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;`):
  `docker run -d --name dms-mssql-etag -p 1433:1433 -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=abcdefgh1!" mcr.microsoft.com/mssql/server:2022-latest`
  The MSSQL fixtures auto-provision per-test databases (`dmsfp<guid>`) and drop them in teardown.
- Test commands (from `D:\tanager\DMS-etag-content-version`):
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit`
  - `dotnet test src/dms/core/EdFi.DataManagementService.Core.Tests.Unit`
  - `dotnet test src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit`
  - `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Postgresql.Tests.Integration` (needs PG container)
  - MSSQL integration needs a SQL Server container (see `eng/docker-compose/`).

## Commit log (this branch, oldest → newest)
```
aef1e8f3 docs: add implementation plan for ContentVersion-based _etag (Option 4)
753b4474 docs: decide If-Match state-significant projection for ContentVersion _etag
938bb0b4 feat: add EtagValue helper for composed strong entity-tag wire format
de6b4f43 feat: add VariantKey and VariantKeyFactory for representation-sensitive etags
def35fab feat: add ProfileVariantCodeRegistry for stable profileCode in variantKey
00578611 feat: add IEtagComposer and register it in backend DI
d4007c1b feat: add EtagMatchProjection for state-significant If-Match comparison
72f1ac15 refactor: source profileCode via SHA-256 prefix of profile name
0140e93e feat: compose read-path _etag from ContentVersion + variantKey
2953e25c maint: Adjust to properly ignore superpowers
7f76e903 feat: compose write-path _etag and use projection for If-Match
b39f0a4b perf: replace write-path hydrate readback with a ContentVersion scalar read
a6ac43ff test: pass EtagComposer to integration recording materializers
b02b883e fix: thread profile name into write If-Match precondition; update PG profile etag tests
```
