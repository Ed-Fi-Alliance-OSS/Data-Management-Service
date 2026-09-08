# Implementation Plan: Correlation ID Normalization (DMS-1457 / FR-LOG-3 → FR-LOG-6)

> [!WARNING]
> The files in this `tasks` directory are ephemeral, committed only for the duration
> of the autonomous coding run. All three files (`declined-findings.md`, `plan.md`,
> and `todo.md`) must be removed with `git rm` before final merge. This removal
> should be performed by a human, not by the autonomous coding agent itself.

> **Audience:** an autonomous coding agent (the "team lead") that will orchestrate
> implementation and review sub-agents. Read this document top to bottom before
> spawning anything. Section 9 defines the review loop you must run.

---

## 0. Handoff prerequisites — READ FIRST

This plan was written in a session whose work is **not yet on the remote**. Before
you begin, verify your checkout actually contains the requirements:

```bash
git log --oneline -1 -- docs/PRD-v8.1.md   # expect a commit adding FR-LOG-3..6
grep -n "FR-LOG-3" docs/PRD-v8.1.md        # expect a hit
grep -c "whitelist" docs/LOGGING.md         # expect 0
```

- **Branch:** `copilot/dms-1457-validate-correlation-id`
- If `grep -n "FR-LOG-3" docs/PRD-v8.1.md` returns nothing, your checkout predates
  the requirements commit. **Stop and report this** rather than guessing — but note
  that §2 below quotes the requirements verbatim, so you can proceed from this
  document alone if a human confirms.
- `docs/LOGGING.md` and `docs/CONFIGURATION.md` may have uncommitted edits that
  document the *target* behavior (deliberately ahead of the code). If they are
  absent from your checkout, treat §2 as the source of truth and expect Task 7 to
  create them.
- **Toolchain:** the authoring session had no `dotnet` available, so **no build,
  format, or test command in this plan has been executed**. Verify your own
  toolchain (`dotnet --version`, `dotnet csharpier --version`) before relying on
  the verification commands, and report if anything is missing.

**Artifacts in this set:**

| File | Purpose |
| --- | --- |
| `tasks/plan.md` | This document: context, decisions, risks, and the review-loop spec (§9) |
| `tasks/todo.md` | The task checklist with per-task acceptance criteria and verification |
| `tasks/declined-findings.md` | Findings deliberately not fixed; **every reviewer must read it first** |
| `tasks/review-round-<N>.md` | You create one per round (plan §9.2 Step 4) |

**Provenance:** this plan and its file/line inventory were produced with AI assistance
from a source-code review. Line numbers were accurate at branch
`copilot/dms-1457-validate-correlation-id` at authoring time — **re-verify each
reference against the working tree** before acting on it, and treat any mismatch as a
signal the codebase has moved rather than as licence to guess. If this plan is shared
as a decision-support document, note the AI assistance and that a human reviewed it.

---

## 1. Overview

DMS accepts a client-supplied correlation ID from a host-configured request header
and uses it to tie a client-visible error response to the server's log entries.
Today that value is normalized inconsistently: the frontend request-logging layer
applies a character filter and a length cap, but most error response bodies echo
the value exactly as the client sent it. The result is that the ID a client reads
from a failed request is not reliably the ID that appears in the logs — defeating
the purpose of the correlation ID — and an unbounded, unfiltered client-controlled
string reaches multiple response paths.

This work makes normalization **uniform and single-sourced**: one function, applied
once at ingestion, so every downstream consumer — every log event and every error
response body — carries the identical normalized value.

**Bias toward simplicity** (repo Principle 4, `reference/AGENTIC-WORKFLOW.md`): the
goal is *fewer* places that touch this value, not a new abstraction layer. If your
implementation adds more than one new type, you have probably overbuilt it.

---

## 2. Requirements (verbatim — `docs/PRD-v8.1.md` §3.11)

These are the acceptance criteria. The functionality reviewer (§9) checks against
this text, not against your interpretation of it.

