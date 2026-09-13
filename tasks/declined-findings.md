# Declined Findings Ledger — DMS-1457 / FR-LOG-3..6

Every reviewer sub-agent (functionality and clean-code) **must read this file before
reviewing** and must **not re-raise** anything recorded here. The team lead appends to
it in Step 4 of each round (see `tasks/plan.md` §9.5).

Format: one entry per declined finding, with the severity as reported and the
rationale for declining.

---

## Pre-seeded before round 1

These are known-likely false positives, declined in advance with rationale. They are
pre-recorded because a reviewer reading only the FR text will very plausibly report
them.

### D-1 — "FR-LOG-4 is unmet: CMS enforces no correlation ID max length"

- **Reported severity (anticipated):** High / Critical
- **Decision:** Declined — out of scope by design.
- **Rationale:** `docs/PRD-v8.1.md` is the **Ed-Fi API (DMS)** product PRD. CMS is
  governed by `docs/PRD-CMS-v1.0.md`, whose **FR-ERROR-2 requires** `correlationId`
  to equal `HttpContext.TraceIdentifier`, with **NFR-OBS-4** and **OUT-9** recording
  the lack of client-supplied correlation IDs as a deliberate, documented asymmetry.
  CMS has no `CorrelationIdHeader` setting and reads no correlation header at all, so
  no client-controlled value can reach it; `HttpContext.TraceIdentifier` is a bounded
  server-generated token. See plan AD-4. **`src/config` must not be modified.**

### D-2 — "The response-body outliers still use `traceId.Value` directly"

Refers to `FailureResponse.cs:332` (`ForAuthenticationFailure`),
`Middleware/ProfileResolutionMiddleware.cs:175`,
`Middleware/JwtRoleAuthenticationMiddleware.cs:174`, and `Handler/Utility.cs:104`.

- **Reported severity (anticipated):** High
- **Decision:** Declined — already correct.
- **Rationale:** Under plan AD-1, normalization happens at `TraceId` construction, so
  the `traceId.Value` these sites read is **already normalized**. Editing them adds
  churn without changing behavior. The only sites needing change are those that
  construct a `TraceId` while bypassing `ExtractTraceIdFrom`.

### D-3 — "`Core/Model/No.cs:106` constructs an un-normalized `TraceId`"

- **Reported severity (anticipated):** Medium
- **Decision:** Declined — inert code path.
- **Rationale:** `No.CreateFrontendRequest` is a null-object factory. Its only caller
  is `No.RequestInfo` (`No.cs:130`), which has no production callers. It is not
  reachable from an HTTP request, so it cannot carry client input.

### D-4 — "`LoggingMiddleware` emits `traceId` while everything else emits `correlationId`"

