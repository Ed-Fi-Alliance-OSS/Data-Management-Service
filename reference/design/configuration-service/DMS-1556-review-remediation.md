# DMS-1556 Review Remediation — PR #1317, review round 1

Status: **v3 — APPROVED FOR R1.1 ONLY (Codex, 2026-10-02).**

- v1 was not approved. v2 applied the corrections in §0.1, and v3 folds in the approval
  corrections in §0.2.
- R1.1 commits this file alone. This approval authorizes no implementation, no Docker
  work, no Jira publication and no push.
- Each later step needs its own explicit approval.
- The local Jira drafts (`eng/performance/dms-1556/artifacts/review-remediation-r1/jira-drafts.md`)
  are gitignored, unpublished, and outside this commit.

This document is an addendum to
[DMS-1556-cms-signing-key-resolution-under-load.md](./DMS-1556-cms-signing-key-resolution-under-load.md)
("the design spec"). It does not replace that spec or rewrite its history. Approvals,
evidence and dispositions recorded there and in
[DMS-1556-investigation.md](./DMS-1556-investigation.md) ("the investigation") stay as
recorded, with their original commit provenance.

- **Worktree / branch:** `C:\dev\ed-fi\Data-Management-Service\src\Data-Management-Service-DMS-1556`, `DMS-1556`.
- **Reviewed head:** `9ff5a3e8f` (PR #1317 head; tree identical to `41de06755`). `9bf1bcad0`
  remains the implementation-tested SHA for every result recorded before this remediation.
- **Inputs:**
  - the panel review report (`C:\reviews\dms-1556-01.txt`, 9 findings);
  - Codex's source verification of the same head (§1);
  - Codex's review of v1 (§0);
  - Jira DMS-1556 (description; comments 99414 and 99452);
  - the design spec, the investigation, `CS-AUTH.md` and `docs/CONFIGURATION.md`.
- **Constraints:**
  - No schema change; `RelationalMappingVersion` stays `v3`.
  - No new configuration settings.
  - No pool, `max_connections`, timeout or thread-pool change.
  - No `src/dms` change, as a **scope decision** of this remediation (§4, AC 6). AC 6 does
    not forbid DMS changes; it requires independence from the DMS-1557 partial-catalog
    correction.

Step ids use an `R` prefix (`R2.1`) so they cannot be confused with the design spec's steps.
Commit messages read `[DMS-1556] R<step> — <summary>`.

### Carried-forward exceptions (unchanged by this remediation)

- **Default stress gate:** the default-setting `stress-256x128` result stays **FAILED**
  (53300 only). It is accepted as a ticket-scoped exception (4.4 review). It is not rerun
  as a pass, and no pool or `max_connections` change follows.
- **Waived mutants:** M2a, M2b, the three 2.2 unknown-kid mutants, the 3.1
  challenge-classification mutant and the 3.1 wiring revert stay **UNRUN — execution
  requirement waived**. None counts as passed, and none is retried by any route.
- **Local `build-dms.ps1 Build` exception (NU1008/MSB3073):** in this worktree,
  `./build-dms.ps1 Build -Configuration Release` exits 1 in the `Acme.CustomValidationProof`
  plugin-fixture publish. MSBuild walks up into the main checkout's `src\Directory.Packages.props`
  because the worktree sits inside it. This is recorded at 4.2 and post-merge validation.
  It is accepted only when the E2E assemblies are rebuilt from the tested commit and no
  tracked file changed (R4.2). It is **not** a prerequisite pass, and an exit-1 for any
  other reason is a failure. CI is unaffected.
- **Historical passes** keep their recorded commits and images. New results are recorded
  against the remediation's final commit and images, in a new investigation section.
  Nothing earlier is relabelled.

## 0. Review history

### 0.1 v1 → v2 (Codex review of v1, 2026-10-02)

| # | Correction | Applied in |
| --- | --- | --- |
| 1 | v1 R4's `Fail` + 499 design let aborted requests continue into anonymous endpoints, including token issuance and revocation. Authorization never challenges an anonymous endpoint, so a 499 challenge cannot stop it. | **R4 removed. Finding 5 deferred.** Existing cancellation behavior is kept. The record of the rejected design is in §3.3. A future cleanup must preserve termination of both protected and anonymous requests. Former R5 is now R4. |
| 2 | The no-spin argument was wrong. `Task.Delay(TimeSpan, TimeProvider, …)` truncates to whole milliseconds, so a positive sub-millisecond wait completes at once, and recomputation can spin indefinitely on a stationary fake clock. | §3.2 *Timer resolution*: positive waits are rounded **up** to whole milliseconds, at least 1 ms; admission is still decided only by a remaining time ≤ 0. New deterministic fractional-deadline tests R3.2 (i)/(j). The same latent defect exists today on the UTC path and is fixed by the same change. |
| 3 | The AC 3 argument treated DMS's refresh interval as a hard trust bound, and AC 6 was misread as forbidding `src/dms` changes. | §4 AC 3 states the precise limitation: removing a retired key from DMS needs a **successful** metadata refresh, or a restart that successfully loads the corrected set. AC 6 is corrected here and in §4: keeping DMS changes out is a justified scope decision. |
| 4 | The drill could pass by token expiry alone. Key counts were treated as proof of identity. There was no negative gate case. | §6 R4.4 split into: (A) bootstrap, both supported sequences; (B) normal rotation; (C) controlled early retirement, with the same unexpired old-key token valid before and rejected after; (D) negative verification-gate cases (stale CMS instance, unavailable CMS instance). Two CMS and two DMS instances. Tokens identified by their decoded `kid`. Key counts are diagnostic only. |
| 5 | The bootstrap statement was too broad. `start-local-config.ps1` starts CMS (`:176`) before `-InitDb` (`:220`), and the CMS E2E wrapper uses that path. | §2.1 and §3.1 document both supported sequences. Inserting the first key after CMS starts is an existing repository path. |
| 6 | The clock contract and validation gates were imprecise. | §3.2 now covers: (a) the scheduler is not wholly monotonic; (b) a wall step does not wake a pending timer, and behavior is defined at the next request, signal or timer wake; (c) the combined age can decrease after a forward step is reversed; (d) suspension delays cooldown and backoff, with qualified recovery bounds; Linux-only claims. §6 R4.3 separates the healthy all-200 gate from the outage gate. R4 has executable commands, identity provider settings, target databases and image provenance, and the NU1008 exception above. |
| Q | Answers to v1 §9 (Q-R1 to Q-R6) | §9, recorded as decisions |
| + | Shared-stack coordination | New gate G-STACK (§6 conventions): `dms-local`, and the CMS E2E stack that collides with it, are not stopped, rebuilt or reseeded while DMS-1440 owns them. Independent work continues meanwhile. |

AC review recorded by Codex on v1: AC 1 and AC 6 preserved; AC 2, AC 4 and AC 5 have an
acceptable validation direction; AC 3 conditional on the clock and runbook corrections. No
AC is newly demonstrated by an unimplemented spec.

### 0.2 v2 → v3 (Codex approval of v2 for R1.1 only, 2026-10-02)

**Confirmations:**

| Item | Decision |
| --- | --- |
| Drill topology | Two CMS and two DMS instances, using `docker run` clones: **accepted**. The feasibility gate stays, and the drill stops if either clone cannot run. Still conditional on the explicit G-STACK handoff. |
| Timer tests R3.2 (i)–(k) | **Cover the required cases**, with these refinements (applied): the stationary-clock test synchronizes with the scheduler's recomputation at the 0.5 ms remainder; it asserts a finite cap on status reads; it terminates cleanly when the truncation mutant spins. No observation delay, and no exact reads/waits/signals identity. |
| AC 3 wording | **Precise enough for design approval.** Compliance still depends on implementation review and the required drill results. |

**Corrections folded into R1.1:**

| # | Correction | Applied in |
| --- | --- | --- |
| 1 | `-LabelPrefix r1-catalog` fails `Invoke-ControlBatch.ps1`'s `^[a-z0-9]+$` pattern | R4.3 commands use `r1catalog`. `-BlockLabel` (`^[a-z0-9-]+$`) is unchanged. |
| 2 | The drill named the Compose project `dms-local` as its network. The Compose files use the external network `dms`. | R4.4 *Topology*: network attachments and mounts discovered by inspection; issuer and metadata routing preserved; distinct names (`dms1556-drill-cms-b`, `dms1556-drill-dms-b`) and loopback host ports; clone cleanup removes only those two containers. |
| 3 | Scenario resets and key names were undefined. A2 replaced K1, but B assumed K1 still existed. | R4.4 *Keys and state*: the order is A1 → A2 → B → C → D, carrying state forward, with keys K0–K3 named. DMS-B is stopped before A2 starts it. B begins by establishing that both DMS caches hold K1 and lack K2. The same T1/T2 tokens are used through C. A voided scenario restarts the chain from A2. |
| 4 | Remaining absolute wording | §3.1: "two independent caching layers"; new-key tokens fail "until a successful metadata refresh or a restart". §3.2: R3.1 adds no new mismatch, and the existing fractional-delay spin remains until R3.2; rounding adds under 1 ms, and execution can be later. R2.2: the token status is read for tokens that reach that stage. |
| 5 | A DLL timestamp is not proof of a fresh build | R4.2 requires build-log evidence: the plugin publish is the only error, and the E2E assembly and its references were rebuilt from the tested checkout. The timestamp is supplementary. |
| D | Jira drafts B and C | Refined locally and still unpublished. B: "successful" refresh; concurrent unknown-key requests coalesced; the failed-refresh rejection applies to the unknown-key token, and known-key behavior is unchanged. C: requires resolving the actual service graph or starting the host, because building a provider alone can miss factory-hidden dependencies. |

**AC position at approval:** AC 1 and AC 6 remain preserved. The designs and planned
evidence for AC 2–5 are acceptable. This approves the plan, not completed AC verification.

## 1. Dispositions

| # | Finding (report severity) | Verified? | Disposition | Where |
| --- | --- | --- | --- | --- |
| 1 | JWKS lags the key table, and the runbook omits DMS and other JWKS consumers (major) | **Confirmed, with the corrections in §2.1** | **Fix before merge (documentation).** Write operational procedures for: first-key bootstrap in both supported sequences, rotation, early retirement or compromise, certificate replacement, and downstream consumers. No CMS JWKS code change; no DMS change. | R2.1; required drill R4.4 |
| 2 | Snapshot age and timing gates use the adjustable wall clock (minor) | **Confirmed** | **Fix, as a separate focused phase.** Snapshot state uses the combined age, `max(wall, monotonic)`. Cooldown and backoff use monotonic time. The scheduler uses combined age for normal refresh and monotonic time for retries, with millisecond round-up. UTC timestamps stay for diagnostics. | R3.1–R3.3 |
| 3 | `CONFIGURATION.md:896` says "A request does not read the key table" (minor) | **Confirmed** | **Fix the docs:** describe the steady-state fast path and name its exceptions. | R2.2 |
| 4 | 4-argument `AddPostgresOpenIddictStores(…, JwtSettings)` cannot be resolved (minor, pre-existing) | **Confirmed as pre-existing** (same on `origin/main`; no production caller) | **Defer.** The overload and the fixture stay. The fixture proves registration idempotence, not container validity. Whether to remove or repair is left to a compatibility analysis (draft ticket C). | §3.4 |
| 5 | Request cancellation produces two authentication Error logs (nit) | **Supported by the call path** (§2.5) | **Deferred** (v1 R4 rejected, §0.1 item 1). Existing behavior kept. | §3.3 |
| 6 | The log-signal table omits the token manager's `Token key id …` line (nit) | **Confirmed**; two more category lines are missing as well (§2.6) | **Fix** with the doc corrections. | R2.2 |
| 7 | Design spec says every transition raises `AttemptStateChanged` (nit) | **Confirmed inaccurate** | **Correct the wording** with a dated note. Signaling behavior unchanged. | R2.2 |
| 8 | Compose logging guard excludes `local-config-diagnostics.yml` (nit) | **Confirmed coverage gap**; `cms-monitor` already has the cap | **Defer.** | §3.4 |
| 9 | Four settings absent from the sample `appsettings.json` (nit) | **Confirmed omission**; defaults and binding exist | **Defer** (Q-R6). | §3.4 |

**Not adopted:**

- **The report's optional JWKS hardening** ("lag capped at about 40 s"). Its bound is not
  established: an in-flight load that read old rows adds another load-timeout term, and no
  complete timing argument exists. The R2.1 verification gate makes it unnecessary for
  correctness.
- **A DMS-side unknown-key refresh.** It is the real downstream improvement, but it stays
  outside this remediation by scope decision (§4 AC 3, AC 6; draft ticket B).

## 2. Source evidence (at `9ff5a3e8f`; paths relative to `src/config` unless noted)

### 2.1 Finding 1 — JWKS publication, downstream caches, bootstrap paths

- **Issuance reads the database directly.** Every mint signs with the newest active row
  (`OpenIddictTokenManager.cs:130`, `:397`; ordered by `CreatedAt DESC`, design spec F9). An
  inserted key switches signing at once on every CMS instance.
- **JWKS serves the instance's snapshot:** `GetUsableAsync(RequestAborted)`
  (`Modules/JwksEndpointModule.cs:36`).
- **Correction to the report: a JWKS fetch can trigger a load.** `GetUsableAsync`
  (`SigningKeySnapshotProvider.cs:166-202`) behaves in three ways:
  - a *fresh* snapshot is returned at once;
  - an *overdue* snapshot is returned, and a background load is admitted (`:177-182`);
  - with *none* or an *expired* snapshot, the call starts or joins a load and awaits it
    (`:184-190`).

  The real risk remains: a fresh snapshot is served unchanged, so a fetch made shortly
  after an insert lacks the new key.
- **The 350 s figure is conditional.** `T_prop` is load timeout + refresh interval + 10 %
  jitter + load timeout (design spec §4.5). It assumes default settings, every load
  succeeding within its deadline, and the scheduler running. It is an expectation for a
  healthy instance, not something an operator can sleep on.
- **An empty JWKS is the last successful load.** `200 {"keys":[]}` means the current
  snapshot holds zero keys (`Succeeded(0)`). That is the instance's last successful load,
  possibly minutes old, not a live read. `CS-AUTH.md:315` overstates it.
- **DMS (`src/dms`, read-only here):**
  - **Startup fetch.** DMS fetches discovery and JWKS at startup (`WarmUpOidcMetadataTask`,
    Order 400, `src/dms/core/…/Startup/WarmUpOidcMetadataTask.cs:44-88`) and logs
    `… SigningKeys: {SigningKeyCount}`, a count, not key identities. A failed fetch, for
    example a CMS JWKS 503, fails startup (`DmsStartupOrchestrator.cs:93-96`).
  - **Cache.** An IdentityModel `ConfigurationManager` with `AutomaticRefreshInterval`
    (`AutomaticRefreshIntervalHours`, default 24) and `RefreshInterval`
    (`RefreshIntervalMinutes`, default 60) (`DmsCoreServiceExtensions.cs:546-558`;
    `JwtAuthenticationOptions.cs:48-53`).
  - **Refresh trigger.** `RequestRefresh()` is called only on an issuer mismatch
    (`JwtValidationService.cs:117`). A token with an unknown `kid` gets 401 and no refresh.
  - **These intervals are refresh schedules, not trust bounds.** A refresh that fails
    leaves the cached set in place. A retired key leaves DMS only after a *successful*
    refresh, or a restart that *successfully* loads the corrected set.
  - **No token-status call.** DMS validates signatures locally (`JwtValidationService.cs:100-166`),
    so CMS revocation does not reach DMS-protected requests. *Re-confirmed by citation in R2.1.*
  - **Whether DMS caches an empty set depends on ordering.** A DMS starting while CMS's
    snapshot is empty caches an empty set only if no authenticated DMS→CMS call before
    its warm-up triggered CMS's unknown-key refresh. Startup task ordering
    (`ValidateStartupInstancesTask` 310, warm-up 400, `CacheClaimSetsTask` 410) makes that
    path-dependent. The runbook does not rely on either outcome; the drill records which
    one happened (R4.4 A).
- **Two supported bootstrap sequences exist in the repository:**
  - *Key before CMS:* `eng/docker-compose/start-local-dms.ps1:986-999` runs
    `setup-openiddict.ps1 -InitDb` (key insert, `setup-openiddict.ps1:838-844`) before
    `docker compose up config`.
  - *CMS before key:* `eng/docker-compose/start-local-config.ps1:176` starts the config
    services, then `:220` runs `-InitDb`. The CMS E2E wrapper `setup-local-cms.ps1:67`
    invokes it. That is the path on which CI exposed the empty-snapshot cooldown gap
    fixed in `9a5fee6f1`/`9bf1bcad0`.

### 2.2 Finding 2 — clock basis

- **Snapshot age.** `SigningKeySnapshot.GetState` subtracts `RetrievedAt` from
  `GetUtcNow()` (`SigningKeySnapshot.cs:104-117`), and
  `It_treats_a_clock_moved_backwards_as_fresh` (`SigningKeySnapshotTests.cs:210`) pins
  this.
- **Provider gates on UTC:**
  - cooldown (`SigningKeySnapshotProvider.cs:331-335`, `_lastCompletedAt`);
  - retry eligibility (`:345`, `_nextAttemptAt` set at `:500`/`:513`);
  - state (`:152`, `:170`, `:285-286`).
- **Scheduler deadlines on UTC:** `snapshot.RetrievedAt + RefreshInterval × factor`
  (`SigningKeyRefreshService.cs:164-177`) and `status.NextAttemptAt` (`:125`, `:157`). It
  waits with `Task.Delay(startAt − now, timeProvider, …)` (`:188`).
- **Already monotonic:** the load deadline (`:372`) and late-result rejection (`:407`).
- **Only decision consumer of `NextAttemptAt`:** the scheduler.
- **Latent timer-resolution defect (Codex).** `Task.Delay(TimeSpan, TimeProvider, …)`
  truncates to whole milliseconds (runtime v10.0.1 `Task.cs`). Both jitters are fractional
  (`:168-171`; provider `:575-576`), so a deadline can fall between milliseconds. The
  timer fires at the truncated instant; the remaining sub-millisecond wait becomes an
  already-completed delay; the loop recomputes and repeats. On a real clock this busy
  loop lasts under 1 ms. On a stationary fake clock it never ends.
- **Test clocks.** All existing test clocks derive from `FakeTimeProvider` (10.3.0), whose
  wall time and timestamp move together.
- **Platform scope.** On Linux, .NET's timestamp uses `CLOCK_MONOTONIC`
  (runtime `src/native/minipal/time.c`), which excludes suspended time
  (`clock_gettime(2)`). CMS images are Linux (Alpine). This spec makes no suspension claim
  for other platforms or for particular VM pause mechanisms.

### 2.3 Finding 3

`docs/CONFIGURATION.md:896` is unqualified, but loads do run on behalf of requests:

- a cold or expired request starts or joins a load and waits (`SigningKeySnapshotProvider.cs:184-190`);
- an overdue request admits a background load (`:177-182`);
- an unknown `kid` may start a load (`:217-248`).

`CS-AUTH.md:218` lists the triggers right after its claim and needs a qualifier only.

### 2.4 Finding 4

As reported. Verified by Codex on this branch and on `origin/main`.

### 2.5 Finding 5 — cancellation call path (record only; deferred)

- **The escape.** `MessageReceivedAsync` awaits the provider with `RequestAborted`
  (`SigningKeyBearerEvents.cs:70`, `:84-87`, `:96`), and only
  `SigningKeysUnavailableException` is caught, so an abort's OCE escapes.
- **Two Error logs.** `JwtBearerHandler` (aspnetcore v10.0.1) logs
  `Exception occurred while processing message.`. Then the scheme's
  `OnAuthenticationFailed` logs a second Error (frontend `WebApplicationBuilderExtensions.cs:378-381`;
  backend `JwtAuthenticationExtensions.cs:91`), and the handler rethrows.
- **The 499.** `UseExceptionHandler` (`Program.cs:91`) turns an aborted request's OCE into
  499. `RequestLoggingMiddleware` logs it at Information. Neither protected nor anonymous
  endpoints run, because the exception terminates the request.
- **Kept as is.** That termination is the property any future cleanup must keep.

### 2.6 Finding 6 — missing log rows

| Source | Level | Message |
| --- | --- | --- |
| `OpenIddictTokenManager.cs:497-501` (introspection, revocation) | Warning | `Token key id {KeyId} was not in the signing-key snapshot; unknown-key refresh outcome: {Outcome}` |
| `Validation/EnhancedTokenValidator.cs:112` (introspection) | Error | `Token validation could not reach a decision: the {Category} is unavailable` |
| `OpenIddictTokenManager.cs:683` (revocation) | Error | `Failed to revoke token: the {Category} is unavailable` |

### 2.7 Finding 7

- **Raise the signal:** `Complete` (`SigningKeySnapshotProvider.cs:521-531`) and
  `ReleaseWhenFinishedAsync` (`:459-476`).
- **Only increment `StateVersion`:** `StartAttemptUnderLock` (`:389`) and `Dispose` (`:263`).
- **Already accurate:** the provider XML comment (`:21-23`).
- **Inaccurate:** design spec §4.4 line 270.

## 3. Design decisions

### 3.1 Finding 1 — operational procedures (R2.1)

The runbook is rewritten around **two independent caching layers**: each CMS instance's
snapshot, and each consumer's cache. The substance, not the final wording:

1. **What each layer guarantees.**
   - **CMS issuance** switches at the next mint, on every instance.
   - **CMS validation and JWKS** use that instance's snapshot.
     - A healthy instance is expected to publish a change within `T_prop`: 350 s at the
       defaults, conditional on every load succeeding within its deadline and the
       scheduler running. It is not a sleep-and-proceed guarantee.
     - A failing instance publishes the change after its first successful load following
       recovery. Its retired-key trust is bounded by `SigningKeyMaxStalenessSeconds` (§3.2).
   - **Consumers** (DMS, or any JWKS consumer) keep what they fetched.
     - DMS fetches at startup and refreshes on its own schedule (about 24 h by default). It
       does not refresh on an unknown key id.
     - Its schedule is not a trust bound: a retired key leaves a DMS instance only after a
       successful refresh, or a restart that successfully loads the corrected set.
     - CMS's bounds say nothing about consumers.
   - **Revocation** (`/connect/revoke`) affects only CMS's per-request status check. DMS
     does not consult it.
2. **The verification gate.** Before restarting or otherwise refreshing any consumer:
   - Confirm the expected `kid` in `GET {instance}{pathBase}/.well-known/jwks.json` on
     **every serving CMS instance**. The `kid` is present after an insert and absent after
     a retirement.
   - Address each instance individually: container or pod address, or a per-instance
     port-forward. Requests through a load balancer do not prove coverage.
   - `503` means that instance has no usable snapshot. Stop.
   - A connection failure means the instance is unavailable. Stop until it is serving and
     passes the check, or until it is deliberately removed from service.
   - `200 {"keys":[]}` reports that instance's last successful load, not a live read.
   - The expected `kid` is the inserted row's `KeyId` (printed by
     `Generate-OpenIddictKey-Insert.ps1`, or `SELECT "KeyId" … WHERE "IsActive"`), or the
     certificate thumbprint.
   - Restarting a CMS instance forces a startup load. It shortens the wait but does not
     replace the check, because the startup load can fail.
