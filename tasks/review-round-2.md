# Review Round 2 — DMS-1457 / FR-LOG-3..6

Team lead record per `tasks/plan.md` §9.2 Step 4.

- **Branch point:** `5202c06` · **Round 1:** `7835227` · **Round 1 record:** `29c02e3` · **Round 2:** `be941dd`
- **Roles:** fresh implementation sub-agent; fresh read-only functionality reviewer; fresh
  read-only clean-code reviewer (a different agent). Both reviewers were tool-restricted to
  read-only and both read `tasks/declined-findings.md` (D-1..D-12) first.

## Headline

Round 2 closed all 14 round-1 corrections — both reviewers independently confirmed every
one. But the functionality reviewer found that **round 1's FR-LOG-6 sweep stopped at the
frontend assembly**, and that is a genuine requirement violation. I verified it firsthand
and rate it **Critical**.

---

# Raw report 1 — Functionality reviewer (round 2)

**Verification:** `csharpier check` clean (2779 files); `Frontend.AspNetCore.Tests.Unit`
**553 passed / 0 failed**; `Core.Tests.Unit` 5091 passed / 6 failed (the known ApiSchema
stand-in artifacts); `Backend.Tests.Unit` 3785 / 0; `Core.Tests.Unit --filter
~LoggingSanitizer` 17/17.

## Per-FR verdict

| FR | Verdict | Basis |
| --- | --- | --- |
| **FR-LOG-3** | **partially met** | Allowlist correctly a bare `!char.IsControl(c)` (`LogSanitizer.cs:162`), surfaced `LoggingSanitizer.cs:41`, applied `CorrelationIdNormalizer.cs:77`, strict path untouched. **But** "SHALL NOT be conflated with any stricter allowlist … such as the request method or path" is violated at 32 log sites — F-1. |
| **FR-LOG-4** | **met** | Cap `CorrelationIdNormalizer.cs:56-75`; default `AppSettings.cs:14`; override `:29`; startup rejection `:79-83`; documented `docs/CONFIGURATION.md:22`. Tests `CorrelationIdNormalizerTests.cs:116`, `ExtractTraceIdFromTests.cs:152`, `:216`. |
| **FR-LOG-5** | **met** | No reject/throw path in `CorrelationIdNormalizer.cs:49-78`. Tests `CorrelationIdParityTests.cs:212` (now genuinely differential), `CorrelationIdNormalizerTests.cs:141`, `:187`. |
| **FR-LOG-6** | **partially met** | Every **error response body** correct; every **frontend** log entry correct. **Not** met for "every log entry" in Core and Backend — F-1. No test captures a Core-layer log event, which is why it survived. |

## §6 edge cases
Cases 1–6, 8, 10 **real**. Case 7 real at the normalizer; ingestion point now covers
control-chars-only (`ExtractTraceIdFromTests.cs:246`) but **not** header-present-with-empty-value
(F-3). Case 9 **real for the frontend layer only** — all four status codes are produced
*before* the request reaches the Core pipeline, so no Core-layer log record is ever captured;
that blind spot is the direct cause of F-1 going undetected. Round 1's vacuous same-file-constant
assertion was confirmed deleted. Case 10 is now genuinely differential (four statuses compared
against a clean-ID control arm sent at `:156-179`).

## Round 2's three riskiest changes — verified correct
- **`ExtractTraceIdFrom` restructure**: all six paths walked and correct (header absent; present-but-empty; present-and-clean; present-all-control-chars; setting empty; `TraceIdentifier` itself normalizing to empty). No regression vs `5202c06` — the old `!IsNullOrEmpty(correlationId)` guard on the `StringValues`→`string?` conversion is exactly equivalent to the new `clientSupplied.Length > 0`. Multi-valued header behavior unchanged.
- **Surrogate drop**: correct on all three boundary cases. Valid pair ending exactly at the boundary → `value[retained-1]` is the *low* half, `IsHighSurrogate` false, pair preserved. Lone low surrogate → preserved, harmless. Length 1 → `value.Length > effectiveMaxLength` cannot fire; `retained - 1 >= 0` always holds, so no `IndexOutOfRange`. Idempotence preserved: the code can never *create* a trailing orphan.
- **`Normalize` called twice on the control-only path**: reviewer agrees with the lead; no failure scenario constructible. Off the hot path, pure and idempotent, and the alternative reintroduces the duplication R1-10 removed.