> - **FR-LOG-3.** The system SHALL apply a documented, logging-safe character
>   allowlist to a correlation ID — whether client-supplied or system-generated —
>   before it is used in a log entry or in any error response body. The allowlist
>   SHALL exclude, at minimum, control characters capable of forging additional log
>   lines or otherwise corrupting structured log output (e.g., carriage return, line
>   feed). It SHALL NOT be limited to only alphanumeric characters: a client-supplied
>   correlation ID commonly originates from an upstream system's own identifier
>   scheme, and narrowing the allowlist to alphanumerics defeats the purpose of
>   accepting a client-supplied value in the first place (see FR-LOG-2). This
>   correlation-ID-specific allowlist is distinct from, and SHALL NOT be conflated
>   with, any stricter allowlist the system applies to internally-controlled logged
>   values such as the request method or path.
> - **FR-LOG-4.** The system SHALL enforce a maximum length on a correlation ID,
>   whether client-supplied or system-generated, with a documented default and a
>   host-configurable override.
> - **FR-LOG-5.** When a client-supplied correlation ID contains one or more
>   characters excluded by the allowlist (FR-LOG-3), exceeds the maximum length
>   (FR-LOG-4), or both, the system SHALL NOT reject the request solely for this
>   reason. It SHALL instead deterministically adjust the value — removing
>   disallowed characters, truncating to the maximum length, or both — before using
>   it anywhere, so the request the client is actually trying to make still succeeds
>   or fails on its own merits rather than on the shape of an operational identifier.
> - **FR-LOG-6.** The adjustment described in FR-LOG-5 SHALL be applied identically
>   everywhere a correlation ID is used — every log entry and every error response
>   body, regardless of HTTP status code or which part of the system produces the
>   response — so that NFR-OBS-1's log/response parity guarantee holds for every
>   failed request, not only a subset of failure types.

**Ordering decision for FR-LOG-5 — TRUNCATE FIRST, then filter.** *(Decided by the
product owner; was open question 1.)*

Filter-then-truncate and truncate-then-filter give different results: removing
characters first frees room, so a long hostile value would retain *more* trailing
content. **Truncate to `CorrelationIdMaxLength` first, then remove control
characters.** This matches what `LoggingMiddleware.cs:46-49` already does, so the
existing truncation test stays valid.

Implement this order in **exactly one** function (Task 2) and pin it with a test that
would fail if the order were swapped — e.g. a value whose first `MaxLength` characters
contain control characters, so the two orders yield observably different output.

> Note the consequence, and do not "fix" it: because truncation runs first, a value
> that is over-length **and** contains control characters can produce a result
> *shorter* than `CorrelationIdMaxLength`. That is correct and intended.

---

## 3. Current state

Verified by reading the code on branch `copilot/dms-1457-validate-correlation-id`.

