# DMS-1556 Implementation Spec — CMS profile requests return HTTP 500 during concurrent catalog loading

Status: **v4 — Phase 0 complete (0.1–0.6 approved, Codex, 2026-09-29/30). G2 approved 2026-09-30 with P-G1, P-G2, P-G3 and P-7.4 in force (§0.00). Phases 1–3 are complete (every step approved; 3.4 at `c799f084d`, 2026-10-01). Phase 4: 4.1 reviewed 2026-10-01 (evidence in `DMS-1556-investigation.md` §4.1). The catalog gate is met on both profiles. The default-setting 256/128 gate **remains failed** (53300 only): it is a separate connection-capacity limitation, it is kept for final review, and no pool, `max_connections` or concurrency change follows. 4.2 approved 2026-10-01 (§4.2 there): DMS shards 1 and 2 are green on 2 independent runs each, and CMS E2E is green once. 4.3 (docs) approved at `1bc6f6fa3`: `docs/CONFIGURATION.md` and `CS-AUTH.md` describe the settings, behavior and rotation runbook. 4.4 (final matrix and push-readiness report, `DMS-1556-investigation.md` §4.4) approved at `a59b931ce`. Its gate decisions are recorded in §7.4: the default stress failure is a ticket-scoped exception (kept FAILED), the seven unrun mutants are waived (never passed), CI is a merge and completion gate, and evidence archival is required before completion. `main` (`5c964676f`) is merged locally (investigation, *Integration with `main`*, approved). Post-merge validation (investigation, *Post-merge validation*): both integration lanes, CMS E2E, DMS shards 1 and 2 once each, and the P-runner-approx catalog runtime (3 cold + 1 warm, every response 200) are green on the merged tree. A matched control shows round 1's slowdown relative to 4.1 is environmental, not the merged code. The evidence bundle is prepared locally, not yet uploaded. Push and ready-for-review each await explicit approval.** Phase 0 evidence is in `DMS-1556-investigation.md`. v1 and v2 (both 2026-09-29) were reviewed and not approved; v3 applied the round-2 findings (§0.1) on top of the round-1 dispositions (§0.2) and was approved for step 0.1 only, with the corrections in §0.0 applied here as v4. Approval applies only to the scope reviewed: production implementation remains conditional on the evidence review at G2, and if Phase 0 evidence changes the mechanism or the fix, the affected sections and §2 are revised and re-approved before any later phase starts.
Worktree: `C:\dev\ed-fi\Data-Management-Service\src\Data-Management-Service-DMS-1556`, branch `DMS-1556` (fresh from `main` at `5e0d010af`). Target: Ed-Fi API v8.1. `SchemaHashConstants.RelationalMappingVersion` stays `v3`; no schema migration.

## 0. Review history and dispositions

### 0.00 Step 0.6 decisions at G2 (**approved 2026-09-30; in force**)

All four proposals were approved at the G2 architectural review of `e4c3afeb9`, with
the clarifications below. Their evidence and reasoning are in
`DMS-1556-investigation.md` §0.6.

- **P-G1:** keep E1's provisional classification and the CI-specific attribution
  explicitly separate. Evidence archival (the Jira upload) remains outstanding.
- **P-G3:** 10 s `LoadTimeout` is a policy informed by handshake measurements, not a
  measured bound for the complete load operation.
- **P-7.4:** headroom testing supplements the unchanged all-200 gate. Default-setting
  53300 failures still require review.
- **H3/H5 scope:** both conclusions are limited to the measured workloads (§3.5).
  Warm-first results establish that metadata amplification is **not necessary** for the
  stall; they do not disprove it universally.
- **I-3 wording:** corrected in §4.8, with the JWKS row of §4.7 and §6 made consistent.
- **Parser move (1.3):** kept focused. The approved format-cache removal stays; no
  additional cleanup.
- **Checkpoint grouping:** steps 1.1 and 1.2 form one checkpoint (§5).

| Id | Decision | Applies to |
| --- | --- | --- |
| P-G1 | Add **G1-S (stress record)**: R-500 at `stress-256x128` under a condition without slot exhaustion, in a round whose stacks show the stall's signature. G1-S never changes the G1 catalog record. G1 *partially reproduced (provisional)* plus G1-S *reproduced* establishes the mechanism for G2 and AC 1 on a combined basis; the catalog-workload 500 on the runner stays inferred until CI. Proposed records: G1 unchanged; G1-S reproduced (2 of 3 headroom runs, 3 × profile-path `AuthenticateSASL` timeout 500s, no 53300). | §3.6, §2 AC 1 |
| P-G2 | **Proceed with Phases 1–3 under §4 as written.** H1 is supported by direct evidence (stacks, queue growth, timeout clustering) and corroborated by E4 and E3. No blocking elsewhere. H2 is a modifier (high connection counts not necessary for the stall, E5). H3 is refuted for the measured catalog workloads on this host. H4 is a modifier of H1. H5 (metadata amplification) is not necessary for the stall and was not observed in the measured workloads. | §3.6 G2, §4.2 C-1 |
| P-G3 | Settings unchanged: RefreshInterval 300 (policy), MaxStaleness 3600 (Q13), Cooldown 30 (security/availability policy), LoadTimeout 10 (a policy informed by measured unstalled handshakes ≤ 183 ms, not a measured bound for the complete load operation), backoff and `Retry-After` unchanged (policy). Npgsql `Timeout`/`Max Pool Size` and the thread-pool minimum unchanged (measured, and a non-goal). Optional: a docs-only note in 4.3 on `Max Pool Size` = `max_connections` producing 53300 under stress. | §3.6 G3, §4.3.12, §4.3 docs |
| P-7.4 | Keep 7.4-H unchanged. Add a headroom (`max_connections=200`) 256/128 run alongside it (an addition, not a replacement). Pre-register that a 256/128 default run failing **only** with 53300 is a gate failure returned to review, never a pass and never a licence for a pool or `max_connections` change. Record that no pre-fix P-dev 256/128 baseline exists. | §7.4, Phase 4.1-H |

### 0.0 Approval-round corrections (v3 → v4, 2026-09-29)

Step 0.1 was approved on v3 with two corrections and two question decisions, all applied in v4:

| Item | Disposition |
| --- | --- |
| 1 Scheduler due-time expression (`NextAttemptAt > now ? NextAttemptAt : lastSuccessAt + RefreshInterval`) lost the retry deadline exactly when it became due, and a success signal could authorize an immediate extra load | §4.4 "Scheduling" restated as explicit due-time rules: never loaded → due immediately; last attempt failed → due time remains `NextAttemptAt`, **including when it is already past**; last attempt succeeded → due time is the next normal refresh deadline; a state-change signal causes recomputation and never itself authorizes a load; an attempt starts only when due, eligible, and no attempt is in flight. New tests 1.6-h (retry deadline reached exactly) and 1.6-i (successful publication causes no additional load before the next due time); M8 extended with 1.6-h and M10 added for 1.6-i; step 1.6 is a scheduler review checkpoint. |
| 2 The spec referenced v2 sections ("As v2 §7.1", "As v2 §6", "Conventions as before") that are not preserved anywhere — the file is untracked | §1.5/§1.6, §1.8, §4.9, §5 conventions, §6, §7.1, and §8 are restated inline and self-contained in v4. The review-history tables (§0.1, §0.2) remain as history only and are not prerequisites for understanding the operative plan. |
| Q17 approved with adjustments | Dump analysis reads captured runtime state; it does not execute `ThreadPool.GetMinThreads` in the target. The `dotnet-dump` version is pinned and its `threadpool` output is verified rather than assumed: the current diagnostics command exposes the value as `Worker Min Limit` (`ThreadPoolCommand` reports `threadPool.MinThreads`). Dumps are collected outside timed burst windows so collection does not contaminate timeout evidence. |
| Q18 approved | 87 total requests / 87 maximum concurrency for the catalog-shaped workload; 256 / 128 for additional stress. Overlap is measured after admission through the semaphore and through response-body completion. The two workloads are separately identified in all evidence. |

### 0.1 Round 2 findings (v2 → v3)

| Finding | Disposition |
| --- | --- |
| 1 Refresh scheduler waits for the *later* of tick and retry deadline, contradicting the ~60 s recovery bound | **Deadline-driven scheduling** (§4.4 "Scheduling"): after a failure the service sleeps until the retry deadline (`NextAttemptAt`), not the next tick; the deadline is re-read on every wake because request-triggered attempts move it; bounds are split into *attempt start* (`T_start ≤ max backoff`) and *attempt completion* (`+ LoadTimeout`). Test 1.6-e: startup retrieval fails, store recovers, **no requests**, recovery within the documented bound under fake time. |
| 2 Final gate allowed the original profile failure to remain | **Full-request success gate restored**: in healthy baseline-comparison runs every expected profile request must return HTTP 200 with the expected `id`/`definition` (harness validates bodies); stage-classified 503/500 expectations apply only to deliberately injected outage runs. §2 (AC 2), Phase 4.1 and §7.4 now use the same criterion. |
| 3 Attribution overstated; concurrency undefined | Effective thread-pool minimum is read from the target process with `ThreadPool.GetMinThreads` via a dump (`dotnet-monitor /dump` + `dotnet-dump analyze -c threadpool`), never inferred from thread counts; the "two controls → two mechanisms" rule is removed and an ineffective E4 does **not** exclude starvation (stacks decide); the harness separates `-MaxConcurrency` from `-TotalRequests` (IDs repeat when total exceeds the seeded count) and reports the **measured** peak overlap; R-slow-only evidence is provisional and does not establish the reported mechanism for AC 1. |
| 4 Rotation runbook not executable with the unchanged issuer | Runbook rewritten around the actual behavior: the private-key query selects the newest active row (`ORDER BY CreatedAt DESC LIMIT 1`), so inserting a newer active key switches signing on the next mint; the resulting window in which another instance may 401 a legitimate new-key token is bounded and documented; first-sighting acceptance is stated as *conditional on gate and cooldown eligibility*; old-key retention accounts for `TokenExpirationMinutes` **plus** `TokenValidationClockSkew` (5 min). Tests cover eligible and suppressed refresh and two independent providers (1.5-h/p/q, 3.1-e/q). |
| 5 Mutations that would not fail the named tests | M2 split: **M2a** boundary short-circuit, pinned by a counting configuration-manager decorator asserting zero downstream configuration/validation calls on a boundary failure; **M2b** translation removed at all three events, pinned by 3.1-f/g turning into 500s. M5 uses **successive, non-overlapping** unknown-kid requests within one cooldown (1.5-j rewritten). "3.4-a evidence" removed from must-fail; connection measurements are observational only. |
| 6 Background certificate initialization races token issuance | A shared `DevelopmentCertificateStore` singleton owns check-and-create for the development certificate under one `SemaphoreSlim`, writes atomically (temp file + rename), and is used by **both** `CertificateSigningKeySource` and `LoadActiveSigningKeyFromCertificatesAsync` (issuance semantics unchanged: same file, same thumbprint kid). Deterministic first-start test with concurrent snapshot load and token issuance asserts the issued token's kid equals the published thumbprint (1.5-r). |
| Sequencing | 2.1 registers only what exists after Phase 1 (sources, provider, hosted service, `TimeProvider`); the configuration manager and shared events are registered in 2.3 where they are defined. Timer-driven tests moved from 1.5 to 1.6; 1.5 keeps only provider-API, time-advancing tests. |

Question decisions recorded: **Q13** `MaxStaleness = 3600 s`, explicit configurable policy independent of token lifetime; a full database outage still prevents authentication through the uncached token-status check — stale keys give no general outage availability (§4.5). **Q14** `RefreshOnIssuerKeyNotFound = false`; the boundary owns unknown-key refresh. **Q15** introspection keeps `{"active": false}` with a categorized Error log — documented as *preserving existing behavior*, not as a protocol requirement. **Q16** `OpenIddictKey` is reset in the integration database; hosts and refresh services are disposed before reset; key-table fixtures are `[NonParallelizable]`.

### 0.2 Round 1 findings (v1 → v2), still in force

Plain `IConfigurationManager<OpenIdConnectConfiguration>` (never `BaseConfigurationManager`); boundary classification in `OnMessageReceived`/`OnChallenge` via `context.Fail(exception)` (the handler rethrows unless an event supplies a result); retry eligibility in the provider governing every trigger; separate bounds `T_prop`/`T_max`; consumer-by-consumer semantics with OAuth contracts preserved; mutation-based revert checks; Phase 0 with controls E3/E4/E5, R-500/R-slow, M-conn, disk evidence, E6 moved to Phase 4; fix-demonstrating vs compatibility tests; steps split for reviewable commits. Q1 commit harness; Q2 supply manager only, `Authority` stays, `MetadataAddress` inert; Q3 503 for protected requests and JWKS only; Q5 typed exception; Q6/Q9/Q10/Q11/Q12 as recommended; Q7 remove only the format cache; Q8 evidence summary in repo, raw captures to Jira.

## 1. Problem statement, confirmed facts, verified framework behavior, hypotheses, scope

### 1.1 Problem statement

During a burst of authenticated `GET /v3/profiles/{id}` requests (DMS fans out one request per catalog entry with `Task.WhenAll`; ~87 profiles in the affected runs), CMS in self-contained mode with database-backed signing keys returned HTTP 500 for existing profiles after ~15 s. The 500 originated in `ProfileRepository.GetProfile` (`NpgsqlConnection.OpenAsync` → `TimeoutException` during SASL authentication or a read). In the same window CMS logged `Failed to fetch public keys for JWKS` (7× and 13×), also from connection opening in `OpenIddictDataRepository.GetActivePublicKeysInternalAsync`. PostgreSQL logged no crash, restart or `too many connections`. The mechanism is unproven (Jira DMS-1556).