- **Reported severity (anticipated):** Medium
- **Decision:** **Declined by product owner** — out of scope; filed as
  **[DMS-1518](https://edfi.atlassian.net/browse/DMS-1518)**.
- **Rationale:** Real inconsistency, but renaming the field in the 500 response body
  is a client-visible contract change beyond FR-LOG-3..6, and FR-LOG-6 explicitly
  tolerates either key name. DMS-1518 covers the broader defect this is a symptom of:
  DMS emits **two** competing 500 body shapes — the Ed-Fi problem-details shape via
  `FailureResponse.ForSystemError`, and an ad-hoc `{message, traceId}` via
  `FailureResponse.ForServerErrorMessageBody` (4 call sites). **Do not rename the
  field, and do not consolidate the 500 shapes, in this work.** Plan §8 decision 5.

### D-5 — "The strict allowlist is duplicated in three places"

- **Reported severity (anticipated):** Medium
- **Decision:** **Accepted as-is by product owner** — not a defect, and **no follow-up
  ticket is required**.
- **Rationale:** Pre-existing condition, not introduced by this work. The three copies
  (`Backend.External/LogSanitizer.cs`, `src/config/datamodel/.../LoggingUtility.cs`,
  and the test-only `EdFi.InstanceManagement.Tests.E2E/Infrastructure/LogSanitizer.cs`)
  stay as they are, kept in sync by their existing comments. Consolidating would
  require touching `src/config`, forbidden by AD-4. **Do not report this.** Plan §8
  decision 6.

### D-7 — "The allowlist is too permissive — it admits quotes, angle brackets, or non-ASCII"

- **Reported severity (anticipated):** High / Medium
- **Decision:** **Declined by product owner** — the allowlist definition is settled.
- **Rationale:** Plan §8 decision 2 fixes the allowlist as "all printable non-control
  characters" (`!char.IsControl(c)`, no positive enumeration). The value reaches log
  sinks through structured-log parameters and response bodies through
  `JsonSerializer`/`JsonObject`, both of which escape their own output — character
  exclusion is not the control protecting those sinks, and narrowing the set would
  re-break FR-LOG-3's explicit "SHALL NOT be limited to only alphanumeric characters"
  requirement. See AD-3.

### D-8 — "Public/client documentation of the new behavior is missing"

- **Reported severity (anticipated):** Medium
- **Decision:** **Deliberately out of scope for this repository.**
- **Rationale:** Plan §8 decision 4. Client-facing documentation of correlation ID
  behavior (max length, truncation, character removal) belongs in the **separate Ed-Fi
  documentation repository**. The only in-repo obligation is a `TODO` note at the end
  of the PR description. `docs/LOGGING.md` and `docs/CONFIGURATION.md` (Task 7) cover
  the host-facing side and are sufficient here.

### D-6 — "XML doc comments still say 'whitelist' instead of 'allowlist'"

- **Reported severity (anticipated):** Low / Nit
- **Decision:** Optional. Not a defect; do not block on it.
- **Rationale:** Terminology cleanup in code comments is welcome if the file is already
  being edited, but must not expand the diff or consume review rounds. Only the PRD and
  host-facing docs were required to use "allowlist".

---

## Round-by-round declines

*(Team lead: append below, one section per round.)*

### Round 1

#### D-9 — "`SanitizeForLog`'s 55-line body is copied into `SanitizeCorrelationIdForLog`"

`LogSanitizer.cs:85-141` vs `:18-74`, differing only in which predicate is called.

- **Reported severity:** Medium (clean-code reviewer). **Re-confirmed as:** Low.
- **Decision:** Declined for this ticket — genuine smell, wrong ticket.
- **Rationale:** `tasks/todo.md` Task 1 explicitly directed mirroring the reference
  implementation's shape ("Match that shape … so the new function is consistent with its
  neighbour"), and AD-3 directs leaving the strict `SanitizeForLogging` path untouched. A
  delegate-based extraction rewrites allocation-sensitive code on the hot path shared by
  *every* `Method`/`Path` log call, for zero behavior change, which is scope creep under
  §9.6. The concrete harm cited — the algorithm changing in one copy and not the other —
  already manifested once as comment drift, and that instance is fixed directly by R1-03.
  **Recommended as a follow-up ticket**, noted in the final report. Distinct from D-5,
  which covers three pre-existing cross-assembly copies of the *strict* allowlist.

#### D-10 — "`ExtractTraceIdFromTests.cs` is named after a method, not a code area"

- **Reported severity:** Nit. **Re-confirmed as:** Nit.
- **Decision:** Declined.
- **Rationale:** `tasks/todo.md` Task 3 names this exact file explicitly: "**New test
  file:** `src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/ExtractTraceIdFromTests.cs`".
  Renaming it would diverge from the plan for a naming preference. The sibling-file
  convention the reviewer cites is real but does not outrank an explicit instruction.

#### D-11 — "The `/health/document-cache` 401/403 bodies now honor the configured correlation header"

Raised by the implementation sub-agent as a new ambiguity, not covered by plan §8.

- **Severity:** Medium (a decision, not a defect).
- **Decision:** **Accepted as intended.** Keep the new behavior.
- **Rationale:** Plan §3's current-state table lists this site's bypass of
  `ExtractTraceIdFrom` as the defect ("Bypasses `ExtractTraceIdFrom`; no cap applied ❌"),
  and `todo.md` Task 5 assigns exactly that fix. Task 4 pushes the same direction for
  `MapFallback`. Routing through `ExtractTraceIdFrom` makes the endpoint consistent with
  every other path, which is FR-LOG-6's intent, and the value is now normalized so it
  carries no injection risk. It *is* a behavior change beyond pure normalization — that
  endpoint previously always used `HttpContext.TraceIdentifier` and ignored the client
  header — so it is called out explicitly in the final report and the PR description
  rather than left for a reviewer to discover.

#### D-12 — "No E2E negative scenarios were added to `CorrelationId.feature`"

- **Severity:** Low.
- **Decision:** Declined/deferred with rationale.
- **Rationale:** Task 6 phrased this as "**Consider** adding negative scenarios here rather
  than editing the existing one" — a consideration, not an acceptance criterion, and every
  Task 6 acceptance criterion is met in-process. `StepDefinitions.cs:1309-1310` strips
  `correlationId` from both actual and expected bodies before comparing, so a meaningful
  negative scenario would require a new step definition, and no E2E environment is runnable
  in this sandbox to verify it. Adding an unverifiable E2E scenario is worse than adding
  none. The in-process parity fixture covers the same ground with stronger assertions.
  The existing `@API-061` scenario was not touched and still passes.

### Round 3

#### D-13 — "`LoggingMiddleware`'s constructor should be a primary constructor"

- **Reported severity:** Nit. **Re-confirmed as:** Nit.
- **Decision:** Declined.
- **Rationale:** After round 3 removed the vacuous `_correlationIdMaxLength` field, the
  constructor does nothing but two `?? throw` null guards, which is a fair observation. But
  the change is pure taste with no failure or maintenance scenario attached, on a file in
  every request's path. `AGENTS.md` lists primary constructors among the .NET 10 idioms to
  *use*, not a rule to retrofit into existing types. Declining keeps the diff proportionate.

#### D-14 — "`OAuthManager.cs:70` logs the upstream identity provider's raw response body through no sanitizer"

- **Reported severity:** raised by the round-3 functionality reviewer as an explicit
  out-of-scope observation, not as a finding against FR-LOG-3..6.
- **Decision:** Declined as out of scope for DMS-1457 — **but escalated in the final report
  as a recommended follow-up ticket.** This is not a silent drop.
- **Rationale:** `{Content}` is an external service's raw response body logged at Warning
  level with no sanitizer, one argument away from the R3-01 fix, so it is a genuine
  log-forging vector and worth its own ticket. It is not, however, a correlation ID: it is
  outside FR-LOG-3..6, the line is pre-existing and untouched by this diff, and plan §9.6
  forbids scope creep. Fixing it here would also require choosing a sanitizer for a value
  whose safe shape nobody has specified — `SanitizeForLogging` would mangle a JSON error
  payload, and `SanitizeForConsole` deliberately preserves `\r`/`\n`, which is the wrong
  control for a log sink. That is a design decision for a human, not a drive-by edit.

### Round 4

#### D-15 — "Entailed assertions remain in several `It_` methods"

`CorrelationIdNormalizerTests.cs:87-90`, `LoggingSanitizerTests.cs:163-167`,
`CorrelationIdParityTests.cs:297-317`.

- **Reported severity:** Nit. **Re-confirmed as:** Nit.
- **Decision:** Declined.
- **Rationale:** The reviewer's own analysis defeats the finding, and it said so: unlike the
  `HaveLength(8)` assertion removed in round 4, each of these is its own `It_` method with a
  requirement-named intent (`It_cannot_forge_an_additional_log_line`,
  `It_leaves_no_line_ending_that_could_forge_a_log_line`,
  `It_never_lets_a_control_character_reach_a_response_body_or_a_log_event`), so each documents
  a distinct guarantee even where the assertion is logically entailed. Removing them would
  trade documentation value for a purity the codebase does not observe — multi-assert `It_`
  methods are the norm here, roughly 7,100 of 11,600 in `src/dms`.

#### D-16 — "Nothing automated prevents a future log site from passing the `TraceId` struct again"

- **Reported severity:** Low. **Re-confirmed as:** Low.
- **Decision:** Declined for this ticket — **escalated in the final report as a recommended
  follow-up.** Not a silent drop.
- **Rationale:** `TraceId` is `public record struct TraceId(string Value)` with no `ToString()`
  override, so `logger.LogX("{TraceId}", traceId)` compiles silently and renders
  `TraceId { Value = … }` — the defect fixed in round 4 at `OAuthManager.cs:71`. The
  reviewer that raised it recommended a follow-up ticket rather than a change here, and I
  agree: both plausible fixes are disproportionate. A `ToString()` override modifies
  `Core.External`, a published contract assembly, which AD-2 keeps out of scope; and a
  source-scanning architecture test is machinery this repository has no precedent for (the
  reviewer checked — Task 5's "guard test" turned out to be the parity fixture, not a grep
  test). The proportionate mitigations already shipped: round 4's `<remarks>` warning on
  `SanitizeForLogging`, and the structural coverage at
  `CorrelationIdRecordingLoggerProvider.cs:52`, which records a `TraceId` property only when
  it is a `string` — so the frontend parity fixture's exact event-count assertion would fail
  if any of those sites regressed to passing the struct.

#### D-17 — "`GetResult.GetSuccess.LastModifiedTraceId` is a correlation-ID-shaped contract member"

`Core.External/Backend/GetResult.cs:28`.

- **Reported severity:** Nit, raised by the reviewer as explicitly out of scope.
- **Decision:** Declined.
- **Rationale:** Inert — all three production producers pass literal `null`, there is no DDL
  column behind it, and its only reader is a Polly `ResultFormatter` copy. Pre-existing and
  untouched by this diff. More importantly it is semantically *outside* FR-LOG-6: if it were
  ever populated it would carry a **prior** request's identifier, which the log/response
  parity guarantee for the current request neither covers nor should.

---

## Round 6 — product owner overrides

Two entries above were **reversed by the product owner** on PR #1236 and are now
implemented. Their original entries are left intact above, unedited, so the reasoning
that was current at the time is still readable. **Do not treat D-9 or D-16 as declined.**

### D-9 — REVERSED. Now implemented.

The duplicated sanitizer loop was collapsed into a single private
`LogSanitizer.Sanitize(string?, Func<char, bool>)` helper parameterized by the allowlist
predicate, matching the shape used by the closed competing PR #1232. Behavior is
unchanged: the leading `ReplaceLineEndings`, the early return of the *original* instance
when nothing needs removing, `string.Empty` when nothing survives, the exact-size
`string.Create`, and the `S3267` suppression all remain. The predicate travels with the
source in a value tuple so the `string.Create` lambda stays `static`. `IsAllowedChar`,
the strict `Method`/`Path` predicate, is untouched.

Alongside it, and for the same reason (alignment with #1232), the two members added by
this work were renamed to drop the `For…` suffix:
`LogSanitizer.SanitizeCorrelationIdForLog` → `LogSanitizer.SanitizeCorrelationId`, and
`LoggingSanitizer.SanitizeCorrelationIdForLogging` →
`LoggingSanitizer.SanitizeCorrelationId`. The pre-existing pair is asymmetric
(`SanitizeForLog` vs `SanitizeForLogging`) and the new members had propagated that;
dropping the suffix removes the asymmetry and is more accurate, since this value goes
into both log events and response bodies. The pre-existing `SanitizeForLog`,
`SanitizeForLogging` and `SanitizeForConsole` were **not** renamed — out of scope, and a
much larger blast radius.

### D-16 — REVERSED. Now implemented.

The decline rested on "a source-scanning architecture test is machinery this repository
has no precedent for". That premise was factually wrong:
`src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/CdcConnectorTemplateIntegrationBoundaryTests.cs`
walks up to the `.sln` with a `FindRepositoryRoot()` helper and asserts on the contents
of repository files. AD-2 names a guard test as the *only* mitigation for `TraceId`
being unvalidated, and what had shipped asserted only about four already-existing
endpoints.

`src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit/CorrelationIdConstructionSiteGuardTests.cs`
now scans production `src/dms` sources — comments and literals blanked first, so prose
cannot produce a false positive — and asserts (a) that the set of files constructing a
`TraceId`, in either the explicit `new TraceId(` or the target-typed
`TraceId t = new(` spelling, is exactly `{AspNetCoreFrontend.cs, Core/Model/No.cs}`, and
(b) that no production file passes a trace- or correlation-shaped argument to the strict
`SanitizeForLog`/`SanitizeForLogging`. Failures name the offending file, line and source
text. Both assertions were verified to fail against a deliberately planted violation.

### D-18 — DIRECTED by the product owner. Now implemented. Amends plan §8 decision 2.

The product owner directed that Unicode **format** characters (category `Cf`) be removed
from a correlation ID in addition to control characters. This **changes the allowlist that
plan §8 decision 2 previously fixed as a bare `!char.IsControl(c)`**; the plan text is
amended by this entry. The current rule, in
`src/dms/backend/EdFi.DataManagementService.Backend.External/LogSanitizer.cs`, is:

```csharp
static c => !char.IsControl(c) && char.GetUnicodeCategory(c) != UnicodeCategory.Format
```

so the removed set is now **Cc + Cf + the Unicode line/paragraph separators** (`U+2028`,
`U+2029`, removed by the leading `ReplaceLineEndings` as before).

**Security rationale.** `Cf` covers the bidirectional embeddings and overrides
`U+202A`–`U+202E` (notably RIGHT-TO-LEFT OVERRIDE), the isolates `U+2066`–`U+2069`, the
zero-width characters `U+200B`–`U+200D` and `U+2060`, the directional marks `U+200E`/`U+200F`,
`U+00AD` SOFT HYPHEN and `U+FEFF` BOM. None can forge a log line — both sinks escape their
own output — but each defeats FR-LOG-6's guarantee that an operator can *search the logs for
the ID the client received*: a bidi override renders the remainder of a log line
right-to-left in a viewer, so the displayed ID is not the stored ID; a zero-width character
makes two visually identical IDs distinct strings, so a copied ID silently fails to match.

**Do not report the `Cf` exclusion as a violation of plan §8 decision 2**, and **do not
re-raise D-7**. D-7 declined *narrowing the allowlist to alphanumerics*, which is a different
thing and **remains declined**: printable punctuation, symbols, non-ASCII letters (Lu/Ll/Lo)
and whitespace of category `Zs` — SPACE and `U+00A0` NO-BREAK SPACE included — must still be
preserved, per FR-LOG-3. Do not narrow the allowlist beyond Cc + Cf + Zl/Zp. The rule is
expressed as a Unicode **category** test on purpose, not a code-point enumeration; do not
propose converting it to a list.

Pinned by `Given_SanitizeCorrelationId_With_Bidirectional_Format_Characters`,
`Given_SanitizeCorrelationId_With_Zero_Width_Characters`,
`Given_SanitizeCorrelationId_With_Non_Ascii_Characters` and
`Given_SanitizeCorrelationId_Applied_Twice` in
`src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/Utilities/LoggingSanitizerTests.cs`.
Documented as a product change in `docs/LOGGING.md` §"Correlation ID normalization" and
`docs/CONFIGURATION.md`.

### D-19 — "`CorrelationIdMaxLength` needs an upper bound"

- **Reported severity (anticipated):** Low / Medium
- **Decision:** **Declined by the product owner.**
- **Rationale:** The suggestion that `CorrelationIdMaxLength` be validated against a maximum
  as well as a minimum was put to the product owner on PR #1236 and declined. The setting is
  host-operator configuration, not client input: a host that sets an absurd cap is
  misconfiguring its own logs, and the existing `> 0` validation — which fails loudly, at
  `Critical`, short-circuiting every request — already covers the case that breaks the
  service. **Do not re-raise this.**

**Everything else in D-1..D-17 remains declined.**