| Location | Behavior today |
| --- | --- |
| `src/dms/core/EdFi.DataManagementService.Core.External/Model/TraceId.cs` | `public record struct TraceId(string Value)` — no validation, accepts anything |
| `src/dms/frontend/.../AspNetCoreFrontend.cs` (`ExtractTraceIdFrom`, ~line 401) | Reads the header named by `AppSettings:CorrelationIdHeader`; returns `new TraceId(raw)` with **no** filtering or capping. Empty setting ⇒ falls back to `HttpContext.TraceIdentifier` |
| `src/dms/frontend/.../Infrastructure/LoggingMiddleware.cs` | Truncates to `CorrelationIdMaxLength`, **then** filters via `LoggingSanitizer.SanitizeForLogging`. Uses the result for all its log events and for the JSON body it writes on an unhandled exception. ✅ parity here only |
| `src/dms/core/.../Response/FailureResponse.cs` | **Every** factory passes `correlationId: traceId.Value` — raw, unfiltered, uncapped. Covers 400/401/403/404/405/409/412/415/429/500/503 ❌ |
| `src/dms/frontend/.../Program.cs:217-226` (`MapFallback`) | Reads the header raw **and** hardcodes a `?? "correlationid"` fallback header name, so a host with `CorrelationIdHeader` empty still has client values honored here — contradicts `ExtractTraceIdFrom` ❌ |
| `src/dms/frontend/.../Modules/HealthCheckEndpointModule.cs:104` | `TraceId traceId = new(httpContext.TraceIdentifier);` — **target-typed `new`**, so a `new TraceId(` grep misses it. Bypasses `ExtractTraceIdFrom`; no cap applied ❌ |
| `src/dms/frontend/.../Infrastructure/WebApplicationBuilderExtensions.cs:238` | 429 rate-limit body: calls `ExtractTraceIdFrom`, so it inherits whatever that returns |
| `src/dms/frontend/.../Modules/TokenEndpointModule.cs:87` | Calls `ExtractTraceIdFrom`; logging only |
| `src/dms/core/.../Utilities/LoggingSanitizer.cs` | `SanitizeForLogging` delegates to the canonical `LogSanitizer.SanitizeForLog`. `SanitizeForConsole` strips control chars **but deliberately preserves `\r` and `\n`** |
| `src/dms/backend/EdFi.DataManagementService.Backend.External/LogSanitizer.cs` | The canonical strict allowlist: `!char.IsControl(c) && (char.IsLetterOrDigit(c) \|\| c is ' ' or '_' or '-' or '.' or ':' or '/' or '\\')`. Note it permits backslash. Two-pass, allocation-exact `string.Create` implementation |
| `src/dms/frontend/.../Configuration/AppSettings.cs` | `CorrelationIdMaxLength`, default `255` (`DefaultCorrelationIdMaxLength`), rejected at startup if `<= 0`. ✅ FR-LOG-4 largely satisfied already |

**Consequences to fix:** the strict allowlist reused from `Method`/`Path` violates
FR-LOG-3's "SHALL NOT be limited to only alphanumeric"; `FailureResponse` and
`MapFallback` violate FR-LOG-6.

**Do not reuse `SanitizeForConsole`** for this — preserving `\r`/`\n` is exactly the
log-forging vector FR-LOG-3 names.

---

## 4. Architecture decisions

**AD-1 — Normalize once, at the frontend ingestion boundary.** Make
`ExtractTraceIdFrom` (and every other place a `TraceId` is first created from
request data) return an already-normalized value. Downstream consumers —
`FailureResponse`'s ~30 factories, all handlers, all log events — then need **no
changes**, because the value they receive is already correct.
*Rejected alternative:* normalizing at each of the ~30 `FailureResponse` call sites.
Repetitive, and a new call site would silently reintroduce the bug.

> **Corollary — do not "fix" the response-body outliers.** These four sites build a
> body without going through `FailureResponse.CreateBaseJsonObject`:
> `FailureResponse.cs:332` (`ForAuthenticationFailure`),
> `Middleware/ProfileResolutionMiddleware.cs:175`,
> `Middleware/JwtRoleAuthenticationMiddleware.cs:174`, and
> `Handler/Utility.cs:104` (`ToJsonError`). Under AD-1 they are **already correct**,
> because the `traceId.Value` they read is normalized at construction. Touching them
> is unnecessary churn. Reviewers: these are **not** findings.
>
> The sites that genuinely need changing are only those that **construct a `TraceId`
> without going through `ExtractTraceIdFrom`** — currently `Program.cs:226` (Task 4)
> and `HealthCheckEndpointModule.cs:104` (Task 5).

**AD-2 — Do not put normalization inside `TraceId`.** `TraceId` lives in
`Core.External`, a published contract assembly, and normalization needs the
`CorrelationIdMaxLength` value from frontend configuration — which `Core.External`
must not depend on. Changing the record struct's shape would also churn every
construction site including tests.
*Consequence:* nothing structurally prevents a future raw `new TraceId(...)`. Mitigate
with Task 5's guard test, not with a type change.

**AD-3 — Add a correlation-ID-specific filter; keep the strict one for `Method`/`Path`.**
FR-LOG-3 requires these be distinct. Implement the character filter as a new member
alongside the existing canonical one (`LogSanitizer` in
`EdFi.DataManagementService.Backend.External`, surfaced through
`Core/Utilities/LoggingSanitizer.cs` the same way `SanitizeForLogging` already is),
so there remains a single definition of each allowlist.
Leave `SanitizeForLogging` untouched; `Method` and `Path` keep using it.