3. **Consumer recovery.** Waiting for CMS does not clear a consumer's cache.
   - After the gate passes, restart each DMS instance.
   - Verify acceptance **on each instance** with a token whose decoded header `kid` is the
     expected key. The warm-up line's `SigningKeys: {n}` is a count and diagnostic only.
   - A DMS instance started while CMS has no usable snapshot fails startup; start it again
     after the gate.
4. **Procedures.**
   - **First-key bootstrap, key before CMS** (the `start-local-dms.ps1` sequence). CMS's
     startup load finds the key. Pass the gate, then start DMS.
   - **First-key bootstrap, CMS before key** (the `start-local-config.ps1` / CMS E2E
     sequence).
     - Every running CMS instance first published an empty snapshot (Warning
       `Signing-key snapshot {n} is empty`).
     - After the insert, CMS issues tokens with the key at once. CMS validation accepts them
       through the once-per-instance bootstrap allowance, then the cooldown, then the
       schedule. JWKS shows the key only after that instance's next successful load.
     - Pass the gate on every instance before starting DMS. Restart any DMS instance that
       started in between, and verify acceptance on it (it may have cached an empty or
       partial set; §2.1).
   - **Rotation (add a key).**
     1. Work at a quiet time and insert the new key. Each DMS instance rejects new-key
        tokens until a successful metadata refresh or a restart loads the new set.
     2. Pass the gate.
     3. Rolling-restart DMS, verifying new-key acceptance on each instance.
     4. Restart DMS while the old key is still active, so DMS loads both keys and
        pre-rotation tokens keep validating.
     5. Keep the old key active for at least `TokenExpirationMinutes` + 5 min after the
        insert.
   - **Retirement after the retention window.**
     1. Set `IsActive = false`.
     2. Pass the gate (kid absent). An instance that cannot read the key store keeps the
        key, and its JWKS keeps listing it, for up to `SigningKeyMaxStalenessSeconds` after
        its last successful load. So the gate does not pass while such an instance serves.
     3. Restart each DMS instance and verify it rejects a token signed with the retired key.
     4. Until a successful refresh or restart, a DMS instance keeps accepting retired-key
        tokens.
   - **Early retirement / suspected compromise.**
     1. Retire the key and restart every CMS instance.
     2. Pass the gate, then restart every DMS instance.
     3. Verify on each instance that a still-unexpired token of the retired key is rejected
        and a new-key token accepted.
     4. Revoke known tokens through `/connect/revoke`, which protects CMS endpoints only.
   - **Certificate replacement (certificate mode).**
     - Issuance switches at once. CMS validation and JWKS switch at the instance's next
       load.
     - With one key there is no overlap: outstanding old-certificate tokens fail once each
       layer refreshes.
     - Work at a quiet time: replace the file, restart each CMS instance, pass the gate
       (new thumbprint), restart each DMS instance, and expect clients to re-authenticate.