## Findings

### F-1 — **Critical** — 32 production log sites push the correlation ID through the *strict* `Method`/`Path` allowlist, so Core- and Backend-layer log entries carry a different value than the response body for the same request
Root site `Core/Middleware/RequestResponseLoggingMiddleware.cs:32`. Clearest single-function
proof: `Core/Middleware/ResolveMappingSetMiddleware.cs:43` vs `:56`.

`FrontendRequest.TraceId.Value` is already correctly normalized (AD-1 holds), but 32 sites
re-sanitize it with `LoggingSanitizer.SanitizeForLogging` — the strict
`!IsControl && (IsLetterOrDigit || ' ' '_' '-' '.' ':' '/' '\')` allowlist — and log *that*.

**Failure scenario.** Host defaults, log level Information. Client sends `GET /ed-fi/students`
with `x-correlation-id: 3f2b+aQ==/{svc}` (a base-64-ish upstream ID — FR-LOG-3's stated
motivating case). Deployment is missing the relational compiler, so `ResolveMappingSetMiddleware`
answers 503:
- `:56` → 503 body: `"correlationId": "3f2b+aQ==/{svc}"`
- `:40-43` → `LogError`, 13 lines earlier in the same method: `TraceId: 3f2baQ/svc`
- `RequestResponseLoggingMiddleware.cs:109` → Information-level `HttpRequestCompleted`, `RequestLayer=Core`, `TraceId=3f2baQ/svc`
- `RequestResponseLoggingMiddleware.cs:38` → the Core scope stamps `TraceId=3f2baQ/svc` on *every* Core log event for the request
- Frontend `LoggingMiddleware.cs:56` → `TraceId=3f2b+aQ==/{svc}` — correct

The operator copies `3f2b+aQ==/{svc}` from the client's 503, finds the Frontend record, and
does **not** find the Error record saying why.

Why it is an FR violation, not style: FR-LOG-6 requires the adjustment "applied identically
everywhere … **every log entry** … for every failed request, **not only a subset of failure
types**" — parity currently holds for frontend-produced failures (404/401/403/429/500) and
breaks for Core/Backend-produced ones (400 validation, 403 authorization, 409, 503,
500-unknown-backend), i.e. the majority of real failures. FR-LOG-3 forbids conflation with the
`Method`/`Path` allowlist, and these sites literally do that. `docs/LOGGING.md:67-71`, shipped
in this diff, claims normalization for the `TraceId` field in **both** layers, and `:158-159`
documents a `TraceId`+`RequestLayer` workflow that silently fails for any punctuation-bearing ID.

Corroboration that this is an incomplete sweep, not a convention: `Handler/Utility.cs:133` and
`Response/SecurityConfigurationFailureLogger.cs:75` already log `TraceId.Value` **unsanitized**
beside strictly-sanitized `Path`; `LoggingMiddleware.cs:47-49`'s new comment says the frontend
"deliberately applies no second, differently-shaped normalization" while the Core middleware does
exactly that; and **`RequestResponseLoggingMiddlewareTests.cs:247,258` still assert
`"traceidwithunsafe"` and still pass** — the exact Core-layer analogue of the frontend tests
filed as R1-02. Not covered by D-2 (opposite class), D-5 (duplicate *definitions*), or D-7
(narrowing the allowlist).

**Fix:** at the 32 sites, swap to `LoggingSanitizer.SanitizeCorrelationIdForLogging` — idempotent,
cannot diverge from the ingestion value, keeps the CodeQL sanitizer marker. Test blast radius:
2 assertions.

Reviewer's own scoping note: 9 files across two assemblies, a real scope increase, and
**pre-existing** — none of these lines is in `5202c06..HEAD`. Fixing only the 12 Core sites and
ticketing the 20 Backend ones is defensible, but closing FR-LOG-6 as "met" while
`RequestResponseLoggingMiddleware.cs:32` stands is not: it emits a divergent correlation ID at
the shipped default log level on every request.

