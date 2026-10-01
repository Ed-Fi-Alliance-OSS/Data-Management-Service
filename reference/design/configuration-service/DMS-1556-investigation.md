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

## 0.4 — E2 concurrency sweep, M-conn, disk (2026-09-30)

### What E2 can and cannot show

E2 is a **warm** sweep by definition (spec §3.4). Healthy warm results cannot refute
the cold-only behavior E1 recorded, and nothing below is read that way. This step
measures how latency, connection creation and reuse, pool usage, PostgreSQL
waits/CPU, disk, and thread-pool counters scale with measured overlap. It assigns no
hypothesis verdicts (step 0.6) and runs no control (E3/E4/E5 are step 0.5). **G1 stays
"partially reproduced (provisional)" and AC 1 remains unsatisfied.** One stress-point
observation that matches the R-500 signature is recorded for review below; this step
does not re-decide G1 on its basis.

"Warm" is operationalized per point as: CMS restart (token minted first, as in E1),
then an untimed serial warm-up of 87 requests at concurrency 1 covering every seeded
profile, then 5 timed rounds 3 s apart. Round 1 is the first concurrent burst on a
process whose pool holds only what the serial pass needed (3–4 connections at every
point); rounds 2–5 run on the pool round 1 grew, because Npgsql keeps idle connections
300 s. Because each point restarts CMS, a point's pool cannot carry over into the next
point, and sweep order cannot shape the results.

### Environment and commands

- Stack as in 0.3 (dms-local, `.env.e2e`, `DMS_CONFIG_LOG_LEVEL=Debug`, both diagnostics
  overlays), recomposed to **P-runner-approx** before the sweep with
  `docker compose … -f local-resource-runner-approx.yml --env-file .env.e2e -p dms-local
  up -d --no-deps db config` (the exact command is in the tooling README). Before
  recreating, the running containers' compose config hashes were matched against
  `docker compose … config --hash` to confirm the env file and log level. The driver
  refuses to start unless both containers report the profile's CPU limit.
- Verified: CMS and PostgreSQL `NanoCpus=2000000000`, CMS `DOTNET_PROCESSOR_COUNT=4`,
  **`Worker Min Limit` = 4 from the dump at every one of the 21 points** (each dump taken
  after the point's timed rounds, outside the timed windows, Q17). Host: 16 logical cores;
  Docker VM `NCPU=16`, 15.2 GiB. PostgreSQL 16.8, `max_connections=100`,
  `shared_buffers=16384` (8 kB pages), `checkpoint_timeout=300`, `max_wal_size=1024`.
  The CMS connection string sets no pool keys, so Npgsql 8 defaults apply
  (`Max Pool Size=100`, `Timeout=15`).
- Host noise, recorded but not controlled: four unrelated, idle containers from other
  work were running (two PostgreSQL, two SQL Server). The DMS container was running but
  idle, and no non-harness request reached CMS in any timed round.
- Clock: the Docker VM clock ran 37.6–48.0 ms ahead of the host in every point's probe
  (probe round trip 100–124 ms, so the true skew is at most that). Windows are padded
  (0.25 s for events and pg-activity, 1.5 s for 1 s counters) and rounds are 3 s apart,
  so the skew cannot move a sample into the wrong round.
- Command: `Invoke-E2Sweep.ps1 -ResourceProfile p-runner-approx -Repetitions 3`
  (defaults: the spec's seven points, `-Rounds 5`, `-InterRoundDelaySeconds 3`,
  `-WarmupConcurrency 1`). Repetitions 1 and 3 ran the points ascending, repetition 2
  descending. Wall time was about 30 minutes.
- Coverage: every one of the 21 timed bursts passed the sampler coverage gate
  (`requiredFailures: []` in each burst summary). 11,670 timed requests in total.
- Analysis: `dms-1556-analysis.psm1` slices every capture to each round's
  `[startUtc, endUtc]` (new in the harness summary). M-conn counts `connection
  authorized` lines whose client host is the CMS container's IP and whose database is the
  CMS database (`edfi_datamanagementservice`). Sampler sessions are excluded by
  application-name prefix, and every other client is reported separately. The only other
  clients in any timed round were the PostgreSQL container's `pg_isready` health probes
  (1–3 in 18 of the 105 rounds) and a single DMS connection to its own database.

### Results — catalog points (T = 87)

Ranges are across the 3 repetitions: round 1 is one round per repetition (n = 3), and
rounds 2–5 are four per repetition (n = 12). Every request at every catalog point
returned **HTTP 200 with a validated body** (7,830 of 7,830). CMS logged no
request-scoped warnings or errors in any catalog round; the only background line was
one `WorkerPollFailed` in round 1 of each 87x87 repetition.

| Point | Round | p50 (ms) | p99 (ms) | Overlap | CMS conns created | CMS backends peak | Pool busy / busy+idle max | Thread-pool threads max (queue max) | PG CPU % max (mean) | PG active backends peak |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 87x4 | 1 | 8.6–9.6 | 50.5–62.2 | 4 | 0 | 4 | 0 / 4 | 8–9 (0) | 6.2–7.6 (4.4–5.2) | 0 |
| 87x4 | 2–5 | 6.5–7.4 | 41.7–49.8 | 4 | 0 | 4 | 0–2 / 4 | 8–10 (0–1) | 7.4–10.6 | 0 |
| 87x8 | 1 | 14.6–16.9 | 62.3–69.7 | 8 | 4–5 | 8 | 0 / 8 | 9 (0–1) | 10.3–12.2 (6.2–7.2) | 0 |
| 87x8 | 2–5 | 10.9–12.6 | 50.6–75.5 | 8 | 0–1 | 8–9 | 0–1 / 8–9 | 14–16 (0–1) | 8.2–9.6 | 0–1 |
| 87x16 | 1 | 51.5–65.9 | 93.2–217.3 | 16 | 10–13 | 14–16 | 0–8 / 14–16 | 13–16 (0–8) | 12.7–19.2 (7.4–13.2) | 0–2 |
| 87x16 | 2–5 | 22.4–58.2 | 55.1–262.1 | 16 | 0 | 14–16 | 0–6 / 14–16 | 17–26 (0–1) | 6.7–10.2 | 0–1 |
| 87x32 | 1 | 93.1–102.8 | 396.2–1493.8 | 32 | 26–27 | 30–31 | 22–31 / 30–31 | 16–27 (3–13) | 26.2–28.9 (8.7–12.5) | 0–2 |
| 87x32 | 2–5 | 44.1–115.7 | 71.3–1885.1 | 32 | 0–3 | 30–33 | 0–30 / 30–33 | 25–33 (0–11) | 8.5–17.7 | 0–2 |
| 87x64 | 1 | **7860–8962** | **7944–9050** | 64 | 60–62 | 64–66 | 60–63 / 64–66 | 57–61 (**39–48**) | 31.0–62.7 (7.6–8.1) | 5–12 |
| 87x64 | 2–5 | 72.8–1086 | 106.8–1160 | 64 | 0 | 64–66 | 0–61 / 64–66 | 57–65 (0–4) | 6.7–13.1 | 0–2 |
| 87x87 (catalog) | 1 | **12827–13839** | **12842–13862** | 87 | 80–83 | 84–87 | 84–87 / 84–87 | 76–80 (**64–66**) | 72.1–73.1 (6.8–7.2) | 2–12 |
| 87x87 (catalog) | 2–5 | 155–219 | 171–231 | 87 | 0–1 | 85–87 | 0–58 / 85–87 | 76–80 (0–3) | 7.0–16.2 | 0–2 |

PostgreSQL CPU is docker-stats `CPUPerc` (100 % = one CPU; the cap is 200 %). The
thread-pool baseline before round 1 was 3–5 threads at every point.

**Disk and PostgreSQL I/O, all catalog rounds:** zero checkpoints, zero slow statements
(`log_min_duration_statement=250`), zero lock waits, and no PostgreSQL errors. Per round,
the `pg_stat_io` deltas were 0–6 writes and 0–9 fsyncs, and docker BlockIO writes were at
most 10 kB. Host `PhysicalDisk(_Total)` average latency peaked at 6.9 ms per transfer
(one 87x64 round 1; every other catalog round ≤ 3.6 ms), with idle time ≥ 92.8 % in
every round but one 87x4 sample at 68.3 %. That sample fell in a round with zero
PostgreSQL writes and a 7 ms p50, so it is not attributable to the workload. Active CMS
backends showed wait-event types only `(none)` (on-CPU) and `Client`, in single digits of
backend-samples.

### Results — M-conn: round 1 at 64 and 87 (direct evidence from `log_connections`)

Round 1 at 87x64 and 87x87 has a distinctive shape, the same in all six runs:

- **Completion is simultaneous.** Every request completes inside the last ~0.3–0.9 s of
  the round (87x87 repetition 1: first/last completion at 12.56/12.85 s). p50 ≈ p99 in
  the table above is that shape.
- **Connections reach PostgreSQL early, and authentication completes only at the end.**
  PostgreSQL logs `connection received` from the CMS IP throughout the round (median
  arrival 1.3–3.9 s into it), but `connection authorized` for most of those backends
  lands in the final second:

  | Run | Round length (s) | Connections received | Received → authorized p50 / max (ms) |
  | --- | --- | --- | --- |
  | 87x64 rep 1 / 2 / 3 | 8.0 / 8.2 / 9.1 | 60 / 61 / 62 | 5812 / 5140 / 6355 (max 7040 / 5403 / 8799) |
  | 87x87 rep 1 / 2 / 3 | 12.8 / 13.9 / 12.9 | 84 / 84 / 81 | 8060 / 9113 / 8988 (max 12540 / 13539 / 12543) |

  Per-second timeline, 87x87 repetition 1: connection creates `2, 0×6, 5, 0×4, 76`
  (seconds 0–12); thread-pool threads 16 → 75 in steps of 4–10 per second; thread-pool
  queue 64 → 11; Npgsql busy connections 25 → 87; completions 0 until second 12, then 87.
  The PostgreSQL log shows `connection authenticated: … method=scram-sha-256`
  immediately before each `authorized`, so the time between `received` and
  `authenticated` is the SCRAM exchange. PostgreSQL is waiting for the client during that
  time: active backends ≤ 12 and mean PostgreSQL CPU about 7 %.
- **The same signature in E1's cold rounds (re-read from the retained 0.3 captures with
  the new analysis; no new runs).** P-runner-approx runs 1, 2, 4, 5: 86 connections
  received each, received → authorized p50 9.96–10.51 s (max 14.2–14.7 s),
  authorizations bunched at 14.3–15.1 s, thread-pool threads 3–5 → 80–83. Run 3, the
  run with the key-fetch timeouts and 401s: 206 connections were received, most of them
  after 15 s (received-offset p50 16.2 s), and the first authorizations came at 15.3 s,
  past the `Timeout=15` boundary. The other four runs released just inside it. P-dev runs 1–5: received → authorized p50 77–129 ms (max ≤ 183 ms), cold
  round 0.8–0.9 s, threads 3–4 → 52–67.
- **Reuse.** In rounds 2–5, CMS created 0–1 connections per round at every catalog point
  except 87x32 (0–3). Every request was served from the pool round 1 grew, and pool size
  stayed ≈ measured overlap (busy+idle max 4 / 8–9 / 14–16 / 30–33 / 64–66 / 85–87). In
  round 1 the physical creates track overlap minus the pre-burst pool:
  4–5 / 10–13 / 26–27 / 60–62 / 80–83 for N = 8 / 16 / 32 / 64 / 87.
  *Derived, not measured:* F3 counts four sequential pool acquisitions per authenticated
  profile request (three authentication reads plus the profile query), so round 1 at
  87x87 reuses about 1 − 83/(4 × 87) ≈ 76 % of acquisitions, and rounds 2–5 about 100 %.
  The four-per-request figure comes from code inspection; Npgsql exposes no acquisition
  counter.

### Results — stress-256x128 (additional stress, Q18; separately identified)

| Round | Statuses (3 repetitions summed) | p50 (ms) | p99 (ms) | CMS conns created / closed | Pool busy max | PG `53300 too many clients` | Thread-pool threads (queue) | PG CPU % max (mean) |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 200×746, 401×13, **500×9** | 1265–2125 | 21324–21975 | 338–379 / 298–305 | **100** (= `Max Pool Size`) | 6–7 per round | 102–105 (107–114) | **202–205** (20–23) |
| 2–5 | 200×3032, 401×31, **500×9** | 465–1670 | 1273–7360 | 164–341 / 194–344 | 85–100 | 6–9 per round | 102–129 (0–24) | 146–218 (34–131) |

At the stress point the pool reaches `Max Pool Size=100`, which equals PostgreSQL's
`max_connections=100`. Other clients (the sampler sessions, CMS's own `postgres`
database pool, DMS) push PostgreSQL over its limit, and it rejects new sessions with
`FATAL: sorry, too many clients already` **before authentication**. Rejected sessions
never log `connection authorized`, so they are not counted as creates. CMS connections
churn heavily: in each stress repetition's PostgreSQL log, 935–1,032 sessions to the
CMS database lasted under 1 s (warm-up and inter-round gaps included). **Why
those connections close is not established** (candidates are connectors broken by
timeouts and rejection-driven retries). PostgreSQL CPU saturates the 2-CPU cap in round 1.

**Manual correlation of all 62 non-200s** (response `correlationId` joined to the CMS
log `RequestId`; every one had matching log lines):