5. **Rewording elsewhere.**
   - Correct the empty-JWKS text at `CS-AUTH.md:315`, in the *Empty key set* bullet and in
     the `Store holds no active key` row: an empty JWKS is the last successful load's
     result.
   - Add that a failed load is never published as empty: JWKS answers 503 with no usable
     snapshot, and otherwise serves the last good keys.
6. **Scope.**
   - No code change.
   - Design spec §4.5 history kept; a dated pointer is added.

### 3.2 Finding 2 — clock contract (R3.1–R3.3)

#### Rules

| Decision | Basis | Comparison (existing boundary preserved) |
| --- | --- | --- |
| Snapshot state Fresh / Overdue / Expired | **Combined age** `max(wallAge, monotonicAge)`: `wallAge = GetUtcNow() − RetrievedAt`, `monotonicAge = GetElapsedTime(RetrievedAtTimestamp)` | Fresh while `age ≤ RefreshInterval`; Overdue while `age ≤ MaxStaleness`; Expired after |
| Unknown-key cooldown | Monotonic, `GetElapsedTime(lastCompletedTimestamp)` | Suppressed while `elapsed < Cooldown`; allowed at equality |
| Retry eligibility (backoff) | Monotonic, `GetElapsedTime(failedAtTimestamp)` vs the drawn delay | Refused while `elapsed < delay`; eligible at equality |
| Scheduler, retry deadline | **Monotonic:** `Status.RetryDelayRemaining`, computed under the provider lock | Start when remaining ≤ 0 |
| Scheduler, normal refresh deadline | **Combined age:** `refreshAfter(version) − snapshot.GetAge(…)`, with `refreshAfter = RefreshInterval × factor` and the factor drawn once per snapshot version | Start when remaining ≤ 0 |
| Scheduler wait | `startIn = max(refreshRemaining or 0, RetryDelayRemaining)` per the existing due-time rules (never loaded → 0; last failed → retry; last succeeded → refresh) | Start iff `startIn ≤ 0`; otherwise wait `RoundUp(startIn)` or the signal |
| Load deadline, late-result rejection | Already monotonic | Unchanged |
| `RetrievedAt`, `NextAttemptAt`, `Refused.NextAttemptAt`, Debug `DueAt` | UTC, diagnostics only; `DueAt` = `now + startIn` | No decision reads them |

