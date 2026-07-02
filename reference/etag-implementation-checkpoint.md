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
- **Unit — all green:** backend `1844`, core `2558`, frontend `192`.
- **PostgreSQL integration (filter `IfMatch|Etag|Cascade`): 16/17 pass.** Started a throwaway container for this (see Environment).
- **MSSQL integration: NOT run** (needs a SQL Server container). MSSQL mirror-tests will need the same updates as the PG ones.

### The 1 remaining PG failure (next task)
`It_should_cascade_abstract_reference_identity_updates_into_runtime_written_reference_columns`
(`PostgresqlRelationalWriteAuthoritativeSampleSmokeTests.cs` ~line 1965). Root cause: the shared helper
`RelationalGetIntegrationTestHelper.CreateExpectedEtag` (in
`src/dms/backend/EdFi.DataManagementService.Backend.Tests.Common/RelationalGetIntegrationTestHelper.cs`
line ~53-67) still computes the expected `_etag` via `ResourceEtagFormatter.FormatEtag` (old hash).
It **cannot** derive the composed etag from the body (composed etag depends on ContentVersion, not content).
**Also confirmed (good):** the cascade DID bump the referrer's `ContentVersion` (actual etag was `7-...`),
so the ADR's identity-cascade precondition holds for this case.

**Fix approach for the helper:** make expected-response comparisons etag-agnostic — e.g. stop setting
`_etag` from a hash in `CreateExpectedExternalResponse`, and strip `_etag` inside
`NormalizeJsonNode`/`CanonicalizeJson` so body comparisons ignore it; convert direct `_etag` equality
assertions (e.g. `AssertStudentSchoolAssociationExternalResponse` ~line 90-93; the cascade test) to
composed-format checks (presence, and "changed after write"). `AssertWriteResultEtagParity` should
still hold (write etag == GET etag under the same variant) — keep it. This helper is used broadly, so
running the **full** PG integration suite after the change is required to catch other callers.

## Remaining work (ordered)
1. **Fix `RelationalGetIntegrationTestHelper`** etag handling (above); re-run the **full** PG integration suite (not just the filter) to catch all callers.
2. **Mirror all integration-test updates to MSSQL** (`*.Mssql.Tests.Integration`): profile If-Match/etag tests, the smoke tests, and any `CreateExpectedEtag`-based assertions. Needs a SQL Server container; run the MSSQL suite.
3. **Phase 5 — HTTP quoting:** serve `ETag` header quoted via `EtagValue.ToHeaderValue`; make `WritePreconditionFactory` (`src/dms/core/.../Backend/WritePreconditionFactory.cs`, reads `If-Match`) quote-tolerant via `EtagValue.TryParseHeaderValue`. Update `AspNetCoreFrontendResponseHeaderTests`. (Header is currently emitted **unquoted** — pre-existing non-conformance.)
4. **Phase 6 — descriptors + dead code:** convert the descriptor etag path (currently still hashes via `RelationalApiMetadataFormatter.FormatEtag(ExtractedDescriptorBody)` — separate handlers, self-consistent) to composed; then remove `ResourceEtagFormatter` / `RelationalApiMetadataFormatter` and the materializer hash fallback once no callers remain. Check `CanonicalJsonSerializer` for non-etag callers before removing.
5. **Phase 7 — E2E + docs:** review `etag.feature` (E2E); apply the companion design-doc edits to `reference/design/backend-redesign/design-docs/{update-tracking,transactions-and-concurrency,flattening-reconstitution}.md`; flip ADR status to Accepted on team sign-off.
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