- **18 × HTTP 500, all on the profile path**: `RequestLoggingMiddleware` logged
  `HttpRequestFailed` with the stack running through `ProfileModule.GetById` →
  `ProfileRepository.GetProfile` → `NpgsqlConnection.OpenAsync` (F8's open outside the
  `try`).
  - 17 carry `PostgresException 53300: sorry, too many clients already`, a
    connection-slot exhaustion that the ticket explicitly did **not** observe
    (PostgreSQL logged no "too many connections" there).
  - **1 matches the ticket's CMS-side signature exactly:** `ProfileRepository.GetProfile`
    → `OpenAsync` → `PoolingDataSource.OpenNewConnector` → `NpgsqlConnector.Authenticate`
    → `AuthenticateSASL` → `NpgsqlException: Exception while reading from stream` →
    inner `TimeoutException: Timeout during reading attempt`, returned 500 after 19.8 s
    (repetition 3, round 1). It occurred in a round where 53300 rejections were also
    happening, so this run cannot separate the two conditions. It is recorded as an
    **R-500-matching observation at the stress workload, confounded by slot
    exhaustion**, for review. It does not by itself change G1, which the spec defines on
    E1's cold catalog runs.
- **44 × HTTP 401 on the valid burst token**, in two groups of 22:
  - The E1 path (F3/F5): key retrieval failed (53300, read timeout, or connect failure),
    `Failed to fetch public keys for JWKS` was logged, and the empty key list led to the
    `missing or unknown 'kid' … available keys:` rejection.
  - **The token-status path**, which 0.3 did not observe: `OpenIddictTokenManager` logged
    `Token validation failed` with the exception raised from
    `OpenIddictTokenRepository.GetTokenStatusAsync` →
    `OpenIddictDataRepository.GetTokenStatusAsync` (20 with 53300, 2 with
    `TimeoutException: Timeout during reading attempt`). This is today's behavior of
    turning a token-status store failure into 401, the path D-7 / §4.7 change to 503.

### Evidence vs inference

Directly measured in this step:

1. At T = 87 under P-runner-approx, the steady state is healthy at every overlap up to
   87 (all-200, p99 ≤ 1.9 s, no new connections, low PostgreSQL CPU).
2. The first concurrent burst on a warmed, small-pool process degrades sharply between
   N = 32 and N = 64. At 64 and 87 it produces the E1 cold-round shape (all requests
   complete together at 8–9 s and 13–14 s), with no timeouts and no non-200s.
3. In those rounds CMS opens about N−4 physical connections, PostgreSQL receives them
   early, the SCRAM exchange for most of them completes only in the round's final second
   while PostgreSQL is mostly idle, and the CMS thread pool grows from ~4 threads to about
   N at 4–10 threads per second with a queue of up to 66 items.
4. The E1 P-runner-approx cold rounds show the same handshake signature. The E1 ~15 s
   plateau therefore does not appear to be set by Npgsql's `Timeout=15`: the warm E2
   plateau at 87 was 12.8–13.9 s with no timeout, and only E1 run 3's handshakes crossed
   15 s.
5. No catalog round shows disk, checkpoint, slow-statement, or lock-wait activity.

Inferred, not established here:

- That the handshake stalls because the CMS side cannot schedule the continuations of
  its asynchronous opens until enough thread-pool threads exist (consistent with H1's
  sync-over-async resolver, F1). No stacks were captured in this step. Whether the waiting
  threads sit in `IssuerSigningKeyResolver` (F1), in another blocking frame, or in
  client-side SCRAM computation is exactly what the 0.5 stacks and the E4 thread-minimum
  control are designed to decide.
- That on the shared CI runner, where the same growth would take longer, the handshake
  crosses `Timeout=15` more often, producing the ticket's 500s. This is plausible from
  E1 run 3 and the stress observation, but it is not measured here.
- H2 (connection creation cost): physical creates do scale with overlap (point 3), but
  PostgreSQL-side SCRAM compute is not what the rounds wait on at T = 87 (idle
  PostgreSQL). The client-side share of SCRAM cost is unmeasured. At the stress point,
  connection churn does saturate PostgreSQL's CPU. Neither observation is a verdict.
- H3 (PostgreSQL/disk saturation): no supporting evidence at any catalog point. The
  stress-point CPU saturation coincides with connection churn, not with I/O.
- H5 (self-referential metadata): no discovery or JWKS self-request reached CMS in any
  timed round. The F6 self-fetch happens once at the first authenticated request, which
  in E2 falls inside the untimed warm-up (in E1 it was visible in the cold round).

### Limitations

- The concurrency-1 warm-up warms JIT for the serial paths only. Code reached only under
  concurrency (pool growth, contended waits) may still be cold in round 1, so round 1
  measures "pool growth plus first concurrency", not pool growth in isolation.
- Rounds 2–5 of the catalog points last 0.2–1.2 s, so the 1 s counters give 1–3 samples
  per round and instantaneous peaks (busy connections, active backends) can fall between
  samples. Creates and disconnections come from event logs and are exact, and pool size
  (busy+idle) is persistent state.
- Npgsql's "Busy Connections" counts connectors checked out of the pool, including ones
  still opening; it is not a count of connections executing SQL.
- The stress point is confounded by `Max Pool Size` = `max_connections` = 100. It says
  nothing about the ticket's environment beyond the one matching trace, and a follow-up
  with pool or `max_connections` headroom would be needed to separate the conditions.
- P-runner-approx approximates the CI runner and is not its envelope. Debug logging adds
  CMS work at every point (constant across E1/E2), and the host ran unrelated idle
  containers.
- Scope classification of CMS warnings/errors (request-scoped / background /
  unattributed) says where a line was emitted, not what caused a response. Only the
  per-request join above ties a status to a cause.

### Deviations

- The sweep driver recorded `samplerRequiredFailures: null` for all 21 points in
  `e2-p-runner-approx-index.json`: the same PowerShell empty-array unwrap as the E1
  driver bug. The authoritative per-burst summaries all record `requiredFailures: []`,
  and the console reported 0 failures per point. The driver is fixed; the sweep was not
  re-run for a derived field.
- The harness gained `-InterRoundDelaySeconds` (default 0, preserving E1 behavior) and
  per-round `startUtc`/`endUtc` in its summary. Round 1 of E1 cannot be sliced this finely
  from its own summary; the E1 re-read above derives the round-1 window from the round-1
  CSV's first admission and last completion.
- Raw captures (≈ 230 MB for the 21 points plus 21 dumps of ≈ 437 MB each) are retained
  locally under `eng/performance/dms-1556/artifacts/` for the Jira upload.

Review qualifications carried forward from the 0.4 approval: the E2 round-1 condition is
**pool growth plus first concurrent execution** (not pool growth in isolation), the
catalog captures show **little I/O** (not literally none), and low PostgreSQL CPU plus
delayed authorization do not by themselves prove where execution waited. Step 0.5
supplies the stacks that speak to the last point.

## 0.5 — E3/E4/E5 controls, stacks, headroom, E7 (2026-09-30)

### What 0.5 can and cannot show

Controls corroborate; stacks are the direct evidence (spec §3.5). Improvement under E4
supports H1, and no improvement would not exclude it. E3 removes database-backed key
retrieval **collectively** (both per-request key reads; the token-status read stays on
the database), so it cannot separate blocking from round-trip cost. E5 changes **two**
things at once: fewer physical connections and more waiting for a pooled connection
(bounded by the same `Timeout=15`). No verdicts are assigned here (step 0.6). **G1 stays
"partially reproduced (provisional)" and AC 1 remains unmet.**

### Design and commands

- Resource profile: P-runner-approx throughout (CPU caps and `DOTNET_PROCESSOR_COUNT=4`
  verified by each block before it ran). Host noise as in 0.4 (four unrelated idle
  containers).
- **Matched pairs.** Each control block ran immediately after its own baseline block,
  both in one sequence: `Invoke-Step05Sequence.ps1 -Repetitions 3`, which calls
  `Set-Dms1556StackCondition.ps1 -Condition <c>` and then
  `Invoke-ControlBatch.ps1 -Condition <c> -BlockLabel <pair>`. Every condition differs
  from `baseline` by exactly one overlay: `local-control-e3-certificates.yml`
  (certificate mode with the development certificate on the `/diag` volume),
  `local-control-e4-threads.yml` (`DOTNET_ThreadPool_ForceMinWorkerThreads=0x80`),
  `local-control-e5-pool16.yml` (`Maximum Pool Size=16` appended to the connection
  string), and `local-postgresql-headroom.yml` (`max_connections=200`). Each block
  verified the live container state against its label before running (certificate flag,
  thread knob, pool size, `max_connections`, CPU caps).
- **Consistent reset.** Every run starts from a CMS restart. `cold-87x87` is the E1
  shape (token minted, restart, burst). `warm-87x87` is the E2 shape (restart, serial
  warm-up of 87 requests at concurrency 1, then the **first concurrent burst after serial
  warm-up**). Both run 5 rounds, 3 s apart, with body validation and the sampler coverage
  gate, and alternate order per repetition. PostgreSQL was not restarted within the
  catalog pairs; the stress pair recreated PostgreSQL before **both** of its blocks.
- **Stacks.** dotnet-monitor `/stacks` was requested at 2, 8, and 14 s into round 1 of
  every run and classified by `Get-DmsStackSummary`. A capture takes about 2.4 s, and the
  snapshot happens at an unknown instant inside it, so each capture is recorded as the
  **interval** `[requested, completed]` (`Get-DmsStackCaptureRecord`). The record gives
  PostgreSQL connections received and authorized at **both** boundaries, plus the
  thread-pool thread and queue counts and Npgsql busy connections as ranges over the
  livemetrics samples inside the interval. One dump per run was taken **after** the
  timed rounds (Q17).
- **Headroom (separately labeled).** The slot-exhausted stress workload
  (`warm-256x128`) ran as its own pair: baseline versus headroom, where the **only**
  change is `max_connections` 100 → 200.