The scheduler is therefore **not** wholly monotonic. Its normal refresh follows the same
combined age as snapshot state, so a forward wall step can bring a refresh forward. Its
retries are monotonic only.

#### Timer resolution

- `RoundUp(t) = max(1 ms, ⌈t⌉ to the next whole millisecond)`, applied to every positive
  wait.
- **Never early.** Admission is decided only by a fresh recomputation finding
  `startIn ≤ 0`. Rounding adds less than 1 ms to the requested wait and never makes a
  start early. Actual execution can be later than the rounded instant because of timer
  and thread-pool scheduling.
- **No spin.** Every pass that does not start an attempt either waits on a pending timer of
  at least 1 ms or waits on the signal. Status reads are therefore bounded by the number of
  timers, signals and attempts.
- This replaces the v1 claim, and it also fixes the latent defect on today's UTC path
  (§2.2).

#### Wall-clock steps do not wake anything

A wall step does not wake a pending relative timer. Its effect appears only at the next
evaluation point:

- **Next request:** `GetUsableAsync` computes the combined age at that instant, and the
  existing rules apply:
  - after a forward step, Overdue → background load, or Expired → awaited load;
  - after a backward step, the monotonic age governs, and nothing changes.
- **Next signal:** the scheduler recomputes from the current status. A forward step may make
  `refreshRemaining ≤ 0`, so it starts one load, through conditional admission.
- **Next timer wake:** the same recomputation.
- **No evaluation point:** a step has no effect. A pending refresh timer fires at the
  monotonic instant computed before the step.

#### Guarantees, stated precisely

- **Age never falls below monotonic elapsed time** since publication. Trust in a snapshot
  therefore never extends past `MaxStaleness` of monotonic time, however the wall clock is
  stepped.
- **While the wall clock is not set back,** the bound also holds in wall time. The
  combined age catches suspension that the monotonic clock excludes on Linux.
- **Expiry is not latched.** After a forward step is reversed, the combined age returns to
  the monotonic age, and a snapshot that read as Expired during the step can read as usable
  again. That is within the guarantee, because its monotonic age is still
  ≤ `MaxStaleness`. Latching is not proposed: it would add state without strengthening the
  trust bound.
- **Forward steps cost availability, not security.**
  - On a healthy store: at most one early reload per step. The next snapshot's
    `RetrievedAt` is the new wall time, so the two ages agree again.
  - On a failing store: an early fail-closed 503 while the step stands.
- **Residual.** Trust can exceed `MaxStaleness` of *real* time only when both clocks
  under-count the same interval, for example a suspended Linux host whose wall clock is
  also set back. Token `exp` validation stays on the wall clock, unchanged.

#### Suspension (Linux)

Cooldown and backoff count only non-suspended time on Linux, so a delay interrupted by a
suspension still has its remaining non-suspended time to run after resume. The recovery
bounds (`R + 72 s + LoadTimeout`, and the other bounds of design spec §4.4) hold in
monotonic time. In real time they lengthen by any suspended time that overlaps the window.
The docs say so (R3.3). Normal refresh uses the combined age, so after a resume a reload is
due at the next evaluation point if wall time shows it overdue.

#### Interactions preserved (each pinned by an existing or new test)

- **Exact boundaries.** Under fake time without steps, both clocks move together, so every
  existing boundary fixture must pass unchanged.
- **Jitter.** The backoff factor is drawn per failure and the refresh factor once per
  version. A step must not redraw it.
- **Single flight and ownership.** Unchanged.
- **Late results.** Unchanged.
- **Bootstrap allowance.** Spent at refresh start, and reachable only inside the now
  monotonic cooldown.
- **Disposal and shutdown.** Unchanged.
- **Publication instant.** `RetrievedAt` and `RetrievedAtTimestamp` are both taken at
  publication under the lock, which preserves `T_prop`.
- **Same clock.** A timestamp is meaningful only with the `TimeProvider` that produced it.
  The provider and scheduler share the DI singleton, and tests must too.

#### API

- `SigningKeySnapshot` gains a `long RetrievedAtTimestamp` constructor parameter and
  `TimeSpan GetAge(TimeProvider)`.
- `GetState(DateTimeOffset, …)` becomes `GetState(TimeSpan age, …)`, which throws on a
  negative age.
- `SigningKeyProviderStatus` gains `RetryDelayRemaining`.
- `ISigningKeySnapshotProvider` is unchanged.

#### Interim safety (R3.1 → R3.2)

R3.1 changes snapshot state only. Gates and scheduler both stay on UTC and agree with each
other, so R3.1 introduces no new mismatch between them. The existing fractional-delay
spin (§2.2) remains until R3.2. R3.2 moves the gates, the scheduler and the timer
round-up together.

### 3.3 Finding 5 — deferred (record of the rejected design)

**v1's design is rejected.** It called `Fail(oce)` and then answered the challenge with
499. A `Fail` result lets the pipeline continue, and `AuthorizationMiddleware` never
challenges an anonymous endpoint. An aborted request with a bearer header would therefore
have continued into token issuance, introspection, revocation, discovery or health, which
changes endpoint execution only to remove log noise. `NoResult()` has the same continuation
and adds a false 401.

The existing behavior stays: the exception terminates the request (499), and no endpoint
runs. A future cleanup must keep termination for both protected and anonymous requests.
For example, it could filter the log for request-aborted cancellations, or keep the
rethrow and suppress only the scheme's duplicate log. It needs its own design and pipeline
tests.

### 3.4 Deferred items and follow-ups (no work here)

The drafts are local and unpublished (`…/artifacts/review-remediation-r1/jira-drafts.md`):

- **Draft B:** DMS refreshes OIDC metadata on an unknown signing key id, with a throttle
  and no last-known-good acceptance.
- **Draft C:** the `JwtSettings` overload, remove or repair, settled by compatibility
  analysis.
- **Finding 8:** the compose logging guard for diagnostics overlays. Separate work.
- **Finding 9:** sample `appsettings.json` completeness. Deferred (Q-R6).
- **Finding 5:** a future log-only cleanup under the constraint in §3.3.

## 4. Challenge against the original AC

