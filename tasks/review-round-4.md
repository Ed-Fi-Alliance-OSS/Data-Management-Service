# Review Round 4 — DMS-1457 / FR-LOG-3..6

Team lead record per `tasks/plan.md` §9.2 Step 4.

- **Branch point:** `5202c06` · R1 `7835227` · R2 `be941dd` · R3 `63d7bd9` · **R4 `48d029f`**
  (records: `29c02e3`, `65279c8`, `010fb2f`)
- **Roles:** fresh implementation sub-agent; two fresh, tool-restricted read-only reviewers
  (different agents). Both read D-1..D-14 first.

## Headline

**All four FRs met.** The functionality reviewer ran a **requirement-scoped** sweep — 15
divergence classes, deliberately not keyed to any prior bug's mechanism — and **found nothing
new**. The clean-code reviewer read the cumulative four-round diff as a single change and
judged it **ready to merge**, with 2 Low and 5 Nit observations, all in test-support code or
comments, none with correctness impact.

---

# Raw report 1 — Functionality reviewer (round 4)

**Verification:** `csharpier check src/dms` clean (2780); Frontend 556/0; Backend 3785/0;
Core 5094 passed / 6 failed (the known stand-in set, same six as rounds 2–3);
`Core.Tests.Unit --filter OAuth|LoggingSanitizer|RequestResponseLogging` 54/0; both new OAuth
tests execute and pass by name.

## Per-FR verdict — all met