- Coverage: every completed burst passed the coverage gate. `Worker Min Limit` from the
  dumps: **4** in every baseline, E3, E5, and headroom run; **128 in every E4 run**
  (E4's effective minimum verified, not assumed).

### Results — round 1 (the stall) by condition

Ranges are over 3 runs per cell. Every catalog request in every condition returned
**HTTP 200 with a validated body**.

| Pair / condition | Workload | Round-1 p50 (ms) | CMS connections created | Received → authorized p50 (ms) | Thread-pool threads max (queue max) | Pool busy max | PG CPU % max (mean) |
| --- | --- | --- | --- | --- | --- | --- | --- |
| e3 / baseline | cold | 16429–16685 | 189–229¹ | 103–188¹ | 87 (14) | 86–87 | 155–184 (12–15) |
| e3 / baseline | warm-first | 13820–14391 | 82–85 | 9053–10111 | 80–82 (64–68) | 85–88 | 74–79 (6–7) |
| **e3 / certificates** | cold | **635–797** | 5–7 | 35–68 | 13–15 (3–6) | 1–8 | 8–14 (5–6) |
| **e3 / certificates** | warm-first | **880–935** | 69–77 | 341–465 | 4–10 (41–58) | 25–37 | 57–65 (25–29) |
| e4 / baseline | cold | 14582–15118 | 84–85 | 9580–10334 | 82–84 (11–15) | 84–87 | 47–77 (5–6) |
| e4 / baseline | warm-first | 13553–14133 | 82–84 | 8872–9891 | 79–81 (66–68) | 83–84 | 47–72 (7) |
| **e4 / threads (min 128)** | cold | **849–1095** | 53–76 | 7–15 | 104–108 (0–2) | 26–59 | 63–86 (26–30) |
| **e4 / threads (min 128)** | warm-first | **168–244** | 7–11 | 7–11 | 34–42 (0) | 0–1 | 18–22 (10–12) |
| e5 / baseline | cold | 14574–14826 | 83–84 | 9894–10302 | 82–83 (11) | 84–87 | 69–74 (6) |
| e5 / baseline | warm-first | 13826–14572 | 84 | 9344–10008 | 80–83 (67–74) | 84–87 | 43–75 (6–7) |
| **e5 / pool 16** | cold | **12656–15849** | 12–37 | 15–14551² | 75–87 (12–20) | 11–16 | 15–49 (3–4) |
| **e5 / pool 16** | warm-first | **12232–14232** | 12–13 | 38–14052² | 74–82 (65–66) | 16 | 13–20 (2–3) |

¹ The three pair-e3 cold baseline runs crossed the 15 s `Timeout`. The first
authorization came at 15.6 s in each, 203–244 connections were received for 87 requests
(most after 15 s, received-offset median 15.6–16.1 s), and the 103–188 ms median
describes those late connections. Those runs also logged 14–15 request-scoped `Failed to fetch public keys
for JWKS` (`NpgsqlException`) lines each while **every response was 200**; one E5 cold
run shows the same (14 lines, all 200). F3 gives each request two key reads, and when
the verification read fails the request is 401 (0.3). By elimination, these failures
were the **resolver's** read, and signature validation still succeeded, presumably on
the metadata keys F6 merges in. That last step is inferred, not observed.
² E5's handshake timing is bimodal: three runs at 15–38 ms and three at 12–15 s. With
only 12–13 physical connections, whether those few handshakes stalled varied by run,
but **the round-1 stall occurred in all six**.

Rounds 2–5 (steady state) were healthy in every condition, all 200. p50 ranges:
baselines 107–231 ms (one e5-baseline round at 1681 ms), E4 159–305 ms, E5 106–242 ms,
and E3 574–1134 ms. E3's steady state is slower because certificate mode loads the
X.509 file on every key read (F10): rounds 2–5 CMS CPU peaked at 78–202 % (median
139 %) versus 28–124 % (median 48 %) in the baselines. That is a property of the
control, not of the stall.

**Disk and PostgreSQL I/O (all catalog blocks):** host disk latency peaked at 7.8 ms;
per round, `pg_stat_io` writes were 0–7, slow statements 0, checkpoints 0–1, and there
were no PostgreSQL errors. Little I/O throughout.

### Results — managed stacks during the stall

Same pairs, round 1. Each capture is the interval `[requested, completed]` in seconds
from the round start; the snapshot lies somewhere inside it. "Resolver wait" is a
synchronous `Task.InternalWait` under `JsonWebTokenHandler.ValidateSignature`, which is
IdentityModel's synchronous `IssuerSigningKeyResolver` call (F1). "Parked" requires the
parked-worker signature (only `LowLevelLifoSemaphore` waits above `WorkerThreadStart`).
Every capture also shows 7 dedicated runtime threads (Main, Kestrel heartbeat, socket
event loop, timer, gate, counters, file watcher), omitted below. Counter ranges come
from the 2–3 livemetrics samples inside each interval.

| Condition (workloads) | Capture interval (s) | TP workers | Resolver wait | Parked | Active (Npgsql / other) | Received: request → completion | Authorized: request → completion | Threads / queue across interval |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| baseline, 3 pairs (cold) | 2.0 → 4.3–4.5 | 40–41 | **40–41** | 0 | 0 / 0 | 25–32 → 38–45 | **0–3 → 0–3** | 31–42 / 2–7 |
| baseline, 3 pairs (cold) | 8.0 → 10.4–10.5 | 65 | **65** | 0 | 0 / 0 | 53–60 → 62–70 | **0–3 → 0–3** | 58–66 / 9–14 |
| baseline, 3 pairs (warm-first) | 2.0 → 4.4–4.5 | 41 | **41** | 0 | 0 / 0 | 30–34 → 42–46 | **0–5 → 0–5** | 31–43 / 39–53 |
| baseline, 3 pairs (warm-first) | 8.0 → 10.4–10.5 | 65–66 | **65–66** | 0 | 0–1 / 0–1 | 57–62 → 67–73 | **0–5 → 0–5** | 58–67 / 21–32 |
| baseline, 3 pairs (cold) | 14.0 → 16.3–16.5 | 82–87 | 0–7 | 80–84 | 0–2 / 0–1 | 76–84 → 86–220 | 0–3 → **83–191** | 82–87 / 0–14 |
| baseline, 3 pairs (warm-first) | 14.0 → 16.4–16.5 | 79–83 | 0 | 79–83 | 0 / 0 | 82–86 → 82–86 | 0–85 → 82–85 | 79–83 / 0 |
| e5 / pool 16 (both) | 2.0 → 4.4–4.5 | 40–41 | **40–41** | 0 | 0 / 0 | 12–13 → 12–13 | 0–8 → 0–8 | 31–43 / 3–54 |
| e5 / pool 16 (both) | 8.0 → 10.4–10.5 | 64–65 | **64–65** | 0 | 0 / 0 | 12–13 → 12–13 | 0–8 → 0–8 | 58–67 / 10–32 |
| stress baseline + headroom | 2.0 → 4.5 | 41 | **41** | 0 | 0 / 0 | 27–36 → 39–51 | 0–8 → 0–8 | 33–43 / 76–96 |
| stress baseline + headroom | 8.0 → 10.4–10.5 | 65–66 | **65–66** | 0 | 0 / 0–1 | 54–66 → 64–77 | 0–8 → 0–8 | 59–67 / 56–76 |
| stress baseline + headroom | 14.0 → 16.4–16.5 | 89–90 | **89–90** | 0 | 0 / 0 | 78–91 → 90–109 | 0–8 → 0–8 | 83–91 / 36–71 |
| e3 (both) / e4 (both) | all three | 4–15 / 34–108 | 0 | all | 0 / 0 | unchanged | unchanged | round 1 ended before each capture |

- **The 2 s and 8 s captures lie entirely inside the unauthorized phase.** Authorizations
  are the same at both boundaries of every baseline, E5, and stress capture (at most 8,
  and at most 5 in the catalog baselines), and the round ends after the capture. Within
  those intervals every thread-pool worker is in the signing-key resolver's synchronous
  wait, except that one warm-first 8 s capture has one worker in Npgsql code and another
  has one worker initializing its dispatch state. No worker is parked or in SCRAM
  computation. The pool grows about 4 threads per second (31–33 at 2 s, 58–59 at 8 s),
  and each new worker joins the resolver wait. Meanwhile PostgreSQL has received 25–73
  CMS connections, and the pending opens' continuations stay queued (queue up to 53 in
  the warm-first workload) with no free worker to run them.
- **The baseline catalog 14 s captures span the release.** In the cold runs,
  authorizations rise from 0–3 at the request to 83–191 at completion, and in the
  warm-first runs round 1 ended before or during the capture. Their parked workers
  therefore cannot be placed before or after authorization. **The earlier claim that
  workers were free while authorizations stayed at zero, and the "post-release login
  lag" drawn from it, are withdrawn:** they came from aligning each capture to its
  request instant.
- The stress 14 s captures still show every worker in the resolver wait with at most
  8 authorizations at either boundary: at 256/128 the stall outlasts the 14 s capture.
- The explicit CMS resolver frame (`WebApplicationBuilderExtensions…
  <ConfigureIdentityProvider>b__8`) is visible on every waiting thread in cold runs, but
  on only 21–27 threads in warm-first runs. There the wait appears directly under
  `ValidateSignature`; a JIT-inlined lambda after tier-up (the warm-up executed it) is
  the likely reason, not established. Both shapes block in the same synchronous wait
  under `ValidateSignature`.
- E5 shows the same all-workers-in-resolver-wait picture with only 12–13 physical
  connections and all 16 pool slots busy.

### Results — stress pair (slot exhaustion versus headroom)

| Condition | Runs | Round-1 statuses | Rounds 2–5 statuses | PG `53300` per round | Rounds 2–5 CMS creates |
| --- | --- | --- | --- | --- | --- |
| baseline (`max_connections=100`) | 3 | 200×737, 401×26, **500×5** | 200×3036, 401×22, **500×14** | 5–8 | 200–271 |
| **headroom** (`max_connections=200`) | 3³ | 200×754, 401×11, **500×3** | **200×3072** | **0** | 0 |

³ Headroom repetition 1 failed before any timed round: the warm-up's token request hit
a CMS connection that the PostgreSQL recreate had just terminated
(`57P01: terminating connection due to administrator command`). This was a sequencing
bug in the condition script, fixed by restarting CMS after every PostgreSQL recreate. One
replacement repetition ran under the fixed script (block `pair-headroom-rerun`), so
headroom has repetitions 2, 3, and the replacement.

Every non-200 in both blocks was joined by `correlationId`/`RequestId` to its CMS log
lines (none uncorrelated):

- **Headroom, 3 × 500:** all three are `ProfileRepository.GetProfile` → `OpenAsync` →
  `AuthenticateSASL` → `NpgsqlException: Exception while reading from stream` → inner
  `TimeoutException: Timeout during reading attempt`, the ticket's CMS-side signature,
  here **with no slot exhaustion anywhere in the block**. The 11 × 401 are key reads (5)
  and token-status reads (6) failing to open a connection within the timeout (SASL read
  timeouts, a stream read timeout, and `Failed to connect … TimeoutException`).
- **Baseline stress, 19 × 500:** round 1 has 3 × `AuthenticateSASL` timeout, 1 × stream
  read timeout during open, and 1 × 53300; rounds 2–5 have 14 × 53300. The 48 × 401 split
  between 53300 and open timeouts on the key and token-status reads.
- Headroom therefore separates the two conditions the 0.4 stress trace confounded.
  Slot exhaustion (53300) accounts for the steady-state failures and disappears with
  headroom. The round-1 open timeouts, including the profile-path SASL-timeout 500s,
  persist without it; they occur in the first burst, while the stacks show every worker
  in the resolver wait. This is an **R-500-matching observation at the stress workload
  (256/128)**. G1 is defined on the E1 catalog cold runs and stays "partially
  reproduced (provisional)"; these headroom timeout reproductions are preserved for the
  explicit G1/G2 assessment in 0.6.

### Results — E7 (real workload, shard 2)

- **E7a, as specified:** `./build-dms.ps1 E2ETest -Configuration Release
  -SkipDockerBuild -IdentityProvider self-contained -EnvironmentFile './.env.e2e'
  -TestFilter 'Category=@e2e-ci-shard-2'`. **220 passed, 0 failed, 2 skipped (19 m 54 s)**,
  matching the known local baseline. `build-dms.ps1` tears the stack down and recomposes
  it from its own compose set, so this run had **no** diagnostics or resource overlay: it
  ran uncapped (P-dev-like) with Information-level CMS logs. From the saved CMS log, the
  real workload issued **one** catalog burst about 9.5 min into the run: 87
  `GET /v3/profiles/{id}` (85 completing in one second), all 200, with a maximum
  server-side elapsed time of **404 ms**.
- **E7b, overlay variant:** on the stack E7a provisioned, CMS and PostgreSQL were
  recomposed to P-runner-approx with both diagnostics overlays
  (`Set-Dms1556StackCondition.ps1 -Condition baseline -RecreateDb -ClaimsMountSource
  eng/docker-compose/.e2e-claims`). Beforehand the config hash was matched to confirm
  that only the overlays and the claims mount differ. DMS was restarted for the
  recreated PostgreSQL. Shard 2 then ran through `Invoke-E7Shard.ps1` with the same
  test-process context `build-dms.ps1` builds, `--no-build`, and samplers over the whole
  run (coverage passed). **220 passed, 0 failed, 2 skipped (20 m 2 s).** The single
  catalog burst came about 10 min in: 87 GETs, all 200, **maximum server-side elapsed
  13.3 s**. 91 CMS connections were created, handshakes had a median of 6.5 s (72 over
  1 s, 56 over 5 s), thread-pool threads grew 4 → 78 with a queue of up to 61, and pool
  busy peaked at 86. There were no connection errors; the PostgreSQL `ERROR` lines in the
  window are E2E test-data unique/foreign-key violations.
- E7 limitations: E7b cannot run through `build-dms.ps1` (reason above). It reused E7a's
  data without a reset, so the suite's profile creation met 87 existing names ("Profile
  name must be unique" warnings; the tests still passed). No stacks were captured in E7
  because the burst time is not known in advance. There was one run of each. The local
  host is not the CI runner, so the CI envelope still needs CI after push.

### Evidence vs inference

Directly measured in this step:

1. In every baseline stall (cold and first concurrent burst), the stack captures that
   lie entirely inside the unauthorized phase (2.0–4.5 s and 8.0–10.5 s) show every
   thread-pool worker, all but one in two captures, blocked in a synchronous wait under
   IdentityModel's signing-key resolver call. The pool grows about 4 workers per second
   with queued work, and the CMS connections PostgreSQL has received stay unauthorized
   until the round releases. The 14 s captures span the release and say nothing about
   the order of worker release and authorization.
2. **E4** (worker minimum 128, verified from dumps) removes the stall: round 1 drops
   from 13.6–15.1 s to 0.17–1.1 s, handshakes take 7–15 ms, all 200.
3. **E3** (certificate-mode keys) removes the stall: round 1 drops to 0.6–0.9 s.
4. **E5** (pool 16) does not: round 1 stays at 12.2–15.8 s with 12–13 physical
   connections, and the stacks still show every worker in the resolver wait.
5. At the stress workload, PostgreSQL headroom removes all steady-state failures
   (53300) but not the first-burst open timeouts, including 3 profile-path
   `AuthenticateSASL` timeout 500s.
6. Observationally, the real workload (shard 2) contains one catalog burst of 87
   profile requests. It completed in 0.4 s uncapped (E7a) and took up to 13.3 s under
   P-runner-approx (E7b), with the same handshake and thread-pool signature. E7b ran on
   reused data and without stacks, so it corroborates the shape and establishes nothing
   by itself.

Bearing on the hypotheses (the attribution table and verdicts are step 0.6):

- **H1:** direct evidence (point 1), corroborated by E4 (point 2). E3 is consistent but
  cannot isolate blocking from round-trip cost. Blocking elsewhere is not indicated: no
  other frame holds workers during the stall.
- **H2:** bounding connections to 16 (E5) leaves the stall in place, so high connection
  counts are **not necessary** for the stall at the catalog workload. That does not
  eliminate every connection-related contribution: E5 also adds pool waiting, and its
  few handshakes still stalled in half the runs. At the stress workload, connection churn
  saturates PostgreSQL's CPU, but only in the slot-exhausted regime. No SCRAM computation
  appeared in any capture inside the unauthorized phase. *(Corrected in 0.6: an earlier
  wording said "on any captured stack". One worker was in SCRAM HMAC computation in one
  release-spanning 14 s capture, pair-e3 baseline cold repetition 3, while authorizations
  rose from 0 to 189.)*
