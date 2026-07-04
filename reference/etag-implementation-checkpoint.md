# ETag → ContentVersion implementation — checkpoint

**Purpose:** Resume point so context can be cleared. Captures what's done, what's
left, and how to continue implementing the ADR
(`reference/adr-etag-from-content-version.md`) + companion design-doc edits
(`reference/adr-etag-from-content-version-designdoc-edits.md`).

**Date:** 2026-07-01 (updated 2026-07-03) · **Author:** Stephen Fuqua, with AI assistance (Claude Opus 4.8).
Draft work on a branch; nothing merged. **ADR accepted 2026-07-03**; all design-doc edits applied.
**All implementation phases (1–7) are complete.** Only PR + human review + merge remain.

## Where the work lives
- **Worktree:** `D:\tanager\DMS-etag-content-version`
- **Branch:** `etag-content-version` (tracks `origin/main`), currently **ahead 31 commits**, working tree clean (except the unrelated `link-injection.md` PRD-appendix edit, intentionally left uncommitted).
- **Plan:** `docs/superpowers/plans/2026-07-01-etag-from-content-version.md` (Phases 1–5, 7); `docs/superpowers/plans/2026-07-02-descriptor-composed-etag.md` (Phase 6).
- Execution: Phases 1–5/7 via superpowers:executing-plans; Phase 6 via subagent-driven-development. All stages done.

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

## Test status (final, 2026-07-03)
- **Build:** `src/dms/EdFi.DataManagementService.sln` — 0 warnings, 0 errors (SonarAnalyzer clean).
- **Unit — all green:** backend `1839`, core `2554` (+1 pre-existing skip), frontend `193` (+2 pre-existing skips).
- **Integration — targeted, all green (2026-07-03):** PG descriptor read+write `22`; MSSQL descriptor read+write `12`. (Phase 6 only changed the descriptor path; the resource-path etag surface was converted and verified in earlier phases — see below. Per policy, only affected classes were run via `--filter`, never the whole suite.)

### Historical unit counts (pre-Phase-6)
- **Unit:** backend `1844`, core `2558`, frontend `193` (Phase 5 added a header-quoting test). Counts shifted slightly after Phase 6 deleted the two dead formatters + their unit tests.
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
4. **Phase 6 — descriptors + dead code: DONE.** Executed subagent-driven per `docs/superpowers/plans/2026-07-02-descriptor-composed-etag.md`. T1 `DescriptorVariantKey` (`aa7d0e4f`); T2 descriptor read composed etag (`ac52efc3`); T3 descriptor write + If-Match composed etag (`dc88e797`); T4 require EtagVariant+MappingSet on external-response materialization, dropping the read-materializer hash fallback (`27e4509a`); T5 drop the request-body `_etag` hash stamp + handler fallback (`fee0628b`); T6 delete the two dead formatters + migrate 5 test callers (`ddac0fe0`). **Trigger caveat honored:** descriptor `ContentVersion` INSERT uses `RETURNING`/`OUTPUT` (trigger mirrors at insert); UPDATE uses a follow-up `SELECT Document.ContentVersion` (AFTER trigger bumps post-statement). `ResourceEtagFormatter` + `RelationalApiMetadataFormatter` deleted; `git grep` for them returns nothing. `CanonicalJsonSerializer` kept (non-etag caller). `RelationalCommittedRepresentationReader` kept (light ContentVersion reader).
5. **Phase 7 — E2E (Task 7.3): DONE** (`03df4f3c`). `etag.feature` format-agnostic; step defs strip the quoted `ETag` header on capture. `ProfileFiltering.feature` 07–08 updated for the reversal. **E2E NOT executed** (needs full DMS+PG+Keycloak stack) — compile-verified only. **Task 7.4 (design-doc edits + flip ADR to Accepted): DONE 2026-07-03** (`e75e59d5`). ADR `Status` → Accepted; companion edits file → APPLIED; `update-tracking.md` / `transactions-and-concurrency.md` / `flattening-reconstitution.md` updated (composed `_etag`, normative `variantKey` encoding, projected `If-Match` rules, DocumentCache freshness on `ContentVersion` alone, read path no longer hashes). 3b audit confirmed no residual canonical-JSON-for-`_etag` rationale.
6. **Remaining: PR + human review + merge only.** Full solution build + all unit suites + targeted PG/MSSQL descriptor integration all green as of 2026-07-03 (see Test status). Next: `superpowers:finishing-a-development-branch`.

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
0bdd4634 docs: add etag implementation checkpoint for context reset
0312d17e test: update PG integration etag assertions for composed ContentVersion _etag
a9eeefd2 docs: mark PG integration etag caller updates done in checkpoint
ebe5e3b6 test: mirror composed ContentVersion _etag assertions to MSSQL integration
d170f082 docs: mark MSSQL integration etag mirror done in checkpoint
da2046bb feat: serve ETag as a quoted strong validator; accept quoted If-Match
586fbda8 docs: mark Phase 5 (HTTP etag quoting) done in checkpoint
03df4f3c test: update E2E etag scenarios for composed etag + quoted ETag header
2f1a7b21 docs: record Phase 7 E2E done + Phase 6/7.4 gating in checkpoint
aa7d0e4f feat: add DescriptorVariantKey for the fixed descriptor etag variantKey
ac52efc3 feat: compose descriptor read _etag from ContentVersion + variantKey
cf2564f9 docs: checkpoint Phase 6 progress (T1+T2 done) and T3 trigger caveat
dc88e797 feat: descriptor write + If-Match compose ContentVersion etag (no hash)
27e4509a refactor: require EtagVariant+MappingSet for external-response materialization
fee0628b refactor: drop request-body _etag hash stamp and handler fallback
ddac0fe0 refactor: delete dead content-hash etag formatters, migrate test callers
e75e59d5 docs: accept ContentVersion _etag ADR; align the three design docs
```