| AC (verbatim, Jira) | Proposed final behavior | Changed here? | Position for Codex's review |
| --- | --- | --- | --- |
| 1. Establish and document the failure mechanism using a repeatable stress/regression scenario and runtime evidence. | Unchanged: G1 provisional + G1-S reproduced; H1 (investigation §0.6). | No. A new investigation section adds remediation evidence only. | Preserved (Codex, v1). |
| 2. CMS serves existing profiles reliably during the catalog-load burst within the supported CI/resource envelope; requests do not fail because authentication blocks async database work. | Fast path still lock-free and non-blocking: it adds one `GetTimestamp()` and a `max`. Healthy catalog gate re-run (R4.3). Stress exception carried. | R3 touches the per-request state check. | Validation direction accepted (Codex, v1). |
| 3. If the signing-key resolver is changed, resolve from a safe in-memory snapshot with asynchronous refresh/coalescing or equivalent design, preserving key rotation, unknown-key behavior, revocation validation, and failure semantics. Do not trade availability for accepting invalid tokens or indefinitely trusting retired keys. | **CMS:** rotation, unknown-key and revocation semantics unchanged. Retired-key trust on each instance never exceeds `MaxStaleness` of monotonic time, and wall steps cannot extend it (residual stated in §3.2). **Operations:** consumer refresh is gated on per-instance verification. **Downstream:** removing a retired key from DMS needs a successful DMS metadata refresh, or a restart that successfully loads the corrected set. DMS's refresh interval is a schedule, not a trust bound. | R2.1, R3 | **Conditional (Codex, v1),** pending these corrections and the required drill R4.4. The claim: this preserves the existing operational rotation model. Before DMS-1556, a DMS restart was also needed to pick up a new key, and the gate restores that outcome. It does **not** establish a universal downstream retirement guarantee. Keeping DMS unknown-key refresh out is a scope decision: DMS-1556 targets CMS key resolution, and the DMS change is drafted separately (B). |
| 4. Retain diagnosable dependency errors and avoid silently treating failed key retrieval as a valid empty key set. | Code unchanged. The docs state precisely what an empty JWKS means. Cancellation behavior unchanged (finding 5 deferred), so no failure can be hidden by this remediation. | R2.1 wording | Validation direction accepted. |
| 5. Add coverage for concurrent cold requests, database interruption/recovery, and signing-key rotation; rerun the affected PostgreSQL self-contained E2E shards. A single passing rerun alone does not establish the fix. | Existing coverage, plus clock-step, fractional-deadline and no-spin tests (R3). The required drill (R4.4). The key-store outage run (R4.3). Shards 1 and 2, ≥ 2 independent runs each, on rebuilt images (R4.2). | Adds tests and evidence. | Validation direction accepted. |
| 6. Keep the DMS failure-handling correction independent: preventing this CMS timeout must not be DMS's only protection against upstream 500s. | DMS-1557 stays independent and required. This remediation adds no `src/dms` change. | No. | Preserved. AC 6 requires independence from DMS-1557, not the absence of DMS changes. Excluding the DMS refresh is a scope decision (AC 3 row). |

## 5. Finding / AC → change → test matrix

| Finding | AC | Step | Change | Test / evidence (F = fails if reverted) |
| --- | --- | --- | --- | --- |
| 1 | 3, 4 | R2.1 | Procedures, empty-JWKS wording, consumer guidance | Claim-to-source table; anchors resolve; **required** drill R4.4 A–D |
| 3 | 3 | R2.2 | Fast-path wording | Claim-to-source table |
| 6 | 4 | R2.2 | Three log rows | Each row matched to its code literal |
| 7 | — | R2.2 | Dated wording correction | Code citation |
| 2 | 3, 5 | R3.1 | Combined snapshot age | R3.1 (a)–(g) (F); M16, M17 |
| 2 | 3, 5 | R3.2 | Monotonic cooldown and backoff; scheduler on combined and monotonic time; ms round-up | R3.2 (a)–(l) (F); M18–M22; M4/M5/M8/M10–M15 re-run |
| 2 | 3 | R3.3 | Clock-contract docs | Claim-to-source table |
| — | 2, 4, 5 | R4.1–R4.5 | — | Matrix, E2E, catalog and outage runtime, drill |

## 6. Phases and steps

**Conventions for every step:**

- **Edit.** Make the smallest coherent edit plus its regression coverage.
  `dotnet csharpier format` the changed C# files.
- **Check.** Run the step's checks. Review the diff for unrelated edits, races, error
  handling, authentication regressions and AC compliance.
- **Commit.** Commit locally, staging only that step's files.
- **Report:** SHA, each file and why, behavior changes, commands and results,
  skipped/blocked checks, remaining risks.
- **Stop** for user and Codex approval.
- **Approval scope.**
  - Approval of this spec authorizes **only R1.1**.
  - A rejected step is corrected and re-reviewed first.
  - A material change revises this spec, with renewed approval.
- **Mutation checks.** Applied locally and reverted, then a `--no-incremental` rebuild
  before any `--no-build` rerun. A mutation edit blocked by tooling is recorded
  **UNRUN** and never retried by another route.
- **Tooling.** Rider MCP for in-project edits, with its VCS root verified first; never
  `reformat_file`. Nothing is pushed.

**G-STACK (shared-stack coordination gate).**

- **The gate.** The DMS-1440 session currently owns the local Docker E2E stack. Do not
  stop, rebuild, reseed or recreate `dms-local`, and do not start the CMS E2E stack (its
  containers and ports collide with `dms-local`), until the user confirms a handoff.
- **Check before acting:**
  `docker ps -a --format '{{.Names}} {{.Label "com.docker.compose.project"}} {{.Status}}'`.
- **Our containers only:** `cms-pg-integration-1556` and `cms-mssql-integration-1556`.
  Other sessions' containers (`*-1440`, `*-1437`, `*-1431`) are never touched.
- **While waiting:** R2, R3 and R4.1 proceed. R4.2–R4.4 wait.

**Common commands** (worktree root, pwsh, each lane in a fresh `pwsh -NoProfile` process):

- **Backend unit:**
  `dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit`.
- **Frontend unit:** `$env:DatabaseSettings__DatabaseConnection = 'host=127.0.0.1;port=1;database=none;username=none'`,
  then
  `dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit`.
  This is CI's no-database condition, as validated for `9a5fee6f1`.
- **Format:** `dotnet csharpier check src/config`.

### Phase R1 — Spec and AC review

**R1.1 Spec approval and commit.**

- *Covers:* all findings; AC 1–6 (§4).
- *Change:* commit this file only.
- *Evidence:* the recorded approval.
- *Commit:* `[DMS-1556] R1.1 — Review remediation spec (round 1)`.
- **Stop.**

**R1.2 Jira (outside the implementation chain; Q-R5).**

- Draft A (the exact description addition) and drafts B and C are prepared locally.
- Each is published only after its content is reviewed and explicitly authorized.
- No step depends on R1.2.

### Phase R2 — Documentation corrections

**R2.1 Operational procedures (finding 1; AC 3, AC 4).**

- *Behavior:* none. Docs only, per §3.1.
- *Files:*
  - `reference/design/configuration-service/CS-AUTH.md`: *Rotating and retiring* rewritten
    per §3.1 items 1–4; the `:315` bullet, the *Empty key set* bullet and the
    `Store holds no active key` row; the bootstrap paragraph cross-referenced to both
    sequences.
  - `docs/CONFIGURATION.md`: a cross-reference.
  - `docs/OWASP-AUTH-COVERAGE.md`: the retirement qualifier names consumer caches and the
    successful-refresh condition, if its text implies otherwise.
  - Design spec: a dated pointer at §4.5.
  - `reference/design/configuration-service/README.md`: a link to this addendum.
- *Exclusions:* all code; JWKS hardening; DMS refresh.
- *Risk:* instructions that do not match behavior. Mitigated by the claim-to-source table
  and the required drill R4.4.
- *Checks:*
  - every operational claim mapped to `file:line`, with §2.1's DMS claims re-cited
    (including no token-status call);
  - internal anchors resolve;
  - `git diff --stat` shows only these files.
- *Commit:* `[DMS-1556] R2.1 — Rotation, bootstrap, retirement and consumer-refresh procedures`.
- **Stop.**

**R2.2 Wording and log signals (findings 3, 6, 7).**

- *Files:*
  - `docs/CONFIGURATION.md:896`: "In steady state a request does not read the key table",
    followed by the exceptions:
    - a cold or expired request starts or joins a load and waits for it, bounded by
      `SigningKeyLoadTimeoutSeconds`;
    - an overdue request starts a background load;
    - an unknown key id may start a load, subject to cooldown and backoff;
    - the token status is still read, uncached, for every token that reaches the
      status-validation stage.
  - `CS-AUTH.md:218`: the same qualifier.
  - `CS-AUTH.md` *Log signals*: the three rows of §2.6, plus the alerting note
    (`was not in the signing-key snapshot` matches both unknown-key lines).
  - Design spec §4.4 line 270, a dated correction: completed attempts and released
    operations raise `AttemptStateChanged`; every transition, including attempt start and
    disposal, changes `StateVersion`.
