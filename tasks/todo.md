# Task List: Correlation ID Normalization (DMS-1457 / FR-LOG-3 → FR-LOG-6)

Companion to `tasks/plan.md`. Requirements are quoted verbatim in plan §2.
Work top to bottom. Check items off as they land.

---

## Phase 1 — The normalizer (foundation)

### Task 1: Add the correlation-ID character filter

**Description:** Add a new sanitizing function that strips **all** control
characters from a string while preserving every other printable character. This is
the FR-LOG-3 allowlist, and it is deliberately *broader* than the existing
`SanitizeForLogging` (which permits only alphanumerics, space, and `_-.:/` and is
correct for `Method`/`Path`). Define it next to the existing canonical
implementation so each allowlist has exactly one definition.

**The allowlist is settled** (plan §8 decision 2): **"all printable non-control
characters"** — the predicate is a bare `!char.IsControl(c)`, with **no positive
character enumeration**. Do not add one.

**Acceptance criteria:**
- [ ] A new function strips control characters, **including `\r`, `\n`, `\t`, and `\0`**
- [ ] Printable punctuation commonly found in upstream IDs — `+ = { } @ | , # ( ) [ ] < > " '` — is **preserved**
- [ ] Non-ASCII printable characters are **preserved** (`char.IsControl` is Unicode-aware)
- [ ] The predicate is `!char.IsControl(c)` and nothing more — no alphanumeric or punctuation allowlist layered on top
- [ ] `SanitizeForLogging` and `SanitizeForConsole` are left behaviorally unchanged
- [ ] Null/empty input returns empty string without throwing

**Verification:**
- [ ] Unit tests pass: `dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/...` (and/or the Core unit test project, depending on where the function lands)
- [ ] `dotnet csharpier format src/dms` produces no diff
- [ ] Existing `LoggingSanitizerTests` still pass unmodified

**Dependencies:** None

**Files likely touched:**
- `src/dms/backend/.../External/LogSanitizer.cs` *(canonical allowlist definition — confirm exact path)*
- `src/dms/core/EdFi.DataManagementService.Core/Utilities/LoggingSanitizer.cs` *(delegating surface, mirroring `SanitizeForLogging`)*
- the corresponding `*.Tests.Unit` test file(s)

**Estimated scope:** S

> ⚠️ Do **not** implement this by copying `SanitizeForConsole` — it intentionally
> preserves `\r` and `\n`, which is the exact log-forging vector FR-LOG-3 names.

> ⚠️ **Keep the `ReplaceLineEndings` call.** `LogSanitizer.SanitizeForLog` begins with
> `input = input.ReplaceLineEndings(string.Empty);` and the comment above it explains
> why: it is behaviorally redundant, but **CodeQL's log-injection analysis models
> `ReplaceLineEndings` as a sanitizer and does not understand the custom character
> loop**. A new filter that omits it will likely trip CodeQL log-injection alerts in
> CI even though it is functionally correct. Mirror that call in the new function and
> carry the explanatory comment.

**Reference implementation to mirror** (`src/dms/backend/EdFi.DataManagementService.Backend.External/LogSanitizer.cs`):
its shape is `ReplaceLineEndings` → first pass counting allowed chars and detecting
whether any work is needed → early return of the original instance when clean →
`string.Create` with exact allocation. Match that shape (including the
`#pragma warning disable S3267` for the deliberate non-LINQ loop) so the new function
is consistent with its neighbour.

> **Terminology:** the existing XML doc comments in `LogSanitizer.cs` and
> `LoggingSanitizer.cs` say "whitelist". The PRD and host-facing docs now say
> "allowlist". Updating those comments while you are in the file is welcome but
> **optional and Low priority** — do not let it expand the diff or distract the
> review loop.

---

### Task 2: Add the combined normalize entry point

**Description:** Add a single function that performs the complete FR-LOG-5
adjustment: apply Task 1's character filter **and** enforce
`CorrelationIdMaxLength`. This is the one function every ingestion point will call.
It lives where the configured max length is reachable (the frontend), and takes the
max length as a parameter rather than reaching for configuration itself, so it stays
unit-testable.

**The order is settled** (plan §8 decision 1): **truncate to `CorrelationIdMaxLength`
first, then strip control characters.** This matches `LoggingMiddleware.cs:46-49`.