- **H3:** little I/O, no checkpoints or slow statements in any catalog block.
- **H4:** profile-dependent in both the harness (E1) and the real workload
  (E7a 0.4 s versus E7b 13.3 s).
- **H5:** not exercised by any control. No self-request appeared in any timed round
  (0.4); nothing here adds evidence.

### Remaining uncertainties

- The order of worker release and connection authorization at the end of a stall is
  not resolved: the only captures near the release (14 s) span it. A capture closer to
  the release, or finer-grained counters, would be needed. Also open: why the pair-e3
  cold runs crossed 15 s while the other cold baselines released at 14.6–15.1 s.
- A stack capture is an interval of about 2.4 s whose snapshot instant is unknown.
  Statements about a capture hold only where the aligned quantities are constant
  across the interval (true of the 2 s and 8 s baseline captures, not of the catalog
  14 s captures).
- E5's bimodal handshake timing with few connections.
- Whether `/stacks` perturbs the stall. Each capture takes about 2.4 s; warm-first
  round 1 was 13.6–14.4 s with captures here versus 12.8–13.9 s without them in 0.4. The
  controls' effects (13–15 s → under 1.1 s) far exceed that difference.
- Whether the inlined-resolver-frame explanation for warm-first stacks is right. It
  does not change the classification.
- The CI envelope: P-runner-approx approximates the runner, and CI after push remains
  the envelope evidence (AC 2).

### Deviations

- E7 ran in two parts (E7a as specified without overlays, E7b with overlays through a
  direct shard run), because `build-dms.ps1 E2ETest` recomposes the stack and cannot carry
  the overlays.
- Headroom repetition 1 is excluded (setup failure before any timed round) and replaced;
  `Set-Dms1556StackCondition.ps1` was fixed to restart CMS after every PostgreSQL
  recreate, and to always start the monitor sidecar and optionally keep the claims mount.
- The `build-dms.ps1 Build -Configuration Release` run needed for E7 failed only in
  `EdFi.DataManagementService.Tests.Integration` (the known NU1008 failure for worktrees
  nested under the main checkout); the E2E assembly built. The build also rewrote
  `src/config/frontend/…/packages.lock.json`, which was reverted.
- E7a's teardown (`down -v`) removed the stack volumes, including the 87 DMS1556 seeded
  profiles. The stack is left at `baseline` under P-runner-approx on E2E data; a later
  harness run needs `-Seed` again.
- Raw captures (control blocks, stacks, dumps, E7 logs and trx) are retained locally
  under `eng/performance/dms-1556/artifacts/` for the Jira upload.

### Review corrections (after `5095625a3`; analysis only, no workload rerun)

Two analysis defects were found in review and corrected. The stack tables were
regenerated from the retained captures with `Invoke-ControlBatchStackReanalysis.ps1`
(126 capture records, the same set as before); the superseded tables are kept beside
them as `e5-*-stacks.superseded.csv`.

1. **Capture alignment.** Stack contents had been joined to connection counts and
   counters at the capture's *request* instant, although retrieval takes about 2.4 s.
   Each capture is now the interval `[requested, completed]`, with connection counts at
   both boundaries and counter ranges across it (`Get-DmsStackCaptureRecord`,
   `Get-DmsLivemetricsIntervalSummary`); the point lookup was removed. Validation case:
   pair-e3 baseline cold repetition 1, 14 s capture, spans 14.01–16.46 s, and
   authorizations rise from **0 to 191** inside it (received 77 → 220). Its 84 parked
   workers therefore do not show free workers while authorizations were zero, and that
   claim is withdrawn above. The same run's 2 s (2.01–4.40 s) and 8 s (8.01–10.48 s)
   captures keep 0 authorizations at both boundaries.
2. **Idle classification.** Any `PortableThreadPool+WorkerThread` frame had been
   classified as idle, which includes ordinary worker dispatch. `threadpool-parked` now
   requires the parked-worker signature: only `LowLevelLifoSemaphore` waits above
   `WorkerThreadStart`. The 126 block records contain **3,412** such threads. *(Corrected
   in 0.6: this sentence had reported 3,651, a total that also counted nine tooling
   shake-down captures outside the evidence corpus; see 0.6 bookkeeping.)*
   Dedicated runtime threads are `runtime-infrastructure`, and other execution is
   `active-other`. Per-thread validation against retained examples, all passing:
   socket-completion dispatch without Npgsql frames → active-other; socket completion
   running an Npgsql continuation → npgsql-active; `JobDatabaseSession.ReleaseAfterAsync`
   → active-other; worker initialization (`ThreadPoolWorkQueue.CreateThreadLocals`) →
   active-other; the gate thread creating workers → runtime-infrastructure; a parked
   worker → threadpool-parked; a resolver wait → resolver-wait. Four of the 126
   captures change, one thread each (three executing workers and the gate thread, all
   previously counted as idle); no resolver-wait count changes.

## 0.6 — Attribution, gate records, and implementation challenge (2026-09-30)

### Scope

This step analyzes retained evidence only. No workload ran, and no production code
changed. Evidence is cited by the IDs in the tooling README's **Evidence index**
(`eng/performance/dms-1556/README.md`). Every verdict and gate decision below is a
**proposal for the G2 review**, not a decision. Two wording errors found in the 0.5 text
are corrected in place and listed under Bookkeeping.

### The mechanism, link by link

Each link is marked **M** (measured directly) or **I** (inferred).

1. **M.** In every baseline stall, the captures that lie wholly inside the unauthorized
   phase (2 s and 8 s) show the thread-pool workers in a synchronous `Task` wait under
   `JsonWebTokenHandler.ValidateSignature`, which is IdentityModel calling CMS's
   `IssuerSigningKeyResolver` (F1). Across the 36 baseline catalog captures at 2 s and
   8 s, **1,904 of 1,906 workers** are in that wait; the other two are one worker in
   Npgsql code and one initializing. The same holds in all E5 and stress captures. No
   thread in any of the 126 records is in any other synchronous wait (EV-STK).
2. **M.** The pool grows about 4 workers per second and every new worker joins the
   wait. The thread-pool queue stays non-empty across those captures (2–54 items in the
   catalog workloads, 56–96 at stress) (EV-STK, livemetrics ranges).
3. **M.** Meanwhile PostgreSQL has received the CMS connections, but authorizations stay
   flat across both capture boundaries (at most 5 in the catalog baselines). PostgreSQL
   is mostly idle (mean CPU 5–7 % in every catalog round that released before
   `Timeout=15`) and waits for the client in the SCRAM exchange (EV-E2 M-conn,
   EV-E3/E4/E5).
4. **I.** The continuations of the pending opens, the client's steps of the SCRAM
   exchange, are queued work that cannot run while every worker is blocked. The round
   releases once the pool approaches about N workers (76–87 at N = 87, 57–61 at N = 64).
   The queue's contents were not captured, so the queued items are not observed to be
   these continuations. E4 corroborates this link: with a worker minimum of 128, round 1
   takes 0.17–1.1 s and handshakes take 7–15 ms.
5. **M.** Failures cluster where the stall crosses Npgsql's `Timeout=15`, and nowhere
   else:
   - E1 run 3: 12 key-fetch timeouts and 3 × 401 (EV-E1-RA).
   - The pair-e3 cold baselines: 14–15 resolver-read failures per run, all responses
     200 (EV-E3).
   - Stress round 1: profile-path open-timeout 500s, including the unconfounded headroom
     500s (EV-HR).

   The same blocking code serves the headroom steady state (rounds 2–5) with **zero**
   non-200s (200 × 3,072).
6. **M.** The stall also occurs in the warm-first workload (serial warm-up, then the
   first concurrent burst). By then the F6 metadata self-fetch has completed and the
   serial paths are JIT-warm. So cold key loading, the metadata fetch, and process start
   are not necessary for the stall. This matters because the real workload's catalog
   burst has the warm-first shape: it arrives about 10 minutes into shard 2 on a CMS that
   has already issued tokens (EV-E7A, EV-E7B).
7. **I.** On the CI runner, the ticket's 500s are this same chain crossing `Timeout=15` on
   the profile path at the catalog workload. Locally, the catalog workload never produced
   a 500. CI after push is the evidence for this link.

### Attribution table (proposed verdicts)

| Hypothesis | Direct evidence | Corroborating controls | Proposed verdict | Limitations |
| --- | --- | --- | --- | --- |
| **H1** thread-pool starvation from the blocking resolver | Links 1–3 and 5 (EV-STK, EV-E2, EV-E3/E4/E5, EV-HR, EV-E1-RA) | **E4** removes the stall (round 1 13.6–15.1 s → 0.17–1.1 s; `Worker Min Limit` 128 verified, EV-DUMP). **E3** removes it (0.6–0.9 s) but cannot separate blocking from round-trip cost. **E5** leaves it in place, as H1 predicts: blocking does not depend on connection count. | **Supported** | The snapshot instant inside each ~2.4 s capture is unknown. The 14 s captures span the release, so the release order is unresolved. Link 4 is inferred (queue contents not captured). `/stacks` may perturb the stall by about 1 s, against control effects of 13 s or more. P-runner-approx is not the runner. No control is the fix, so the fix's effect is predicted here and measured only in Phase 4. |
| **H2** connection-creation and SCRAM cost | Creates track overlap (80–83 at N = 87). Received → authorized takes 5–10 s while PostgreSQL is idle. No SCRAM computation in any capture inside the unauthorized phase; one worker in one release-spanning capture. At stress, `Max Pool Size` 100 = `max_connections` 100 → 53300 rejections, and churn saturates PostgreSQL CPU. | **E5**: with 12–13 physical connections the stall persists (12.2–15.8 s). **Headroom** removes the steady-state 53300s. E3 changes key retrieval as a whole and cannot isolate H2. | **Modifier**, narrowly: high connection counts are **not necessary** for the stall (E5). Slot exhaustion is a separate stress-workload failure (53300) that headroom removes. Not refuted. | E5 changes two things at once (fewer connections, more pool waiting), and its handshake timing is bimodal. The client-side SCRAM cost is known only from stacks. Why stress connections churn is not established. |
| **H3** PostgreSQL / disk saturation | 0–1 checkpoints, no slow statements, no lock waits, `pg_stat_io` 0–7 writes per round, host disk latency ≤ 7.8 ms, active backends only on-CPU or waiting on `Client` | E4 removes the stall; H3 predicts no effect | **Refuted** for the measured catalog workloads on this host (scope clarified at G2) | The host disk (NVMe) is not the runner's, and the runner's disk is unmeasured. The stress CPU saturation coincides with connection churn, not I/O. |
| **H4** runner contention | P-dev 0/5 stalls (cold p99 802–922 ms) versus P-runner-approx 5/5 (~15 s) (EV-E1-DEV/RA). E7a max 0.4 s versus E7b 13.3 s. | The profile pair itself | **Modifier**: resources decide whether the stall appears and how long it lasts. This fits H1: the release needs about N workers, and the pool starts from the worker minimum (16 on P-dev, 4 on P-runner-approx). | Each profile changes two knobs together (CPU cap and `DOTNET_PROCESSOR_COUNT`). The runner envelope is CI's to show. |
| **H5** self-referential metadata amplification | No discovery or JWKS self-request in any timed E2+ round. The F6 self-fetch happens once per process. All `Failed to fetch` lines carry `/v3/profiles` request paths. The stall persists in warm-first, after the self-fetch (link 6). | E0(c): supplying a manager removes the backchannel (a design fact, not a control) | **Not necessary for the stall** (warm-first), and no amplification was observed in the measured workloads. This does not universally disprove amplification (scope clarified at G2). | No control exercised it. F6 has an unmeasured side effect: merged metadata keys may have let pair-e3 cold requests validate after their resolver reads failed (0.5 footnote 1, inferred). That bears on I-5, not on the stall. |

### G1 record

**G1 as defined (E1 cold catalog runs) is unchanged: partially reproduced
(provisional).** It is not relabeled. The stress R-500 evidence is recorded beside it as
its own record:

| Source | Condition | Runs with a round-1 profile-path 500 | Profile-path 500s | Confounder |
| --- | --- | --- | --- | --- |
| 0.4 E2 stress-256x128 (EV-E2) | `max_connections=100` | 3 of 3 (2, 3, and 4 round-1 500s). Only repetition 3's includes the exact ticket signature. | 18 in all rounds: 17 × 53300, 1 × `AuthenticateSASL` timeout | Slot exhaustion in the same round |
| 0.5 stress baseline (EV-HR) | `max_connections=100` | 3 of 3 | Round 1: 5 (3 × SASL timeout, 1 × stream-read timeout during open, 1 × 53300). Rounds 2–5: 14 × 53300 | Slot exhaustion in the same block |
| **0.5 headroom (EV-HR)** | `max_connections=200` | **2 of 3** (repetition 2: 1; replacement: 2; repetition 3: 0) | **3, all `ProfileRepository.GetProfile` → `OpenAsync` → `AuthenticateSASL` → `TimeoutException`**, completing at 19.5–23.5 s | **None**: no 53300 anywhere in the block, and rounds 2–5 were 200 × 3,072 |

In the headroom runs, the 2 s, 8 s, and 14 s stress captures show every worker in the
resolver wait, with at most 8 authorizations through 16.5 s. The 500s complete 3–7 s
after the last capture, so the failure instant itself was not captured. Their durations
exceed `Timeout=15`, consistent with a profile open that started 4–8 s into the round
and then starved (**inferred**). The headroom 500s meet the §3.2 R-500 text (HTTP 500 on
`GET /v3/profiles/{id}` with `NpgsqlException`/`TimeoutException` from `OpenAsync` in the
profile path). They do not meet G1's scope, which is the five cold E1 catalog runs.
These are warm-first runs at the stress workload.