| FR | Verdict | Evidence |
| --- | --- | --- |
| **FR-LOG-3** | **met** | Bare `!char.IsControl(c)` `LogSanitizer.cs:162`; applied by `SanitizeCorrelationIdForLog` `:87`; surfaced `LoggingSanitizer.cs:48`; reached from `CorrelationIdNormalizer.cs:87`. Strict allowlist untouched and structurally separate `LogSanitizer.cs:152`. **Non-conflation holds in BOTH directions**: `SanitizeForLogging` receives zero trace-shaped expressions anywhere in `src/`, and the correlation sanitizer receives *only* correlation values (10× `request.TraceId.Value`, 9× `traceId.Value`, 8× `requestInfo.FrontendRequest.TraceId.Value`, plus `partitionRequest`/`deleteRequest` and the normalizer's `truncated`). |
| **FR-LOG-4** | **met** | Cap `CorrelationIdNormalizer.cs:59-87`; default `AppSettings.cs:14`; override `:29` (env `AppSettings__CorrelationIdMaxLength`); startup rejection `:80-85` wired through `AppSettingsValidator` and `Program.cs:268`. Applies to the server-generated branch (`AspNetCoreFrontend.cs:433`). |
| **FR-LOG-5** | **met** | No throw/reject path `CorrelationIdNormalizer.cs:59-88`; non-positive `maxLength` degrades to default `:66`. Differential end-to-end `CorrelationIdParityTests.cs:233`. Order pinned `CorrelationIdNormalizerTests.cs:141` and again at `CorrelationIdParityTests.cs:62`. |
| **FR-LOG-6** | **met** | Single ingestion point `AspNetCoreFrontend.cs:415-437`, the only reachable `new TraceId(...)` (`:436`; sole other is `No.cs:106`, D-3). All six callers route through it. Round 4's one-token fix at `OAuthManager.cs:71` closes the last divergent site. Tests: `CorrelationIdParityTests` (404/401/403/429 bodies + logs, exact counts), `LoggingMiddlewareTests.cs:430` (500), `RequestResponseLoggingMiddlewareTests.cs:265` (Core layer), the new OAuth fixture (502). |

## §6 edge cases — all ten real, none vacuous
Notable checks: case 2's differential arm (`SanitizeForLogging(UpstreamId).Should().NotBe(UpstreamId)`)
is what makes it non-trivial — it fails if the two allowlists ever converge. Case 5 is a single
equality that kills the swapped order. Case 7's `It_really_did_send_the_header` defeats the
`HeaderDictionary`-indexer vacuity trap, and the reviewer verified the fixture installs an
`IHttpRequestFeature` so the production `TryGetValue` branch really is taken.

Additional structural finding in the reviewer's favour: `CorrelationIdRecordingLoggerProvider.cs:52`
records a `TraceId` property **only when it is a `string`**, so combined with the exact
`HaveCount(HostileArmLoggedEventCount)` assertion the frontend fixture *would* fail if any of
those sites regressed to passing the struct. The round-4 defect class is therefore covered
structurally in the frontend arm as well as by name in OAuth.

## The requirement-scoped divergence sweep (15 classes) — nothing new found

| # | Class | Result |
| --- | --- | --- |
| 1 | Second `TraceId` construction | ✅ Only the ingestion point and `No.cs:106`. Zero production `FrontendRequest with { … }` mutations, so `TraceId` cannot be replaced mid-pipeline. |
| 2 | `TraceId` struct where `.Value` was meant (`ToString()`, boxing, interpolation, `object`/`params object?[]`) | ✅ Zero remaining. Brace-matching parser over **every** `Log*(` call in `src/`, zipping placeholder names to arguments positionally; 100% of production sites resolve to `.Value`, a `sanitizedTraceId` local, or the correlation sanitizer. Inverse sweep too. |
| 3 | Template/argument **arity** mismatch on a `{TraceId}` template | ✅ None; no misordered bindings. |
| 4 | `Substring`/`Truncate`/`Trim`/case changes/range indexers | ✅ None. Only the normalizer's own `value[..retained]`. |
| 5 | Strict-vs-broad sanitizer misapplication, **both directions** | ✅ No trace value reaches `SanitizeForLogging` or `SanitizeForConsole`; no non-trace value reaches the correlation sanitizer. |
| 6 | Re-reading the raw header instead of the ingested value | ✅ Only the ingestion point reads the configured header. `MapFallback`'s hardcoded `?? "correlationid"` is gone. `ExtractHeadersFrom` does forward the raw header into `FrontendRequest.Headers`, so all six consumers were traced: each reads a *specific named* header, none enumerates or logs the collection. |
| 7 | `Activity`/OpenTelemetry tags, metric labels | ✅ No correlation ID in any span tag or metric label. `LoggingMiddleware.cs:53` is `Activity.TraceId` (W3C), a different concept, correctly named `ActivityTraceId`. |
| 8 | Outbound HTTP headers | ✅ Only `Authorization`, `Tenant`, `Accept`, `Location`, `ETag`, `Vary`, `Retry-After`, `WWW-Authenticate`. Never propagated upstream, so no second normalization on egress. |
| 9 | Database columns | ✅ Not persisted. `LastModifiedTraceId` — the only correlation-shaped DB-facing member — is literal `null` at all three production producers. |
| 10 | Framework-generated `traceId` bodies | ✅ **None registered** — no `AddProblemDetails`, `UseExceptionHandler`, `UseStatusCodePages`, `IProblemDetailsService`, `AddHttpLogging`/`UseHttpLogging`/`W3CLogging`. Serilog has only `Enrich.FromLogContext()`. |
| 11 | Exception messages | ✅ The wrapper message uses the normalized value and is only ever logged with a **constant** template. No production DMS code passes an exception message as a log template (which would let `{…}` in a correlation ID become a placeholder). The two `$"… {{TraceId}}"` sites correctly escape the braces and pass `.Value`. |
| 12 | `catch` blocks reconstructing an identifier | ✅ All four use the normalized value. `LoggingMiddleware`'s `OptionsValidationException` re-normalization cannot diverge from a body, because when validation fails `Program.cs:154` maps no endpoint and the only responder writes a bare 500 with **no body and no correlation ID**. Conversely `UseRateLimiter`, `MapRouteEndpoints`, `MapHealthChecks` and `MapFallback` are all inside the `invalidConfigurationException is null` block, so the new `options.Value` accesses cannot throw at request time. |
| 13 | Encoding asymmetry between log sink and body | ✅ The four body writers escape differently (`UnsafeRelaxedJsonEscaping`, the `ConfigureHttpJsonOptions` encoder, `JsonNode.ToString()`, `JsonSerializer.Serialize`) but all are lossless under JSON decoding, so the value a client *parses* is identical to the value logged. The one lossy case — a lone surrogate, which `Utf8JsonWriter` replaces with U+FFFD — is exactly what the truncation guard prevents. |
| 14 | Multi-valued / repeated correlation header | ✅ Deterministic comma-join, applied once at the single ingestion point. Unchanged from `5202c06`. |
| 15 | Idempotence / length invariant of a double application | ✅ Re-derived by hand; boundary arithmetic re-walked (`retained ≥ 1` and `value.Length > effectiveMaxLength`, so `value[retained-1]` can never be out of range; a length-1 astral input yields `""`, documented). |

## Findings

### N-1 — **Low** — nothing automated prevents a *future* log site from passing the `TraceId` struct again
`Core.External/Model/TraceId.cs:11` is `public record struct TraceId(string Value)` with no
`ToString()` override, so `logger.LogX("{TraceId}", traceId)` compiles silently and renders
`TraceId { Value = … }`. Round 4 added a durable doc guard for the *sibling* mistake, but none for
this one across the ~140 bare `TraceId.Value` log sites. **Reviewer's own recommendation: a
follow-up ticket, not a round-5 change** — a `ToString()` override lives in the published
`Core.External` contract assembly (AD-2 territory), and a source-scanning guard test would be new
machinery with no precedent in this repo (it checked; Task 5's "guard test" turned out to be the
parity fixture, not a grep test).

### N-2 — **Nit** — `LoggingSanitizer.cs:21` still says control characters are "ASCII < 32"
`char.IsControl` is Unicode-aware and also excludes U+007F–U+009F (category Cc), so the claim
understates the strict allowlist. Round 4 edited the two lines immediately above it. Not covered by
D-6, which is about the word "whitelist" only.

### N-3 — **Nit, explicitly out of scope** — `GetResult.GetSuccess.LastModifiedTraceId` is an inert correlation-ID-shaped contract member
`Core.External/Backend/GetResult.cs:28`. All three production sites pass literal `null`; no DDL
column behind it; only reader is a Polly `ResultFormatter` copy. Raised because it is the one place
a *second* correlation-ID-shaped value could enter a log sink if anyone wired it up — and it would
carry a **prior** request's identifier, which FR-LOG-6 does not and should not cover.

## Reviewer's plain statement
> "No, I have no finding above Nit that is in scope for DMS-1457. … My sweep was deliberately
> structured by the requirement rather than by any prior bug's mechanism, and it covered fifteen
> distinct divergence classes including several the previous three rounds did not name. **It found
> nothing new. Round 4 converges.**"

Process caveat it handed up: the six `Core.Tests.Unit` failures are sandbox artifacts, which means
the ApiSchema-dependent portion of the Core suite has not actually been exercised on this branch in
any round. Nothing in the diff touches ApiSchema, so it judges the risk negligible, but it should be
re-run in CI before merge.

---

# Raw report 2 — Clean-code reviewer (round 4)

**Verification:** Frontend 556/0; Core 5094/6 (delta from round 3 is exactly round 4's two new
tests); the new fixture 2/2 by filter; independent sweep for a `TraceId` struct passed bare into any
`Log*` template → **zero remaining** (13 hits, all `string` locals); `SanitizeForLogging(...TraceId...)`
in production → **zero**; `src/config` untouched; zero added `== null`/`!= null`/`Newtonsoft`.

It also checked one thing that *looked* like a bug and found it sound: `HealthCheckEndpointModule`
and `MapFallback` dereference `options.Value` unguarded while `LoggingMiddleware.ExtractTraceId`
still catches `OptionsValidationException`. That asymmetry is safe because `Program.cs:152` forces
validation eagerly and the `:154` guard means neither is ever mapped when validation failed, whereas
`LoggingMiddleware` is registered at `:150`, *before* the guard — so its catch is the one that
genuinely needs to exist. Coherent by construction.

### 1 — **Low** — the new OAuth regression guard is invisible to `--filter FullyQualifiedName~OAuth`
`OAuthManagerTests.cs:421-474`. Every other fixture in the file is nested inside `OAuthManagerTests`;
the new one closes the outer class at `:411` and sits at namespace scope, so its FQN contains no
"OAuth" token. **Verified empirically:** `--filter "FullyQualifiedName~OAuth|…"` ran 132 tests and
did not include these two. **Scenario:** a maintainer edits `OAuthManager.cs`, runs the natural
targeted filter, sees green, and reintroduces exactly the defect this fixture exists to catch. Full
CI still catches it, so no correctness impact today. A one-line brace move fixes it.

### 2 — **Low** — `ExtractTraceIdFromTests.cs:224-227`'s comment describes a division of test responsibility the file does not honor
The comment asserts the fixture's subject is "header selection, the disable path, and the fallback
branch", but three of its ten fixtures re-pin normalizer behavior using the *same literals* as
`CorrelationIdNormalizerTests` (control characters `:98`, upstream punctuation `:75`, both
truncation bounds `:122`). Only the truncate-then-filter *order* is genuinely not re-pinned. The
comment is also orphaned — it floats between two `[TestFixture]` classes. **Scenario:** a maintainer
changing the allowlist reads it, concludes expectations live in one place, updates only
`CorrelationIdNormalizerTests`, and is surprised by failures in a file the comment said was about
header selection.

### 3 — **Nit** — `OAuthManagerTests.cs:414-416` says "the one place"; there are two
The catch branch at `OAuthManager.cs:82-86` pairs `LogError(..., traceId.Value)` with
`ForGatewayError(traceId)` — also a 502, already correct. An auditor reading "the one place" would
skip it.

### 4 — **Nit** — `docs/CONFIGURATION.md:22` still gives the pre-round-4 account of a shorter-than-cap result
Says a value ends up shorter "if both over-length **and contains control characters**". Round 4
corrected the parallel sentence in `LOGGING.md:232-238` to cover the surrogate case; this one was
not updated. The same fact stated two ways in two documents that link to each other.

### 5 — **Nit** — dummy field initializers where the rest of the diff uses `default!`
`OAuthManagerTests.cs:432-433` uses `new()` / `new JsonObject()`, both immediately reassigned in
`[SetUp]`. `CorrelationIdParityTests.cs` uses `default!` throughout, and round 2 specifically
converted that file from `null!` to `default!`. Three idioms for one problem now appear in one change.

### 6 — **Nit** — `HostileArmLoggedEventCount`'s doc now restates what `MainFactoryArmLoggedEventCount`'s says
`CorrelationIdParityTests.cs:83-90`. The load-bearing half (the ordering invariant) is unique and
worth keeping; the enumeration is duplicated.

### 7 — **Nit** — a handful of entailed assertions remain, on the same ground that justified round 4's `HaveLength(8)` removal
`CorrelationIdNormalizerTests.cs:87-90`, `LoggingSanitizerTests.cs:163-167`,
`CorrelationIdParityTests.cs:297-317`. Unlike `HaveLength(8)`, each is its own `It_` with a
requirement-named intent, so they carry documentation value the removed one did not. **Reviewer's
own view: keeping them is defensible.** Also notes multi-assert `It_` methods are the repo norm
(~7,100 of 11,600 in `src/dms`), so that is convention-as-practiced, not a deviation.

### Verdicts
- **Cumulative diff: ready to merge.** Exactly one new production type (`CorrelationIdNormalizer`, 89 lines) against §1's budget, and it earns its place. The 32-site swap is mechanically uniform with the strict allowlist correctly *retained* on every internally-controlled neighbour. The round-1 hardcoded-fallback removal and the D-11 health-endpoint change are both explained at the site rather than left to be discovered. The one architectural seam a reviewer will notice — truncation in the Frontend assembly, allowlist in `Backend.External`, 32 downstream sites re-applying only the allowlist half — is correct (the value is already capped upstream, re-application is idempotent) and documented at both ends.
- **Test coverage: adequate, real redundancy but no vacuity.** **No vacuous assertion anywhere in the cumulative diff**; the two that existed were removed in round 2. Highest-value tests are the differential ones.
- **Test integrity: clean, verified independently.** Over the full cumulative diff, `git diff 5202c06..HEAD -- '*Tests.Unit*'` contains **exactly 7 removed lines**, all literal swaps (2 + 5). Per-commit intra-round removals all fall inside the authorized set. Zero `[Test]`/`[TestCase]`/`[TestFixture]` deletions across four rounds; zero `[Ignore]`, `[Explicit]`, `Assert.Pass`, `Assert.Inconclusive`, `Skip`, `#if DEBUG`, `TODO`/`FIXME`/`HACK` in the diff's code; net +4 tests and a strictly stronger assertion set.
- Repo-wide `csharpier check .` shows **6 errors, all pre-existing and all outside this diff** (`eng/CmsHierarchy/*`, `src/Directory.Packages.props`, three `reference/design/.../examples/*.xml`).

---

# Compiled, de-duplicated finding list with my adjudication

| ID | Source | Finding | Reviewer | **Mine** | Decision |
| --- | --- | --- | --- | --- | --- |
| **R4-01** | Clean 1 | New OAuth fixture invisible to `--filter ~OAuthManager` | Low | **Low** | **Fix now** — it protects the round-4 regression guard |
| **R4-02** | Clean 2 | `ExtractTraceIdFromTests.cs:224-227` comment misdescribes the file | Low | **Low** | **Fix now** |
| **R4-03** | Func N-2 | `LoggingSanitizer.cs:21` "ASCII < 32" understates `char.IsControl` | Nit | **Low** ↑ | **Fix now** — factual error |
| **R4-04** | Clean 4 | `docs/CONFIGURATION.md:22` pre-round-4 account of shorter-than-cap | Nit | **Low** ↑ | **Fix now** — factual, and the two docs cross-link |
| **R4-05** | Clean 3 | "the one place"; there are two | Nit | **Nit** | **Fix now** |
| **R4-06** | Clean 5 | Dummy field initializers vs `default!` | Nit | **Nit** | **Fix now** |
| **R4-07** | Clean 6 | Duplicated doc enumeration on the count constants | Nit | **Nit** | **Fix now** |
| **R4-08** | Clean 7 | Remaining entailed assertions | Nit | **Nit** | **Decline** → ledger D-15 |
| **R4-09** | Func N-1 | No automated guard against a future `TraceId`-struct log site | Low | **Low** | **Decline for this ticket** → ledger D-16, recommend follow-up |
| **R4-10** | Func N-3 | `LastModifiedTraceId` inert contract member | Nit | **out of scope** | **Decline** → ledger D-17, note only |

## Severity re-confirmations, with reasons
- **R4-03 and R4-04 upgraded Nit → Low**, on the same principle I applied in round 3: a doc that is
  *wrong* outranks a doc that is *ugly*. `LoggingSanitizer.cs:21` states a factual bound on
  `char.IsControl` that is incorrect, and `CONFIGURATION.md:22` enumerates the causes of a
  shorter-than-cap result and is now missing one — in a row that cross-links to the `LOGGING.md`
  sentence round 4 just corrected.
- **R4-08 declined.** The reviewer's own analysis defeats the finding: each remaining entailed
  assertion is its own `It_` with a requirement-named intent, so it documents a distinct guarantee,
  and multi-assert `It_` methods are the repo norm at roughly 7,100 of 11,600. Removing them would
  trade documentation value for a purity that the codebase does not observe.
- **R4-09 declined for this ticket, escalated in the final report.** I agree with the reviewer's own
  recommendation. Both plausible fixes are disproportionate: a `ToString()` override changes a
  published contract assembly (AD-2), and a source-scanning architecture test is machinery this repo
  has no precedent for. The proportionate mitigation already shipped in round 4 — the
  `SanitizeForLogging` `<remarks>` warning — plus the structural coverage the functionality reviewer
  identified at `CorrelationIdRecordingLoggerProvider.cs:52`.
- **R4-10 declined.** Pre-existing, inert, untouched, and semantically *outside* FR-LOG-6: it would
  carry a prior request's identifier, which the parity guarantee neither covers nor should.

## Round 4 stop-condition evaluation

**Not yet converged under a strict reading of §9.4(a)**, which requires *all* FRs met **and** both
reviewers returning **only Nit-level** observations. All four FRs are met and the functionality
reviewer reports nothing above Nit in scope — but the clean-code reviewer returned two Low findings.
Both are in test-support code and comments with no correctness impact, and both are cheap.

Proceeding to **round 5 — the last round permitted by §9.4(b)** — with a correction list that is
entirely comments, docs, a brace move and field initializers. **No production behavior changes in
round 5**, which means the round-4 reviewers' per-FR verdicts remain valid for the production code
regardless of round 5's outcome.

## Test-integrity audit for this round (§9.4 gate 4)
My own checks on `48d029f`: `csharpier check src/dms` clean (2780 files); only two pre-existing test
files have ever been edited across all four rounds (`RequestResponseLoggingMiddlewareTests.cs` 30/2
and `LoggingMiddlewareTests.cs` 5/5, both authorized literal swaps); every other test file is a pure
addition; no `[Ignore]`/`[Explicit]`/`Assert.Pass`/`Assert.Inconclusive` added anywhere;
`src/config` untouched. Independently confirmed by both reviewers, one of which reduced it to the
sharpest possible statement: **exactly 7 removed lines in all test files across the entire
cumulative diff, all of them literal swaps.**