**Acceptance criteria:**
- [ ] One function truncates **then** filters, in that order
- [ ] A test pins the order — use an input whose first `MaxLength` characters contain a control character, so swapping the order produces observably different output
- [ ] Normalization is **idempotent**: `f(f(x)) == f(x)` (plan AD-5)
- [ ] A non-positive max length falls back to `AppSettings.DefaultCorrelationIdMaxLength` rather than throwing or returning empty
- [ ] No new public type beyond what is strictly needed (plan §1: bias to simplicity)
- [ ] Accepts that a result may be **shorter** than `CorrelationIdMaxLength` when the truncated prefix contained control characters — this is intended, not a bug to compensate for

**Verification:**
- [ ] Unit tests cover plan §6 cases 1–7
- [ ] Test asserting the chosen filter/truncate order would fail if the order were swapped
- [ ] `dotnet csharpier format src/dms` clean

**Dependencies:** Task 1

**Files likely touched:**
- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/` (new small helper, or an existing infrastructure type)
- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/`

**Estimated scope:** S

---

## ✅ Checkpoint A — Foundation

- [ ] `./build-dms.ps1 Build` succeeds
- [ ] `./build-dms.ps1 UnitTest` passes
- [ ] `dotnet csharpier format src/dms` produces no diff
- [ ] No production call site changed yet — this checkpoint is purely additive
- [ ] Report the chosen filter/truncate order and the allowlist definition to the team lead before proceeding

---

## Phase 2 — Single ingestion point

### Task 3: Normalize inside `ExtractTraceIdFrom`, reconcile `LoggingMiddleware`

**Description:** Make the frontend's trace-ID extraction return an already-normalized
`TraceId`, so every downstream consumer (all of `FailureResponse`, all handlers, all
log events) receives the correct value without changes. Then reconcile
`LoggingMiddleware`, which currently does its own truncate-then-filter: either drop
the now-redundant work or leave it as a harmless idempotent second application —
but do not leave two *different* normalizations in play.

**Acceptance criteria:**
- [ ] `ExtractTraceIdFrom` returns a normalized value for both branches: the client-supplied header **and** the `HttpContext.TraceIdentifier` fallback (FR-LOG-3/4 cover system-generated IDs too)
- [ ] `LoggingMiddleware`'s log events and its unhandled-exception response body carry the same value as before or better — never a differently-normalized one
- [ ] `Method` and `Path` still use `SanitizeForLogging` (unchanged)
- [ ] The `OptionsValidationException` fallback path in `LoggingMiddleware.ExtractTraceId` still yields a normalized value
- [ ] A request with a hostile correlation ID returns the **same HTTP status** as the identical request with a clean one (FR-LOG-5)

**Verification:**
- [ ] Frontend unit tests pass
- [ ] New test: hostile ID in → normalized ID appears in the log scope
- [ ] New test: over-length ID is capped at the configured value
- [ ] Existing `LoggingMiddleware` tests pass; if one encoded the old raw-echo behavior, flag it to the team lead rather than quietly editing it

**Dependencies:** Task 2

**Files likely touched:**
- `src/dms/frontend/.../AspNetCoreFrontend.cs` (`ExtractTraceIdFrom`, lines 401-413)
- `src/dms/frontend/.../Infrastructure/LoggingMiddleware.cs` (lines 46-49 do the current extract → truncate → filter)
- **New test file:** `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/ExtractTraceIdFromTests.cs` — **no test anywhere currently references `ExtractTraceIdFrom`**, despite it being the single choke point for the whole request pipeline. Creating this file is part of the task, not optional.
- `src/dms/frontend/.../Tests.Unit/Infrastructure/LoggingMiddlewareTests.cs` (existing coverage to preserve)

**Estimated scope:** M

> Note: `ExtractTraceIdFrom` reads `options.Value`, which can throw
> `OptionsValidationException`. Only `LoggingMiddleware.ExtractTraceId` (lines 209-219)
> catches it today; the other three callers do not. Do not change that behavior in this
> task, but be aware normalization must not introduce a new throw on that path.

---

### Task 4: Fix the `MapFallback` catch-all 404

**Description:** `Program.cs` (~line 212) builds its own trace ID inline: it reads the
header directly **and** hardcodes a `"correlationid"` fallback header name. That
second part is a separate defect — a host that left `CorrelationIdHeader` empty
(feature disabled) still has client-supplied values honored on unmatched routes,
contradicting `ExtractTraceIdFrom`. Route this path through the same extraction used
everywhere else.