**Proposed gate clarification (P-G1, requires approval).** Add to §3.6:

> **G1-S (stress record).** R-500 at `stress-256x128` under a condition without slot
> exhaustion, in a round whose stacks show the stall's signature. G1-S never changes the
> G1 catalog record. When G1 is *partially reproduced (provisional)* and G1-S is
> *reproduced*, the mechanism counts as established for G2 and AC 1 on a **combined
> basis**:
>
> - the catalog workload reproduces the stall (R-slow);
> - the stress workload reproduces its 500 outcome;
> - the same stack signature is present in both.
>
> The catalog-workload 500 on the CI runner stays recorded as inferred, with CI after
> push as its evidence.

Why the combined findings justify proceeding:

- The stress 500s have the ticket's exact exception and code path.
- They occur only in the stalled round, with the catalog stall's stack signature.
- They disappear from the steady state even though the blocking code is still present.
- At the catalog workload, the controls that remove the stall (E4, E3) are already
  measured.

What the combination does **not** show: no control ran at the stress workload, so
"removing the stall removes the stress 500s" is predicted, not measured. If the reviewer
wants that corroboration before G2, one E4 stress pair (baseline versus E4, both under
headroom) would supply it. It is not run here and not requested by this step.

**Proposed G1 record:** *partially reproduced (provisional)* for the catalog workload
(unchanged), plus **G1-S reproduced** (2 of 3 headroom runs). The combined basis is
proposed for AC 1.

### Challenging the proposed implementation

Class codes: **M** — the measured mechanism requires it. **A** — an AC requires it.
**C** — it prevents a regression that another change would introduce. **P** — a policy
choice (value or trade-off). **O** — optional; kept only by an earlier decision.

| # | Change (spec ref) | Class | Why it is necessary | If omitted | Preserves |
| --- | --- | --- | --- | --- | --- |
| 1 | Remove the blocking resolver; supply keys through a plain `IConfigurationManager` (D-2) | M; AC 2, AC 3 | The stall *is* the synchronous resolver wait (link 1). IdentityModel 8.12 has no asynchronous resolver, and the configuration manager is JwtBearer's only asynchronous key hook, awaited with `RequestAborted` (V-2, E0(d)1). | Any resolver that can wait on I/O keeps the stall. | I-1 (validation parameters unchanged), I-5 (no backchannel, E0(c)) |
| 2 | Shared key state in one singleton (D-1) | M, C | The request path must read keys from memory. F7 creates three `OpenIddictTokenManager` instances, which would otherwise load and hold keys independently. | Three independent loads and possibly different views of the key set. | I-8 |
| 3 | Single-flight load, startup load, deadline-driven refresh (D-5, §4.4) | M; AC 3 ("asynchronous refresh/coalescing") | The reproduced shape is a burst: 87 concurrent requests must share one *awaited* load, and the startup load means the first burst normally finds a snapshot already published. | Without coalescing, a cold burst issues one load per request. Without the scheduler, rotation and retirement propagate only through request traffic. | `T_prop`, `T_start` bounds |
| 4 | Boundary readiness in `OnMessageReceived`, `Fail(exception)` at all three events, 503 + `Retry-After` (D-3, D-9) | A (AC 4); C | E0(a)1: a throwing manager escapes as a 500, so without translation the fix would replace today's 500s with different 500s. E0(b): a boundary failure makes no manager call. | M2b, i.e. 500s | Token, introspection, and revocation contracts (§4.7); I-7 |
| 5 | Failure classes; a failed load is never published as empty (D-4) | A (AC 4), measured | E1 run 3: a swallowed timeout became an empty key set and a 401 on a valid token ("available keys:" empty). F5: JWKS answers `200 []` on failure. | Misreported invalid tokens under load | I-8 |
| 6 | `VerifyTokenAsync` reads the snapshot (§4.3.9) | M, A | This second per-request key read is the one whose failures produced the E1 run-3 and stress 401s. It also cuts authentication database acquisitions from three to one. | 401s under load persist even with the resolver fixed. | Signature and `kid` checks (I-9) |
| 7 | Token-status check stays per request and uncached; store failures become a typed exception and 503 (D-7) | A (AC 3 revocation, AC 4) | Stress token-status failures were reported as 401 invalid-token (22 in 0.4, 6 in headroom). The read is already asynchronous and never appeared in a blocked frame, so it needs no structural change. | Caching it would trade revocation for availability, which AC 3 forbids. | **I-2 uncached status**, I-3 |
| 8 | Gated unknown-`kid` refresh with `Cooldown` (D-6, Q14) | A (AC 3 rotation, unknown key); P (cooldown value) | Today every request re-reads keys, so a newly inserted key is accepted on its first request. The snapshot must keep prompt first-sighting acceptance, bounded against floods of arbitrary `kid`s (I-6). | New-key tokens get 401 on every instance until `T_prop` (about 5 min): a rotation regression. | I-6, I-9; rotation bounds §4.5 |
| 9 | `MaxStaleness` fail-closed; no last-known-good or metadata fallback (Q13, I-5, V-3) | A (AC 3, "not indefinitely trusting retired keys") | It bounds trust after the last successful retrieval. Today's metadata merge is an unbounded side channel for key trust (inferred from the pair-e3 all-200 runs). | Retired keys trusted without bound during a key-store outage | **`T_max`, retirement bound** |
| 10 | Retry gate with backoff (D-5) | P; AC 5 (interruption and recovery) | It limits repository traffic during an outage to one attempt per backoff interval, whatever the request rate, and it recovers with no requests. | Request-rate retries against a failing store | I-6 |
| 11 | Cancellation overload on the repository, both engines (D-8) | C | `LoadTimeout` and waiter detachment need a cancellable read, and MSSQL shares the contract. | A hung load holds the gate until Npgsql's own timeout. | — |
| 12 | JWKS from the provider: served from a usable snapshot even after a failed refresh; 503 only when no usable snapshot exists; `200 []` only for `Succeeded(0)` (3.3) | A (AC 4) | F5 | JWKS keeps masking failures as an empty key set. | I-8 |
| 13 | `DmsJwtBearer` parity (3.2) | C (latent M) | It has the same blocking resolver (F2) and is registered by both stores, though no endpoint selects it. Its reflection call targets the `ValidateTokenAsync` that change 7 alters. | A latent copy of the measured defect and a second, divergent validation path | Same invariants as `Bearer` |
| 14 | `DevelopmentCertificateStore` (D-10) | C | The hosted service's startup load creates the file concurrently with issuance in development-certificate mode (the round-2 race). | The issued token's `kid` can differ from the published certificate. | I-10 |
| 15 | Parser move (§4.3.3); format-cache removal (Q7) | C; **O** | The sources parse keys outside the token manager, so the parser must move. Removing the cache is required by neither the mechanism nor an AC: with parsing once per load it no longer earns its keep. | Parser: nothing compiles. Cache: no functional effect. | — |
| 16 | Options with fail-fast validation (§4.3.12) | C, P | Values under G3 below | — | — |

**Deliberately unchanged, and why the evidence does not require a change:**

- `ProfileRepository.GetProfile` still opens outside its `try` (F8, Q10). The 500s run
  through that path, but their cause is the starvation. The fix removes the cause, and
  DMS-side handling stays DMS-1557 (AC 6).
- Npgsql `Timeout`, `Max Pool Size`, and the thread-pool minimum stay unchanged (§1.6).

**Alternatives the evidence rules out:**

- **A thread-pool minimum (E4).** It removes the stall locally but keeps the blocking
  resolver. The release needs about N workers, so any fixed minimum is a bet on host and
  burst size.
- **A pool bound (E5).** It does not remove the stall.
- **A longer Npgsql `Timeout`.** The stall grows with burst size (8–9 s at N = 64,
  13–14 s at 87, more than 16.5 s at 128, with 500s completing at 19–23 s), so a longer
  timeout only moves the failure point.
- **A synchronous in-memory cache inside the existing resolver.** It would remove the
  per-request read on cache hits, which is the real workload's warm-first shape. But a
  cold or expired cache blocks every concurrent request on one shared load. E5 shows
  that fewer database operations under blocking still stall; applying that to a single
  shared load is an inference. AC 3 also names asynchronous refresh/coalescing.

**Security and success requirements preserved** (unchanged from §4.8 and §7.4):

- I-1 issuer/audience/lifetime/signature: 3.1-c/j. I-9 missing-`kid`/forged-signature
  rejection: 3.1-j/k. Revocation still rejected: 3.1-b.
- I-2 uncached status: 3.1-l, M3.
- I-3 (wording corrected at G2) authentication succeeds only with a usable snapshot
  **and** a successful, valid token-status check. A failed key refresh keeps serving
  usable keys until `MaxStaleness`. No usable snapshot, or a token-status failure,
  answers 503: 3.1-f/g/m.
- Rotation and retirement bounds `T_prop`/`T_max`: 1.5-h/i/o/p/q, 1.6-f, 3.1-e/h/m/q, 3.4-b/c.
- I-5 no fallback: 3.1-o, M7.
- I-6 bounded loads: 1.5-j/l, M4/M5.
- I-8 no empty-as-success: 1.5-f/g, 3.3.
- Full-request success: **7.4-H** (every expected request in every healthy
  baseline-comparison run returns 200 with the expected `id` and `definition`) is not
  relaxed by anything proposed here.

**Proposed test clarification (P-7.4, requires approval).** The pre-fix stress baseline
shows a second, independent failure at `stress-256x128` with default
`max_connections=100`. In steady-state rounds that have no stall, 53300 slot exhaustion
produced 14 × 500 and many 401s, and headroom removed every one of them. The fix changes
neither `Max Pool Size` nor `max_connections` (§1.6), and the fixed image's pool demand
at 256/128 is unmeasured. So 7.4-H at 256/128 with default settings could fail for a
reason outside the design. The proposal:

- Keep 7.4-H exactly as written.
- Add a headroom 256/128 run alongside it (an addition, not a replacement), so a
  53300-free stress result exists either way.
- Pre-register: a 256/128 default run that fails **only** with 53300 is a **gate
  failure** that returns to review. It is not a pass, and it does not license a pool or
  `max_connections` change.

Also recorded: no pre-fix `stress-256x128` baseline exists for **P-dev** (E2 ran only
under P-runner-approx), so the P-dev stress comparison in 4.1-H will lack a baseline
unless one is run first.

**Prediction to be tested in 4.1-H (not evidence).** With `Worker Min Limit` 4 under
P-runner-approx, the fixed image's round 1 at 87/87 shows **zero resolver-wait threads**
and no stall. The E4 and E3 round-1 times (0.17–1.1 s) are results of the controls, not
predictions for the fix.

### Proposed G2 decision

**Proceed with Phases 1–3 under §4 as written**, with P-G1 and P-7.4 applied if
approved. §4's mechanism assumptions stand, and no design section needs revision.

- G2's proceed condition is met. Direct evidence supports H1 (stacks, queue growth,
  timeout clustering), and two controls corroborate it (E4; E3 with its stated limits).
- The "blocking elsewhere" branch does not apply: no thread in any of the 126 records is
  in another synchronous wait.
- The H2-dominant branch does not apply (E5), so connection-string guidance is not part
  of the fix. A docs-only operational note is proposed under G3.
- The H3/H4-dominant and inconclusive branches do not apply: H3 is refuted for the
  measured catalog workloads on this host, and H4 is a modifier of H1.
- Phase 1 starts only after this review approves it. Phase 1 is the options step, and
  step 1.6 remains its own scheduler checkpoint.

**G2 decision record (2026-09-30).** `e4c3afeb9` was approved as completing Phase 0,
with G2 approved and no further investigation run required. P-G1, P-G2, P-G3 and P-7.4
are in force (spec §0.00). Clarifications carried into the next commit:

- H3 and H5 conclusions are limited to the measured workloads. Warm-first establishes
  that metadata amplification is not necessary for the stall.
- I-3 is reworded: a usable snapshot and a successful, valid token-status check. A key
  refresh failure keeps serving usable keys until `MaxStaleness`. The JWKS wording is
  made consistent.
- The 10 s `LoadTimeout` is a policy informed by handshake measurements, not a measured
  bound for the whole load operation.
- The parser move stays focused.
- Steps 1.1 and 1.2 form one checkpoint.

Evidence archival (the Jira upload) remains outstanding.

### Proposed G3 decisions (settings)