### F-2 — **Low** — host-facing docs never state the "normalizes to empty ⇒ server-generated" rule
`docs/CONFIGURATION.md:21` still says "present **with a non-empty value**, that value identifies
the request". `x-correlation-id: \t` is a non-empty value, yet after R1-01 the server-generated
identifier is used (`AspNetCoreFrontend.cs:435`, pinned `ExtractTraceIdFromTests.cs:246`). Round 1's
F-1 cited this very sentence as the authority for the fix; the fix landed, the sentence did not
move. `docs/LOGGING.md:197-250` doesn't mention the fallback either. Not covered by D-8, which
scopes out *public/client* docs only.

### F-3 — **Low** — no test for the configured header present with an **empty** value
`AspNetCoreFrontend.cs:428-431`. Covered: header absent (`:159`), setting empty (`:180`),
control-chars-only (`:228`). Not covered: present-and-empty, the only reason
`clientSupplied.Length > 0` exists. **Trap for whoever writes it:** `HeaderDictionary`'s indexer
*removes* the key when assigned a `StringValues` that `IsNullOrEmpty` considers empty, so
`Headers[name] = ""` silently produces the header-absent case and the test would be vacuous while
appearing green. Use `Headers.Append(name, "")`.

### F-4 — **Low** — the surrogate guard's boundary case is untested, so its predicate can be mutated undetected
`CorrelationIdNormalizer.cs:69`. The only test cuts at 7, *inside* the pair. Change
`char.IsHighSurrogate` to `char.IsSurrogate` and it still passes — but
`Normalize("abcdef\U0001F600ghij", 8)` would go from the correct `"abcdef😀"` to
`"abcdef\uD83D"`, reintroducing exactly the parity break R1-06 closed. One `[TestCase]` at
maxLength 8 asserting `"abcdef\U0001F600"` kills the mutation.

### F-5 — **Nit** — `HttpContext.TraceIdentifier` now read on every call
`AspNetCoreFrontend.cs:421`. One small string allocation per request even when the client value
wins. Deferring it would reintroduce the per-branch laziness R1-10 removed. Recorded, not recommended.

### F-6 — **Nit** — `AppSettings.CorrelationIdMaxLength` XML doc says "client-supplied" only
`AppSettings.cs:25-27`. The cap demonstrably applies to the server-generated identifier too
(`ExtractTraceIdFromTests.cs:216`). Pre-existing.

### F-7 — **Nit** — `_rateLimitedLoggedTraceIds.Should().NotBeEmpty()` doesn't prove the *rejected* request logged
`CorrelationIdParityTests.cs:265`. Two requests hit that factory; the assertion passes if only the
permitted one logged. R1-07 is still genuinely closed by the body assertion at `:243`.
`HaveCount(2)` would make the log arm self-evident.

## Round-1 items confirmed genuinely fixed
R1-01 through R1-15, each with the citation. Test-integrity: `--numstat` on `*Tests*` shows
`118/0`, `516/0`, `251/0`, `268/0`, `8/8`; the only deletions are the five authorized literal
replacements and two `null!`→`default!` lines. No `[Ignore]`, `[Explicit]`, `Assert.Pass`, or
removed assertion.

---

# Raw report 2 — Clean-code reviewer (round 2)

**Verification:** `Frontend.AspNetCore.Tests.Unit` **553 passed / 0 failed** (round 1 was 550/2);
`Core.Tests.Unit --filter ~LoggingSanitizer` **17/17** (was 16); `csharpier check` clean.

**Bottom line: no findings above Low.**

### Low 1 — `LoggingMiddleware._correlationIdMaxLength` is now state whose only observable value is a constant
`LoggingMiddleware.cs:20`, ctor `:24-39`, sole read `:222`. R1-09 is genuinely fixed, but removing
the ternary left the field vacuous, and that is new in this diff. `IOptions<AppSettings>` is a
singleton `OptionsManager<AppSettings>` and `OptionsCache` memoises through a `Lazy<T>` in
`ExecutionAndPublication` mode (which caches the exception too). So if the ctor's `.Value`
succeeded, `ExtractTraceIdFrom`'s cannot throw; if it threw, the field is already `Default`. The
`try` branch's assignment can never be the value read at `:222`. The comment at `:30-31` tells the
next maintainer the configured cap reaches `Normalize` on the misconfiguration path; it cannot.
12 lines collapse with no behavior change, and the existing test still passes.

