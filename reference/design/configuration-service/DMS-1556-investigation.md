# DMS-1556 investigation record

Evidence log for the Phase 0 investigation defined in
[DMS-1556-cms-signing-key-resolution-under-load.md](DMS-1556-cms-signing-key-resolution-under-load.md) (§3).
Each step lands as an increment of this document. Raw captures that exceed what belongs in
the repository go to Jira DMS-1556.

**Scope reminder:** nothing in this document establishes the ticket's failure mechanism
until the E1+ experiments run. Step 0.2 in particular establishes *integration
feasibility* of the proposed design mechanisms; it does **not** establish the root cause
and satisfies no part of AC 1.

## 0.2 — E0 framework probes (2026-09-30)

### What E0 answers

The spec's design (D-2 plain `IConfigurationManager<OpenIdConnectConfiguration>`, D-3
request-boundary classification via `context.Fail(exception)`, I-5 no metadata/backchannel
fallback) rests on verified-by-reading framework facts V-1, V-2, and V-5. E0 pins those
facts *empirically* on the exact versions CMS ships with, so later phases cannot be
undermined by a misreading. Framework observations are distinguished from behavior the
scratch provider itself supplies; the scratch provider is a stand-in, not the design.

### Environment and reproducibility

- Probe project: `eng/performance/dms-1556/E0Probes` (scratch; not in any solution or CI
  lane). Command: `dotnet test eng/performance/dms-1556/E0Probes` (SDK 10.0.401).
- Pinned versions, matching `src/Directory.Packages.props`:
  `Microsoft.AspNetCore.Authentication.JwtBearer` / `Microsoft.AspNetCore.TestHost`
  **10.0.1**, `Microsoft.IdentityModel.*` **8.12.0**. The project opts out of central
  package management so a nested worktree cannot silently float these.
- Host shape: in-process TestServer whose Bearer scheme mirrors the CMS self-contained
  configuration (`WebApplicationBuilderExtensions.cs:335-384`: `Authority`,
  `MetadataAddress`, `SaveToken`, `Audience`, `RequireHttpsMetadata=false`, TVP with
  `ValidateAudience/Issuer/IssuerSigningKey`, `ValidIssuer=Authority`, `RoleClaimType`),
  with two probe differences: `options.ConfigurationManager` supplies a plain scratch
  manager instead of the blocking `IssuerSigningKeyResolver`, and probe events capture
  pipeline observations. Tokens are minted with a probe RSA key whose public half the
  manager publishes — the same shape as CMS database-key mode.

### Results (24/24 passed, `Failed: 0`, first run; re-run green after formatting)