- *Checks:* as R2.1, plus each log row compared to its code literal.
- *Commit:* `[DMS-1556] R2.2 — Fast-path wording, log signals, AttemptStateChanged wording`.
- **Stop.**

### Phase R3 — Clock contract

**R3.1 Combined snapshot age (finding 2; AC 3, AC 5).**

- *Behavior:* snapshot state uses `max(wallAge, monotonicAge)`. Gates and scheduler are
  unchanged.
- *Files:*
  - backend:
    - `SigningKeys/SigningKeySnapshot.cs`;
    - `SigningKeys/SigningKeySnapshotProvider.cs` (`Classify` stamps both; `StateOf`
      uses the age).
  - backend tests:
    - `SigningKeySnapshotTests.cs`;
    - `SigningKeysUnavailableExceptionTests.cs`;
    - `SigningKeyTestSupport.cs` (new `SkewableTimeProvider`);
    - a new provider fixture.
  - frontend: `BearerPipelineHost.cs` (registers the skewable clock over its fake), plus
    one pipeline fixture.
- *Test clock:* `SkewableTimeProvider` **wraps** a `FakeTimeProvider`:
  - `GetUtcNow()` returns the inner value + `WallOffset`;
  - `GetTimestamp`, `TimestampFrequency` and `CreateTimer` delegate unchanged;
  - `StepWallClock(TimeSpan)` moves wall time only;
  - `Advance` moves both.

  A self-test pins that a step moves neither `GetTimestamp` nor pending timers.
- *Tests (new; F):*
  - **(a) Backward step, failing store.** Store failing after one load; step back 2 h.
    Monotonic exactly `MaxStaleness`: served. One tick more:
    `SigningKeysUnavailableException(SnapshotExpired)`.
  - **(b) Backward step, overdue.** Step back after `RefreshInterval` elapsed: Overdue,
    one background load admitted.
  - **(c) Forward step past `MaxStaleness`, failing store:** Expired at the next request.
    Healthy store: one awaited load, served.
  - **(d) Forward then backward, failing store.** Step +2 h: the next request gets
    Expired/unavailable. Step −2 h: the next request is served again, monotonic age
    ≤ `MaxStaleness`, which pins the non-latching contract.
  - **(e) A step alone changes nothing.** A step with no request leaves `Status`
    unchanged in load count and version; only the state read at the next evaluation
    reflects it.
  - **(f) Unit.** A negative age throws. The boundaries at exactly `RefreshInterval` and
    `MaxStaleness` hold whichever clock dominates.
  - **(g) Pipeline.** A retired-key token with the key store failing gets 503 once
    monotonic time passes `MaxStaleness`, despite a 2 h backward step.
  - **(C)** All existing boundary fixtures pass unchanged.
- *Mutants:*
  - M16 (wall-only age) must fail (a), (b) and (g).
  - M17 (monotonic-only age) must fail (c).
- *Exclusions:* the gates and the scheduler (R3.2). Interim safety: §3.2.
- *Risk:* the constructor ripple (five test sites); mixed clocks in tests.
- *Commands:* backend and frontend unit suites; CSharpier.
- *Expected:* zero failures. Before editing, record a baseline run of both unit suites on
  `9ff5a3e8f`; the counts afterwards must exceed it by exactly the new tests.
- *Commit:* `[DMS-1556] R3.1 — Snapshot age from the larger of wall and monotonic time`.
- **Stop.**

**R3.2 Monotonic gates, scheduler deadlines, timer round-up (finding 2; AC 3, AC 5).**

- *Behavior:* §3.2 *Rules* and *Timer resolution*.
- *Files:*
  - backend:
    - `SigningKeySnapshotProvider.cs`;
    - `SigningKeyProviderStatus.cs`;
    - `SigningKeyRefreshService.cs`.
  - backend tests:
    - `SigningKeySnapshotProviderTests.cs`;
    - `SigningKeyRefreshServiceTests.cs`;
    - `SigningKeyProviderStatusTests.cs`;
    - `SigningKeyTestSupport.cs` (wait recording in elapsed terms, plus the due time of
      every created timer).
- *Tests (new; F):*
  - **Cooldown and bootstrap allowance:**
    - **(a)** Cooldown under ±2 h steps: suppressed one tick before the monotonic
      cooldown, allowed at exactly it.
    - **(b)** A forward step does not open the cooldown.
    - **(c)** Bootstrap allowance under a step: one refresh inside the cooldown, then
      suppressed.
  - **Retry gate:**
    - **(d)** Under ±2 h steps: `Refused(RetryDelay)` before the monotonic backoff,
      admitted at exactly it.
  - **Scheduler deadlines under wall steps:**
    - **(e) Failure, then a backward step:** the service starts at the monotonic retry
      deadline. Remaining = 0 starts (1.6-h analogue).
    - **(f) Success, then a backward step:** the next load starts after `refreshAfter` of
      monotonic time, and the factor is not redrawn.
    - **(g) Forward step while the scheduler is parked on its refresh timer:** no wake, no
      status read, no load at the step. Then either:
      - (g1) a request arrives: Expired, awaited load, publication, and the signal moves
        the service to the new snapshot's deadline;
      - (g2) no request; monotonic time reaches the pending timer: the service recomputes
        and starts exactly one load, then normal cadence.
    - **(h) Forward step during a backoff:** no early start.
  - **Timer resolution (fractional deadlines):**
    - **Harness for (i)–(k).** These tests use no observation delays, and they assume no
      exact identity between reads, waits and signals.
      - The `ObservedSnapshotProvider` decorator counts status reads. When the count
        passes a fixed **cap** while the fake clock has not moved, it completes a
        `SpinDetected` signal and cancels the hosted service's stopping token. A spinning
        mutant therefore terminates cleanly instead of hanging the run.
      - The `SchedulerTimeProvider` completes a `Parked` signal, with the due time, each
        time the service creates a scheduler wait timer.
      - Each test awaits `Task.WhenAny(Parked, SpinDetected)`. It asserts which one
        completed, and asserts reads ≤ cap.
      - A fixture-level timeout exists only as a hang guard. It is never the assertion.
    - **(i) Fractional refresh deadline.** `FixedRandom` gives `refreshAfter` a
      sub-millisecond fraction (for example 300 s × 1.0000012).
      - Every created wait timer has a whole-millisecond due time of at least 1 ms.
      - Advancing to one tick before the deadline: `Parked` again, no load.
      - Advancing to the rounded instant: exactly one load starts.
      - Status reads stay within the cap throughout.
    - **(j) Stationary clock, sub-millisecond remainder.**
      1. Advance the fake clock so that 0.5 ms remains, then hold it.
      2. The test awaits the `Parked` signal of the **recomputation at that remainder**,
         identified by a wait created after the advance with a due time of 1 ms. That is
         the synchronization point.
      3. It then asserts that the reads are within the cap, `SpinDetected` has not
         completed, and no load has started.
      4. Advancing 1 ms admits exactly one load.

      Under M22 (truncation), the recomputation at 0.5 ms never parks. `SpinDetected`
      completes at the cap, the service stops, and the test fails on that assertion.
    - **(k)** The same as (i) for a fractional backoff (retry path).
  - **(l) Regression of today's defect.** On the pre-change code, (j) reaches
    `SpinDetected`. M22 below shows this.
  - **(C)** All 1.5 and 1.6 fixtures and pipeline 3.1-n/q pass unchanged.
- *Mutants:*
  - M18 (wall-clock cooldown) must fail (a) and (b).
  - M19 (wall-clock retry gate) must fail (d).
  - M20 (scheduler retry deadline from UTC `NextAttemptAt`) must fail (e).
  - M21 (scheduler refresh deadline from UTC `RetrievedAt`) must fail (f).
  - M22 (round-up removed, truncating delay) must fail (i) and (j) through `SpinDetected`
    and the read cap, and terminate cleanly.
  - Re-run M4, M5, M8a/b, M10a/b and M11–M15.
- *Risk:* scheduler spin or missed wake (guarded by (g)–(k), the bounded-read assertions
  and the re-run mutants). Rounding delays a start by less than 1 ms; this is accepted.
- *Commands:* as R3.1.
- *Commit:* `[DMS-1556] R3.2 — Monotonic cooldown and backoff, scheduler deadlines, timer round-up`.
- **Stop.**

**R3.3 Clock-contract documentation (finding 2).**

- *Files:*
  - `CS-AUTH.md`, *Refresh, backoff, cooldown and staleness*: the rules table in prose;
    combined age; non-latching; wall steps take effect at the next evaluation point; the
    Linux suspension consequence and the qualified recovery bounds; the residual; token
    `exp` still on the wall clock.
  - `docs/CONFIGURATION.md`, the `MaxStaleness` and cooldown rows.
  - Design spec §4.4/§4.5: a dated note.