### 1.2 Confirmed facts (code inspection; paths relative to `src/config`)

| # | Fact | Where |
| --- | --- | --- |
| F1 | The default `Bearer` scheme's `IssuerSigningKeyResolver` blocks the request thread: `tokenManager.GetPublicKeysAsync().ConfigureAwait(false).GetAwaiter().GetResult()`. | `frontend/…/Infrastructure/WebApplicationBuilderExtensions.cs:387-414` |
| F2 | `DmsJwtBearer` (registered by `AddJwtAuthentication` from both store registrations) has the same blocking resolver and an `OnTokenValidated` that calls `ValidateTokenAsync` by reflection. No endpoint selects it. | `backend/…Backend.OpenIddict/Extensions/JwtAuthenticationExtensions.cs:50-173` |
| F3 | Per authenticated request in database-key mode, authentication performs **three sequential database acquisitions** before the endpoint's query: resolver key read; `VerifyTokenAsync` key read; `GetTokenStatusAsync`. Physical connection counts and reuse are a **measurement** (M-conn). | `OpenIddictTokenManager.cs:401-412, 465-471, 555-561`; `Backend.Postgresql/OpenIddict/Repositories/OpenIddictDataRepository.cs:489-497, 544-551`; `Backend.Postgresql/Repositories/ProfileRepository.cs:122-125` |
| F4 | `_keyFormatCache` caches detected key formats only. | `OpenIddictTokenManager.cs:37-48, 716-738` |
| F5 | `GetPublicKeysFromDatabaseAsync` swallows exceptions and returns a possibly empty/partial list; JWKS then answers `200 {"keys":[]}`. | `OpenIddictTokenManager.cs:783-787`; `Modules/JwksEndpointModule.cs:20-25` |
| F6 | `Authority`/`MetadataAddress` are set and no `ConfigurationManager` is supplied, so post-configuration builds an HTTP manager targeting CMS itself (E2E `IdentitySettings__Authority=http://ed-fi-api-config:8081`); first authenticated request self-fetches discovery + JWKS; metadata keys are merged into validation keys (V-2, V-5). | `WebApplicationBuilderExtensions.cs:339-341`; `eng/docker-compose/.env.e2e:41,73,157` |
| F7 | `ITokenManager` transient at `:211`, then singleton in `AddPostgresOpenIddictStores` (`:35`) / `AddMssqlOpenIddictStores` (`:33`); concrete (`:34`/`:32`) and `ITokenRevocationManager` (`:36`/`:34`) are separate singletons → **three `OpenIddictTokenManager` instances**. Shared key state must be its own singleton. | as cited |
| F8 | `ProfileRepository.GetProfile` opens outside its `try`; open failures → `GlobalExceptionHandler` → 500; query failures → `FailureUnknown` → 500. Unchanged (Q10). | `ProfileRepository.cs:122-145` |
| F9 | Keys are seeded by tooling (`Generate-OpenIddictKey-Insert.ps1`, `OpenIddict-Crypto.psm1`; pgcrypto from migration `0020`). **The private-key query selects the newest active row** (`WHERE "IsActive" = TRUE ORDER BY "CreatedAt" DESC LIMIT 1`; MSSQL `TOP 1 … ORDER BY CreatedAt DESC`), so inserting a newer active key changes signing on the next mint; there is no separate activation control. `ExpiresAt` is never queried. | `OpenIddictDataRepository.cs:523-542` (PG), `:564-585` (MSSQL); `0019_Create_OpenIdKeys_Table.sql` |
| F10 | Certificate mode serves keys from an X.509 file re-read per call; when `UseDevelopmentCertificates` is on, **both** `LoadActiveSigningKeyFromCertificatesAsync` (issuance) and `GetPublicKeysFromCertificatesAsync` (validation) independently check `File.Exists` and create the file. Certificate mode is a zero-code control removing database I/O from both key reads. | `OpenIddictTokenManager.cs:87-118, 648-679` |
| F11 | Npgsql 8.0.4 defaults with the E2E string: `Max Pool Size=100`, `Timeout=15` s, idle lifetime 300 s; PostgreSQL `max_connections` 100 shared by DMS, CMS, job workers, tests. | `.env.e2e:152`; `eng/docker-compose/postgresql.yml:13` |
| F12 | CI runs PostgreSQL, CMS, DMS and the test process on one `ubuntu-latest` runner; actual vCPU/memory recorded at investigation time. | `.github/workflows/on-dms-pullrequest.yml:2011-2123` |
| F13 | DMS `Task.WhenAll` fan-out; no per-server cap; DMS caches its CMS token (1500 s), no re-mint on 401; DMS-side handling is DMS-1557 (AC 6). | `src/dms/core/…/Profile/CachedProfileService.cs:658-668`; `ConfigurationServiceTokenHandler.cs:34-67` |
| F14 | No test exercises the real `JwtBearer` pipeline with database keys (integration tests use `AddTestAuthentication`; frontend tests fake `ITokenManager` or run it over a fake repository). | `Jobs/JobApiIntegrationTests.cs:116-139`; `IdentityModuleTests.cs:1438-1466` |
| F15 | Pre-existing, out of scope: `MaxKeyCacheSize`/`KeyFormatCacheSize` spelling; duplicate `OpenIddict.AddValidation`; shadowed transient at `:211`. | `appsettings.json:61` |
| F16 | `/health` returns the time only. | `Modules/HealthModule.cs` |
| F17 | `JwtTokenValidator.TokenValidationClockSkew = 5 min`; the expired-token sweep already keeps rows for expiration + skew. | `Backend.OpenIddict/Token/JwtTokenValidator.cs:69`; `TokenCleanupService.cs:79-84` |

### 1.3 Verified framework behavior (version-pinned sources read 2026-09-29)

- **V-1** `JwtBearerHandler.HandleAuthenticateAsync` (aspnetcore v10.0.1): `MessageReceived` → `if (messageReceivedContext.Result != null) return …`; `TokenValidated` → same; outer `catch` → `AuthenticationFailed` → returns `Result` if set, **otherwise rethrows**; `HandleChallengeAsync` returns when `eventContext.Handled`.
- **V-2** `SetupTokenValidationParametersAsync`: a `BaseConfigurationManager` is assigned to `tokenValidationParameters.ConfigurationManager`; **any other** `IConfigurationManager` is awaited via `GetConfigurationAsync(Context.RequestAborted)` and its `Issuer`/`SigningKeys` are concatenated into the cloned parameters, leaving `TokenValidationParameters.ConfigurationManager` null.
- **V-3** IdentityModel 8.12.0 `JsonWebTokenHandler.ValidateTokenAsync(JsonWebToken, …)`: first `GetBaseConfigurationAsync(CancellationToken.None)` is try/caught (IDX10261, continues with null); `LastKnownGoodConfiguration` assigned on success regardless of `UseLastKnownGoodConfiguration`; second call after `RequestRefresh()` unguarded; LKG loop when enabled. **Rejected for this design.**
- **V-4** `RefreshOnIssuerKeyNotFound` only calls `RequestRefresh()`; no in-request retry on the non-`Base` path.
- **V-5** `JwtBearerPostConfigureOptions`: `ValidAudience` from `options.Audience` when unset; HTTP manager and `Backchannel` created **only when `options.ConfigurationManager == null`**.
- **V-6** `ValidateSignature` throws `SecurityTokenSignatureKeyNotFoundException` (IDX10500) with no keys.
- **V-7** `ThreadPool.GetMinThreads(out workerThreads, out completionPortThreads)` reports the configured minimum independent of the current thread count (API documentation); the runtime knob `DOTNET_ThreadPool_ForceMinWorkerThreads` takes a hexadecimal value.

### 1.4 Hypotheses (not mutually exclusive)

- **H1 thread-pool starvation** from the blocking resolver. Primary evidence: thread stacks blocked under the resolver plus `threadpool-queue-length` growth; E4 supports but neither proves alone nor excludes when ineffective (§3.5).
- **H2 connection creation and SCRAM cost** under the burst. Primary evidence: M-conn creation burst and PostgreSQL CPU; E5 and E3 change the outcome.
- **H3 PostgreSQL saturation** (checkpoint I/O, CPU, disk): waits, slow statements, disk stalls; unaffected by E4.
- **H4 runner contention**: profile-dependent reproduction.
- **H5 self-referential metadata amplification** (F5/F6): self-requests correlated with `Failed to fetch` lines.

### 1.5 Scope

- CMS (`src/config`) only: authentication key resolution, the request boundary of both JwtBearer schemes, JWKS, and the OpenIddict token manager's key/status paths.
- Self-contained identity mode with database-backed signing keys is the primary target. Certificate mode is touched only where named: the shared development-certificate store (D-10), which changes *where and by whom* the file is created, not what is issued.
- PostgreSQL **and** MSSQL backends wherever the repository contract changes (D-8): both implementations of `GetActivePublicKeysInternalAsync` gain the cancellation-token overload.
- Investigation tooling (Phase 0): the burst harness and diagnostics recipes under `eng/performance/dms-1556/` and the compose overlays under `eng/docker-compose/`. These are committed; captured evidence artifacts are not (gitignored `artifacts/` and `.dms-dotnet-diagnostics/` paths).

### 1.6 Non-goals (scope exclusions)

- No change under `src/dms` (AC 6; DMS-side failure handling is DMS-1557).
- No token-validity caching: the per-request token-status check stays uncached (I-2, D-7).
- No connection-string timeout, thread-pool minimum, or pool-size change as the fix — those appear only as Phase 0 experiment controls (E4, E5).
- No rotation tooling, key-management endpoints, or key-table schema change; `ExpiresAt` stays unqueried (F9). `SchemaHashConstants.RelationalMappingVersion` stays `v3`.
- No Keycloak-mode behavior change (no OpenIddict stores are registered there).
- OAuth endpoint contracts preserved exactly as §4.7 states them (token, introspection, revocation).
- Pre-existing issues in F15 (settings spelling, duplicate `AddValidation`, shadowed transient) stay untouched.

### 1.7 Questions decided at the step-0.1 approval

| Q | Question | Decision (2026-09-29) |
| --- | --- | --- |
| Q17 | Reading the effective thread-pool minimum from the container: `dotnet-monitor /dump` → `dotnet-dump analyze <dump> -c threadpool` inside a Linux container matching the CMS image's runtime (the dump is Alpine/linux-musl and must be analyzed on a matching runtime). Acceptable as the "approved diagnostic mechanism"? | **Approved with adjustments.** Dump analysis reads captured runtime state — it does not execute `ThreadPool.GetMinThreads` in the target. The `dotnet-dump` version is pinned, and its output is verified rather than assumed: the current diagnostics `ThreadPoolCommand` reports `threadPool.MinThreads` and exposes the value as `Worker Min Limit`. Dumps are collected outside timed burst windows so collection does not contaminate the timeout evidence. |
| Q18 | `-TotalRequests` default for the burst: 87 (one per profile, matching DMS) with `-MaxConcurrency 87`; the N=128 point uses `-TotalRequests 256 -MaxConcurrency 128` with cycling IDs. | **Approved.** 87/87 is the catalog-shaped workload; 256/128 is additional stress. Overlap is measured after admission through the semaphore and through response-body completion, and the two workloads are separately identified in the evidence (`catalog-87x87`, `stress-256x128`). |

### 1.8 Sources read

- aspnetcore v10.0.1: `JwtBearerHandler.HandleAuthenticateAsync` / `HandleChallengeAsync`, `SetupTokenValidationParametersAsync`, `JwtBearerPostConfigureOptions` (V-1, V-2, V-5).
- IdentityModel 8.12.0: `JsonWebTokenHandler.ValidateTokenAsync(JsonWebToken, …)`, `BaseConfigurationManager`/LKG behavior, `ValidateSignature` (V-3, V-4, V-6).
- `ThreadPool.GetMinThreads` API documentation and the `DOTNET_ThreadPool_ForceMinWorkerThreads` runtime knob (V-7).
- `dotnet/diagnostics` `Microsoft.Diagnostics.ExtensionCommands.ThreadPoolCommand` — the `threadpool` command reports `threadPool.MinThreads` and prints it as `Worker Min Limit` (Q17).
- CMS sources cited fact-by-fact in §1.2 (F1–F17), including `OpenIddictDataRepository.GetActivePrivateKeyInternalAsync` on both engines and `JwtTokenValidator.TokenValidationClockSkew`.
- Npgsql 8.0.4 pooling defaults; `eng/docker-compose/.env.e2e`, `postgresql.yml`, and `.github/workflows/on-dms-pullrequest.yml` for the deployment topology (F6, F11, F12).

## 2. Acceptance-criteria mapping (Jira, verbatim)