| Probe | Fixture | Framework fact pinned (F) / scratch-provider behavior (S) | Observed |
| --- | --- | --- | --- |
| E0(a)1 | `Given_a_throwing_manager_without_failure_translation` | **F (V-1):** manager exception type is delivered to `OnAuthenticationFailed`; with no event supplying a result the handler **rethrows** (today's 500 shape). | `ProbeDependencyException` captured in the event and observed by the client as the thrown exception; manager called exactly once. |
| E0(a)2 | `Given_a_throwing_manager_with_failure_translation` | **F (V-1):** `context.Fail(exception)` in `OnAuthenticationFailed` prevents the rethrow; `OnChallenge.AuthenticateFailure` is the exact exception. | 401 challenge instead of an escaped exception; `AuthenticateFailure` is `ProbeDependencyException`. |
| E0(b) | `Given_failure_at_the_message_received_boundary` | **F:** `context.Fail(exception)` in `OnMessageReceived` short-circuits the handler — **no `GetConfigurationAsync` call follows** — and the exception reaches `OnChallenge`. | Manager call count **0**; 401 challenge; `AuthenticateFailure` is the boundary's exception. This is the load-shedding property D-3 relies on during retry-delay windows. |
| E0(c) | `Given_a_supplied_manager_with_authority_and_metadata_address_set` | **F (V-5/I-5):** with a `ConfigurationManager` supplied, `Authority` + `MetadataAddress` set exactly as CMS sets them cause **zero backchannel HTTP traffic**, and post-configuration keeps the supplied manager. | Authority pointed at a live local TCP socket counting connection attempts: 3/3 authenticated requests returned 200 and the counter stayed **0**; `IOptionsMonitor` shows the same manager instance after post-configuration. |
| E0(d)1 | `Given_a_completed_request_with_a_recording_manager` | **F (V-2):** the token passed to `GetConfigurationAsync` **is** the request's `HttpContext.RequestAborted` (token equality, not just linkage). | Recorded token equals the middleware-captured `RequestAborted` for the same request. |
| E0(d)2 | `Given_cancellation_during_a_gated_cold_load` | **F:** aborting a request cancels the token *inside* `GetConfigurationAsync`. **S:** waiter-only detachment — the canceled request leaves the shared load while the other waiter completes — is the scratch provider's single-flight design (the D-5 shape), demonstrated on top of the framework fact. | With two requests gated inside one cold load: canceling request A produced `OperationCanceledException` at A's waiter and at A's client; the surviving waiter then completed and request B returned **200**. |
| E0(e) | `Given_the_boundary_translation_prototype` | **S:** cold/expired/usable are scratch-provider states. **F:** the `Fail` → challenge mechanics (pinned in a/b) and, on the recovered 200, V-2's clone facts: the `TokenValidationParameters` actually handed to token validation has `ConfigurationManager == null` and the manager's signing keys are concatenated into `IssuerSigningKeys` (observed by a recording `JsonWebTokenHandler` registered in `options.TokenHandlers`). | Cold → **503 + Retry-After**; expired → **503**; recovered → **200**; manager called only for the recovered request (boundary made no retrieval while unavailable); captured clone: `ConfigurationManager` null, `IssuerSigningKeys` contains the probe kid. |
| E0(f) | `Given_audience_configuration_from_options` | **F (V-5):** `options.Audience` becomes `ValidAudience` when the TVP sets none — exactly the CMS self-contained shape (`ValidateAudience=true`, no `ValidAudience`). | Token with the configured audience → 200; different audience → 401 with `SecurityTokenInvalidAudienceException` captured in `OnAuthenticationFailed`. |

### Contradictions

None. Every probed behavior matched the spec's §1.3 readings (V-1, V-2, V-5) on the
pinned versions. Two nuances worth recording:

- E0(a)1 confirms that without event-supplied results the manager's exception escapes the
  pipeline — the design's 503 mapping (D-3/D-9) is therefore *required*, not cosmetic, to
  avoid replacing today's 500s with different 500s.
- E0(d)1's token **equality** establishes that the handler passes `RequestAborted` itself
  — direct propagation with no detached or linked intermediate token. It establishes
  nothing more: a cancellation token is not unique per request and must not be used as a
  request identifier.

### What E0 deliberately does not show

No load, no database, no thread-pool interaction, no reproduction of the reported 500s:
those are steps 0.3–0.5 (E1–E5/E7) and gates G1/G2. AC 1 remains evidence-gated.

## 0.3 — E1 baseline → G1 (2026-09-30)

### Environment

- Stack: dms-local per spec §3.1 (`teardown-local-dms.ps1`; `setup-local-dms.ps1
  -EnvironmentFile ./.env.e2e`; unchanged CMS image, self-contained identity,
  database-backed keys, PostgreSQL 16.8, DMS running but idle). Diagnostics overlays
  applied (`local-config-diagnostics.yml`, `local-postgresql-diagnostics.yml`) with CMS
  at `Serilog Debug`; 87 profiles seeded (`Invoke-CmsProfileBurst.ps1 -Seed
  -ProfileCount 87`).
- Resource profiles (spec §3.1.8), applied by composing/removing
  `eng/docker-compose/local-resource-runner-approx.yml`:
  - **P-dev**: host 16 cores; CMS and PostgreSQL CPU unlimited; `Worker Min Limit`
    from per-run dumps: **16**.
  - **P-runner-approx**: CMS and PostgreSQL capped at 2 CPUs (`NanoCpus=2000000000`
    verified), `DOTNET_PROCESSOR_COUNT=4`; `Worker Min Limit` from per-run dumps: **4**.
- Command per profile: `Invoke-E1Baseline.ps1 -ResourceProfile <p-dev|p-runner-approx>`
  → 5 runs × (`Invoke-CmsProfileBurst.ps1 -TotalRequests 87 -MaxConcurrency 87 -Rounds 5
  -Cold -ValidateBodies -WithSamplers`), per-run CMS/PostgreSQL logs scoped to the run
  window, one full dump per run collected after the run (outside timed windows, Q17).
- Every run: measured peak overlap **87** in every round, sampler coverage complete
  (`requiredFailures` empty, all 10 runs), zero transport errors. Raw captures (CSVs,
  summaries, sampler files, logs, dumps) retained locally under
  `eng/performance/dms-1556/artifacts/` for the Jira upload.

### Results — P-dev (5 runs, catalog-87x87, cold + 4 warm)

All 435 requests per run returned **HTTP 200 with validated bodies**; zero
request-correlated NpgsqlException / TimeoutException / key-fetch-failure lines in CMS
logs across all runs. Cold-round p99 was 802–922 ms; the worst round of every run was a
**warm** one (round 2 in four runs, round 3 in run 1) at 1348–2408 ms p99, with the
remaining warm rounds at 237–743 ms p99. **Classification: neither × 5.**

### Results — P-runner-approx (5 runs, catalog-87x87, cold + 4 warm)

| Run | Cold round p50 / p99 (ms) | Warm rounds p50 range (ms) | Statuses (run total) | CMS request-correlated Npgsql / Timeout / key-fetch-failure lines¹ | Background worker-poll timeouts¹ | Classification |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | 14843 / 14995 | 364–579 | 200×435 | 0 / 0 / 0 | 1 | R-slow (provisional) |
| 2 | 15230 / 15350 | 412–544 | 200×435 | 0 / 0 / 0 | 1 | R-slow (provisional) |
| 3 | 17007 / 17305 | 384–481 | 200×432, **401×3** | **12 / 12 / 12** | 2 | R-slow (provisional) |
| 4 | 15489 / 15590 | 396–559 | 200×435 | 0 / 0 / 0 | 1 | R-slow (provisional) |
| 5 | 15586 / 15681 | 465–492 | 200×435 | 0 / 0 / 0 | 2 | R-slow (provisional) |

¹ Counted from the run-window CMS logs with `WorkerPollFailed` lines excluded from the
request-correlated Npgsql/Timeout columns and reported separately: the background job
worker's poll timeouts share the exception types but correlate with no request, so they
can support neither R-500 nor the mechanism.

Observations (recorded as observations; attribution belongs to 0.4–0.6):

- **The ~15 s cold-round plateau.** In every run, effectively all 87 cold-round requests
  — including those admitted at t≈0 — completed together at ~15–17 s (p50 ≈ p99), then
  every warm round was healthy (~0.5 s). The plateau sits exactly at Npgsql's default
  `Timeout=15` window (F11), the same boundary the ticket's 500s reported.
- **Run 3 reproduced the ticket's log signature without the 500.** Twelve
  `Failed to fetch public keys for JWKS` Error lines during the cold round, carrying
  `Npgsql.NpgsqlException (0x80004005): The operation has timed out` (connection open)
  and `Exception while reading from stream ---> TimeoutException` — the exact CMS-side
  signature in Jira. **All twelve lines carry `/config/v3/profiles/{id}` request
  paths**: the message is emitted by the shared key-fetch helper
  (`GetPublicKeysFromDatabaseAsync`, F5) during in-request key retrieval, so its text
  alone establishes neither `/jwks` endpoint traffic nor metadata amplification (H5
  remains an open hypothesis with no supporting evidence from this step).
- **A client-visible failure signature: 401 on valid tokens under load.** Run 3's cold
  round returned 401 for three requests (profiles 3, 44, 45) carrying the same valid
  token that succeeded 84 times in the same round (completion at ~15.5 s;
  time-to-first-failure 15534 ms, completion-based). The correlated traces pin the
  mechanism precisely: framework token validation **succeeded** (`JWT token validated
  successfully`), then the **second per-request key retrieval** (F3) inside
  `OnTokenValidated` → `ValidateTokenAsync` → `VerifyTokenAsync` timed out;
  `GetPublicKeysFromDatabaseAsync` swallowed it (F5), logging `Failed to fetch public
  keys for JWKS` at Error and returning an **empty key list**, and verification then
  rejected the token with the Warning `"Token validation failed" verification
  (signature or key id): "missing or unknown 'kid' header (…); available keys: "` —
  available keys empty — producing `context.Fail` and 401. Server-side records exist
  (the Error and Warning above); the failure is nonetheless misreported to the *client*
  as an invalid token. This is the swallowed-empty-key-set behavior F5/I-8 name and the
  duplicate key acquisition F3 documents — the design's D-4 (`Failed(Retrieval)` is
  never `Succeeded(0)`) and single-snapshot retrieval (D-2) target exactly this path
  (AC 3/4). An earlier draft of this section attributed these 401s to a token-status
  read timeout with no server-side record; the correlated traces contradict that, and
  it is corrected here.