### Low 2 — `ExtractTraceIdFrom`'s fallback uses three coupled pieces of state where two lines suffice
`AspNetCoreFrontend.cs:421-437`. Readable, but `serverGenerated` + `fromClient` + a ternary inside
the `Normalize` call + a compound guard is more machinery than the behavior needs. Because
`Normalize("")` short-circuits at `CorrelationIdNormalizer.cs:51`, this is exactly equivalent in
four lines: normalize `clientSupplied`; if the result is empty, normalize `TraceIdentifier`. Gains:
removes the `fromClient` flag whose invariant a third source must preserve (the exact omission class
this ticket closes), and stops reading `HttpContext.TraceIdentifier` eagerly.

### Low 3 — `docs/CONFIGURATION.md:21` now describes the *opposite* of R1-01's behavior
Same as functionality F-2, found independently. "Round 1's F-1 cited this sentence as the promise
the code broke; the code moved and the sentence did not, so the mismatch survives with its sign
flipped." The one the reviewer "would not ship without".

### Low 4 — the parity fixture's snapshot invariant is enforced only by a comment
`CorrelationIdParityTests.cs:148-151`. The setup rewrite is **sound, not brittle, in the direction
that matters**: the hostile warm-up keeps `_loggedTraceIds` homogeneous so `OnlyContain` at `:256`
stays strong, and the snapshot sits after the 429 and before the clean arm. `OnlyContain` verified
not accidentally satisfiable. One direction degrades **silently**: a maintainer adding a fifth
hostile-arm request *after* line 151 gets a green fixture in which that request's log is never
checked. Asserting an expected event count would make the invariant self-enforcing.

### Nits
- `AspNetCoreFrontend.cs:402` — summary says "normalized **exactly once**", but the fallback path calls `Normalize` twice.
- `CorrelationIdNormalizer.cs:19-48` — no part of the doc mentions the surrogate trim, though it is the one adjustment that can make the result `maxLength - 1` for an input with **no** disallowed characters, and can return `string.Empty` for a non-empty input that is not all control characters (`Normalize("\U0001F600x", 1)`). The `<returns>` enumeration is incomplete.
- `CorrelationIdParityTests.cs:470` — `CorrelationIdRecordingLoggerProvider` is an assembly-wide `internal` helper at the bottom of a fixture file, while this project's other shared helpers each get their own file. Its private `NullScope` re-implements `NullLogger.Instance.BeginScope`.
- `AspNetCoreFrontend.cs:425` — `out var headerValue` is the only implicitly-typed local in an otherwise fully explicitly-typed method.
- Per D-6 only because `LogSanitizer.cs` was edited this round: the class summary `:9` and `SanitizeForLog`'s doc `:15-16` still say "whitelist" while the new member eight lines below says "allowlist".

### Verdicts
- **Surrogate branch**: correct and well-scoped; also checked that an orphan arriving *in* the header is unreachable (Latin-1 cannot produce surrogates; `Encoding.UTF8` maps invalid sequences to U+FFFD, never a lone surrogate), so truncation really is the only path.
- **U+2028/U+2029 doc accuracy**: verified from primary behavior, not trusted. All six corrected sites are accurate, and no remaining site overstates preservation. PRD FR-LOG-3 says "SHALL exclude, **at minimum**, control characters", so removing two extra line-breaking characters is compliant.
- **Test coverage**: every round-2 test checked against "would it fail if the feature were reverted?" — all six pass that bar.
- **Test integrity**: clean, confirmed independently; only the three authorized changes.

---

# Compiled, de-duplicated finding list with my adjudication