- *Checks:* the claim-to-source table; platform claims limited to Linux.
- *Commit:* `[DMS-1556] R3.3 — Document the clock contract`.
- **Stop.**

### Phase R4 — Final regression validation and push readiness

Every R4 result is recorded against the **tested commit**: the final remediation HEAD, with
a clean `git status` at build time.

**Image provenance (R4.2–R4.4).**

- Build images only from that commit, after a teardown, with no `-SkipDockerBuild` after a
  teardown.
- Record for each lane:
  - `git rev-parse HEAD`;
  - `docker image inspect --format '{{.Id}} {{.Created}}'` for `ed-fi-api-config-local`
    and `ed-fi-api-local`;
  - the running containers' image ids (`docker inspect --format '{{.Image}}'`).
- Tag the catalog image `ed-fi-api-config-local:dms1556-<sha>`.
- A lane whose running image id differs from the recorded build is void.

**R4.1 Automated matrix (no G-STACK dependency).**

- **Unit:** backend and frontend (common commands).
- **PostgreSQL integration.**
  - Start the container: `docker start cms-pg-integration-1556` (PostgreSQL 16, trust
    auth, database `edfi_configurationservice`, on `127.0.0.1:5437`).
  - Build: `dotnet build src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration`.
  - Verify that the gitignored override
    `…Postgresql.Tests.Integration/bin/Debug/net10.0/appsettings.Test.json` reads
    `host=127.0.0.1;port=5437`. A `--no-incremental` build replaces it, so restore it if
    needed.
  - Run: `dotnet test … --no-build`.
  - Verify that mid-run `pg_stat_activity` shows the suite on 5437.
- **SQL Server integration.**
  - Start: `docker start cms-mssql-integration-1556` (SQL Server 2025, `127.0.0.1,14335`).
  - Set `$env:ConnectionStrings__MssqlAdmin = 'Server=127.0.0.1,14335;User Id=sa;Password=<recorded>;TrustServerCertificate=true'`.
  - Run: `dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration`.
  - Zero skips is required, as proof that the variable reached the test process.
- **Format:** `dotnet csharpier check src/config`. PowerShell analysis only if a `.ps1`
  changed (none planned).
- *Expected:*
  - unit lanes and PostgreSQL ≥ 959 and SQL Server ≥ 979 passed, 0 failed;
  - `[Explicit]` DMS-1437 probes `NotExecuted` (62 PostgreSQL / 66 SQL Server), enumerated;
  - an environment failure is reported as environment, never as a pass.
- **Stop** on any failure.

**R4.2 E2E on rebuilt images (G-STACK).**

- **Builds:**
  - `./build-config.ps1 Build -Configuration Release` must succeed.
  - `./build-dms.ps1 Build -Configuration Release`: the recorded NU1008 exception applies.
    Accept exit 1 only if all of these hold:
    - the captured build log shows the `Acme.CustomValidationProof` plugin-fixture publish
      (NU1008/MSB3073) as the **only** error;
    - the same log shows `EdFi.DataManagementService.Tests.E2E` and the projects it
      references building successfully (`-> …\bin\Release\net10.0\…dll` lines) from this
      checkout, at the tested commit;
    - `git status` is clean afterwards.

    The assembly timestamp is supplementary evidence only. Revert any lock-file churn.
    Without that build-log evidence, the lane does not start.
- **CMS E2E**, once, in a fresh process:
  - `./build-config.ps1 E2ETest -Configuration Release -IdentityProvider self-contained`,
    with the default `.env.config.e2e`: PostgreSQL datastore, CMS database per that file.
  - Confirm `EdFi.DmsConfigurationService.Tests.E2E.dll` in the assemblies list and a
    totals line.
  - This lane runs the **CMS-before-key** bootstrap (`start-local-config.ps1`) and covers
    issuance and validation. No CMS E2E scenario covers rotation, so rotation evidence is
    the PostgreSQL integration 3.4-b/c and the drill.
  - Expected baseline: 235 passed, 0 failed, 10 `@MultitenantOnly` skips.
  - Then `teardown-local-cms.ps1`.
- **DMS shards 1 and 2,** ≥ 2 independent runs each, with
  `pwsh ./teardown-local-dms.ps1` (from `src/dms/tests/EdFi.DataManagementService.Tests.E2E`)
  between runs:
  `./build-dms.ps1 E2ETest -Configuration Release -IdentityProvider self-contained -EnvironmentFile './.env.e2e' -TestFilter 'Category=@e2e-ci-shard-1'`
  (and `@e2e-ci-shard-2`).
  - Database `edfi_datamanagementservice_e2e` on host 5435.
  - Verify the settings with
    `docker inspect ed-fi-api --format '{{range .Config.Env}}{{println .}}{{end}}'`:
    `AppSettings__Datastore=postgresql`, and for CMS `AppSettings__IdentityProvider=self-contained`.
  - Expected baseline: shard 1 230/0/2, shard 2 220/0/2. Counts are read from the trx
    `<Counters>`.

**R4.3 Catalog and outage runtime (G-STACK; Q-R4).** From `eng/performance/dms-1556` on a
`setup-local-dms.ps1 -EnvironmentFile ./.env.e2e` stack rebuilt from the tested commit:

```powershell
./Set-Dms1556StackCondition.ps1 -Condition baseline -ResourceProfile p-runner-approx -RecreateDb
./Invoke-CmsProfileBurst.ps1 -Seed -ProfileCount 87
./Invoke-ControlBatch.ps1 -Condition baseline -BlockLabel r1-cold -LabelPrefix r1catalog -ResourceProfile p-runner-approx -Workloads cold-87x87 -Repetitions 3 -DumpOncePerBlock
./Invoke-ControlBatch.ps1 -Condition baseline -BlockLabel r1-warm -LabelPrefix r1catalog -ResourceProfile p-runner-approx -Workloads warm-87x87 -Repetitions 1 -DumpOncePerBlock
./Invoke-Step41Outage.ps1 -Scenarios keylock-cold -ResourceProfile p-runner-approx -Repetitions 1
```

The two gates are separate and are never combined:

- **Healthy catalog gate** (the four catalog runs, 5 rounds each, 1,740 responses):
  - every response is 200 with the expected `id`, `name` and `definition`;
  - no non-200 of any kind;
  - the §4.9 dependency signals are zero;
  - no PostgreSQL `53300`;
  - sampler coverage has zero failures;
  - `Worker Min Limit` is recorded.
- **Injected-outage gate** (the `keylock-cold` run): categorized dependency failures are
  *expected*.
  - While the key table is locked across the CMS restart, protected requests answer 503
    with category `SigningKeyStore` and `Retry-After`, and JWKS answers 503. Never a
    500, never `200 {"keys":[]}`, never an unclassified non-200.
  - The provider's load-failure Errors appear with backoff.
  - After the lock is released at `R`, the first successful load completes within the
    documented bound (`R + 72 s + LoadTimeout`, plus `LoadTimeout` if a load was in
    flight at `R`). After that every response is 200.
  - Stage classification comes from the script's correlation report.
- The stress exception stays as recorded and is not rerun.
- Docker log rotation can truncate Debug CMS logs. A run whose log does not cover its
  window is void and repeated, never counted.

**R4.4 Runbook drill — required evidence (G-STACK).**

- **Purpose.** Execute the R2.1 text exactly, on a `dms-local` stack rebuilt from the tested
  commit, without the diagnostics overlays.
- **Topology:** two CMS and two DMS instances in the `dms-local` Compose project, sharing
  one database.
  - **Discovery first, by inspection.** Read the network attachments and mounts with
    `docker inspect <container> --format '{{json .NetworkSettings.Networks}} {{json .Mounts}}'`.
    The Compose files declare the external network **`dms`**, but the running containers
    are the authority, not this text. Clones attach to the network those containers
    actually use and replicate their required mounts.
  - **CMS-A:** the Compose `ed-fi-api-config-service`.
  - **CMS-B:** clone `dms1556-drill-cms-b`, started with `docker run`.
    - It uses CMS-A's image id and CMS-A's environment, exported from `docker inspect`
      minus container-specific variables such as `HOSTNAME`.
    - It joins the same network, with the same required mounts.
    - It publishes a distinct loopback host port: `-p 127.0.0.1:<port>:<container port>`.
    - The issuer settings (`IdentitySettings__Authority` and the related keys) stay
      unchanged, so it issues and validates exactly like CMS-A.
  - **DMS-A:** the Compose `ed-fi-api`.
  - **DMS-B:** clone `dms1556-drill-dms-b`, built the same way from DMS-A, with a distinct
    loopback host port.
    - `JwtAuthentication` Authority, MetadataAddress and the Configuration Service URL stay
      unchanged, so DMS-B follows the same issuer and metadata routing as DMS-A, through
      CMS-A.
  - **Feasibility check:** run it before any scenario. Both clones must start and answer
    `/health` 200, and DMS-B's warm-up must succeed.
    - If either clone cannot run, the drill stops and the topology returns to review.
    - A single instance is never substituted.