- No HTTP 500 was observed in any run; the profile-path 500
  (`ProfileRepository.GetProfile` → `OpenAsync` timeout) did not reproduce at 87/87 on
  this host. A plausible (unproven) reason: this machine's NVMe/CPU clears the
  connection backlog just inside the 15 s window even under the 2-CPU caps, where the
  shared CI runner does not. Establishing that is E2/E7/CI territory.

### Proposed G1 disposition

**Partially reproduced (provisional)** per spec §3.6: R-slow in 5/5 P-runner-approx runs
(zero in 5/5 P-dev runs), with the ticket's exact CMS log signature present in 1/5 runs
and an additional client-visible availability failure: 401s on valid tokens, produced by
the verification-time key retrieval timing out and being swallowed into an empty key
set (F3/F5). Under G1 this means attribution (0.4–0.5) may proceed, **AC 1 is not
satisfied by this evidence alone**, and AC 2's envelope evidence must lean on E7 and CI
after push. Neither-with-replan does not apply.

### Deviations

- The E1 driver (`Invoke-E1Baseline.ps1`) crashed after run 1 of the first P-dev batch
  on a PowerShell strict-mode aggregation bug (an empty `requiredFailures` array
  unwrapped to `$null`); the bug affected only the driver's summary aggregation, never
  the harness, samplers, or captures. It was fixed and the full 5-run P-dev batch was
  re-run from scratch; the orphaned first-attempt artifacts are not part of the index.
- `Stop-DmsSamplerSet` gained a `-SkipWindowWait` option (used by the harness after a
  5 s post-burst settle) so a generous sampler window does not idle after each run;
  coverage is still judged from actual sample timestamps, and all 10 runs passed it.