| Setting | Proposed | Basis | Rationale |
| --- | --- | --- | --- |
| `SigningKeyRefreshIntervalSeconds` | 300 (unchanged) | **Policy** (rotation/retirement propagation `T_prop`) | The only measured input is that a key read is cheap: one pooled query, with steady-round p50 ≤ 231 ms for requests making four sequential acquisitions (three authentication reads plus the profile query). So the interval is not load-constrained, and 300 s is chosen for propagation time, not cost. |
| `SigningKeyMaxStalenessSeconds` | 3600 | **Policy, fixed by Q13** | Not re-decided |
| `SigningKeyUnknownKeyRefreshCooldownSeconds` | 30 (unchanged) | **Security/availability policy** (I-6 flood bound versus the rotation 401 window) | No measurement bears on it. |
| `SigningKeyLoadTimeoutSeconds` | 10 (unchanged) | **Policy informed by handshake measurements** (not a measured bound for the complete load operation) | Unstalled handshakes took 7–15 ms (E4), 35–68 ms (E3 cold), and 77–129 ms median, 183 ms max (P-dev). 10 s is more than 50 times the slowest, and it ends a load before the request-path `Timeout=15`. Stalled handshakes (5–15 s) are the defect, not a sizing input. |
| Backoff `min(5·2^(n−1), 60) s ± 20 %` | unchanged | **Availability policy** | No measurement bears on it; 4.1-O verifies recovery within `max backoff + LoadTimeout`. |
| `Retry-After` | `min(RefreshInterval, 30)` = 30 s | **Policy** (Q3) | — |
| `RefreshOnIssuerKeyNotFound` | `false` | **Decided (Q14)** | — |
| Npgsql `Timeout`, `Max Pool Size`; thread-pool minimum | **unchanged** | **Measured** (E4, E5, headroom) and a non-goal (§1.6) | E4 masks the symptom without fixing it; E5 does not help. For the docs (4.3) only, one operational note is proposed: a `Max Pool Size` equal to `max_connections`, with other clients sharing the server, produces 53300 under stress. Whether the note belongs in `CONFIGURATION.md` is a policy call for the reviewer. |

### AC status after Phase 0

| AC | Status | What remains |
| --- | --- | --- |
| AC 1 | Proposed **satisfied on the combined basis** (P-G1), pending the G2 decision | Upload the raw captures to Jira (EV index, excluding dumps). The catalog-on-runner 500 stays inferred until CI. |
| AC 2 | Not started (production) | 4.1-H on the fixed image; shards; CI after push |
| AC 3 | Design challenged above, not implemented | Phases 1–3; M1–M10 |
| AC 4 | Design challenged above, not implemented | 4.1-O stage-classified runs; §4.9 log lines |
| AC 5 | Not started | Phase 1–3 tests; shards 1 and 2 ≥ 2 runs each |
| AC 6 | Holding: no `src/dms` path on the branch | Re-checked at push readiness |

### Outstanding evidence (Phase 0 does not supply it)

- **E7b is observational**: one run on reused data without stacks. It corroborates the
  shape and establishes nothing by itself. E7a (uncapped, 0.4 s) says nothing about the
  runner.
- **Final regression evidence** on the fixed image: 4.1-H and 4.1-O, every §7.1 lane,
  and mutations M1–M10.
- **Repeated shards**: shards 1 and 2, at least 2 independent runs each (4.2).
- **CI after push**: the real envelope for AC 2 and the catalog-workload 500 (link 7).
- Still open from 0.5: the release order at the end of a stall, E5's bimodal handshakes,
  and why the pair-e3 cold baselines crossed 15 s.

### Bookkeeping

- **Parked-thread totals (3,412 versus 3,651).** The two totals come from different
  corpora.
  - **3,412** is the sum of `tpParked` over the **126 block records** in the nine
    regenerated `e5-pair-*-stacks.csv` tables (EV-STK), which is the evidence corpus.
  - **3,651** came from classifying **every** `*stacks-t*.txt` under `artifacts/`
    recursively: 135 files. That adds nine tooling shake-down captures: `e5-pilot/`
    (3 captures, 81 parked) and `e5-smoke/` (6 captures, 158 parked). 3,412 + 81 + 158 =
    3,651.

  The 0.5 correction text now reports 3,412. Cross-check against the superseded tables:
  they count 3,416 idle threads over the same 126 records. The difference of 4 is
  exactly the four reclassified threads, and resolver-wait is 3,718 in both versions.
- **SCRAM wording.** The 0.5 H2 bullet said no SCRAM computation appeared "on any
  captured stack". One worker was in SCRAM HMAC computation (`HMACSHA256.HashData`
  under Npgsql authentication) in one release-spanning 14 s capture (pair-e3 baseline
  cold repetition 3, authorizations 0 → 189 across it). The bullet is corrected in
  place. No capture inside the unauthorized phase shows SCRAM computation, and no
  classification or verdict changes.

## 4.1 — Fixed-image runtime evidence (2026-10-01)

### Scope

Spec Phase 4.1: healthy baseline-comparison runs (4.1-H) and injected outage runs
(4.1-O) on the fixed image, at default settings. Nothing in this step changes
production code. The Phase 0 records above are unchanged; Phase 0 numbers quoted here
are for comparison only.

### Image, environment, and effective configuration

- **Image:** `ed-fi-api-config-local:dms1556-c799f084d`, image id
  `sha256:eb4acec72d7c…`, built 2026-10-01 13:26 UTC with `docker compose … build config`
  from a clean worktree at `c799f084d` (the last 3.4 correction; the production code is
  that of 3.3 plus the 3.4 tests). Every block index records the image id of the running
  container, and every run used this image.
- **Stack:** dms-local, self-contained identity, PostgreSQL 16.8, database-backed signing
  keys, DMS idle, with both diagnostics overlays, as in §3.1 and Phase 0. CMS is at
  `127.0.0.1:8081`.
- **Effective configuration** (recorded per block in `conditionState.effectiveConfiguration`,
  secrets redacted):
  - No `SigningKey*` setting is supplied, so the defaults are in force: refresh 300 s,
    maximum staleness 3600 s, unknown-key cooldown 30 s, load timeout 10 s.
  - The connection string has no `Timeout`, `Command Timeout` or pool keywords, so Npgsql
    defaults apply: open timeout 15 s, command timeout 30 s, `Max Pool Size` 100.
  - No thread-pool override. `Serilog` at Debug (as in Phase 0). PostgreSQL
    `max_connections=100`, except in the headroom block (200).
  - **P-dev**: no CPU caps; `Worker Min Limit` 16 (dump). **P-runner-approx**: `cpus: 2`
    on CMS and PostgreSQL, `DOTNET_PROCESSOR_COUNT=4`; `Worker Min Limit` 4 (dump). These
    match the Phase 0 values, so the thread-pool minimum that exposed the stall is
    unchanged.
- **Other containers:** every container outside the dms-local stack was stopped for the
  runs (list in `artifacts/h41-stopped-containers.txt`); the block indexes record none
  running.
- **Data:** 87 harness profiles were re-seeded (the previous manifest was stale after the
  E7 teardowns). The table also holds 87 leftover E2E profiles, so it has 174 rows; each
  request is a primary-key read, and body validation uses only the harness manifest.

### Commands

From `eng/performance/dms-1556`:

```powershell
./Invoke-CmsProfileBurst.ps1 -Seed -ProfileCount 87
./Invoke-Step41Sequence.ps1              # 4.1-H: all blocks below, then restores P-runner-approx
./Invoke-Step41Outage.ps1 -ResourceProfile p-runner-approx -Repetitions 2
./Set-Dms1556StackCondition.ps1 -Condition baseline -ResourceProfile p-dev -RecreateDb
./Invoke-Step41Outage.ps1 -ResourceProfile p-dev -Repetitions 2
./Get-Step41Report.ps1                   # tables below: artifacts/h41-report.md
```

`Invoke-Step41Sequence.ps1` recreates PostgreSQL before every block and runs
`Invoke-ControlBatch.ps1 -LabelPrefix h41`. Every run restarts CMS and has 5 rounds with
body validation, samplers with the coverage gate, and managed stacks at 0/2/8/14 s into
round 1. Cold runs mint the token, restart CMS, and burst at once. Warm runs restart CMS,
then do a serial warm-up of 87 requests before the burst.

### Results — 4.1-H healthy baseline-comparison runs

5 cold and 5 warm runs per workload and profile at default settings, plus 3 + 3 headroom
runs. "Rounds" are the 5 rounds of each run; "cold" is round 1 of a cold run.

| Profile / condition | Workload | Runs | Responses | Non-200 | 200 with invalid body | Round-1 p50 (ms) | Round-1 max (ms) | Rounds 2–5 p50 (ms) | TP threads max, round 1 (queue max) | CMS conns created, round 1 | Sampler coverage failures |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| P-dev | cold-87x87 | 5 | 2,175 | **0** | 0 | 270–381 | ≤ 414 | 81–164 | 17 (0–12) | 69–82 | 0 |
| P-dev | warm-87x87 | 5 | 2,175 | **0** | 0 | 102–176 | ≤ 198 | 88–120 | 16–17 (0) | 9–57 | 0 |
| P-runner-approx | cold-87x87 | 5 | 2,175 | **0** | 0 | 458–562 | ≤ 585 | 85–208 | 5–23 (0–4) | 81–82 | 0 |
| P-runner-approx | warm-87x87 | 5 | 2,175 | **0** | 0 | 259–422 | ≤ 519 | 90–134 | 13–15 (0–3) | 59–79 | 0 |
| P-dev | cold-256x128 | 5 | 6,400 | **131** | 0 | 335–410 | ≤ 825 | 126–250 | 17 (2–17) | 213–387 | 0 |
| P-dev | warm-256x128 | 5 | 6,400 | **148** | 0 | 215–287 | ≤ 661 | 134–223 | 16–36 (1–8) | 174–201 | 0 |
| P-runner-approx | cold-256x128 | 5 | 6,400 | **157** | 0 | 552–780 | ≤ 1,850 | 300–713 | 18–21 (2–45) | 195–274 | 0 |
| P-runner-approx | warm-256x128 | 5 | 6,400 | **155** | 0 | 551–714 | ≤ 1,820 | 226–794 | 12–15 (0–27) | 208–262 | 0 |
| P-runner-approx, headroom (`max_connections=200`) | cold-256x128 | 3 | 3,840 | **0** | 0 | 374–406 | ≤ 1,030 | 180–296 | 20–21 (1–44) | 95–96 | 0 |
| P-runner-approx, headroom | warm-256x128 | 3 | 3,840 | **0** | 0 | 308–382 | ≤ 848 | 181–231 | 5–15 (0–8) | 95–96 | 0 |

Measured peak overlap was 87 in every catalog round and 128 in every stress round.

- **Catalog workload (87/87), both profiles: 8,700 of 8,700 responses are HTTP 200 with
  the expected `id`, `name` and `definition`.** The gate for this workload is met.
- **For comparison (Phase 0, same host and profile, baseline image):**
  - P-runner-approx round 1 took 13.6–16.7 s at p50 in every baseline block (0.5).
  - Thread-pool threads climbed to 79–87, with the queue up to 74.
  - The fixed image's P-runner-approx round 1 is 0.26–0.56 s at p50, with threads 5–23
    and the queue at most 4.
- **Stress workload (256/128) at default settings: the gate is NOT met.** 591 of 25,600
  responses are non-200 across both profiles. Every one was joined through its
  `correlationId` to its CMS log lines; none is uncorrelated. **All 591 have the same
  cause: PostgreSQL `53300: sorry, too many clients already`**. They split by stage:

  | Profile | Workload | 503, authentication stage (`TokenStatusStore`) | 500, after authentication (profile read) |
  | --- | --- | --- | --- |
  | P-dev | cold | 66 | 65 |
  | P-dev | warm | 87 | 61 |
  | P-runner-approx | cold | 101 | 56 |
  | P-runner-approx | warm | 99 | 56 |

  - Every 503 carries the dependency contract (`Retry-After: 30`,
    `application/problem+json`, no `WWW-Authenticate`). Each has its boundary Error line
    `Authentication could not reach a decision: the TokenStatusStore is unavailable (trace …)`,
    whose inner exception is the `53300` `PostgresException`.
  - The 500s are the unchanged profile-repository path (F8).
  - PostgreSQL logged 134–158 `too many clients` rejections per block.
  - **No open timeout, SASL timeout, or `TimeoutException` appears anywhere** in the stress
    runs, and no round shows a stall: the worst request took 1.85 s.
- **Headroom supplements the gate (P-7.4):** with `max_connections=200`, all 7,680 stress
  responses are 200, and CMS creates 95–96 connections in round 1. Removing the slot
  limit removes every failure, so nothing but slot exhaustion remains at 256/128 on this
  host.
- **Pre-registered disposition (P-7.4):** a default-setting 256/128 run that fails only
  with 53300 is a gate failure returned to review, never a pass, and never a licence for a
  pool or `max_connections` change. **This is that case.** It goes to review at this
  checkpoint, and no setting was changed.
- **Context, not a disposition:** CMS's `Max Pool Size` (100) equals the server's
  `max_connections` (100), and DMS and the two samplers share the server. Before the fix,
  the stall itself bounded how fast CMS opened connections. Now 128 concurrent requests
  open connections as fast as PostgreSQL authorizes them, and round 1 creates 174–387
  connections. No pre-fix P-dev 256/128 baseline exists (P-7.4). The Phase 0
  P-runner-approx stress baseline (0.5) also had 53300 failures, there mixed with
  open-timeout 500s under the stall.

**Dependency log signals in the healthy windows** (spec §4.9; counted per run over the
burst window, every run):

- **Catalog and headroom runs: zero** boundary 503 lines, zero JWKS-unavailable lines, zero
  signing-key load failures, zero late-load discards and zero unknown-key refresh
  warnings. No snapshot publication either: the startup load published before each burst.
- **Default stress runs:** `Authentication could not reach a decision … TokenStatusStore`
  appears 66, 87, 101 and 99 times, equal to each workload's 503 count. **No
  `SigningKeyStore` category appears in any healthy run**, and no signing-key load failed.