**The allowlist is defined as "all printable non-control characters"** — i.e. the
predicate is a single negative test, `!char.IsControl(c)`, with no positive character
enumeration at all. *(Decided by the product owner; was open question 2.)*

Consequences to implement deliberately:

- **Every control character is removed**, `\r`, `\n`, `\t`, and `\0` included. This is
  the whole security purpose of the filter.
- **Everything else is preserved**, including `+ = { } @ | , # ( ) [ ] < > " '` and
  non-ASCII letters, digits, and symbols. `char.IsControl` is Unicode-aware, so this
  admits the full printable Unicode range. That is the intended reading of FR-LOG-3's
  "SHALL NOT be limited to only alphanumeric characters."
- **Do not add a positive allowlist on top of it.** A reviewer may argue for excluding
  quotes, angle brackets, or non-ASCII on injection grounds. That argument is already
  settled: the value is written through structured-log parameters and
  `JsonSerializer`/`JsonObject`, both of which escape their own output, so character
  exclusion is not the control that protects those sinks. Report such a concern as an
  observation if you must, but **do not narrow the allowlist without a human decision**.
- The length cap (FR-LOG-4) is what bounds the value's size; the filter bounds only its
  character set.

**AD-4 — CMS (`src/config`) is out of scope.** Verified: CMS has a completely separate
`FailureResponse` (`src/config/datamodel/.../Infrastructure/FailureResponse.cs`) whose
factories take a plain `string correlationId`, and
`Infrastructure/FailureResponseWriter.cs:52` unconditionally overwrites
`correlationId` with `context.TraceIdentifier`. CMS reads only three headers anywhere
(`Authorization`, the tenant header, and nothing else) — it has **no
`CorrelationIdHeader` setting at all**, so no client-controlled value can reach it.
`PRD-v8.1.md` is the **Ed-Fi API (DMS)** product PRD; CMS is governed by
`docs/PRD-CMS-v1.0.md`, whose FR-ERROR-2 *requires* `correlationId` to equal
`HttpContext.TraceIdentifier`, and NFR-OBS-4/OUT-9 record the asymmetry as deliberate.
`HttpContext.TraceIdentifier` is a bounded, server-generated token (e.g.
`0HNCTN1IRQMDG:00000001`), so neither the allowlist nor a length cap buys anything
there.

**Do not modify `src/config`.** A reviewer is likely to report "FR-LOG-4 is unmet in
CMS" — that is a false positive, pre-recorded in `tasks/declined-findings.md`. This
application has separate requirements.