| AC | Text (verbatim) | Steps | Tests (F = demonstrates the fix, C = compatibility) | Completion evidence |
| --- | --- | --- | --- | --- |
| AC 1 | Establish and document the failure mechanism using a repeatable stress/regression scenario and runtime evidence. | 0.1–0.6 | Harness E1–E5; 3.1-s (F) | `DMS-1556-investigation.md`: R-500/R-slow outcome per run (R-slow-only marked provisional), per-experiment tables, measured peak overlap, effective `MinThreads` from the process, M-conn, PostgreSQL/disk samples, stacks, attribution table with verdicts incl. *inconclusive*, E7 observations, G2 decision by Codex. |
| AC 2 | CMS serves existing profiles reliably during the catalog-load burst within the supported CI/resource envelope; requests do not fail because authentication blocks async database work. | 1.x, 2.x, 3.1–3.4 | 3.1-a/s (F), 3.4-a (F) | **Healthy-run gate (§7.4-H):** on the fixed image, every expected profile request in every healthy baseline-comparison run (both profiles, 5 cold + 5 warm at 87/87 and 256/128) returns **HTTP 200 with the expected `id` and `definition`** — zero non-200 responses of any kind; stacks show no blocked authentication frames. Phase 4.2: shards 1 and 2 green on ≥ 2 independent runs each; CI after push confirms the real envelope (recorded in the PR). |
| AC 3 | If the signing-key resolver is changed, resolve from a safe in-memory snapshot with asynchronous refresh/coalescing or equivalent design, preserving key rotation, unknown-key behavior, revocation validation, and failure semantics. Do not trade availability for accepting invalid tokens or indefinitely trusting retired keys. | 1.1–1.6, 2.2, 2.3, 3.1, 3.2 | 1.5-a…r, 1.6-a…g, 2.2, 2.3, 3.1-b…s, 3.4-b/c | §7 fixtures pass; mutations M1–M10 each fail exactly their named fixtures; `T_start`/`T_prop`/`T_max` demonstrated by fake-time tests. |
| AC 4 | Retain diagnosable dependency errors and avoid silently treating failed key retrieval as a valid empty key set. | 1.2, 1.5, 2.2, 2.3, 3.1, 3.3 | 1.5-f/g (F), 2.2-c/d (F), 2.3-b/c (F), 3.1-f/g/i (F), 3.3-a (F) | Injected-outage runs (§4.1-O) show stage-classified 503s with `Retry-After` and the §4.9 Error lines; JWKS 503 vs `200 []` distinguished. |
| AC 5 | Add coverage for concurrent cold requests, database interruption/recovery, and signing-key rotation; rerun the affected PostgreSQL self-contained E2E shards. A single passing rerun alone does not establish the fix. | 1.5, 1.6, 3.1, 3.4, 4.1, 4.2 | Cold: 1.5-a, 3.1-a, 3.4-a. Interruption/recovery: 1.5-f/l/m, 1.6-b/e, 3.1-f/g/i/n. Rotation/retirement: 1.5-h/i/p/q, 1.6-f, 3.1-e/h/q, 3.4-b/c. Certificate race: 1.5-r. | §7.1 executed on the final commit; shards 1 and 2 ≥ 2 runs each; harness 5× per profile. |
| AC 6 | Keep the DMS failure-handling correction independent: preventing this CMS timeout must not be DMS's only protection against upstream 500s. | none in `src/dms` | — | `git diff --stat main...DMS-1556` has no `src/dms` path; DMS-1557 remains required. |

## 3. Phase 0 — Investigation

No production code. Steps 0.2–0.5 stop individually with evidence committed as increments of the investigation document.

### 3.1 Environment

1. Stack: `teardown-local-dms.ps1`; `setup-local-dms.ps1 -EnvironmentFile ./.env.e2e` (self-contained, PostgreSQL 16.8, database keys, DMS idle). CMS at `http://127.0.0.1:8081/config` — the harness targets `127.0.0.1`, not `localhost`, because the Windows dual-stack fallback adds ~2 s to every new connection (a client-side artifact that would contaminate latency evidence; measured during the 0.1 smoke).
2. CMS diagnostics overlay `eng/docker-compose/local-config-diagnostics.yml` (`DOTNET_DiagnosticPorts=/diag/cms-monitor.sock,suspend`, `TMPDIR=/diag`, bind mount) + `dotnet-monitor` sidecar (digest-pinned, `--no-auth`, listen mode, run as root for the cross-container in-process socket): `/livemetrics`, `/stacks`, `/dump`. Suspend mode is required for `/stacks` — the in-process call-stacks feature can only be injected while the runtime waits at startup (verified during the 0.1 smoke), so CMS start depends on the sidecar under this overlay. **Effective thread-pool minimum (Q17):** one `/dump` per run, **collected outside the timed burst windows** (before the first or after the last timed round) so collection does not contaminate timeout evidence; analyzed with the **pinned** `dotnet-dump` version's `threadpool` command in a Linux container whose runtime matches the CMS image (Alpine/linux-musl → Alpine SDK image). The dump is captured runtime state — nothing executes `ThreadPool.GetMinThreads` in the target. The recorded value is the `Worker Min Limit` line (`ThreadPoolCommand` reports `threadPool.MinThreads`); the recipe verifies that label is present in the output and records the tool version, alongside `docker inspect` of `DOTNET_ThreadPool_ForceMinWorkerThreads`/`DOTNET_PROCESSOR_COUNT`.
3. PostgreSQL overlay (scratch): `log_connections`, `log_disconnections`, `log_min_duration_statement=250`, `log_lock_waits`, `log_checkpoints`; sampler (250 ms): `pg_stat_activity` by state/wait_event_type/backend_type, `numbackends`, `pg_stat_io`, `pg_stat_bgwriter`.
4. Container/host samplers: `docker stats` (CPU, memory, PIDs, BlockIO) at 500 ms; host disk counters (`Get-Counter` on Windows; `/proc/diskstats` noted as the runner equivalent).
5. CMS `Debug` log level for correlated durations/trace ids.
6. Seeding: `Invoke-CmsProfileBurst.ps1 -Seed -ProfileCount 87` (idempotent), recording each profile's id and definition hash for body validation.
7. Load: `Invoke-CmsProfileBurst.ps1 -TotalRequests T -MaxConcurrency N -Rounds R [-Cold] [-ValidateBodies]` — `HttpClient` + `Task.WhenAll` with a `SemaphoreSlim(N)` admission gate; request *i* targets profile `ids[i mod 87]` (IDs repeat when `T > 87`); output: per-request CSV (admission and body-completion timestamps, status, ms, trace id, body-valid) and a summary including the **measured peak overlap** and body-validation counts. Per Q18, a request's in-flight interval runs **from admission through the semaphore to response-body completion**, and peak overlap is the maximum number of simultaneously in-flight intervals derived from those timestamps. Defaults per Q18 (87/87); every run is labeled with its workload (`catalog-87x87`, `stress-256x128`, or `workload-TxN`) and the two Q18 workloads are kept separately identified in all evidence.
8. Resource profiles (both recorded with `nproc`, memory, Docker limits, env, `Worker Min Limit`): **P-dev** and **P-runner-approx** (`cpus: 2` on CMS and PostgreSQL; `DOTNET_PROCESSOR_COUNT=4`). P-runner-approx is a stress approximation; the envelope evidence is the real workload (E7 locally, CI after push).

### 3.2 Reproduction definitions

- **R-500**: ≥ 1 HTTP 500 on `GET /v3/profiles/{id}` whose CMS log shows `NpgsqlException`/`TimeoutException` from `OpenAsync` in the profile path and/or `Failed to fetch public keys for JWKS` in the window.
- **R-slow**: no 500 but p99 ≥ 5 s at 87/87. **R-slow alone is provisional**: it may steer the investigation but does not establish the reported timeout/500 mechanism for AC 1; the doc must say so.

### 3.3 Measurements

HTTP status histogram, percentiles, time-to-first-failure, measured peak overlap, body validation; CMS `threadpool-thread-count`, `threadpool-queue-length`, `threadpool-completed-items-count`, lock contention, CPU, GC, working set; ASP.NET current/failed requests; Npgsql meter; **M-conn** (physical connections created in the window from `log_connections`, peak `numbackends` for the CMS role, pool peak in-use, reuse ratio); stacks at t≈2/8/14 s; PostgreSQL waits, slow statements, checkpoints, `pg_stat_io`; disk; CMS log counts (`Failed to fetch`, `/.well-known/` self-requests, `Authentication failed`); `Worker Min Limit` from the dump.

### 3.4 Experiments

| Id | Purpose | Setup | What it can and cannot show |
| --- | --- | --- | --- |
| E0 | Pin framework facts (scratch project) | (a) plain manager throwing → `OnAuthenticationFailed` receives the type; `Fail(ex)` prevents rethrow; `OnChallenge.AuthenticateFailure` is the type; (b) `OnMessageReceived` `Fail(ex)` short-circuits **and no `GetConfigurationAsync` call follows**; (c) manager supplied + `Authority` set → zero backchannel sends; (d) `GetConfigurationAsync` receives `RequestAborted`; abort during a cold load cancels only that waiter; (e) cold failure → 503, expired → 503, recovery → 200, `TokenValidationParameters.ConfigurationManager` null; (f) `ValidAudience` from `Audience` | validity of D-2/D-3, I-5 |
| E1 | Baseline | current image; both profiles; `-Cold` + 4 warm rounds; 87/87; 5× | R-500/R-slow (G1) |
| E2 | Concurrency sweep | `(T,N)` ∈ {(87,4),(87,8),(87,16),(87,32),(87,64),(87,87),(256,128)}, warm, P-runner-approx | degradation shape; M-conn vs measured overlap |
| E3 | Control — remove database-backed key retrieval collectively (certificate mode) | `UseCertificates=true`, dev cert on a writable volume | implicates key retrieval as a whole; does not isolate blocking from round-trip cost |
| E4 | Control — thread availability | `DOTNET_ThreadPool_ForceMinWorkerThreads=0x80`; `Worker Min Limit` confirmed from the dump | vanish → supports H1; **persist → does not exclude H1** (minimum may still be insufficient, or blocking may sit elsewhere) — stacks decide |
| E5 | Control — bound physical connections | `Max Pool Size=16` | failure signature/PG CPU shift → H2 quantified |
| E7 | Real workload: shard 2 via `build-dms.ps1 E2ETest` under P-runner-approx with the overlay (best effort) | local observation of the real workload; complements CI |

### 3.5 Attribution rules

- A control that removes the failure **supports** its hypothesis. Two controls each removing it do **not** establish two mechanisms; both may relieve one mechanism (certificate mode and extra threads can both relieve blocking). Mechanisms are attributed from direct evidence — blocked stacks and queue growth (H1), M-conn and PostgreSQL CPU (H2), waits/`pg_stat_io`/disk (H3) — with controls as corroboration.
- An ineffective E4 never excludes starvation.
- Verdicts: *supported*, *refuted*, *modifier*, **inconclusive**.

Filled at step 0.6 and approved at G2 (2026-09-30). Every verdict is limited to the
measured workloads. Evidence links, limitations, and the link-by-link mechanism are in
`DMS-1556-investigation.md` §0.6.

| Hypothesis | Direct evidence | Corroborating controls | Observed | Verdict |
| --- | --- | --- | --- | --- |
| H1 | blocked stacks under the resolver, queue growth, timeout clustering | E4, E3 | 1,904 of 1,906 workers in the resolver wait in the baseline catalog 2 s/8 s captures; queue 2–96; failures only where the stall crosses `Timeout=15`; E4 and E3 remove the stall | supported |
| H2 | M-conn creation burst, PG CPU | E5, E3 | E5 stall persists with 12–13 connections; PostgreSQL idle during handshakes; 53300 only at stress with `Max Pool Size` = `max_connections` | modifier (not necessary for the stall) |
| H3 | waits, slow statements, disk stalls | samplers; E4 no effect | little I/O, no slow statements or lock waits; E4 removes the stall | refuted for the measured catalog workloads on this host |
| H4 | profile dependence | P-dev vs P-runner-approx | P-dev 0/5 versus P-runner-approx 5/5; E7a 0.4 s versus E7b 13.3 s | modifier (of H1) |
| H5 | self-requests correlated with `Failed to fetch` | E0(c), E1 logs | no timed self-requests; `Failed to fetch` lines on `/v3/profiles` paths; stall persists after the self-fetch | not necessary for the stall (warm-first); no amplification observed in the measured workloads, not universally disproved |

### 3.6 Decision gates

- **G1** R-500 in ≥ 1 of 5 cold runs under either profile → *reproduced*. R-slow only → *partially reproduced (provisional)*; attribution may proceed but AC 1 is not satisfied by R-slow evidence alone, and AC 2's envelope evidence leans on E7 and CI. Neither → re-plan with Codex; Phase 1 does not start.
- **G2** Proceed with Phases 1–3 when direct evidence supports H1 and/or H2 and at least one control corroborates. Stacks showing blocking elsewhere → revise §4 first. H2-dominant → snapshot still removes two of three authentication acquisitions; connection-string guidance added; re-approve. H3/H4-dominant or inconclusive → §4 withdrawn.
- **G3** settings confirmed/adjusted; `MaxStaleness` fixed at 3600 s by Q13.
- **G1-S (stress record; P-G1, in force).** R-500 at `stress-256x128` under a condition without slot exhaustion, in a round whose stacks show the stall's signature. G1-S never changes the G1 catalog record. G1 *partially reproduced (provisional)* plus G1-S *reproduced* establishes the local mechanism for G2 and AC 1 on a combined basis. E1's provisional classification and the CI-specific attribution stay explicitly separate: the catalog-workload 500 on the runner remains inferred until CI.
- **Records at G2 (2026-09-30):** G1 partially reproduced (provisional); G1-S reproduced; G2 approved (P-G2); G3 settings unchanged (P-G3).

### 3.7 Deliverable

`reference/design/configuration-service/DMS-1556-investigation.md`; raw captures to Jira.

## 4. Proposed architecture

### 4.1 Decisions (D)