- **The retired baseline string** `Failed to fetch public keys for JWKS` reads zero by
  construction (spec Phase 4, changed log signal) and is not used as evidence.

### Results — managed stacks, round 1 (4.1-H)

184 healthy captures in all, at 0, 2, 8 and 14 s into round 1 of every run: 160 at
default settings (40 per profile and workload class) and 24 in the headroom block (6 runs).
Each capture is the interval `[requested, completed]`; the snapshot lies somewhere inside.
(Corrected after review: this section first said 160 in all, with the headroom captures
folded into the P-runner-approx stress row. Counts are from the per-block
`artifacts/h41-*-stacks.csv` files and the run summaries; no workload was rerun.)

| Profile, workloads | Captures | TP workers | Resolver wait | Authentication frame in a sync wait | Other sync wait | Parked | Round end vs the 0 s capture |
| --- | --- | --- | --- | --- | --- | --- | --- |
| P-dev, catalog | 40 | 16–17 | **0** | **0** | 0 | all | inside it (round ends before completion) |
| P-dev, stress | 40 | 16–36 | **0** | **0** | 0 | all but 1 worker in 1 capture¹ | inside it |
| P-runner-approx, catalog | 40 | 5–23 | **0** | **0** | 0 | all | inside it |
| P-runner-approx, stress (default) | 40 | 12–21 | **0** | **0** | 0 | all | inside it |
| P-runner-approx, stress (headroom, `max_connections=200`) | 24 | 5–21 | **0** | **0** | 0 | all | inside it |

¹ Warm rep 2, the 2 s capture, after that round had ended: one of 36 workers was writing a
Kestrel connection-accept log line to the Serilog file sink (`OSFileStreamStrategy.Write`
under `RollingFileSink.Emit`). It is not a synchronous wait. (Corrected after review: the
row first said "all".)

- No capture shows a resolver wait (the Phase 0 signature: a synchronous `Task.InternalWait`
  under `JsonWebTokenHandler.ValidateSignature`), nor any thread in a synchronous wait
  with a JwtBearer, signing-key, token-manager or IdentityModel frame. Every worker is
  parked, except the one log write in footnote 1. In the Phase 0 baselines, 40–41
  workers were in the resolver wait at 2 s and 64–66 at 8 s, and 89–90 at 14 s under
  stress (0.5).
- **Limitation:** a fixed-image round 1 ends within 0.3–1.9 s, inside the ~2.4 s 0 s
  capture, and the 2/8/14 s captures follow the round. These stacks therefore cannot be
  placed during the burst, and they show only that no worker is left blocked. The
  counters cover the burst itself (thread-pool threads at most 36, queue at most 45 in a
  1 s sample). The direct stack evidence that authentication does not block workers is in
  4.1-O, where requests are held 15–40 s.

### Results — 4.1-O injected outage runs

`Invoke-Step41Outage.ps1`, 2 repetitions of each scenario per profile, default settings,
with samplers and a JWKS probe every 500 ms beside the rounds. Each run restarts CMS.

- **`pause-warm`** (spec 4.1-O):
  - setup: serial warm-up, then 35 rounds of 870 requests through 87 slots, 2 s apart;
  - fault: `docker pause dms-postgresql` for 20 s, starting 0.4 s into round 4, so 87
    requests are in flight at the freeze and the rest queue behind them;
  - stacks: at 3.4 s and 10.4 s into round 4, both inside the pause.
- **`pause-warm-long`**: the same with a 40 s pause, longer than Npgsql's 30 s command
  timeout.
- **`keylock-cold`**: a key-store-only outage.
  - The token is minted first. One session then holds
    `LOCK TABLE dmscs."OpenIddictKey" IN ACCESS EXCLUSIVE MODE` for 75 s, and CMS is
    restarted under the lock.
  - Token-status and profile reads are unaffected, so this is the only way at default
    settings to reach "no usable snapshot", and with it the JWKS 503.
  - 80 rounds of 87/87, 2 s apart; stacks at 1 s and 5 s into round 1.

Every non-200 was joined through its correlation id to its CMS log lines. **All 4,506
are classified, and none is transport, unclassified, or uncorrelated.** Each run's CMS
log starts before its burst; pause runs stream the log during the burst (see
Deviations). **Every 503 carries the dependency contract** (`Retry-After: 30`,
`application/problem+json`, no `WWW-Authenticate`). **Every 200 has a validated body.**

| Scenario | Profile | Run | Responses | 503, authentication stage | 500 | Fault held (s) | First 200 after removal (s) | Last non-200 after removal (s) | JWKS during the fault |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| pause 20 s | P-dev | 1 / 2 | 30,450 / 30,450 | 9 / 11 `TokenStatusStore` | 0 / 0 | 20.4 / 20.3 | 0.02 / 0.02 | −5.2 / −5.1 | 40 / 40 × 200, 1 key |
| pause 20 s | P-runner-approx | 1 / 2 | 30,450 / 30,450 | 3 / 0 `TokenStatusStore` | 0 / 0 | 20.1 / 20.2 | 0.00 / 0.00 | −5.1 / — | 40 / 40 × 200, 1 key |
| pause 40 s | P-dev | 1 / 2 | 30,450 / 30,450 | 30 / 14 `TokenStatusStore` | 0 / 0 | 40.2 / 40.3 | 0.18 / 0.10 | +0.34 / +0.11 | 60 / 60 × 200, 1 key |
| pause 40 s | P-runner-approx | 1 / 2 | 30,450 / 30,450 | 2 / 0 `TokenStatusStore` | 0 / 0 | 40.1 / 40.3 | 0.26 / 0.23 | +0.16 / — | 61 / 60 × 200, 1 key |
| key-table lock 75 s | P-dev | 1 / 2 | 6,960 / 6,960 | 870 / 1,218 `SigningKeyStore` | 0 / 0 | 74.6 / 75.0 | 0.32 / 0.28 | −9.8 / −4.3 | 54 / 70 × **503** (+1 × 200¹) |
| key-table lock 75 s | P-runner-approx | 1 / 2 | 6,960 / 6,960 | 1,305 / 1,044 `SigningKeyStore` | 0 / 0 | 75.0 / 75.0 | 0.13 / 0.67 | −2.9 / −6.3 | 70 / 64 × **503** (+1 × 200¹) |

¹ The probe was requested before the release and completed just after it: it joined the
load that completed when the lock was released.

**Token-status store outage (pause scenarios).** 69 non-200s in 243,600 responses. All
are 503 `TokenStatusStore` at the authentication stage, and the inner exception of every
one is `System.TimeoutException: Timeout during reading attempt`, the 15 s open timeout
of a new physical connection.

- Each completed 15.0–25.3 s after admission.
- Requests that needed no new connection waited for the server and completed with 200.
  At the 40 s pause, the requests held through the freeze (the 87 in flight, and
  those admitted after them) were held 40.4–42.0 s and then returned 200. Npgsql's 30 s command timeout did not end them while the server was
  frozen. That mechanism is **not established here** (not investigated), so the 40 s
  scenario did not exercise a command timeout. The 503 counts vary with how many new
  connections a run needed.
- **No request reached a post-authentication failure (500).** The unchanged
  profile-repository 500 path (F8) was observed only in the 4.1-H stress runs (53300).
- The snapshot stayed usable, so the JWKS answered 200 with its key throughout every
  pause.
- No signing-key load was attempted or failed: the refresh interval is 300 s and nothing
  triggered an unknown-key refresh.
- Recovery was immediate: the first 200 completed 0.00–0.26 s after unpause, and the
  last 503 0.34 s after it at the latest. No load had failed, so the snapshot recovery
  bound is not engaged.

**Key-store outage (key-table lock).** 4,437 non-200s. All are 503 `SigningKeyStore` at
the authentication stage, and all ended within 10 s:

- **3,915 refused at once:** no usable snapshot, and the backoff gate refuses a request
  load, so there is no inner exception.
- **522 waited on the in-flight load:** they joined it and failed when its 10 s
  deadline passed (`TaskCanceledException` inner).
- **The JWKS answered 503 throughout the lock (54–70 probes per run), never `200 []`.**
  This is AC 4's distinction, observed at runtime.
- **Load attempts:**
  - The startup load failed at its 10 s deadline. Two timer retries followed at backoffs
    of 4.1–5.6 s, 8.3–11.6 s and 19.3–22.8 s (5/10/20 s ±20 %).
  - Each run logs 3 load-failure Errors (`Startup`, `Timer`, `Timer`) and 2–3
    late-discard Warnings for loads that outlived their deadline.
  - In all four runs the next timer attempt was in flight, blocked on the lock, when the
    lock was released. It published the snapshot at the release instant (`retrieved in`
    2.8–8.4 s).
  - The first protected 200 came 0.13–0.67 s after release, and the JWKS 200 0.02–0.15 s
    after. Each run observed at least 139 s after release, with no non-200 after it.
- **Backed-off recovery** (observed in the superseded first set, Deviations):
  - The 4th attempt timed out 0.4 s before the release, so the provider backed off
    47.6 s, and request loads were refused meanwhile (I-6).
  - The snapshot was published 47.2 s after release, within the
    `max backoff + LoadTimeout` = 82 s bound.
  - That run's rounds ended 0.16 s before the publication, so it is log evidence only:
    no 200 was observed.

**Dependency log signals** (spec §4.9; counted over each burst window):

- **Boundary 503 lines:** `Authentication could not reach a decision: the {Category} is
  unavailable (trace …)` equals the 503 count of every run exactly, with the matching
  category (`TokenStatusStore` for pauses, `SigningKeyStore` for the lock).
- **JWKS 503 lines:** `The JWKS could not be served: the SigningKeyStore is unavailable`
  equals the JWKS 503 count (54, 70, 70, 64).
- **Signing-key load failures:** `Signing-key load failed (Startup|Timer): category
  SigningKeyStore, kind Retrieval, consecutive failures n, next attempt in …`, with
  exactly one `Signing-key snapshot 1 published … (Timer)` per lock run.
- **Not seen in any outage run:** empty-snapshot warnings, unknown-key refresh warnings,
  and introspection failures. Neither are the retired `Failed to fetch public keys for
  JWKS` lines (zero by construction; not evidence).

**Managed stacks while the dependency is held.** These 24 captures are the direct
evidence that authentication no longer blocks workers.

- **Pause runs:** both captures fall inside the pause, with 87 requests held in flight.
  - P-dev: 36 thread-pool workers, 35–36 parked; P-runner-approx: 12–21, all parked.
  - In 2 captures one worker was in Npgsql code.
  - Resolver waits, other sync waits, and authentication frames in a sync wait: zero in
    every capture.
  - Thread-pool threads stayed at 14–49 for the whole run. The 1 s queue samples peaked
    at 28–92 per run; this record does not establish when within the run the peaks
    fell.
  - Phase 0 for comparison: during the stall, every worker was in the resolver's
    synchronous wait, and the pool grew about 4 threads per second.
- **Key-lock runs:** 5 (P-runner-approx) and 17 (P-dev) workers, all parked, zero sync
  waits.
  - Round 1 ended inside the first capture, so these captures cannot be placed during a
    held load.
  - Later rounds that waited on a held load for 9.5–9.6 s were not captured. Their
    thread-pool threads stayed ≤ 39 over the run.

### Evidence vs inference

- **Observed:**
  - Catalog 87/87: all 8,700 responses were 200 with validated bodies on both profiles,
    with round 1 well under a second (Phase 0: 13.6–16.7 s on P-runner-approx).
  - No blocked authentication frame in any of the 208 captures: 184 healthy (160
    default, 24 headroom) and 24 outage. (Corrected after review; this line first said
    184.)
  - Workers stay parked while 87 requests are held by a frozen database.
  - Stage-classified 503s carry `Retry-After`, and their Error lines name the category.
  - The JWKS answers 503 when no usable snapshot exists and 200 with keys while one does.
  - Recovery comes at the next load attempt after the dependency returns.
- **Observed, gate failure:** 256/128 at default settings fails with 53300 only (591
  responses, both stages), and headroom removes every failure.
- **Not established:**
  - why Npgsql's command timeout did not end commands held by `docker pause`;
  - the real CI envelope (4.2 and CI after push);
  - behavior on SQL Server (all runtime evidence here is PostgreSQL).

### Limitations

- P-runner-approx is a stress approximation of the runner, not its envelope (§3.1.8).
- The healthy-run stacks end after their sub-second rounds, so they show only that no
  worker is left blocked; the outage stacks carry the direct evidence.
- `docker pause` freezes the server process but keeps its TCP endpoints, so it models a
  hung server, not a refused or reset connection.
- The key-table lock models a key store that blocks. It is not a key store that errors
  immediately; that path is covered by the 1.5/3.1/3.3 fixtures.
- One `Worker Min Limit` dump per block, not per run (Deviations); the values (16 / 4)
  match every Phase 0 dump on the same profiles.
- The CMS log level is Debug, as in Phase 0. That is what made the pause runs' logs
  exceed Docker's rotation.

### Deviations

- **Host memory.** The first 4.1-H sequence was stopped by the host for low memory at
  8 of 10 runs of its first block. Those runs are kept in `artifacts/h41-aborted/` and
  are not evidence. Before the rerun, at the reviewer's direction:
  - every container outside the dms-local stack was stopped (listed in
    `artifacts/h41-stopped-containers.txt`; `docker start` restores them);
  - the dump policy was changed to **one dump per block**, taken after the block's last
    run and outside every timed window.