**AD-5 — Normalization is idempotent.** Normalizing an already-normalized value must
be a no-op. This lets any belt-and-braces second application (e.g. leaving
`LoggingMiddleware`'s existing call in place) stay harmless. Pin it with a test.

---

## 5. Task list

Tasks are ordered so dependencies come first and the tree stays green between them.
Full task detail is in `tasks/todo.md`; this is the index.

### Phase 1 — The normalizer (foundation)

- **Task 1** — Add the correlation-ID character filter + unit tests. *(S)*
- **Task 2** — Add the combined normalize (filter + length cap) entry point + unit tests. *(S)*

### Checkpoint A

- Build clean, format clean, new unit tests pass, no production call sites changed yet.

### Phase 2 — Single ingestion point

- **Task 3** — Normalize inside `ExtractTraceIdFrom`; reconcile `LoggingMiddleware`. *(M)*
- **Task 4** — Fix `Program.cs` `MapFallback`: route through normalization and remove the hardcoded `"correlationid"` fallback. *(S)*
- **Task 5** — Enumerate and fix every remaining `TraceId` construction site; add the guard test. *(M)*

### Checkpoint B

- Every error response body and every log event carries the normalized value.
- Full DMS unit test suite green.

### Phase 3 — Prove parity, then reconcile docs

- **Task 6** — Add an integration-level parity test across representative status codes. *(M)*
- **Task 7** — Reconcile `docs/LOGGING.md` and `docs/CONFIGURATION.md` with what shipped. *(S)*

### Requirement → task traceability

The functionality reviewer should use this to confirm nothing is orphaned.

| Requirement | Satisfied by | Evidence to demand |
| --- | --- | --- |
| **FR-LOG-3** (allowlist; control chars excluded; not alnum-only; distinct from `Method`/`Path`) | Tasks 1, 3 | A test proving `+ = { } @ \| ,` **survive** and `\r \n \t \0` are **removed**; `SanitizeForLogging` unchanged |
| **FR-LOG-4** (max length; documented default; host-configurable) | Task 2 (+ already-shipped `CorrelationIdMaxLength`, default `255`, startup-validated `> 0`) | A truncation test at the configured bound; `docs/CONFIGURATION.md` row |
| **FR-LOG-5** (normalize, never reject; deterministic) | Tasks 2, 3 | A test that a hostile ID yields the **same HTTP status** as a clean one; a pinned filter/truncate order; idempotence |
| **FR-LOG-6** (applied identically everywhere) | Tasks 3, 4, 5, 6 | The parity test across ≥3 status codes from ≥2 layers; the Task 5 enumeration output |

### Checkpoint C — Complete

- All FR-LOG-3..6 acceptance criteria demonstrably met, reviewers returning only Nit-level findings.

---

## 6. Testing strategy

Follow `AGENTS.md`: **NUnit + FluentAssertions**, FakeItEasy for mocks. Fixtures named
`Given_*`, a `Setup` method doing arrange+act, and one `It_*` method per assertion.
Match the surrounding file's style.

**Two notable coverage gaps that exist today** — both are opportunities, not
prerequisites:

- **`LogSanitizer.SanitizeForLog` has no direct test anywhere.** Its only coverage is
  indirect, through `LoggingSanitizerTests` (which has exactly one assertion for
  `SanitizeForLogging` — CR/LF removal — and five for `SanitizeForConsole`).
- **`ExtractTraceIdFrom` has no test at all**, despite being the choke point for the
  entire request pipeline. Closest coverage is incidental, in `RateLimitTests.cs`.

Conversely, `LoggingMiddlewareTests.cs` **already** asserts truncation to
`DefaultCorrelationIdMaxLength` and filtering. Preserve that coverage; do not
duplicate it.

Edge cases that must be covered somewhere:

1. **Well-formed ID passes through byte-for-byte** — the no-regression case.
2. **Upstream-realistic IDs survive** — values containing `+`, `=`, `{`, `}`, `@`,
   `|`, `,` must **not** be stripped (this is the FR-LOG-3 requirement that the
   current strict allowlist violates; a test asserting these survive is the single
   most important new test).
3. **CR/LF and other control characters are removed** — `"trace\r\nid"` must not
   yield two log lines.
4. **Over-length value is truncated** to `CorrelationIdMaxLength`.
5. **Combined** over-length *and* control characters — asserts the chosen order.
6. **Idempotence** — normalize(normalize(x)) == normalize(x).
7. **Empty / whitespace-only / null-ish** input does not throw.
8. **System-generated fallback** (`HttpContext.TraceIdentifier`) is unchanged in the
   normal case — FR-LOG-5's "behavior for system-generated IDs is unchanged".
9. **Log/response parity** — for a hostile client-supplied ID, the `correlationId` in
   the response body equals the `TraceId` in the log event, across several status
   codes.
10. **A malformed correlation ID never changes the request's outcome** — same status
    code as the identical request with a clean ID (FR-LOG-5).

### Verification commands

Unverified in the authoring environment — confirm they work before trusting them.

```bash
# Format (required before any commit)
dotnet csharpier format src/dms

# Targeted unit tests
dotnet test src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/EdFi.DataManagementService.Core.Tests.Unit.csproj
dotnet test src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.csproj
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Tests.Unit/EdFi.DataManagementService.Backend.Tests.Unit.csproj

# Whole-solution gates
./build-dms.ps1 Build
./build-dms.ps1 UnitTest
```

Integration/E2E (Task 6) needs Docker and database setup — see `AGENTS.md`. If that
environment is unavailable, say so explicitly and downgrade Task 6 to an in-process
API-level test in `src/dms/tests/EdFi.DataManagementService.Tests.Integration/`
rather than silently skipping it.

---

## 7. Risks and mitigations

| Risk | Impact | Mitigation |
| --- | --- | --- |
| Loosening the allowlist re-opens log injection | **High** | The new filter must strip *all* control characters, `\r`/`\n` included. Test 3 above is mandatory. Do not copy `SanitizeForConsole` |
| A `TraceId` construction site is missed, leaving a path un-normalized | **High** | Task 5 enumerates by grep rather than trusting this plan's table; guard test pins the invariant |
| Normalization applied twice, in different orders, yielding different values | Medium | AD-5 idempotence + a single normalize entry point |
| Response bodies change shape for existing clients | Medium | Only the *value* changes, never the field name or structure. No contract change; confirm no E2E snapshot asserts a literal correlation ID |
| Reviewers churn on style and never converge | Medium | §9 severity rubric + declined-findings ledger |
| Agent "fixes" failures by weakening tests | **High** | §9 forbids it explicitly and the team lead must diff test files each round |

---

## 8. Decisions (all resolved — do not reopen)

Every open question in this plan has been **decided by the product owner**. They are
recorded here as settled constraints, not as topics for discussion. A reviewer that
argues against one of these is out of scope; note it and move on.

1. **Truncate first, then filter.** ✅ Decided. See the ordering decision in §2 and
   Task 2. Matches existing `LoggingMiddleware` behavior.
2. **The allowlist is "all printable non-control characters"** — a bare
   `!char.IsControl(c)` with no positive character enumeration. ✅ Decided. See AD-3
   for the consequences and for why narrowing it is not a valid finding.
3. **Fold the `MapFallback` fix into this work.** ✅ Decided. Task 4 is in scope for
   this ticket, including removing the hardcoded `?? "correlationid"` fallback. No
   separate ticket.
4. **Public documentation is out of scope for this repository.** ✅ Decided. The
   client-facing documentation of correlation ID behavior — that a maximum length
   applies, that over-length values are truncated, and that control characters are
   removed — belongs in the **separate Ed-Fi documentation repository**, not here.
   **Action required of you:** end the PR description with an explicit `TODO` noting
   that public documentation needs to cover correlation ID usage, including the
   maximum length and truncation behavior, and that the work belongs in the docs
   repository. Do not create files or tickets for it in this repo.
5. **Do not change the `traceId` field name.** ✅ Decided. `LoggingMiddleware.cs:184`
   keeps emitting `traceId` even though every other DMS error body emits
   `correlationId`. FR-LOG-6 tolerates either. The inconsistency is real — it turns out
   to be the narrow symptom of two competing HTTP 500 body contracts — and is filed as
   **[DMS-1518](https://edfi.atlassian.net/browse/DMS-1518)**. It is **not** part of
   this work. Remains recorded as declined finding **D-4**.
6. **The three duplicate copies of the strict allowlist are acceptable.** ✅ Decided.
   `Backend.External/LogSanitizer.cs`,
   `src/config/datamodel/.../LoggingUtility.cs`, and the test-only copy in
   `EdFi.InstanceManagement.Tests.E2E/Infrastructure/LogSanitizer.cs` stay as they
   are, kept in sync by their existing comments. **No follow-up ticket is required.**
   Do not consolidate them, and do not report the duplication as a finding — it is
   pre-existing and explicitly accepted. Remains recorded as declined finding **D-5**.

If you encounter a genuinely *new* ambiguity not covered above, surface it to the
human in your final report rather than guessing.

---

## 9. THE REVIEW LOOP — orchestration you must follow

You are the **team lead**. You do **not** write production code yourself; you
delegate, review the reports, decide, and re-delegate. This mirrors stage 6 of
`reference/AGENTIC-WORKFLOW.md` ("several cycles of review → fix → re-review"), which
also asks that each round be captured in a notes file.

### 9.1 Roles

| Role | Writes code? | Context | Job |
| --- | --- | --- | --- |
| **Team lead** (you) | No | Persistent across rounds | Delegate, compile findings, adjudicate severity, decide stop/continue |
| **Implementation sub-agent** | **Yes** — only this role | Fresh each round | Complete the tasks, or apply the round's correction list |
| **Functionality reviewer** | No — **read-only** | Fresh each round | Check the diff against §2 FR-LOG-3..6 |
| **Clean-code reviewer** | No — **read-only** | Fresh each round | Code smells, consistency with surrounding code, test coverage |

Reviewers must be **separate, fresh-context sub-agents** — never the implementer
reviewing its own work, and never one agent doing both reviews. Reviewers **report**;
they must not edit files.

### 9.2 Round procedure

**Step 1 — Implement.** Spawn the implementation sub-agent.

- Round 1: give it §1–§8 of this document and `tasks/todo.md`, and have it work the
  tasks in order.
- Rounds 2+: give it the correction list from the previous round's Step 4, plus the
  declined-findings ledger (§9.5) so it does not undo a deliberate decision.
- Require it to run format + the targeted unit tests before reporting back, and to
  report honestly which commands it ran and what failed.

**Step 2 — Functionality review.** Spawn the functionality reviewer. Its prompt must
include §2 verbatim. It checks **only**: does the implementation satisfy FR-LOG-3,
FR-LOG-4, FR-LOG-5, FR-LOG-6, and the §6 edge cases? It must cite `file:line` for
every observation and assign a severity from §9.3. It must explicitly state, per FR,
whether it is **met / partially met / not met**.

**Step 3 — Clean-code review.** Spawn the clean-code reviewer. It checks: code smells,
duplication, dead code, naming, consistency with the conventions in `AGENTS.md`
(.NET 10 idioms, `is null`, file-scoped namespaces) and with the immediately
surrounding code, and **test coverage** — including whether the §6 edge cases have
real assertions rather than vacuous ones. `file:line` + severity for each observation.

**Step 4 — Compile and adjudicate.** You then:

1. Merge both reports into one list, de-duplicated.
2. Re-confirm each severity yourself against §9.3 — reviewers inflate. Downgrade or
   upgrade with a one-line reason.
3. Decide per finding: **fix now**, or **decline** (with rationale → §9.5 ledger).
4. Write the whole round to `tasks/review-round-<N>.md`: both raw reports, your
   adjudicated list, and your decisions.
5. Evaluate the stop condition (§9.4). If continuing, return to Step 1 with the
   correction list.

### 9.3 Severity rubric (pin these; do not let reviewers invent their own)

| Severity | Means | Examples for this task |
| --- | --- | --- |
| **Critical** | Violates a FR, or introduces a security or data-integrity defect | `\r\n` still reaches a log sink; a response path still echoes the raw value (FR-LOG-6 unmet); build or tests broken |
| **High** | Requirement met but unreliably; or a real bug on a plausible path | Normalization missed on one construction site; order-of-operations inconsistency between two paths; missing test for an edge case in §6 |
| **Medium** | Works, but a maintenance hazard likely to cause a future defect | Logic duplicated in two places; normalization not idempotent; test asserts implementation detail rather than behavior |
| **Low** | Genuine improvement, no correctness impact | Naming, XML doc gaps, a test that could be a `TestCase` table |
| **Nit** | Taste, formatting, wording | Comment phrasing, member ordering, blank lines |

A finding that only says "consider…" with no failure scenario is a **Nit** at most.

### 9.4 Stop conditions

Stop when **either** holds:

- **(a) Convergence:** in a single round, the functionality reviewer reports every FR
  as **met** *and* both reviewers' observations are **all Nit** severity. This is the
  desired exit.
- **(b) Budget:** **5 rounds** of Step 1 have completed. Stop even if findings remain.

Regardless of which condition ends the loop, you must **also** confirm these
objective gates before declaring done — a clean review over a broken build is a
false pass:

- [ ] `dotnet csharpier format src/dms` produces no diff
- [ ] `./build-dms.ps1 Build` succeeds
- [ ] `./build-dms.ps1 UnitTest` passes
- [ ] No test was deleted, skipped, `[Ignore]`d, or had assertions removed relative
      to the branch point (`git diff --stat` on test files, and read the diff)

If you stop on **(b)** with unresolved Critical or High findings, say so plainly at
the top of your final report. Do not present a budget-exhausted loop as a success.

### 9.5 Declined-findings ledger

Maintain `tasks/declined-findings.md`. Every finding you decide not to fix goes there
with its severity and your rationale. Every reviewer prompt from round 2 onward must
instruct the reviewer to read it first and **not re-raise** anything in it. This is
what stops the loop oscillating.

### 9.6 Hard constraints on the loop

- **Never weaken a test to make a round pass.** Do not delete, skip, `[Ignore]`,
  loosen, or narrow an existing test's assertions to get to green. If an existing
  test genuinely encodes the old (wrong) behavior, that is a **finding to report and
  adjudicate explicitly** — with the old expectation quoted in
  `tasks/review-round-<N>.md` — not a quiet edit.
- **No scope creep.** Anything outside FR-LOG-3..6 and §5's tasks is out of bounds.
  Do not modify `src/config` (AD-4). All of §8 is **decided** — do not reopen a
  decision, and do not implement anything §8 places out of scope.
- **Do not delete the `tasks/` files.** Per the warning at the top of this document
  they are ephemeral and must be removed with `git rm` before merge — **by a human,
  not by you**. Leave them in place.
- **No git operations that change shared state.** Per `reference/AGENTIC-WORKFLOW.md`
  Principle 1, a human owns commits, pushes, PR creation, and merges. Commit locally
  on the working branch if useful, but **do not push, do not open a PR, do not merge**
  without explicit human approval. End the loop with a summary and the diff.
- **Report failures honestly.** If tests fail, quote the output. If you skipped a
  verification step (e.g. no Docker for Task 6), say which and why. A clean-looking
  report that hides a failing gate is the worst possible outcome here.
- **Do not fabricate a reviewer's findings.** Each report must come from an actual
  sub-agent run.

### 9.7 Final report

When the loop ends, produce: the stop condition that fired; per-FR met/not-met status;
the count of findings by severity per round (showing the trend); the declined ledger;
which verification gates passed and which did not; and the complete diff for human
review.

### 9.8 Draft PR description

Draft (do **not** publish — §9.6) a PR description covering what changed and why, plus
a test plan. State explicitly:

- the chosen normalization order (**truncate then filter**) and that it is deliberate;
- the allowlist definition (**all printable non-control characters**);
- that the `MapFallback` hardcoded-header fix is included (§8 decision 3);
- that the `traceId` vs `correlationId` field-name inconsistency is **knowingly left
  alone** and tracked separately (§8 decision 5).

**It must end with this TODO** (§8 decision 4):

```markdown
TODO: Public documentation needs to cover the use of the correlation ID, including
the maximum length and the truncation of longer values. That work belongs in the
Ed-Fi documentation repository, not this one.
```

---

## 10. Definition of done

- [ ] FR-LOG-3, FR-LOG-4, FR-LOG-5, FR-LOG-6 each demonstrably satisfied, with a
      named test backing each
- [ ] One normalization function, called from one ingestion point
- [ ] `Method`/`Path` still use the stricter existing allowlist, unchanged
- [ ] Every §6 edge case covered by a real assertion
- [ ] Truncate-then-filter order implemented in one place and pinned by a test (§8.1)
- [ ] Allowlist is `!char.IsControl(c)` with no positive enumeration (§8.2)
- [ ] `MapFallback` hardcoded `"correlationid"` fallback removed (§8.3, Task 4)
- [ ] `traceId` field name in the 500 body **left unchanged** (§8.5)
- [ ] Format, build, and unit-test gates green (§9.4)
- [ ] No test weakened or removed
- [ ] `src/config` untouched
- [ ] Docs reconciled with shipped behavior (Task 7)
- [ ] Draft PR description ends with the public-documentation TODO (§9.8, §8.4)
- [ ] `tasks/` files left in place for a human to `git rm` before merge
- [ ] Draft PR has been created
