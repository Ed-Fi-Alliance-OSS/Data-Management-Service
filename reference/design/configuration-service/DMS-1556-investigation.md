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