**Acceptance criteria:**
- [ ] The catch-all 404's `correlationId` is normalized, identical to every other path
- [ ] The hardcoded `"correlationid"` fallback is removed; when `CorrelationIdHeader` is empty, the server-generated identifier is used
- [ ] The 404 response body shape is unchanged (still `FailureResponse.ForNotFound`)

**Verification:**
- [ ] New test: unmatched route + hostile correlation ID → normalized `correlationId` in body
- [ ] New test: unmatched route + `CorrelationIdHeader` empty + a `correlationid` header present → client value is **ignored**
- [ ] Frontend unit tests pass

**Dependencies:** Task 3

**Files likely touched:**
- `src/dms/frontend/.../Program.cs`
- `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/`

**Estimated scope:** S

> **In scope — decided.** Plan §8 decision 3: this fix is folded into DMS-1457 rather
> than split into its own ticket. Do not skip it.

---

### Task 5: Enumerate every remaining `TraceId` construction site; add the guard test

**Description:** Because `TraceId` is a plain record struct with no validation
(plan AD-2), correctness depends on every construction site routing through
normalization. **Do not trust this plan's inventory** — enumerate for yourself, then
fix whatever the enumeration finds.

> ⚠️ **A `grep -rn "new TraceId("` alone is insufficient** — it misses target-typed
> `new`, which is how `HealthCheckEndpointModule.cs:104` constructs one
> (`TraceId traceId = new(httpContext.TraceIdentifier);`). Use all of these:

```bash
grep -rn "new TraceId(" src/dms --include=*.cs | grep -v Tests
grep -rnE "TraceId +[A-Za-z_][A-Za-z0-9_]* *= *new\(" src/dms --include=*.cs | grep -v Tests
grep -rn "TraceId:" src/dms --include=*.cs | grep -v Tests
grep -rn "ExtractTraceIdFrom" src/dms --include=*.cs
grep -rn "TraceIdentifier" src/dms --include=*.cs | grep -v Tests
```

**Verified inventory — exactly five production construction sites.** Confirm each
still exists, then classify it:

| # | Site | Status |
|---|---|---|
| 1 | `AspNetCoreFrontend.cs:410` — client header | Fixed by Task 3 |
| 2 | `AspNetCoreFrontend.cs:412` — `TraceIdentifier` fallback | Fixed by Task 3 |
| 3 | `Program.cs:226` — `MapFallback`, bypasses `ExtractTraceIdFrom` | Fixed by Task 4 |
| 4 | `Modules/HealthCheckEndpointModule.cs:104` — target-typed `new`, bypasses `ExtractTraceIdFrom` | **This task** |
| 5 | `Core/Model/No.cs:106` — null-object factory, no production callers | Leave alone; record as deliberate |

These **propagate** an existing `TraceId` rather than construct one, so AD-1 covers
them and they need **no change**: `AspNetCoreFrontend.cs:614` (the whole
`FrontendRequest` pipeline), `Infrastructure/WebApplicationBuilderExtensions.cs:238`
(429 body), `Modules/TokenEndpointModule.cs:87` (logging), and the `TraceId:` named
arguments across `Core/Handler/*.cs` and `Backend/*Contracts.cs`.

**Acceptance criteria:**
- [ ] Every production `TraceId` construction reachable from an HTTP request yields a normalized value
- [ ] A guard test pins the invariant, so a future raw construction fails a test rather than silently regressing (e.g. an end-to-end assertion per entry point, or a reflection/architecture test if the repo has that pattern)
- [ ] Any site deliberately left un-normalized is listed with a rationale for the team lead

**Verification:**
- [ ] `./build-dms.ps1 UnitTest` passes
- [ ] The enumeration commands above are re-run after the change and the output reviewed — paste it into the round report

**Dependencies:** Tasks 3, 4

**Files likely touched:** the sites the enumeration finds (expect 3–5) plus their tests

**Estimated scope:** M

---

## ✅ Checkpoint B — Uniform normalization

- [ ] Every error response body and every log event carries the normalized value
- [ ] `./build-dms.ps1 Build` succeeds
- [ ] `./build-dms.ps1 UnitTest` passes
- [ ] `dotnet csharpier format src/dms` produces no diff
- [ ] `src/config` shows **zero** changes: `git diff --stat -- src/config` is empty (plan AD-4)
- [ ] No test deleted, skipped, or loosened: read `git diff` on all `*Tests*` files