- **Tokens.**
  - Clients: create a vendor and application through CMS's admin API, using the setup's
    CMS client. This yields a DMS client.
  - Minting: `POST /connect/token`.
  - Identification: decode each token's header `kid` and payload `iat`/`exp`, and record
    them.
  - Checks: a **CMS token** (CMS admin client) goes to a CMS protected GET. A **DMS token**
    (DMS client) goes to a DMS protected GET.
  - Outcomes: acceptance is HTTP 200; rejection is 401 with `WWW-Authenticate`.
- **Evidence.**
  - A transcript of every command and response: JWKS bodies per instance, logs, and the
    `docker inspect` image ids of all four instances.
  - The script is kept in the gitignored artifacts with the evidence, not committed.

**Keys and state.** The scenarios run once, **in order A1 → A2 → B → C → D**. Each one
carries forward the key state the previous one left; none starts independently. Key names:

| Key | Introduced | State afterwards |
| --- | --- | --- |
| K0 | by the standard setup, before CMS starts | deleted in A2 |
| K1 | inserted in A2, after CMS started | the baseline for B; retired in C |
| K2 | inserted in B | active from B on |
| K3 | inserted in D1 | active at the end |

**Restarting the chain.** If any scenario is voided, the chain restarts from A2 with fresh
keys. A2 deletes all key rows. There is no partial resumption.

Scenarios:

- **A1. Bootstrap, key before CMS** (state: K0 only).
  - Gate on CMS-A and CMS-B: K0 present on both.
  - A K0 DMS token gets 200 on DMS-A and DMS-B.
- **A2. Bootstrap, CMS before key.**
  1. **Stop DMS-B.**
  2. Delete every key row, then restart CMS-A and CMS-B. Each logs the empty-snapshot
     Warning, and both JWKS answer `200 {"keys":[]}`.
  3. Insert K1.
  4. Before any CMS validation request, record that both JWKS still lack K1. This shows
     that an empty JWKS is the last successful load, not a live read.
  5. **Start DMS-B.** Record its warm-up count, and whether a K1 DMS token is accepted
     there. This is observational only (§2.1 ordering).
  6. Gate: K1 on both CMS instances. Restart DMS-A and DMS-B. A K1 DMS token gets 200 on
     each.
- **B. Normal rotation** (state at entry: K1 only).
  1. **Baseline check.**
     - Both DMS instances were last restarted in A2.6, and no other key has existed since.
     - A K1 DMS token gets 200 on both.

     So both caches hold K1, and neither can hold K2.
  2. **T1.** Mint T1-CMS and T1-DMS, both carrying `kid` K1, and record `exp(T1)`. These
     same tokens are reused through C.
  3. **Insert K2, mint T2.** Mint T2-CMS and T2-DMS (`kid` K2). Before the gate, T2-DMS
     gets 401 on **both** DMS instances, which confirms that neither cache holds K2.
  4. **Gate, then restart only DMS-A.** Gate on both CMS instances (K2 present, by restart
     or by waiting), then restart **only DMS-A**. T2-DMS gets 200 on DMS-A and 401 on
     DMS-B: refresh is per instance.
  5. **Restart DMS-B.** T2-DMS now gets 200 there.
  6. **Old key retained.** T1-CMS and T1-DMS still get 200 on every instance.
  7. Startup key counts are recorded as diagnostic only.
- **C. Controlled early retirement / compromise** (state at entry: K1 and K2 active; the
  **same** T1 and T2 tokens).
  - **Timing.** B2 through C3 must finish inside T1's lifetime (`TokenExpirationMinutes`,
    default 30). If they cannot, C is void and the chain restarts.
  1. **Immediately before retirement:** T1-CMS gets 200 on CMS-A and CMS-B, and T1-DMS gets
     200 on DMS-A and DMS-B. Record the time, with `now < exp(T1)`.
  2. **Retire K1.** Set `IsActive = false` on K1. Restart both CMS instances and gate (K1
     absent on both). Restart both DMS instances.
  3. **The same tokens again:**
     - T1-CMS gets 401 on both CMS instances, and T1-DMS gets 401 on both DMS instances.
     - At each rejection, record `now < exp(T1)` and its margin. That proves the rejection
       comes from retirement, not expiry.
     - T2-CMS and T2-DMS get 200 on every instance.
- **D. Negative verification-gate cases** (state at entry: K2 only).
  - **D1, stale instance.**
    1. Recreate the CMS-B clone with `IdentitySettings__SigningKeyRefreshIntervalSeconds=43200`
       and `IdentitySettings__SigningKeyMaxStalenessSeconds=86400`. Its startup load holds
       K2.
    2. Send it no tokens, so no unknown-key refresh can occur.
    3. Insert K3 and restart only CMS-A.
    4. Gate check: K3 is present on CMS-A and **absent on CMS-B**, so the gate fails and
       the procedure stops. Checking CMS-A alone, as repeated requests behind a load
       balancer may, would have passed.
    5. Restart CMS-B. The gate now passes.
  - **D2, unavailable instance.**
    1. `docker stop dms1556-drill-cms-b`.
    2. The gate check gets a connection failure, and the procedure stops.
    3. `docker start` it. After its startup load, the gate passes.
  - In both cases, no DMS instance is restarted while the gate fails.
- **Clone cleanup.**
  - Remove only the drill's clones: `docker rm -f dms1556-drill-cms-b dms1556-drill-dms-b`.
  - Remove no other container, network or volume.
  - Teardown of the Compose stack follows G-STACK and the handoff, and is reported.

**R4.5 Record and push-readiness review.**

- **Record.** A new investigation section, *Review remediation, round 1*: the tested
  commit, images, every R4 result, the final AC matrix, and the carried-forward exceptions
  restated.
- **Commit:** docs only.
- **Jira.** New raw captures go into the Jira evidence bundle only after their contents
  are reviewed and authorized (R1.2 rules).
- **Report.** Commits and files, test evidence, remaining limitations.
- **Stop for explicit push approval. Nothing is pushed automatically.**
- **After a push:** new CI runs on the new head, never reruns of old runs; a PR description
  section *Review round 1 remediation*.

## 7. New mutation checks

| M | Mutation | Must fail |
| --- | --- | --- |
| M16 | Snapshot age from the wall clock only | R3.1 (a), (b), (g) |
| M17 | Snapshot age from the monotonic clock only | R3.1 (c) |
| M18 | Cooldown on the wall clock | R3.2 (a), (b) |
| M19 | Retry gate on the wall clock | R3.2 (d) |
| M20 | Scheduler retry deadline from UTC `NextAttemptAt` | R3.2 (e) |
| M21 | Scheduler refresh deadline from UTC `RetrievedAt` | R3.2 (f) |
| M22 | Timer round-up removed (truncating delay) | R3.2 (i), (j) |

## 8. Risks

- **Clock contract.** The both-clocks-under-count residual is documented, not eliminated.
  Forward steps can cause early 503s during a key-store outage. Expiry is not latched (by
  design).
- **Runbook burden.** Operators must address CMS instances individually and restart DMS.
  Retirement at DMS depends on a successful refresh or restart. This is the honest cost
  until draft B lands.
- **Drill feasibility.** Cloning CMS and DMS containers may need adjustments, such as port
  mappings or environment. A non-runnable topology returns to review rather than shrinking
  to one instance.
- **Shared stack.** G-STACK can delay R4.2–R4.4. Independent steps proceed.
- **Environment.** Known local issues: Debug-log rotation, host memory pressure, the
  `localhost` / `127.0.0.1` stall, and the NU1008 build exception. Each is classified as
  environment, never as a pass.

## 9. Decisions (answers to v1 §9, recorded 2026-10-02)

| Q | Decision |
| --- | --- |
| Q-R1 clock basis | Combined snapshot age accepted in principle, with the §0.1 item 6 corrections; monotonic cooldown and backoff. |
| Q-R2 cancellation | R4 (v1) deferred; anonymous-endpoint continuation not accepted. |
| Q-R3 follow-ups | Both tickets drafted locally (B, C). For the overload, removal versus repair is left to compatibility analysis. No Jira creation. |
| Q-R4 runtime scope | 3 cold + 1 warm catalog runs and one key-store outage run accepted. The corrected drill is **required** evidence. |
| Q-R5 Jira records | Exact description change prepared locally (draft A). Publish changes or attachments only after content review and explicit authorization; outside the implementation chain. |
| Q-R6 sample settings | Deferred. |

The three confirmations requested in v2 were given at approval (§0.2). Nothing is open.
The next step is R2.1, and it starts only after review of the R1.1 commit and explicit
approval.