- **D-1** Shared key state in a new singleton (F7).
- **D-2** Both schemes receive keys through `SigningKeyConfigurationManager : IConfigurationManager<OpenIdConnectConfiguration>` (plain, V-2/V-5); the blocking resolver is removed from both.
- **D-3** Request boundary in `OnMessageReceived` (shared `SigningKeyBearerEvents`): readiness with `RequestAborted`, unknown-`kid` pre-resolution, classification via `context.Fail(exception)`; `OnAuthenticationFailed`/`OnTokenValidated` also `Fail(exception)` on dependency failures; `OnChallenge` classifies by `AuthenticateFailure`; `RefreshOnIssuerKeyNotFound = false` (Q14).
- **D-4** Failure classes: `Failed(Retrieval)`, `Succeeded(0)`, `Failed(Processing)`, `Succeeded(n)`; a failed load never replaces the current snapshot.
- **D-5** One load gate for every trigger: single-flight + retry eligibility with backoff; **deadline-driven scheduling** in the hosted service (§4.4).
- **D-6** Unknown-kid refresh throttled globally (`Cooldown`) and gated (D-5).
- **D-7** Per-request token-status check retained, uncached; `ValidateTokenAsync` rethrows `AuthenticationDependencyUnavailableException` ahead of its general catch.
- **D-8** Contract change limited to `GetActivePublicKeysAsync(CancellationToken)` / `GetActivePublicKeysInternalAsync(CancellationToken)` on both engines.
- **D-9** 503 problem (`FailureResponse.ForUnclassifiedStatus(503, …)`) + `Retry-After` for protected requests and JWKS; OAuth endpoints preserved (§4.7).
- **D-10** Development-certificate creation has one owner: `DevelopmentCertificateStore` (singleton; `SemaphoreSlim(1,1)`; check-and-create under the lock; atomic write via temp file + `File.Move(overwrite)`), used by both the validation source and the issuance path. Issuance semantics (same file, thumbprint kid, cert lifetime) unchanged.

### 4.2 Conditional

C-1 all of §4 conditional on G2. C-2 `MetadataAddress` documented inert. C-3 settings other than `MaxStaleness` confirmed at G3.

### 4.3 Components (`Backend.OpenIddict` unless noted)