- **Pause placement.** A warm 87-request round completes in about 0.2 s, faster than
  `docker pause` takes effect, and the first shake-down paused between rounds. Pause
  rounds are therefore 870 requests through 87 slots, with the pause 0.4 s in.
- **40 s pause added.** The specified 20 s pause on a warm pool elapses no command
  timeout, so a 40 s variant was added. It did not exercise the command timeout either
  (above).
- **Key-store-only outage added.** The key-table lock reaches the no-usable-snapshot
  state, which no pause on a warm process can reach at default settings.
- **First P-runner-approx outage set superseded** (kept in `artifacts/o41-superseded/`).
  - Docker's json-file rotation (5 × 50 MB) dropped the start of each pause run's
    Debug-level CMS log, leaving 10 non-200s unclassifiable.
  - Its key-lock repetition 2 ended its rounds before recovery. The driver then reported
    recovery from the absence of later failures, a bug now fixed: recovery requires an
    observed 200.
  - The rerun streams the CMS log during pause bursts, runs 80 key-lock rounds, judges
    log completeness against the burst start, and records the time observed after
    removal and the first publication after it.
- **Log-completeness records reconciled (after review, no rerun).** The four key-lock run
  records (`o41-keylock-cold-*-outage.json` and their entries in both
  `o41-*-index.json` files) said `cmsLogComplete=false`, while the report recomputed
  `true`.
  - **The old comparison:** the stored values are what a comparison of the log's first
    line against the harness start (`summary.startedUtc`) gives, not against the burst
    start. The driver's switch to the burst start landed after these records were
    written, and they were not regenerated. This is inferred from the values: all 12
    stored flags match the harness-start comparison, and the driver was committed only
    in its final form.
  - **Why only the key-lock runs:** a pause run's log is streamed from the run start,
    before its warm-up, so its first line precedes the harness start. A key-lock run is
    cold, and the old process is idle until the harness connects: its first line is
    Kestrel accepting the token-mint connection, 2–5 ms after the harness start. The CMS restart and the burst follow 26–30 s later.
  - **Recomputed against the burst start:** the first line precedes the burst by 30.1,
    27.8, 26.2 and 28.6 s, so all four logs are complete. The records now say `true` and
    keep the old value and both instants under `cmsLogCompleteReconciliation`. No
    classification or signal count changes, because those were always computed over the
    full retained log.
- **Data.** The profile table holds 87 leftover E2E profiles besides the 87 harness
  profiles (above).

### For review at this checkpoint

1. **Default 256/128 fails with 53300 only (P-7.4: a gate failure, returned to review).**
   - The catalog workload passes. No setting was changed, and no pool or
     `max_connections` change follows from this record.
   - The decision is the reviewer's.
   - The optional docs note on `Max Pool Size` = `max_connections` proposed at G3 (0.6) is
     relevant to it.
2. **The 40 s pause did not exercise Npgsql's command timeout.** Whether a scenario that
   does is required is the reviewer's call.
3. **Outage scope beyond the specified pause** (the 40 s pause and the key-table lock),
   and the per-block dump policy.

### Review disposition (2026-10-01)

The reviewer inspected the code and retained artifacts and ran no workloads. The 4.1
evidence supports proceeding to 4.2. **This is not approval of the final acceptance
gate: the default stress gate remains failed.**

1. **53300:** a separate connection-capacity limitation. No pool size, PostgreSQL limit or
   production concurrency changes for DMS-1556. The failed default-setting 256/128 result
   is kept for final review; the headroom success does not replace it. No further stress
   runs are required now.
2. **Command timeout:** no further experiment. Why Npgsql's command timeout did not end
   commands held by `docker pause` stays explicitly unexplained. The pause runs establish
   interruption, recovery and open-failure classification, not command-timeout coverage.
3. **Added scenarios:** the 40 s pause, the key-table lock and the per-block dump policy
   are accepted.
4. **Bookkeeping:** the stack totals and the key-lock log-completeness records were
   corrected without rerunning anything (see the managed-stacks table, Evidence vs
   inference, and Deviations).

### AC status after 4.1

| AC | Status | What remains |
| --- | --- | --- |
| AC 2 | **Catalog-shape gate met** on both profiles (8,700/8,700 validated 200s; no blocked authentication frames). The stress-shape gate is **not met** (53300 only, returned to review). | Review disposition of the 256/128 result; 4.2 shards; CI after push |
| AC 4 | **Observed at runtime:** stage-classified 503s with `Retry-After` for both categories, §4.9 Error lines matching 503 counts exactly, JWKS 503 versus 200 with keys, no failed read treated as an empty key set | Docs (4.3) |
| AC 5 | Interruption/recovery covered at runtime (pause and key-table lock, both profiles) | 4.2 shards 1 and 2 ≥ 2 runs each; §7.1 lanes on the final commit |
| AC 1, 3, 6 | Unchanged by 4.1 | as before |

## 4.2 — E2E shards and CMS E2E (2026-10-01)

### Scope

Spec Phase 4.2, as approved at the 4.1 review: DMS E2E shards 1 and 2 on at least two
independent runs each, with teardown/setup between runs, and the CMS E2E suite once,
through the documented build path. No production code changed, and no workload or setting
from 4.1 was rerun or changed.

### Tested code and images

- **Commit:** HEAD `a5ae72e30` (4.1 bookkeeping). Its `src/` is identical to `c799f084d`,
  the implementation the 4.1 image was built from (`git diff c799f084d HEAD -- src/` is
  empty). The branch changes nothing under `src/dms` relative to its `main` base
  `5e0d010af`. The only uncommitted file during the runs was this document.
- **CMS image in the DMS shard runs.** `teardown-local-dms.ps1` removes
  `ed-fi-api-config-local:latest` (and `ed-fi-api-local`), so each run's compose start
  built both images again from this worktree (`local-config.yml`: context
  `../../src/config`).
  - Run 1's CMS image has the same 16 layers and labels as the 4.1 image
    `ed-fi-api-config-local:dms1556-c799f084d` (`eb4acec72d7c`). Its layer list was
    compared while the stack was up.
  - All four builds report the 4.1 build's creation time (`2026-10-01T13:26:17Z`). Every
    step was therefore a cache hit on the 4.1 build. The layers of runs 2–4 were not
    compared before teardown removed them.
  - Image ids differ per build (`d55d23ca781d`, `356151e7801f`, `e02a74cc2f0f`,
    `5d41780de4db`) even with identical layers and labels. The cause was not
    investigated.
- **CMS image in the CMS E2E run:** `start-local-config.ps1 -r` built it without cache
  from the same tree (`46ca1f8b3fe2`, created `20:42:46Z`). A no-cache build has new
  layers, so it is not layer-comparable to the 4.1 image.
- **DMS image:** rebuilt every run by `build-dms.ps1` and compose from the unchanged
  `src/dms`.

### Commands

From the repository root, each lane in its own `pwsh -NoProfile` process (driver output
in the gitignored `eng/performance/dms-1556/artifacts/e42/`: per-lane logs, trx files,
container logs, image records, `e42-index.json`):

```powershell
./build-dms.ps1 Build -Configuration Release
./build-config.ps1 Build -Configuration Release
# shard 1, shard 2, shard 1, shard 2; after each:
#   src/dms/tests/EdFi.DataManagementService.Tests.E2E/teardown-local-dms.ps1
./build-dms.ps1 E2ETest -Configuration Release -IdentityProvider self-contained -EnvironmentFile './.env.e2e' -TestFilter 'Category=@e2e-ci-shard-N'
./build-config.ps1 E2ETest -Configuration Release -IdentityProvider self-contained
#   then src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/teardown-local-cms.ps1
```

`-SkipDockerBuild` was not used. Every teardown removed the containers, the volume and the
local images; only the pinned 4.1 tag survived.

### Results

| Lane | Start (UTC) | Wall time | Passed | Failed | Skipped | trx counters |
| --- | --- | --- | --- | --- | --- | --- |
| DMS shard 1, run 1 | 18:06 | 42.7 min | 230 | 0 | 2 | total 232, executed 230 |
| DMS shard 2, run 1 | 18:49 | 36.3 min | 220 | 0 | 2 | total 222, executed 220 |
| DMS shard 1, run 2 | 19:25 | 40.1 min | 230 | 0 | 2 | total 232, executed 230 |
| DMS shard 2, run 2 | 20:06 | 36.0 min | 220 | 0 | 2 | total 222, executed 220 |
| CMS E2E (self-contained, PostgreSQL) | 20:42 | 2.2 min | 227 | 0 | 8 | total 237, executed 227 |

- **Skips are the known ones and match between runs.**
  - Shard 1: profile scenarios `04 PUT with profile excluding required field succeeds`
    and `06 POST with collection rule on required collection …`.
  - Shard 2: the `@ignore` profile scenarios 03 and 09, as in the shard-2 baseline
    (220/0/2, 0.5 E7a).
  - CMS: the 8 `@MultitenantOnly` scenarios (single-tenant stack). The pending-binding
    ApiClients scenarios 09 and 15 also print as skipped but are outside the totals
    (pre-existing; they are why the trx total is 237).
- **Both E2E assemblies came from this build:** the run logs list
  `bin/Release/…/Tests.E2E.dll` written at 11:05 local time by the Release builds above.
- **DMS restarts are scenario-driven:** both shard 1 runs show 6 DMS startups at the same
  points in the run, and both shard 2 runs show 2. CMS started once per run and did not
  restart.

**CMS logs in the DMS shard runs** (the workload the ticket reported):

- Zero Error-level lines in all four runs.
- The only DMS-1556 signals are snapshot publications, all with 1 key (reloads retrieved
  in 1–18 ms):
  - shard 1: 8 and 9 per run; shard 2: 8 and 8;
  - each run has one `Startup` publication, then one reload about every 5 minutes;
  - the reloads are labelled `Timer` or `Request` (shard 1: 3+4 and 7+1; shard 2: 3+4
    and 5+2). A `Request` reload is a background load started by a request that finds
    the snapshot `Overdue`: older than the 300 s refresh interval, still served. The
    timer fires at 300 s ±10 % jitter, so when its deadline falls after 300 s, a request
    gets there first. These requests were `/v3/authorizationMetadata` and
    `/v3/dataStores` calls from DMS.
- No boundary 503 line (`Authentication could not reach a decision …`), no JWKS 503, no
  signing-key load failure, no late-load discard, and no unknown-key refresh.

**CMS logs in the CMS E2E run:**

- The fresh database has no key at startup, so the `Startup` snapshot published 0 keys.
  The first token was then signed with a key the snapshot did not have. One unknown-key
  refresh followed (`Warning … unknown-key refresh outcome: RefreshedFound`) and
  published snapshot 2 with 1 key. The cooldown design expects exactly this cold path.
- 4 Error lines `Authentication failed: IDX10511: Signature validation failed` are the
  four manipulated-signature scenarios (Token 02, OwaspCriticalPaths 04/05/06), all
  passing with 401. That log line is the scheme's `OnAuthenticationFailed` handler,
  unchanged from `main` and kept at 3.2.
- The other Error/Warning lines come from scenarios that expect a validation failure
  (duplicate application names, a non-unique claim-set name, a claims upload with 3
  failures).

### Deviations

- **`build-dms.ps1 Build -Configuration Release` exited 1** with NU1008/MSB3073 in one
  place: the `EdFi.DataManagementService.Tests.Integration` build publishes the
  `eng/fixtures/plugins/Acme.CustomValidationProof` plugin fixture.
  - Cause: worktree placement. This worktree lies under the main checkout's `src\`, so
    that fixture project inherits the main checkout's central package management.
  - This is a known local condition, not related to this change; CI and a worktree
    outside the main checkout do not hit it.
  - Every other project built, including the DMS E2E test assembly used above. No DMS
    integration lane is part of 4.2.
- **PowerShell analysis:** `eng/Invoke-StagedPowerShellAnalysis.ps1` over all 16
  PowerShell files the branch adds or changes (`eng/performance/dms-1556/*.ps1`, `*.psm1`)
  reported no findings.

### Review disposition (2026-10-01)

The reviewer checked the retained trx files and the run index (`artifacts/e42/`) and ran no
tests. 4.2 is approved.

1. **Runs confirmed:** both independent runs of each DMS shard, and the CMS E2E result.
2. **Build exception:** the failed local DMS Release build (NU1008 on the plugin fixture,
   Deviations) does not invalidate the freshly built E2E assemblies. It stays recorded as a
   build exception.
3. **CMS E2E counts for the final matrix:** 227 passed, 8 reported skips, and 2
   pending-binding `NotExecuted` results (ApiClients 09 and 15): 237 trx entries in all. The
   Results section above already explains the 2.

### AC status after 4.2

| AC | Status | What remains |
| --- | --- | --- |
| AC 2 | Catalog-shape gate met (4.1). **Default stress gate failed** (53300 only; kept for final review per the 4.1 disposition). Shards 1 and 2 green on 2 independent runs each, CMS E2E green. | CI after push (real envelope); final disposition of the failed stress gate |
| AC 4 | Observed at runtime (4.1); no dependency 503 or key-load failure in any E2E run | Docs (4.3) |
| AC 5 | Interruption/recovery at runtime (4.1); shards 1 and 2 rerun ≥ 2 times each, green | §7.1 lanes on the final commit |
| AC 1, 3, 6 | Unchanged by 4.2 | as before |