---

## Phase 3 — Prove parity, then reconcile docs

### Task 6: Add a log/response parity test across status codes

**Description:** FR-LOG-6 is the requirement most likely to regress, because it spans
layers. Add a test that sends a request carrying a hostile, over-length correlation ID
and asserts the `correlationId` in the response body equals the normalized value —
across several status codes produced by *different* code paths (a `FailureResponse`
4xx, the `MapFallback` 404, and the `LoggingMiddleware` 500 if reachable).

**Acceptance criteria:**
- [ ] Parity asserted for at least three distinct status codes from at least two different producing layers
- [ ] The assertion compares against the expected normalized value, not merely "not equal to the raw input"
- [ ] The hostile input includes both a control character and over-length content

**Verification:**
- [ ] The new test passes and fails if normalization is reverted at any one site (sanity-check by temporarily reverting one, then restoring)
- [ ] If the Docker/database environment for integration tests is unavailable, implement this in `src/dms/tests/EdFi.DataManagementService.Tests.Integration/` in-process instead — and **state explicitly** which environment was used

**Existing tests to keep green — check these first:**
- `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/General/CorrelationId.feature`
  (`@API-061`) sends `correlationid: test-correlationId` and asserts it round-trips
  **exactly** into the response body. That value is allowlist-clean, so it must keep
  passing unchanged — if it breaks, the new filter is too strict. This is the single
  most important existing regression target. Consider **adding** negative scenarios
  here rather than editing the existing one.
- `src/dms/frontend/.../Tests.Unit/RateLimitTests.cs` asserts the 429 body's
  `correlationId` equals the supplied header (lines ~129, 135-138) and that an empty
  `CorrelationIdHeader` falls back to `TraceIdentifier`.
- `src/dms/frontend/.../Tests.Unit/Infrastructure/LoggingMiddlewareTests.cs` **already**
  covers truncation (~lines 470-488, asserting `DefaultCorrelationIdMaxLength`) and
  filtering (~lines 452-458). Task 1/2 are not greenfield — preserve this coverage.

> ⚠️ **E2E body comparisons mask `correlationId`.** `StepDefinitions.cs:1309-1310`
> strips `correlationId` from both actual and expected bodies before comparing, which
> is why feature files containing hardcoded literals and `"correlationId": null` pass
> today. **Most E2E tests therefore cannot catch a regression here** — which is
> precisely why this task's explicit parity assertions matter. Do not assume green
> E2E means parity holds.

**Dependencies:** Task 5

**Files likely touched:**
- `src/dms/tests/EdFi.DataManagementService.Tests.Integration/` or the DMS E2E feature files

**Estimated scope:** M

---

### Task 7: Reconcile the documentation with what shipped

**Description:** `docs/LOGGING.md` and `docs/CONFIGURATION.md` were written to describe
the *target* behavior ahead of the code. Verify each claim now matches the
implementation and correct any drift.

**Acceptance criteria:**
- [ ] `docs/LOGGING.md`'s "Correlation ID normalization" section matches shipped behavior — allowlist description, the `255` default, the normalize-not-reject rule, and the everywhere-applied claim
- [ ] `docs/CONFIGURATION.md` rows for `CorrelationIdHeader` and `CorrelationIdMaxLength` are accurate, including `MapFallback`'s corrected behavior from Task 4
- [ ] The word "whitelist" appears nowhere: `grep -ci whitelist docs/LOGGING.md docs/CONFIGURATION.md` returns 0
- [ ] If the shipped filter/truncate order differs from what the docs imply, the docs are corrected

**Verification:**
- [ ] `grep -n "whitelist" docs/LOGGING.md docs/CONFIGURATION.md` → no output
- [ ] Every documented claim traced to a specific test or code line

**Dependencies:** Task 6

**Files likely touched:**
- `docs/LOGGING.md`
- `docs/CONFIGURATION.md`

**Estimated scope:** S

---

## ✅ Checkpoint C — Complete

- [ ] All Definition of Done items in `tasks/plan.md` §10 satisfied
- [ ] Review loop stopped on **convergence**, not budget exhaustion (or the budget stop is reported plainly)
- [ ] Final report written per plan §9.7
- [ ] Diff presented to a human; **nothing pushed, no PR opened** (plan §9.6)