| ID | Source | Finding | Reviewer | **Mine** | Decision |
| --- | --- | --- | --- | --- | --- |
| **R2-01** | Func F-1 | 32 log sites re-narrow the correlation ID through the strict `Method`/`Path` allowlist; Core/Backend log entries diverge from the response body | Critical | **Critical** | **Fix now, all 32** |
| **R2-02** | Func F-2 = Clean Low 3 | Host docs never state the "normalizes to empty ⇒ server-generated" rule; `CONFIGURATION.md:21` now falsifiable | Low | **Medium** ↑ | **Fix now** |
| **R2-03** | Clean Low 1 | `_correlationIdMaxLength` is vacuous state with a misleading comment | Low | **Low** | **Fix now** |
| **R2-04** | Clean Low 2 (+ Func F-5) | `ExtractTraceIdFrom` carries two unnecessary locals and reads `TraceIdentifier` eagerly | Low | **Low** | **Fix now** |
| **R2-05** | Func F-4 | Surrogate guard's predicate can be mutated undetected | Low | **Low** | **Fix now** |
| **R2-06** | Func F-3 | No test for header present with an empty value | Low | **Low** | **Fix now** |
| **R2-07** | Clean Low 4 (+ Func F-7) | Parity fixture's snapshot ordering enforced only by comment | Low | **Low** | **Fix now** |
| **R2-08** | Clean nits + Func F-6 | Summary overstates "exactly once"; `<returns>` omits the surrogate trim; shared provider needs its own file; `out StringValues`; `AppSettings` XML doc says "client-supplied" only; whitelist→allowlist in the edited file | Nit | **Nit** | **Fix now** — all cheap |

## Severity re-confirmations, with reasons

- **R2-01 held at Critical.** I verified both decisive claims firsthand.
  `RequestResponseLoggingMiddleware.cs:32` does apply the strict allowlist to the already-normalized
  TraceId and feeds it into the Core log scope at Information level on every request; and
  `RequestResponseLoggingMiddlewareTests.cs:247,258` do still assert `"traceidwithunsafe"` and still
  pass. Its input is `No.RequestInfo("trace\r\nid\twith{unsafe}")` — the *same* input as the frontend
  test that round 1 filed as R1-02, so the corrected expectation is the identical
  `"traceidwith{unsafe}"` literal. That symmetry is conclusive: this is one defect that was fixed on
  one side of an assembly boundary and missed on the other. `todo.md` Task 3's acceptance criterion
  "do **not** leave two *different* normalizations in play" is unmet.
- **R2-02 upgraded Low → Medium.** Both reviewers found it independently, and it is not merely a
  missing sentence: as written, `CONFIGURATION.md:21` now states the *opposite* of shipped behavior.
  A host operator following it would conclude DMS is broken and file a defect. Task 7 is
  doc reconciliation, so this is squarely in scope.
- **My own scope ruling on R2-01: fix all 32 sites, not just the 12 in Core.** The reviewer offered
  a Core-only option. I am declining that narrowing. FR-LOG-6 says "every log entry … regardless of
  which part of the system produces the response"; a Core-only fix leaves FR-LOG-6 "partially met",
  which I would then have to report as a failed requirement. The change is mechanical, idempotent
  (AD-5), and keeps a CodeQL sanitizer marker at every site. Backend already references Core, so
  `SanitizeCorrelationIdForLogging` is reachable at all 32. The risk of fixing is far below the cost
  of shipping a knowingly-unmet requirement.

## A plan assumption this corrects

Plan AD-1 states: "Downstream consumers — `FailureResponse`'s ~30 factories, all handlers, **all
log events** — then need **no changes**, because the value they receive is already correct." For
error-response bodies that held. For log events it is **false**: 32 sites re-narrow the value after
receiving it. The declined-findings ledger's D-2 correctly covers the *body* case; nothing covered
the *log* case, and neither round-1 reviewer looked past the frontend assembly. Recording this so
the residual risk is visible: the round-1 "FR-LOG-6 met" verdict was reached against an inventory
of `TraceId` *construction* sites, when the requirement is about *use* sites.

## Round 2 stop-condition evaluation

**Not converged.** One Critical and one Medium remain. Proceeding to round 3.

## Test-integrity audit for this round (§9.4 gate 4)

`git diff 5202c06..HEAD --numstat` on `*Tests*` files: `118/0`, `516/0`, `251/0`, `268/0`, and
`5/5` for `LoggingMiddlewareTests.cs`. I read that 5/5 hunk line by line: it is exactly the five
authorized expected literals, with inputs, structure and assertion counts unchanged. No `[Ignore]`,
`[Explicit]`, `Assert.Pass` or `Assert.Inconclusive` anywhere in the diff. `src/config` untouched.
Confirmed independently by both reviewers.