1. `Backend/AuthenticationDependencyUnavailableException` (`Category`: `SigningKeyStore` | `TokenStatusStore`) and `SigningKeys/SigningKeysUnavailableException`.
2. `SigningKeys/SigningKeySnapshot` (immutable; `Keys`, `RetrievedAt`, `Version`, `Source`), `SigningKeyEntry`, `SigningKeyRefreshOutcome`, `SigningKeyProviderStatus`. The key material (key id, modulus, exponent) is private to the entry and never handed out. Consumers receive **detached projections**: `PublicParameters` (copied arrays, for JWKS) and `CreateSecurityKey()` / `SigningKeySnapshot.CreateSecurityKeys()` (a new `RsaSecurityKey` from fresh copies on every call). A consumer that mutates a projection, including `SecurityKey.KeyId` or the RSA arrays, cannot change the snapshot, `ContainsKeyId`, or later projections.
3. `SigningKeys/PublicKeyMaterialParser` (moved; no format cache).
4. `SigningKeys/DevelopmentCertificateStore` (D-10) and `SigningKeys/CertificateSigningKeySource` / `DatabaseSigningKeySource` behind `ISigningKeySource`.
5. `SigningKeys/ISigningKeySnapshotProvider` / `SigningKeySnapshotProvider` (§4.4): `Current`, `Status`, `GetUsableAsync(ct)`, `RefreshAsync(trigger, ct)`, `RefreshIfUnchangedAsync(trigger, observedStateVersion, ct)` (the scheduler's conditional admission), `TryRefreshForUnknownKeyAsync(kid, ct)`, `NextAttemptAt`, and `AttemptStateChanged` (a signal the scheduler awaits).
6. `SigningKeys/SigningKeyRefreshService : BackgroundService` — deadline-driven loop (§4.4 "Scheduling"), `TimeProvider`-based waits, startup attempt non-fatal.
7. `SigningKeys/SigningKeyConfigurationManager` — `GetConfigurationAsync(ct)` → `provider.GetUsableAsync(ct)` → one cached `OpenIdConnectConfiguration` per snapshot version, whose `SigningKeys` come from one `CreateSecurityKeys()` call made when that version is first seen (a detached set the manager owns; the per-version reuse is preserved). The configuration declares **no issuer**: the handler appends a configuration's issuer to each request's valid issuers, so a shared issuer would widen every scheme on the manager. Issuer policy stays with each scheme's own `ValidIssuer` (I-1; corrected at the 3.2 review, 2026-09-30). `RequestRefresh()` is a deliberate no-op that starts no load. The handler calls it only when `RefreshOnIssuerKeyNotFound` is on, which Q14 turns off, and it passes no key id, so a refresh started there could not honor the cooldown (I-6). The request boundary makes the one cooldown-limited unknown-key refresh, with the token's key id (§4.6). *Deviation approved at the 2.3 review (2026-09-30): the earlier text called for a gated refresh here; no additional provider member is added.*
8. `SigningKeys/SigningKeyBearerEvents` — shared handlers (§4.6) composed onto each scheme's `JwtBearerEvents`, preserving each scheme's existing logging in `OnChallenge`/`OnAuthenticationFailed` for non-dependency failures (those are logged and left without a result, so today's rethrow behavior is unchanged).
9. `OpenIddictTokenManager`: `GetPublicKeysAsync` projects the snapshot; `VerifyTokenAsync` uses snapshot keys with one gated unknown-kid refresh; `ValidateTokenAsync` translates `DbException`/`TimeoutException`/`OperationCanceledException` from `GetTokenStatusAsync` into the typed exception and rethrows typed exceptions before its general catch; `LoadActiveSigningKeyFromCertificatesAsync` obtains the development certificate from `DevelopmentCertificateStore` (D-10) — its only change; database issuance untouched.
10. Frontend: `Bearer` supplies the manager and shared events; `JwtAuthenticationExtensions` does the same for `DmsJwtBearer`; `JwksEndpointModule` uses the provider.
11. Registration split by step: 2.1 (sources, store, provider, hosted service, `TimeProvider`), 2.3 (manager, events).
12. Options: `SigningKeyRefreshIntervalSeconds` (300), `SigningKeyMaxStalenessSeconds` (3600, Q13), `SigningKeyUnknownKeyRefreshCooldownSeconds` (30), `SigningKeyLoadTimeoutSeconds` (10); validated fail-fast. They are `IdentitySettings:` keys bound onto `IdentityOptions`, checked by `SigningKeyOptionsValidator` at host start (`ValidateOnStart`), and consumed only through `SigningKeySettings.FromIdentityOptions`, which applies the same validator and throws `OptionsValidationException`. Validation rules (step 1.1; policy, proposed for review with the checkpoint):
    - RefreshInterval: 30–43,200 s. The cap is half the staleness cap, so every accepted interval admits a valid MaxStaleness (approved at the 1.1+1.2 review).
    - MaxStaleness: between 2 × RefreshInterval and 86,400 s. Twice the interval gives retry headroom: a snapshot whose scheduled refresh fails stays usable for at least one more interval while retries run. It does not guarantee recovery through an outage. `T_max` stays bounded.
    - Cooldown: 1–3,600 s. Zero would disable I-6.
    - LoadTimeout: 1–60 s and less than RefreshInterval. This is a policy constraint that keeps one load well inside one interval. Overlapping loads are prevented by the provider's single-flight gate (D-5), not by this rule.

    Every failure is reported, and each message names its `IdentitySettings:` key.

### 4.4 Provider state machine, load gate, scheduling

States of `Current`: `None`, `Usable` (*fresh* ≤ `RefreshInterval`, *overdue* ≤ `MaxStaleness`), `Expired`. Gate: `InFlight`, `NextAttemptAt`, consecutive failures `n`.

- **Load attempt** (any trigger): join `InFlight` if present; else refused when `now < NextAttemptAt`; else start under a token linked to host shutdown + `LoadTimeout`. Success: publish, `n = 0`, `NextAttemptAt = now` (eligible immediately). Failure: `n++`, `NextAttemptAt = now + backoff(n)`, `backoff = min(5·2^(n−1), 60) s ± 20 %`; `Current` unchanged. Every transition raises `AttemptStateChanged`.
- **`GetUsableAsync(ct)`**: fresh → return; overdue → return and request a load (gate may refuse); `None`/`Expired` → attempt; refused/failed → `SigningKeysUnavailableException`; in flight → `await InFlight.WaitAsync(ct)`.
- **`TryRefreshForUnknownKeyAsync(kid)`**: refused when the last *completed* load finished < `Cooldown` ago, or when the gate refuses; else attempt; returns whether the kid is now present.
- **During a retry delay**: cold/expired requests → 503 without repository traffic; overdue → served from `Current`; unknown kid → 401 without a call; scheduler sleeps until the deadline. Repository traffic is bounded by one attempt per backoff interval regardless of request rate.
- **Scheduling (hosted service).** The loop computes a **due time** from the outcome of the last attempt — never from gate eligibility, and never with an expression that switches source at the deadline:
  - **Never loaded** (no attempt has completed): the initial attempt is due immediately.
  - **Last attempt failed**: the due time remains `NextAttemptAt`, **including when it is already past** (at and beyond the retry deadline the due time stays the retry deadline; it never switches to the normal refresh deadline).
  - **Last attempt succeeded**: the due time is the next normal refresh deadline, `lastSuccessAt + RefreshInterval ± 10 %`. The jitter is drawn once per snapshot version, so recomputing never moves the deadline of the same snapshot (step 1.6; ±10 % approved at the 1.6 review).
  - **Signals recompute; they do not authorize.** The service waits until `min(dueAt, next AttemptStateChanged signal)` with `TimeProvider`; on any wake it re-reads the state (a request-triggered attempt may have succeeded, failed, or moved the deadline) and recomputes the due time. In particular, a success signal must not trigger another load merely because the gate is immediately eligible (`NextAttemptAt = now` after success): eligibility says an attempt *may* start, the due time says one *should*.
  - **Signal before status.** Each pass captures the `AttemptStateChanged` task *before* it reads the status, so a transition between the read and the wait completes the task being waited on instead of being missed.
  - **Attempt in flight or store operation outstanding**: `StoreOperationOutstanding` prevents a new load even after `NextAttemptAt`. While either holds, the service waits for the signal alone, with no deadline. No load can start before the operation ends, so an expired due time is not a reason to wake, and the loop cannot spin on it. When the operation ends, its signal wakes the service. If the last attempt failed, the due time is still the retry deadline, now past, so the attempt starts at once.
  - **Start condition**: the service starts an attempt only when `now ≥ dueAt`, the gate is eligible, no attempt is in flight, and no store operation is outstanding.
  - **Admission is conditional on the observed state.** The decision above comes from one status read. Another caller may change the state between that read and admission, for example by publishing fresh keys. The service therefore admits with `RefreshIfUnchangedAsync(trigger, status.StateVersion)`. The provider changes `StateVersion` at every transition: an attempt starting or completing, an outstanding operation ending, and disposal. It compares the version under the gate lock, together with admission. If the version differs, the call is refused with `Refused(StateChanged)` and calls nothing, and the service recomputes. Checking the signal just before the call would leave a window between that check and admission.
  - **Disposal is explicit.** The service stops only when `Status.ProviderDisposed` is true. An exception from the source, including `ObjectDisposedException`, is a `Failed(Retrieval)` like any other, with backoff and recovery (AC 5).

  After a failure the service therefore wakes at the retry deadline, never at the next tick. These rules are pinned by 1.6-b/e (deadline-driven recovery), 1.6-h (retry deadline reached exactly), and 1.6-i (successful publication causes no additional load before the next due time); the scheduler implementation is reviewed at its own checkpoint (step 1.6).
- **Bounds.** Write `B = 60 s + 20 % jitter = 72 s` for the largest backoff, and `E` for the end of the failed attempt's store operation (`E` ≤ the failure time when the operation honored its deadline).
  - *Attempt start*: with no requests, the next attempt starts at `max(NextAttemptAt, E)`, which is at most `max(F + B, E)` after a failure at `F`.
  - *Attempt completion*: that start + `LoadTimeout`.
  - *Recovery*: the store recovers at `R` with no attempt in flight. The first attempt after `R` starts by `max(R + B, E)` (earlier if `NextAttemptAt` falls sooner or a request arrives after it). It observes the recovery and completes by `max(R + B, E) + LoadTimeout`, which is `R + 72 s + LoadTimeout` when no operation is outstanding.
  - *Attempt in flight at `R`*: it started against the failing store and may still fail. Its backoff then counts from its own failure, at most `R + LoadTimeout`, so add `LoadTimeout` to the start bound.

  **Eventual termination is not enough for these bounds.** An operation that outlives its deadline keeps the single-flight slot until it finishes. Until then no overlapping store call starts, admission refuses with `OperationOutstanding`, and the scheduler waits for the operation's completion signal. The `max(…, E)` terms above already account for this; nothing is added on top of them. The recovery bound of 60 s + jitter + `LoadTimeout` holds only when `E ≤ R + B`. The database drivers honor the token (1.4), so `E` normally falls within the deadline. A store call that never returns keeps the instance on its current snapshot, or unavailable, until it does.

### 4.5 Freshness bounds and rotation

- **`T_prop`** (healthy instance: every load succeeds within its deadline; no request-driven refresh needed): a key-table change committed at `t` is visible by `t + LoadTimeout + RefreshInterval + jitter + LoadTimeout`, with the refresh jitter at most 10 % of `RefreshInterval`. At the defaults that is 10 + 300 + 30 + 10 = **350 s**. *Corrected at the 4.3 review (2026-10-01); the earlier text omitted the first `LoadTimeout` term.* That term is the load already running at `t`. It can read the rows before the change and publish them afterward, as late as `t + LoadTimeout`. A snapshot's `RetrievedAt`, and with it the next refresh deadline and the cooldown, counts from publication, not from the read. Example: that load publishes the old rows at `t + 9 s`, the next scheduled load starts 330 s later and takes 9 s, and the change is visible at `t + 348 s`, with every load inside its deadline. Every load that starts after `t` sees the change.
- **New-key fast path** (conditional): a token carrying an unknown kid triggers one refresh **only if** the gate is open and the last completed load is older than `Cooldown`; then the new key is accepted in that request. Otherwise the request is 401 and the key arrives by the earlier of the cooldown end (next unknown-kid request) or `T_prop`. Joining a load that is already in flight adds no load and is allowed during the cooldown, but a load that started before `t` returns without the key. **Bound with traffic** (healthy store, so no backoff): the last load that read the old rows completes before `t + LoadTimeout`; its cooldown ends `Cooldown` later; the first new-key request at or after that end starts a load that completes within `LoadTimeout`. New-key tokens are therefore accepted from `t + LoadTimeout + Cooldown + LoadTimeout` (50 s at the defaults) **provided a new-key request reaches the instance once the cooldown has ended**; otherwise `T_prop` applies.
- **`T_max`** = `MaxStaleness` (3600 s, Q13): maximum trust after the last successful retrieval; beyond it every request fails closed. A key retired during a key-store outage may be trusted up to `T_max` on that instance — the documented, configurable trade-off. **Stale keys give no general outage availability**: with the whole database down, no token is accepted. Where a request stops depends on the snapshot. With no usable snapshot it answers 503 `SigningKeyStore` at the boundary. With a usable snapshot, a token rejected on its own merits is 401 before any status read, and a token that passes validation reaches `GetTokenStatusAsync`, which fails, giving 503 `TokenStatusStore`. `T_max` keeps valid tokens accepted only when key retrieval fails while the token store works. *(Corrected at the 4.3 review; the earlier text said every authenticated request answers 503 regardless of snapshot state.)*
- **Rotation runbook (as the system actually behaves, F9).** (1) Insert the new active key. **Signing switches on the next mint on every instance** (newest active row wins). (2) Validators pick the key up by first sighting when eligible, else within `T_prop`; during that window an instance whose gate is closed or whose cooldown is running may 401 a legitimate new-key token — on a healthy store until the new-key fast-path bound above (`2 × LoadTimeout + Cooldown` after the insert, given an eligible new-key request after the cooldown) or `T_prop`, whichever comes first. If the store was failing, the key arrives with the first successful load after recovery, within §4.4's conditional recovery bounds (`R + 72 s + LoadTimeout` with no attempt in flight at recovery `R`; one more `LoadTimeout` when one is; later still while a store operation outlives its deadline). *Corrected at the 4.3 review; the earlier text gave `min(Cooldown, RefreshInterval) + LoadTimeout` and `max backoff + LoadTimeout` without their assumptions.* Operators who need zero 401s insert the key at a quiet time and wait `T_prop` before expecting new-key traffic. (3) Keep the old key active for at least `TokenExpirationMinutes + TokenValidationClockSkew` (default 30 + 5 min) after the insert so tokens minted before the switch stay verifiable. (4) Set `IsActive = false` on the old key; its tokens are rejected within `T_prop` on healthy instances and never beyond `T_max`.
- **Two instances**: independent gates/cooldowns; A may accept a new-kid token by first sighting while B is suppressed and answers 401 until its cooldown ends or its next tick; tests 1.5-q and 3.1-q pin both outcomes.
- **Unknown kid when refresh fails**: `Current` usable → 401; none/expired → 503.

### 4.6 Request boundary flow (both schemes)

`OnMessageReceived`: extract the bearer token from `Authorization` (same rule as the handler; CMS accepts tokens only there); absent → return. `snapshot = await provider.GetUsableAsync(RequestAborted)`; `SigningKeysUnavailableException` → Error log (category, trace id) → `context.Fail(ex)` (**no downstream call follows**: the handler returns the result before `SetupTokenValidationParametersAsync`, V-1/E0(b)). Parse the header only (`JsonWebToken`); parse failure → return (handler rejects). `kid` present and unknown → `await provider.TryRefreshForUnknownKeyAsync(kid, RequestAborted)` (outcome logged once at Warning, sanitized) → return. Handler: `manager.GetConfigurationAsync(RequestAborted)` (memory), validation, `OnTokenValidated` → `ValidateTokenAsync` (status; dependency → `Fail(ex)`). `OnAuthenticationFailed`: dependency type (direct or inside `AggregateException`) → `Fail(ex)`; otherwise existing logging, no result. `OnChallenge`: `AuthenticateFailure is AuthenticationDependencyUnavailableException` → `HandleResponse()`, 503, `Retry-After: min(RefreshInterval, 30)`, problem body; else existing behavior.

### 4.7 Consumer-by-consumer semantics

| Consumer | Key source | Unknown kid | Key-store failure | Token-status failure | Contract |
| --- | --- | --- | --- | --- | --- |
| Default `Bearer` | manager (snapshot) | boundary refresh if eligible; still unknown → 401 | 503 + `Retry-After` (cold/expired); warm → proceeds | 503 + `Retry-After` | **changed** (was 401) |
| `DmsJwtBearer` (explicit; unused) | same manager, same events | same | same | same | same as `Bearer`; its `EnhancedTokenValidator` pre-check and reflection call are replaced by the shared status check |
| JWKS | provider | n/a | usable snapshot (fresh or overdue) → served from it; no usable snapshot (none or expired) → 503 problem | n/a | **changed** (was `200 []` on failure); `Succeeded(0)` → `200 {"keys":[]}` |
| Introspection | `EnhancedTokenValidator` → `ValidateTokenAsync` → `VerifyTokenAsync` | one gated refresh; still unknown → `active:false` | caught in `EnhancedTokenValidator`, **Error log with category**, `{"active": false}` | same | **preserved existing behavior** (Q15; not a protocol requirement to mask) |
| Revocation | `RevokeTokenAsync` → `VerifyTokenAsync` | same; still unknown → no-op `200` | caught, Error log, `false` → `200` | n/a | **preserved** (RFC 7009 always-200) |
| Token endpoint | `LoadActiveSigningKey` (unchanged; dev-cert path via the shared store) | n/a | existing `FailureUnknown` mapping | n/a | **preserved** |

### 4.8 Security invariants

I-1 issuer/audience/lifetime/signature unchanged. I-2 status check per request, uncached. I-3 authentication succeeds only with a **usable snapshot** (fresh or overdue, within `MaxStaleness`) **and a successful, valid token-status check**. A failed key refresh does not by itself require 503: usable keys keep being served until `MaxStaleness`. No usable snapshot, or a token-status store failure, answers 503. I-4 `T_prop`/`T_max` bounds. I-5 no LKG/metadata fallback; zero backchannel sends. I-6 arbitrary kids → ≤ 1 load per `Cooldown` and per backoff interval. I-7 sanitized kids/categories only; generic 503 body. I-8 failed retrieval never published as empty. I-9 missing `kid` and forged signatures rejected. I-10 issued dev-certificate tokens always verify against the published certificate (D-10).

### 4.9 Logging requirements

- **Load attempt failure**: one Error per attempt with the category (`SigningKeyStore` | `TokenStatusStore`), the trigger (startup | timer | request | unknown-kid), the consecutive-failure count, the next-attempt delay, and the sanitized exception type and message — never key material or connection strings.
- **Snapshot publication**: Information with key count, snapshot version, source (database | certificate), and retrieval duration. `Succeeded(0)` additionally logs a Warning (an empty key set is being served deliberately, distinguishable from a swallowed failure — AC 4).
- **Request boundary 503**: Error with the category and trace id; the response body stays generic (I-7).
- **Unknown-kid refresh**: one Warning per boundary decision with the sanitized kid and the outcome (refreshed-found | refreshed-absent | suppressed-cooldown | refused-gate).
- **Scheduler wake**: Debug with the wake reason (`RetryDeadline` | `Tick` | `Signal`) and the recomputed due time.
- **`DevelopmentCertificateStore` creation**: Information, path only.
- Every string that can derive from a client is normalized/sanitized before logging (`LoggingUtility.SanitizeForLog`), and values are logged as their already-normalized strings, never as the wrapping record/struct (repository logging rules).

## 5. Phases and steps

**Commit and review conventions.** One local commit per numbered step, message `[DMS-1556] <step id> — <summary>`. Every checkpoint: the solution builds (`dotnet build src/config/EdFi.DmsConfigurationService.sln`), the application still resolves its DI graph, and the test projects the step touches pass. Before each commit: `dotnet csharpier format src/config` on changed C# files, and changed PowerShell files pass `eng/Invoke-StagedPowerShellAnalysis.ps1` (also enforced by the pre-commit hook on staged files). Nothing is pushed and no PR is opened until §7.4 is met and push is explicitly approved. Steps marked **stop** end with a checkpoint report — commit SHA, file-by-file changes, validation commands and results, deviations — and wait for review before the next step starts. Phase 0 evidence lands as increments of the investigation document (§3.7). The scheduler implementation (1.6) is reviewed at its own checkpoint before Phase 2 starts.

### Phase 0 — Investigation (each step stops; evidence committed per step)

**0.1** Harness (`-TotalRequests`, `-MaxConcurrency`, `-ValidateBodies`, measured overlap) + diagnostics recipes (overlay, sidecar, samplers, dump/`threadpool` script). **0.2** E0 (incl. (b) no-downstream-call). **0.3** E1 → G1. **0.4** E2 + M-conn + disk. **0.5** E3/E4/E5 (+E7). **0.6** attribution, G1–G3 records, README index; **stop for G2.**

### Phase 1

**1.1 Options** (+ binding tests). **1.2 Exceptions and snapshot model.** *Checkpoint grouping (G2, 2026-09-30): 1.1 and 1.2 land as one commit and one stop — options and their fail-fast validation, the immutable snapshot and outcome models, and the exception contracts.* **1.3 Parser move** (a focused move: format cache removed per Q7, no other cleanup; existing manager tests green). **1.4 Repository cancellation** (both engines; canceled-token tests). *As implemented:* the cancellable overloads sit beside the existing parameterless reads, which forward `CancellationToken.None`, so existing callers and fakes are unchanged. The token reaches `OpenAsync` and the Dapper `CommandDefinition`. Integration tests hold an exclusive table lock so the read is running on the server when the token is canceled. PostgreSQL answers with `OperationCanceledException` whose inner `PostgresException` is `57014`. **SQL Server answers with `SqlException`** ("A severe error occurred on the current command..." followed by "Operation cancelled by user."), not `OperationCanceledException`; a token canceled before the open is an `OperationCanceledException` on both engines. Consequence for 1.5: a load's failure is classified as `Failed(Retrieval)` whatever the exception type, and waiter or timeout cancellation is detected from the token state, never from the exception type. **1.5 Sources, development-certificate store, provider.** *As implemented:*

- **Source result.** A source returns `SigningKeySourceResult` (usable entries plus a discarded-record count) and throws only when the store cannot be read. The provider does the classification: any exception, whatever its type, is `Failed(Retrieval)`; records exist but none usable is `Failed(Processing)`.
- **Load deadline.** The deadline is a `TimeProvider`-driven token linked with provider disposal and passed to the store. The store call runs on the thread pool, so synchronous source work (for example certificate file and crypto work) cannot delay the requesting caller. The attempt's wait is bounded with `WaitAsync`, so it ends at the deadline even when a store ignores the token. A result that arrives at or after the deadline is rejected: the token is canceled, or the elapsed `TimeProvider` time is at least `LoadTimeout`, which covers a late timer callback. The timeout is recorded once, as `Failed(Retrieval)` with a `TimeoutException`, with backoff.
- **Ownership (review of `42d680fc5`, finding 1).** The single-flight slot owns the store operation until the operation itself finishes, not just until its attempt ends. While an operation that outlived its deadline is still running, admission refuses with `Refused(OperationOutstanding)`, even after the backoff has elapsed. The failure count is not incremented again, and no overlapping store call starts. When the operation finishes, its result is discarded, the slot is freed, and `AttemptStateChanged` fires, so the scheduler (1.6) can wake. **Every recovery bound therefore assumes the underlying store operation terminates.** The database drivers honor the token (1.4 integration tests) and certificate reads are finite; a store call that never returns keeps the instance on its current snapshot, or unavailable, until it does. `Status.StoreOperationOutstanding` exposes this state.
- **Admission (finding 2).** Whether a request needs a load is decided under the gate lock against the snapshot as it is then. The lock-free read is only a fast path that returns a fresh or overdue snapshot. A cold or expired caller that another caller overtook with a publication gets the new snapshot, not a redundant load. An overdue background reload is skipped once a fresh snapshot exists, and an unknown-kid refresh reports `AlreadyPresent` if the kid arrived meanwhile.
- **Waiters.** Waiters use `WaitAsync(ct)` on the shared attempt, so a canceled waiter detaches without touching the load or the failure count.
- **Cooldown.** The cooldown check is atomic with the gate check. Joining an attempt already in flight is allowed because it adds no load.
- **Unknown-key result.** `TryRefreshForUnknownKeyAsync` returns a `SigningKeyUnknownKeyOutcome` rather than a bool (AlreadyPresent, RefreshedFound, RefreshedAbsent, SuppressedCooldown, RefusedGate, RefreshFailed), so the boundary can log the §4.9 decision.
- **State-change signal.** `AttemptStateChanged` is a coalesced `Task` re-armed at each completed attempt.
- **Shutdown.** Handled by `Dispose` (DI disposes the singleton at host shutdown).
- **Development certificate.** `DevelopmentCertificateStore` returns a new instance loaded from the file, with its private key, on every call. 1.5-r asserts one creation log, no leftover temporary file, and a single thumbprint across concurrent validators and issuers; the rename step itself is not observed directly.

Tests (fake repository, `FakeTimeProvider`, no timer): (a) 64 concurrent cold callers → one load, same instance; (b) waiter cancellation detaches only the waiter; (c) `LoadTimeout` → unavailable, repository token canceled; (d) overdue → served + one load requested; (e) expired → load awaited; failure → exception; (f) refresh throws → previous retained, `Failed(Retrieval)`; (g) zero rows → `Succeeded(0)`; rows/none parse → `Failed(Processing)`; partial → `Succeeded(n)`; (h) rotation, **eligible**: unknown kid with gate open and cooldown elapsed → one load, key present; (i) retirement removed after `RefreshAsync`; (j) **successive, non-overlapping** unknown-kid calls (100, each awaited) within one cooldown → exactly one load; after the cooldown → one more; (k) invalid settings → constructor throws; (l) waves of 50 cold calls every 100 ms of fake time during immediate failures for 90 s → loads == backoff intervals elapsed + 1, no calls between attempts; (m) recovery → next eligible attempt succeeds; (n) *(moved to 1.6-f)*; (o) refresh failing, fake time past `MaxStaleness`, `GetUsableAsync` → unavailable; just before → served; (p) rotation, **suppressed**: unknown kid inside the cooldown → refused, 401-class result, key absent; after the cooldown → accepted; (q) two independent providers over one fake repository: A eligible (accepts), B suppressed (rejects) then accepts after its cooldown; (r) **certificate first start**: file absent; concurrent `CertificateSigningKeySource.LoadAsync` and `DevelopmentCertificateStore.GetAsync` (as issuance would call) → one file created, both return the same thumbprint, no partial file observed (temp-file rename asserted via directory listing). Commit. **1.6 Refresh service** (timer-driven): (a) startup load then cadence; (b) failure → next wake at the retry deadline, not the tick; (c) stop token quiet; (d) startup failure non-fatal; (e) **startup retrieval fails, store recovers at fake time `R`, no requests arrive → a successful load completes by `R + 60 s + jitter + LoadTimeout`**; (f) key retired, fake time advanced past `T_prop` with the timer driving → key absent, no manual refresh; (g) a request-triggered failure during the sleep moves the deadline and the service wakes at the new deadline (signal); (h) **retry deadline reached exactly**: last attempt failed, fake time advanced to exactly `NextAttemptAt` → the attempt starts (the due time remains the retry deadline at and past equality; it never switches to the refresh deadline); (i) **success authorizes nothing**: a load succeeds (gate immediately eligible, `NextAttemptAt = now`), no requests arrive → the success signal only recomputes the due time and **no additional load starts before `lastSuccessAt + RefreshInterval`**; (j) **operation outlives the backoff, no requests**: startup times out, the backoff elapses with the store operation still outstanding → no overlapping store call and no status reads while parked; the late result is discarded, and its end starts the recovery attempt at once; (k) **past equality with a snapshot**: a request-triggered attempt outlives its deadline and its operation ends after the retry deadline → the load starts when the operation ends, never at the refresh deadline; (l) **signal before status**: the operation ends between the service's status read and its wait → the service still wakes and retries at the retry deadline; (m) **stale status at admission**: the service observes an elapsed refresh deadline, and a request publishes successfully before admission → the provider refuses (`StateChanged`), no additional load starts, and the service waits for the new deadline; (n) **source `ObjectDisposedException`**: the source throws it once and then succeeds → retrieval failure with backoff, and the service recovers at the retry deadline without requests (the provider-disposal fixture is kept). Commit; **scheduler review checkpoint — stop for review of the 1.6 implementation before Phase 2.** *As implemented:*

- **Loop.** `SigningKeyRefreshService : BackgroundService`. Each pass captures `AttemptStateChanged`, reads `Status`, and computes the due time from `LastOutcome`: `null` → now (startup trigger), `Failed` → `NextAttemptAt`, `Succeeded` → `Snapshot.RetrievedAt + RefreshInterval × [0.9, 1.1)`, with the jitter drawn once per snapshot version. With an attempt in flight or a store operation outstanding, the loop awaits the signal only. Otherwise it starts `RefreshAsync(Startup | Timer)` when `now ≥ max(dueAt, NextAttemptAt)`, else waits until then or the signal (`Task.Delay` on the `TimeProvider`, canceled when the signal wins).
- **Wake log.** Debug `(RetryDeadline | Tick | Signal)` with the recomputed due time and the in-flight and outstanding flags.
- **Shutdown.** The stopping token cancels only the service's wait, so the shared load is untouched; the loop ends without a fault. The loop also ends when `Status.ProviderDisposed` is true, because a disposed provider admits nothing.
- **Conditional admission (review of `edfecec3c`, finding 1).** The service admits with `RefreshIfUnchangedAsync(trigger, status.StateVersion)`. The provider refuses with `Refused(StateChanged)`, without a store call, when any transition has happened since that status was read. Then the service recomputes, so a publication by a request between the read and admission moves the next load to the new refresh deadline.
- **Disposal distinguished (finding 2).** A source's `ObjectDisposedException` is a retrieval failure with backoff; only `Status.ProviderDisposed` stops the loop.
- **Test support.** `SchedulerTimeProvider` records the due instant of each `Task.Delay` timer (not load-deadline timers), so a test advances time only once the service is parked on the wait it expects. `ObservedSnapshotProvider` counts status reads, for the no-spin assertions, and injects the transition after a status read for (l).
- **Mutations (local, reverted).** M8 as two mutants: tick instead of retry deadline, and switching to the refresh deadline once `NextAttemptAt` is past. M10 as two mutants: a signal authorizing a load, and eligibility treated as due. Also outstanding operation ignored, and status read before the signal. Each failed its intended fixtures; the results are in the 1.6 checkpoint report.

### Phase 2

**2.1 Registration of Phase 1 services** (sources by mode, `DevelopmentCertificateStore`, provider, hosted service with guard, `TimeProvider`); registration tests: one provider instance, one hosted service, source by mode; application still resolves with the old resolver. Commit. *As implemented:* `OpenIddictServiceCollectionExtensions.AddSigningKeyServices()` registers everything with try-adds:

- `TimeProvider.System`, if no clock is registered already.
- `DevelopmentCertificateStore`.
- Both sources.
- `ISigningKeySource`, chosen at resolution by `IdentityOptions.UseCertificates` (the same switch the token manager reads).
- `ISigningKeySnapshotProvider` as a singleton.
- `SigningKeyRefreshService` via `TryAddEnumerable`.

It is called only by the three self-contained store registrations: PostgreSQL, PostgreSQL with `JwtSettings`, and SQL Server. That is the identity guard: Keycloak mode registers none of it. Registering twice still yields one provider, one refresh service, one source, one store, and one clock. Nothing consumes the provider yet, and the token manager's constructor is unchanged. The refresh service starts with every self-contained host, so each host performs a background key read at startup. The read is non-fatal: a failure is retried with backoff. The database deploy in `Program` runs before `RunAsync`, so the startup read sees the deployed schema. Tests: backend registration (repeated registration including a started service loading into the shared provider, source by mode, a pre-registered clock kept, each store registration called twice), and `WebApplicationFactory` startup (self-contained on both engines, certificate mode, Keycloak registering nothing, `/health` 200, `ITokenManager` still `OpenIddictTokenManager`). **2.2 `OpenIddictTokenManager` on the provider and the shared certificate store** — tests: (a) `GetPublicKeysAsync` projects/throws; (b) valid → true, revoked → false, unknown kid → one gated refresh then false, malformed jti → false; (c) status-store `DbException`/timeout → typed exception rethrown; (d) `RevokeTokenAsync`/`EnhancedTokenValidator` translation → false/invalid + Error log; (e) **issuance and validation on a first start** (dev-cert mode, file absent, concurrent `GetAccessTokenAsync` and `GetPublicKeysAsync`) → the issued token's `kid` equals the published thumbprint and `ValidateTokenAsync` returns true (I-10). Commit. *As implemented:*

- **Constructor.** `OpenIddictTokenManager` takes `ISigningKeySnapshotProvider` and `DevelopmentCertificateStore`, which 2.1 registers. Its per-call key loops are deleted, and nothing in the manager reads the key table any more.
- **Keys.** `GetPublicKeysAsync` projects `GetUsableAsync(None)`: `PublicParameters` and `KeyId` per entry, `Succeeded(0)` as an empty list, and no usable snapshot as `SigningKeysUnavailableException`. `VerifyTokenAsync` reads the `kid` header without validation. When the snapshot lacks it, the manager calls `TryRefreshForUnknownKeyAsync` once, logs a Warning with the sanitized kid and the outcome, and obtains the snapshot to verify against through `GetUsableAsync` again, never `Current` unchecked (review of `2ea7ece4e`, finding 1). A snapshot usable when verification started can cross `MaxStaleness` during a refresh that then fails, and verification then reports `SigningKeysUnavailableException` rather than an invalid token. Fake-time fixtures pin both sides: a failed refresh with the snapshot still usable rejects the token; a failed refresh crossing `MaxStaleness` throws `SnapshotExpired` with no status query. Verification keys come from `CreateSecurityKey()`.
- **Status.** A `DbException`, `TimeoutException` or `OperationCanceledException` from `GetTokenStatusAsync` becomes `AuthenticationDependencyUnavailableException(TokenStatusStore)`, with the store exception as the inner exception. `ValidateTokenAsync`'s general catch filters out the typed exception, so it propagates. Every other exception, a malformed jti included, is still `false`. **SQL Server pool exhaustion (finding 2).** SqlClient 6.1.4 reports a pooled-open timeout as `InvalidOperationException` (`ADP.PooledOpenTimeout`), which that filter misses. The MSSQL `GetTokenStatusAsync` therefore translates `InvalidOperationException` from its own `SqlConnection.OpenAsync` into `AuthenticationDependencyUnavailableException(TokenStatusStore)`. No other `InvalidOperationException` is classified, and no message is matched. The manager passes the typed exception through unchanged. The MSSQL integration fixture runs the repository over an isolated one-connection pool: it holds the lease, observes the typed failure with the driver's exception inside, releases the lease, and reads the status again.
- **OAuth endpoints.** `RevokeTokenAsync` catches the typed exception, logs an Error naming the category, and returns `false`, so the endpoint still answers 200. `EnhancedTokenValidator` does the same and returns no principal, so introspection still answers `{"active": false}`. The token endpoint is unchanged apart from the development-certificate source.
- **Issuance.** In development-certificate mode, issuance takes its certificate from `DevelopmentCertificateStore.GetAsync`. Database issuance is untouched.
- **Interim behavior until 2.3/3.1/3.3.** The old consumers still call the manager directly. On `Bearer` and `DmsJwtBearer`, a token-status store failure now propagates out of `OnTokenValidated` and is rethrown by the handler, so it answers 500 where it was 401 (V-1). A key-store failure inside the blocking resolver is expected to stay 401: the handler's JWS validation turns it into a failed result. No test covers that until 3.1. JWKS answers 500 where it was `200 []` when no usable snapshot exists. 3.1–3.3 replace all of these with 503. *Since 3.1, `Bearer` answers both dependency failures with 503, and `DmsJwtBearer` does since 3.2; JWKS (3.3) still behaves as described here.*
- **Tests moved.** The key-format warning and re-encoded-key fixtures moved from the manager's tests to `SigningKeySourceTests`, where the key loop now lives.
- **Test ownership.** The frontend revocation fixtures register a disposable owner of the real manager's provider and certificate store through a factory, so the host container disposes them with the `WebApplicationFactory`.
- **Mutation probes at 2.2.** M3, partial status translation, issuance creating its own certificate, and M9 were each caught. The unknown-kid mutants (refresh never called, always called, stale snapshot) are **unrun**: the session's permission classifier blocked the edit, and they are left for the §7.3 pass.

**2.3 `SigningKeyConfigurationManager` + `SigningKeyBearerEvents` + their registration** — tests: (a) configuration identity per version; (b) unavailable → typed exception; (c) events with a fake `HttpContext` and a **counting manager**: boundary cold failure → `Result` is a failure carrying the typed exception **and the manager's `GetConfigurationAsync` count is 0**; (d) unknown kid → one refresh request; (e) `OnChallenge` typed → 503 + `Retry-After` + `Handled`; other → untouched; registration test: manager and events resolvable, one instance each. Commit. *As implemented:*

- **Manager.** `SigningKeyConfigurationManager` implements the plain `IConfigurationManager<OpenIdConnectConfiguration>`, not `BaseConfigurationManager`; a structural test pins this. `GetConfigurationAsync(cancel)` passes the request's token to `GetUsableAsync`, so cancellation ends only that caller's wait. It keeps one configuration per snapshot version, built under a lock the first time the version is seen. The configuration's `SigningKeys` come from one `CreateSecurityKeys()` call. ~~Its `Issuer` is `IdentitySettings:Authority`.~~ *Corrected at the 3.2 review (2026-09-30): the configuration declares no issuer.* The handler appends the configuration's issuer to the scheme's valid issuers, so the Authority issuer widened any scheme whose own `ValidIssuer` differs (`DmsJwtBearer` with its own `JwtSettings.Issuer`). With no issuer, the handler appends a null entry, IdentityModel skips it, and each scheme validates only against its own `ValidIssuer` (I-1). The manager no longer takes `IdentityOptions`. A unit test pins the null issuer. A caller holding an older snapshot than the configuration already built gets the newer configuration.
- **`RequestRefresh()` is a deliberate no-op (approved at the 2.3 review, 2026-09-30; §4.3.7 updated).** The handler calls this method with no key id, and only when `RefreshOnIssuerKeyNotFound` is on, which Q14 turns off. A kid-less refresh cannot pass the provider's cooldown, so any load started here could run once per request if that setting were ever enabled (I-6). The boundary already makes the one cooldown-limited refresh, with the token's key id, which keeps AC 3's rotation and throttling requirements. A test pins that the method starts no load. No kid-less provider member is added.
- **Events.** `SigningKeyBearerEvents` is a singleton over the provider, `ITokenManager` and the options. Its members are composed onto each scheme's events in 3.1/3.2:
  - `MessageReceivedAsync` reads the token by the handler's own rule (`Authorization: Bearer`). It calls `GetUsableAsync(RequestAborted)`; a failure is logged at Error with the category and trace id, and `Fail(ex)` follows. It parses the header only. For an unknown `kid` it calls `TryRefreshForUnknownKeyAsync` once, logs a Warning with the sanitized kid and the outcome, and **checks usability again**, failing with the typed exception if the snapshot expired during a failed refresh.
  - `TokenValidatedAsync` is the shared, uncached status check. `false` fails with "Token has been revoked or is invalid."; the typed exception fails with the exception and an Error log.
  - `TryFailOnDependency` fails with the typed exception, direct or inside an `AggregateException`, and returns `true`. Otherwise it returns `false` with no result, so each scheme's existing logging and the handler's rethrow stay as they are.
  - `TryChallengeDependencyAsync` answers a dependency failure with `HandleResponse()`, 503, `Retry-After: min(RefreshInterval, 30)`, and the generic `ForUnclassifiedStatus(503, "Service Unavailable")` problem body. Any other challenge is left untouched.
- **Registration.** `TryAddSingleton` for both types in `AddSigningKeyServices`, so there is one of each however often it is called, and none in Keycloak mode. Neither scheme uses them yet.
- **Tests.** 2.3-c runs the real `JwtBearerHandler`, with a counting manager and the shared message-received and token-validated handlers. On a cold failure the result carries `SigningKeysUnavailableException` and the manager count is 0. The same harness with keys available counts 1 and authenticates, which shows the 0 comes from the boundary.

### Phase 3

**3.1 `Bearer` scheme** (manager, events, `RefreshOnIssuerKeyNotFound=false`, resolver deleted) with pipeline fixtures (real `JwtBearer`; fake repository with gates; endpoint repository faked; recording `BackchannelHttpHandler`; the configuration manager wrapped by a counting decorator in the host): (a) 64 cold → 200, one load [F]; (b) revoked → 401 [C]; (c) expired/wrong audience/wrong issuer → 401 [C]; (d) unknown kid → 401, ≤ 1 load [F]; (e) new key rotated in, **eligible** → accepted in the first-sighting request [F]; (f) cold key-store failure → 503 + `Retry-After`, generic body, **manager call count 0** [F]; (g) status-store failure with warm keys → 503 [F]; (h) key retired, fake time past `T_prop` (hosted timer) → 401 [F]; (i) recovery → 200 [F]; (j) forged signature → 401 [C]; (k) missing kid → 401 [C]; (l) uncached status (revoke between two requests) → second 401, two status reads [C]; (m) refresh failing, past `MaxStaleness` → 503 [F]; (n) three request waves during immediate failures → one load [F]; (o) zero backchannel sends [F]; (p) post-authentication profile-repository failure → 500 unchanged [C]; (q) new key rotated in, **suppressed** (cooldown running) → 401, then 200 after the cooldown [F]; (s) structural: `IssuerSigningKeyResolver == null`, `ConfigurationManager` is ours, `TokenValidationParameters.ConfigurationManager == null` [F]. Commit. *As implemented:*

- **Wiring.** `SigningKeyJwtBearerOptionsExtensions.UseSigningKeySnapshot(options, manager, events)` (backend) supplies `SigningKeyConfigurationManager`, sets `RefreshOnIssuerKeyNotFound = false`, and removes any `IssuerSigningKeyResolver`. It composes the shared events onto the scheme's own:
  - Message-received runs the boundary.
  - Token-validated runs the shared, uncached status check in place of the scheme's inline one.
  - A dependency failure in authentication-failed fails with the typed exception, and one in challenge answers 503.
  - Anything else goes to the scheme's existing handler, so `Bearer`'s `Authentication failed` Error log and its 401 challenge are unchanged.

  The frontend applies it in `AddOptions<JwtBearerOptions>("Bearer").Configure<SigningKeyConfigurationManager, SigningKeyBearerEvents>`, replacing the resolver block. Issuer, audience, lifetime and signing-key validation are as before. `Authority` and `MetadataAddress` stay set and are inert (C-2). 3.2 reuses the same helper for `DmsJwtBearer`.
- **Pipeline host.** `BearerPipelineHost` runs the real application (`WebApplicationFactory<Program>`: real `Bearer` scheme, token manager, provider and refresh service). It replaces these parts:
  - `IOpenIddictTokenRepository` with a fake whose key reads can be gated or failed, and whose status reads can be failed.
  - `IProfileRepository` with a fake.
  - `TimeProvider` with `FakeTimeProvider`.

  Two test decorators and a recording handler observe the host:
  - A counting provider decorator shows how many requests are waiting on a load.
  - A counting configuration-manager decorator is added in `PostConfigure`, after the framework's post-configuration. Were the manager missing, it would wrap the framework's HTTP manager, which then sends over the backchannel.
  - A recording `BackchannelHttpHandler` is set in `Configure`.

  Test tokens carry the role claim as `JwtTokenGenerator` issues it (the `…/identity/claims/role` URI). The host restores that `RoleClaimType`, because the Test settings' short `role` is renamed by inbound claim mapping.
- **Fixtures.** (a)–(q) and (s) as listed.
  - (c), (j) and (k) run as one fixture with a malformed-token case (`Bearer not-a-jwt`). Each of these is an ordinary 401 (`WWW-Authenticate: Bearer`, no `Retry-After`) with no key reload, no status read, and nothing sent.
  - Every dependency 503 is checked for `Retry-After: 30`, `application/problem+json`, the generic body, and no `WWW-Authenticate`.
  - (a) proves the requests were all waiting before the load: 64 provider requests are observed, and none has completed, before the gate opens.
  - (m) also shows that an overdue snapshot is still served while refreshes fail (I-3).
  - Zero backchannel sends (o) is asserted in the cold, unknown-kid, eligible-rotation and cold-failure fixtures.
  - A Keycloak host keeps the framework manager and `RefreshOnIssuerKeyNotFound = true`.
  - Backend unit tests pin the helper's composition: non-dependency failures and challenges reach the scheme's handler; dependency ones do not.
- **Mutation probes at 3.1 (local, reverted).** M7 (manager not supplied) was caught by 3.1-o (the three backchannel assertions) and 3.1-s (the manager and backchannel structural checks), with 24 other failures. The challenge-classification mutant and a wiring revert (the old resolver restored) are **unrun**: the session's permission classifier blocked the edit, and they are left for the §7.3 pass with M2a/M2b.

**3.2 `DmsJwtBearer` parity** (structural (s), explicit-scheme fixture: cold 200, key-store failure 503, revoked 401). Commit. *As implemented:*

- **Wiring.** `AddJwtAuthentication` applies `UseSigningKeySnapshot` in `AddOptions<JwtBearerOptions>("DmsJwtBearer").Configure<SigningKeyConfigurationManager, SigningKeyBearerEvents>`, which replaces the blocking resolver block. The scheme's inline `OnTokenValidated` is deleted: the `EnhancedTokenValidator` pre-check and the reflective `ValidateTokenAsync` call are one duplicate status path, replaced by the shared, uncached check. Its `OnChallenge` Warning and `OnAuthenticationFailed` Error still run for every non-dependency failure. Issuer, audience, lifetime (`ClockSkew`, `RequireExpirationTime`), `RequireSignedTokens` and signing-key validation are unchanged. The `IEnhancedTokenValidator` registration stays for introspection. Every caller of `AddJwtAuthentication` is a self-contained store registration that also calls `AddSigningKeyServices`.
- **Issuer policy (review of `dc6817ab9`).** As first committed, the shared configuration carried `Issuer = IdentitySettings:Authority`, and the handler added it to the valid issuers. A `DmsJwtBearer` registered with a different `JwtSettings.Issuer` (the `AddPostgresOpenIddictStores(…, JwtSettings)` overload) would then also have accepted a correctly signed Authority token. That broke AC 3's requirement to preserve validation, even with no production caller. The correction removes the issuer from the shared configuration (2.3 notes above), so each scheme keeps its own issuer policy. Regression: a host whose `DmsJwtBearer` issuer differs from the Authority.
  - An otherwise valid Authority token is the ordinary 401 at the scheme's issuer check (`SecurityTokenInvalidIssuerException`), after one manager call, with no status read and no endpoint execution.
  - A token from the scheme's own issuer passes that check. The token manager's verification, which has always used the Authority as issuer, then rejects it with 401, exactly as the inline `ValidateTokenAsync` call did before 3.2. With differing settings the scheme accepts no token, before and after.
  - The agreeing-settings fixtures (cold 200, wrong issuer 401) and `Bearer`'s 3.1 fixtures, including its wrong-issuer case, pass unchanged.
- **Explicit scheme selection.** No production endpoint selects `DmsJwtBearer` (F2). The pipeline host adds a probe endpoint, placed before the application's middleware by an `IStartupFilter`. It carries `JwtAuthenticationExtensions.CreateJwtAuthorizeAttribute()` and runs through the real authorization middleware, so only the named scheme authenticates and challenges. The application's authentication middleware (default `Bearer`) is never reached on that path. The host records the scheme behind every message-received event, and each fixture asserts `DmsJwtBearer` was the only one to run. The 200 body echoes the authenticated ticket's scheme. A recording `ILogger<JwtBearerHandler>` captures the scheme's own event logging.
- **Fixtures** (`DmsJwtBearerSchemePipelineTests`):
  - Cold: 16 concurrent → all held until the load, then 200. One key read, manager count 16, 16 status reads, nothing sent.
  - Key-store failure (cold) → dependency 503, manager count 0, no status read, no scheme log, nothing sent.
  - **Status-store failure** (warm) → dependency 503 after one manager call and one status read.
  - Revoked → ordinary 401, one status read, the scheme's challenge Warning.
  - Forged signature → ordinary 401, the scheme's authentication-failed Error and challenge Warning, no reload, no status read.
  - Wrong issuer (settings agree) → ordinary 401 at the issuer check, no status read, no endpoint execution.
  - Scheme issuer differs from the Authority → see the issuer-policy note above.
  - Structural (s), undecorated: no resolver; the shared `SigningKeyConfigurationManager`, the same instance as `Bearer`'s and not a `BaseConfigurationManager`; `TokenValidationParameters.ConfigurationManager == null`; `RefreshOnIssuerKeyNotFound == false`; no backchannel; validation settings as above; the shared message-received and token-validated handlers.
  - A Keycloak host registers no `DmsJwtBearer` scheme.
- **OAuth endpoints.** Introspection, revocation and token contracts are untouched; no endpoint code changed.
- **Mutation probes at 3.2.** None run (not requested at this checkpoint). The 3.1 challenge-classification and wiring-revert mutants stay **unrun** for the §7.3 pass.

**3.3 JWKS** (no usable snapshot → 503 [F]; failed refresh with a usable snapshot → keys served [C]; empty `200 []` [C]; keys [C]). Commit. **3.4 PostgreSQL integration pipeline test** (`[NonParallelizable]`; `OpenIddictKey` respawned; host and hosted services disposed before reset — Q16): (a) 64 concurrent cold → all 200 with expected definitions; connections/backends **recorded only**; (b) newer active key inserted → signing switches on the next mint (assert the minted `kid`) and the token is accepted (eligible first sighting); (c) `IsActive=false` + provider `RefreshAsync` → old-key token 401. Commit.

### Phase 4

**4.1-H Healthy baseline-comparison runs** on the fixed image (both profiles; 5 cold + 5 warm at 87/87 and 256/128; `-ValidateBodies`): gate = every expected request returns 200 with the expected `id`/`definition`; stacks recorded. **4.1-O Injected outage runs** (`docker pause dms-postgresql` 20 s mid-burst): expectations classified by stage — authentication-stage → 503 + `Retry-After`; requests already past authentication may hit the unchanged profile-repository 500 (F8) and are reported separately; recovery within `max backoff + LoadTimeout`. Doc updated; commit. **4.2 E2E** (teardown; rebuild; shards 1 and 2 ≥ 2 runs each; CMS E2E once). **4.3 Docs** (`CONFIGURATION.md`, `CS-AUTH.md` incl. the runbook of §4.5, README, spec status). `CONFIGURATION.md` documents `IdentitySettings:KeyFormatCacheSize` as an ignored compatibility setting with no effect. Decided at the 1.3 review: it is kept for DMS-1556, and removing it belongs to a separately scoped configuration cleanup. **4.4 Final matrix + push-readiness report.**

*4.1 as run (2026-10-01, `DMS-1556-investigation.md` §4.1):*

- **Image and settings.** Image `ed-fi-api-config-local:dms1556-c799f084d`. Default
  settings throughout; the headroom block is the only `max_connections=200` run.
- **4.1-H.** 5 cold + 5 warm runs per workload and profile, plus 3 + 3 headroom runs.
  - Catalog 87/87: all 200 with validated bodies on both profiles.
  - Default 256/128: 53300-only failures on both profiles. These are 503 at
    authentication (`TokenStatusStore`) and 500 after it. By P-7.4 this is a gate
    failure returned to review; no setting was changed.
  - Headroom 256/128: all 200.
- **4.1-O.** Three scenarios, 2 repetitions per profile:
  - the specified pause (`docker pause dms-postgresql`, 20 s, mid-round);
  - a 40 s pause that outlasts Npgsql's 30 s command timeout;
  - a key-store-only outage (`LOCK TABLE dmscs."OpenIddictKey"` held 75 s across a CMS
    restart). It is the only way to reach the "no usable snapshot" state, and so the
    JWKS 503, at default settings.
- **Deviations from the plan as written.**
  - **Dumps:** one `Worker Min Limit` dump per block, not per run, still outside the
    timed windows. A first sequence was stopped for host memory; that was this choice's
    reason.
  - **Pause round size:** the pause rounds are 870 requests through 87 slots, so the
    pause lands mid-round. A warm 87-request round ends before `docker pause` takes
    effect.
  - **Superseded first outage set:** the first P-runner-approx outage set was rerun.
    Docker log rotation truncated its pause-run CMS logs, and its key-lock window ended
    before recovery.

*4.3 as done (2026-10-01, documentation only; no workload rerun):*

- **`docs/CONFIGURATION.md`**, new section *Signing-key settings*:
  - the four `SigningKey*` settings, with defaults, accepted ranges and startup validation
    (self-contained only);
  - `KeyFormatCacheSize` as an ignored compatibility setting;
  - a connection-capacity note: the observed 53300 limitation (P-G3's optional note,
    P-7.4). It recommends no changed default.
- **`CS-AUTH.md`**, new section *Signing keys in self-contained mode*:
  - the snapshot and its sources;
  - refresh, cooldown, backoff and maximum staleness, and the recovery bound;
  - dependency 503 versus ordinary 401 versus a successful empty JWKS, per consumer;
  - why cached keys give no general database-outage availability (token status stays
    uncached);
  - the §4.5 rotation and retirement runbook, with the new-key acceptance delay;
  - the §4.9 log signals.
- **`README.md`** (configuration-service designs): links to this spec, the investigation and
  the CS-AUTH section.
- **Gate state unchanged:** the default 256/128 stress gate remains failed (P-7.4, 4.1
  disposition). CI and final acceptance (4.4, §7.4) are pending.
- **Review corrections of `2a497b067` (documentation only):**
  - Outage classification. With the database down, no usable snapshot gives 503
    `SigningKeyStore`. A usable snapshot gives 401 for a token rejected on its own merits,
    and 503 `TokenStatusStore` for one that passes validation. The text no longer says
    `TokenStatusStore` regardless of the snapshot (here in §4.5 and in `CS-AUTH.md`).
  - The contradictory "an ordinary 401 never results from a key-store failure" is removed.
  - `T_prop` includes the load in flight at the change: 350 s at the defaults (§4.5).
  - The new-key bound now states its assumptions: a healthy store, and an eligible
    new-key request after the cooldown, giving 50 s. Failing-store recovery refers to
    §4.4's conditional bounds.
  - `docs/OWASP-AUTH-COVERAGE.md` now qualifies retirement as effective after validators
    refresh and links the runbook.

*4.4 as done (2026-10-01, `DMS-1556-investigation.md` §4.4; no production change, no
load or E2E rerun):*

- **§7.1 on the final commit `1bc6f6fa3`:**
  - backend unit 1861/0/0 and frontend unit 1772/0/0;
  - PostgreSQL integration 937/0/0 and SQL Server integration 957/0/0, on the dedicated
    `127.0.0.1` targets. The full SQL Server lane was last run at 1.4, so this is its
    first full run on the final code.
  - CSharpier and PowerShell analysis are clean.
  - The E2E lanes are reused from 4.2 (identical `src/`).
- **§7.3:**
  - Re-run on the final code and caught: M1, M3, M4, M5 and M6, each on both its
    provider/manager leg and its pipeline leg, plus M8a/b, M10a/b, M11 and M12.
  - Reused with unchanged code: M7, M9, M13, M14 and M15.
  - **M2a was denied by the permission classifier; M2b was not attempted (same kind of
    edit).** Both stay unrun, with the 2.2 unknown-kid and 3.1 challenge/wiring mutants.
- **§7.4:** not met. The default 256/128 gate failed (53300, P-7.4), and M2a/M2b are
  unrun. Push is not approved.

**Changed log signal (recorded at the 3.3 review).** The baseline code logged `Failed to fetch public keys for JWKS` when a key read failed, and `Invoke-E1Baseline.ps1` counts that string (`$cmsKeyFetchFailures`). The fixed code never emits it: since 2.2 a key-store failure is logged by the snapshot provider's load-attempt Error (§4.9, category `SigningKeyStore`), and since 3.3 a JWKS request with no usable snapshot logs `The JWKS could not be served: the SigningKeyStore is unavailable (trace …)`; protected requests log `Authentication could not reach a decision: the {Category} is unavailable (trace …)`. On the fixed image that counter therefore reads zero whatever happens, and Phase 4 must **not** treat it as evidence that dependency failures disappeared: 4.1 counts the new signals, and outage runs (4.1-O) must show them. The historical baseline evidence (0.3–0.6, `DMS-1556-investigation.md`) stays unchanged.

## 6. Compatibility and rollout

- **Wire contracts**: token, introspection, and revocation endpoints unchanged (§4.7). Protected requests and JWKS gain 503 + `Retry-After` where they previously answered 401 / `200 {"keys":[]}`: protected requests when no usable snapshot exists or the token-status check fails, and JWKS when no usable snapshot exists. A failed refresh with a usable snapshot still serves. This is the deliberate, documented contract change (D-9, AC 4).
- **Configuration**: the new `SigningKey*` options (§4.3.12) all have defaults, so existing deployments start unchanged with no configuration edit; invalid values fail fast at startup.
- **Database**: no schema change on either engine; `RelationalMappingVersion` stays `v3`. The repository contract change is additive (cancellation-token overload, D-8).
- **Keycloak mode**: untouched — the affected components are only registered by the self-contained store registrations.
- **Certificate mode**: development-certificate creation now has a single owner (same file, same thumbprint kid; no change for deployments that supply their own certificate or use database keys).
- **Rotation**: semantics documented as the system actually behaves (F9, §4.5 runbook); `MaxStaleness` is a documented, configurable policy (Q13).
- **Rollback**: reverting the commit range restores the blocking resolver; no data migration in either direction.

## 7. Test matrix, mutation checks, push-readiness

### 7.1 Automated matrix

All commands run from the worktree root in pwsh unless noted. [F]/[C] labels as in §5.

- **CMS unit tests** (every `*.Tests.Unit` project under `src/config`): `dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit` and `dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit`.
- **PostgreSQL integration**: `dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration` against a trust-auth PostgreSQL on `localhost:5432` with database `edfi_configurationservice` created. Key-table fixtures are `[NonParallelizable]` and respawn `OpenIddictKey` with hosts disposed first (Q16).
- **MSSQL integration**: `dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration` with `ConnectionStrings__MssqlAdmin` pointing at a dedicated local SQL Server (e.g. `Server=localhost,1434;User Id=sa;Password=<pw>;TrustServerCertificate=true`). The variable absent means the tests **silently skip** — its presence is checked before the run counts.
- **CMS E2E**: from `src/config/tests/EdFi.DmsConfigurationService.Tests.E2E`: `pwsh ./setup-local-cms.ps1`, `dotnet test`, `pwsh ./teardown-local-cms.ps1`.
- **DMS E2E shards 1 and 2, ≥ 2 independent runs each**: `./build-dms.ps1 Build -Configuration Release` first (missing Release E2E assemblies otherwise yield a zero-test false green), then `./build-dms.ps1 E2ETest -Configuration Release -IdentityProvider self-contained -EnvironmentFile './.env.e2e' -TestFilter 'Category=@e2e-ci-shard-1'` (and `-shard-2`). `-SkipDockerBuild` only when the image already matches the branch (a cross-branch stale image aborts setup on a schema-hash mismatch); teardown/setup between runs.
- **Formatting gate**: `dotnet csharpier check src/config` (scoped: the whole tree has accepted pre-existing drift in six unrelated files).
- **PowerShell analysis**: `pwsh ./eng/Invoke-StagedPowerShellAnalysis.ps1 <changed .ps1/.psm1 paths>` (also runs on staged files from the pre-commit hook).

### 7.2 Runtime evidence

4.1-H tables (all-200 with body validation, both profiles), 4.1-O stage-classified tables, stacks, counters, M-conn, `Worker Min Limit`, PostgreSQL/disk samples.

### 7.3 Mutation checks (applied locally, reverted, `--no-incremental` rebuild)

| M | Mutation (guarantee) | Must fail |
| --- | --- | --- |
| M1 | `GetUsableAsync` always loads (coalescing) | 1.5-a, 3.1-a |
| M2a | boundary swallows `SigningKeysUnavailableException` and returns without a result (short-circuit) | 2.3-c, 3.1-f (manager call count) |
| M2b | dependency translation removed in `OnMessageReceived`, `OnAuthenticationFailed` **and** `OnTokenValidated` (typed exceptions rethrown by the handler → 500) | 3.1-f, 3.1-g, 3.1-m |
| M3 | `ValidateTokenAsync` returns `false` on `DbException` | 2.2-c, 3.1-g |
| M4 | gate ignores `NextAttemptAt` | 1.5-l, 3.1-n |
| M5 | `Cooldown` check removed | 1.5-j (successive), 1.5-p, 3.1-q |
| M6 | `MaxStaleness` ignored | 1.5-o, 3.1-m |
| M7 | `options.ConfigurationManager` not supplied | 3.1-o, 3.1-s |
| M8 | scheduler waits for the tick instead of the retry deadline (incl. switching the due time to the refresh deadline once `NextAttemptAt` is due) | 1.6-b, 1.6-e, 1.6-h |
| M9 | `DevelopmentCertificateStore` lock removed (both paths create independently) | 1.5-r, 2.2-e |
| M10 | scheduler starts a load from the success signal (treats gate eligibility as due-ness) | 1.6-i |
| M11 | scheduler ignores an outstanding store operation (retries on the expired deadline while it runs) | 1.6-j, 1.6-k |
| M12 | scheduler reads the status before capturing the state-change signal | 1.6-l |
| M13 | scheduled admission unconditional, or conditional on a version re-read at admission | 1.6-m |
| M14 | a source `ObjectDisposedException` treated as provider disposal | 1.6-n |
| M15 | provider ignores the observed state version | 1.6-m, provider conditional-refresh fixture |

### 7.4 Push-readiness criteria

**7.4-H**: every healthy baseline-comparison run in §7.2 has 100 % HTTP 200 with validated bodies at 87/87 and 256/128 under both profiles. **7.4-O**: injected-outage runs match §4.6/§4.7 by stage. All lanes in §7.1 green with skips enumerated; M1–M15 confirmed; shards 1 and 2 green on ≥ 2 independent runs each; no `src/dms` path in the diff; CSharpier clean; docs merged; every §2 row filled; explicit approval to push. *In force (P-7.4, §0.00):* a headroom (`max_connections=200`) 256/128 run supplements the unchanged all-200 gate. A default-setting run with 53300 failures goes to review; it is never a pass and never licenses a pool or `max_connections` change.

*Gate decisions at the 4.4 review (2026-10-01; `DMS-1556-investigation.md` §4.4, Review
disposition):*

- **7.4-H stress, ticket-scoped exception.** The default-setting 256/128 result stays
  **FAILED** (591 responses, all PostgreSQL `53300`, 4.1). It is accepted as an exception
  for DMS-1556 only:
  - the evidence separates connection-slot exhaustion from the authentication stall;
  - the catalog workload the ticket concerns passes (8,700/8,700).
  The result is not a pass. No pool size, `max_connections` or concurrency change follows,
  and the headroom run stays supplementary evidence.
- **M1–M15, partial waiver.** The execution requirement is waived for M2a and M2b, the three
  2.2 unknown-kid mutants, the 3.1 challenge-classification mutant and the 3.1 wiring revert.
  - They stay *UNRUN — execution requirement waived*; none counts as passed.
  - The alternative evidence: the reviewed boundary code, the real-pipeline assertions, the
    configuration-manager call counts, the rotation tests, and the executed mutations.
  - The denied edits are not retried by any route.
- **CI.** CI is a merge and completion gate, not a prerequisite for pushing. Draft PRs skip
  CI, so pushing and marking the PR ready each need explicit approval after the integration
  review.
- **Evidence archival.** An indexed bundle (checksums, commit and image provenance; memory
  dumps excluded from the ordinary Jira bundle) is required before the ticket is completed.
- **Integration.** `main` is integrated by a merge, not a rebase, because the published
  commits and the evidence anchor on the existing ids. The checks on the merged tree are
  selected at the integration review.

## 8. Risks

- **Reproduction may be host-dependent** (H4): P-runner-approx is a stress approximation, not the CI envelope; the real envelope comes from E7 and CI after push. G1 has the explicit re-plan path when neither R-500 nor R-slow reproduces.
- **The diagnostics overlay changes the measured system** (dotnet-monitor event pipes, PostgreSQL statement/connection logging I/O): every compared run keeps the overlay constant, and dumps are collected outside timed burst windows (Q17).
- **Dump analysis requires a linux-musl-compatible `dotnet-dump` host** — mitigated by running the pinned tool inside a container whose runtime matches the CMS image.
- **`AttemptStateChanged` signaling must not wake the scheduler in a tight loop** (coalesced signal, tested by 1.6-g), **and a signal must never authorize a load** — a success with an immediately-eligible gate would otherwise cause a spurious reload (tested by 1.6-i).
- **Jittered deadlines make equality timing subtle**: the due-time rules are pinned at exact-deadline boundaries with fake time (1.6-h) rather than trusting comparison direction.
- **Fixing blocking may unmask a second mechanism** (H2/H3 under higher effective concurrency): §3.5 attributes mechanisms from direct evidence, and G2 re-approves §4 if the dominant mechanism is not H1.
- **Stack/environment drift between runs** (shared Docker network, stale images): teardown/setup per §3.1 and the schema-hash guard on `-SkipDockerBuild` (§7.1).
