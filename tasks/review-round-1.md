# Review Round 1 — DMS-1457 / FR-LOG-3..6

Team lead record per `tasks/plan.md` §9.2 Step 4. Contains both raw reviewer reports,
the compiled de-duplicated finding list, my re-confirmed severities, and my decisions.

- **Branch point:** `5202c06`
- **Round 1 implementation commit:** `7835227`
- **Working branch:** `claude/dms-1457-correlation-id-normalization` (PR base is
  `copilot/dms-1457-validate-correlation-id`)
- **Roles:** implementation sub-agent (fresh), functionality reviewer (fresh, read-only),
  clean-code reviewer (fresh, read-only, a different agent). Neither reviewer wrote code;
  both were tool-restricted to read-only and both read `tasks/declined-findings.md` first.

## Environment caveat that colors every gate in this round

The real `EdFi.DataStandard*.ApiSchema` NuGet packages are unreachable from this sandbox
(`pkgs.dev.azure.com` → `connect_rejected`, organization egress policy). They are
content-only, so local stand-ins carrying the repo's own minimal ApiSchema fixture were
used. Consequence: **6 pre-existing `Core.Tests.Unit` failures at the branch point** which
assert real shipped Ed-Fi resources exist in the package. They are unrelated to this work
and unchanged by it. Neither reviewer independently confirmed those 6 pass on a
fully-restored checkout — stated rather than glossed.

---

# Raw report 1 — Functionality reviewer

> Narrative preambles are condensed; every verdict, finding, `file:line`, severity and
> failure scenario is reproduced.

**Verification run:** `dotnet csharpier check src/dms` clean (2779 files);
Frontend.AspNetCore.Tests.Unit **550 passed / 2 failed** (exactly the two known
`LoggingMiddlewareTests` cases at lines 289 and 450); Core.Tests.Unit 5090 passed / 6
failed (all sandbox ApiSchema artifacts); `Core.Tests.Unit --filter ~LoggingSanitizer`
16/16 passed. Captured the full working-tree diff before the commit landed, then
re-verified against `git diff 5202c06..HEAD --stat` — identical file set and content
(13 files, +1230/−44).

## Per-FR verdict

| FR | Verdict | Evidence | Backing test |
| --- | --- | --- | --- |
| **FR-LOG-3** | **met** | Allowlist `!char.IsControl(c)` at `LogSanitizer.cs:152`; filter `LogSanitizer.cs:85-141`; surfaced `LoggingSanitizer.cs:39-40`; applied `CorrelationIdNormalizer.cs:52` before any use. Strict allowlist untouched (`LogSanitizer.cs:78-82`; `LoggingMiddleware.cs:43-45` still uses `SanitizeForLogging` for Method/Path/PathBase). Documented `docs/LOGGING.md:197-235`. | `It_preserves_printable_punctuation` (`CorrelationIdNormalizerTests.cs:60`); `It_is_broader_than_the_strict_method_and_path_allowlist` (`LoggingSanitizerTests.cs:66`); `It_removes_every_control_character` (`CorrelationIdNormalizerTests.cs:81`) |
| **FR-LOG-4** | **met** | Cap at `CorrelationIdNormalizer.cs:48-50`, reached from the single ingestion point `AspNetCoreFrontend.cs:410,418,420` for both branches. Default `255` `AppSettings.cs:14`; override `:29`; startup-rejected if `<= 0` `:80`. Documented `docs/CONFIGURATION.md:22`. | `It_truncates_to_the_host_configured_maximum_length` (`CorrelationIdNormalizerTests.cs:116`); `It_caps_at_the_host_configured_maximum_length` (`ExtractTraceIdFromTests.cs:152`); `It_normalizes_the_fallback_branch_too` (`ExtractTraceIdFromTests.cs:216`) |
| **FR-LOG-5** | **met** | `CorrelationIdNormalizer.cs:41-53` has no reject/throw path; null/empty → `string.Empty` (`:43-46`); non-positive `maxLength` → documented default (`:48`). Order truncate-then-filter (`:50` then `:52`). | `It_produces_the_status_codes_the_parity_assertions_depend_on` (`CorrelationIdParityTests.cs:133`); `It_truncates_before_it_filters` (`CorrelationIdNormalizerTests.cs:141`); `It_is_idempotent` (`:170`) |
| **FR-LOG-6** | **met** | Enumerated construction sites independently. Production `TraceId` constructions: `AspNetCoreFrontend.cs:418`, `:420`, `Core/Model/No.cs:106` (D-3, inert). All other production sites call `ExtractTraceIdFrom`: `AspNetCoreFrontend.cs:622`, `LoggingMiddleware.cs:214`, `WebApplicationBuilderExtensions.cs:238`, `HealthCheckEndpointModule.cs:110`, `TokenEndpointModule.cs:87`, `Program.cs:222`. `LoggingMiddleware.cs:49` consumes the ingested value with no second differently-shaped pass; `:185` writes that same value to the 500 body. No response path reads the raw header; the raw value in `FrontendRequest.Headers` (`AspNetCoreFrontend.cs:371-397`) is never logged or serialized. | `It_normalizes_the_correlation_id_in_the_map_fallback_404_body` / `_401_` / `_403_` / `_429_` (`CorrelationIdParityTests.cs:144,150,156,162`); `It_logs_the_same_value_the_response_bodies_carry` (`:168`); `It_never_lets_a_control_character_reach_a_response_body_or_a_log_event` (`:176`) |

## §6 edge-case coverage

| # | Case | Covered by | Real or vacuous |
| --- | --- | --- | --- |
| 1 | Byte-for-byte passthrough | `CorrelationIdNormalizerTests.cs:34`; `ExtractTraceIdFromTests.cs:66` | Real |
| 2 | `+ = { } @ \| ,` survive | `CorrelationIdNormalizerTests.cs:60`; `ExtractTraceIdFromTests.cs:89`; `CorrelationIdParityTests.cs:191`; negative control `LoggingSanitizerTests.cs:66` | Real (negative control at `LoggingSanitizerTests.cs:68` makes it non-trivial). `CorrelationIdParityTests.cs:195` asserts on a `const` and is vacuous; `:196` is the real assertion. |
| 3 | CR/LF and other control chars removed | `CorrelationIdNormalizerTests.cs:81,87`; `LoggingSanitizerTests.cs:35,41`; `ExtractTraceIdFromTests.cs:113`; `CorrelationIdParityTests.cs:176` | Real |
| 4 | Truncated to `CorrelationIdMaxLength` | `CorrelationIdNormalizerTests.cs:110,116`; `ExtractTraceIdFromTests.cs:144,152`; pre-existing `LoggingMiddlewareTests.cs:462` | Real — asserts at both default and non-default bound, so cannot pass on a hardcoded 255 |
| 5 | Combined, asserting the order | `CorrelationIdNormalizerTests.cs:141,147,152`; `ExtractTraceIdFromTests.cs:237`; `CorrelationIdParityTests.cs:54-61` | Real — `Be("abcdefghi")` + `NotBe("abcdefghij")` fails if order swapped; arithmetic verified independently |
| 6 | Idempotence | `CorrelationIdNormalizerTests.cs:170`, 5 cases incl. over-length-and-hostile and `""` | Real |
| 7 | Empty / whitespace / null-ish no throw | `CorrelationIdNormalizerTests.cs:182,191,200,209`; `:224`; `LoggingSanitizerTests.cs:81,87,93,99` | Real at normalizer level. See **F-1**: ingestion-point behavior for a value that normalizes to empty is untested and wrong. |
| 8 | System-generated fallback normalized | `CorrelationIdNormalizerTests.cs:243`; `ExtractTraceIdFromTests.cs:173`, `:216`, `:194` | Real — `:216` load-bearing (proves fallback branch is capped) |
| 9 | Log/response parity, hostile ID, several status codes | `CorrelationIdParityTests.cs:144,150,156,162` (bodies, 4 codes, 3 layers) + `:168` (logs) | Real, one gap — see **F-2**: log parity asserted for 404/401/403 only; 429 is body-only |
| 10 | Malformed ID never changes the outcome | `CorrelationIdParityTests.cs:133` | Real but indirect — asserts four hardcoded statuses under a hostile ID rather than differentially against a clean ID. Clean-ID controls exist in other fixtures (`CorrelationIdParityTests.cs:398`; `HealthCheckEndpointModuleTests.cs:209,226,245,269`; `RateLimitTests.cs:202`). Adequate, not elegant. |

## Findings

### F-1 — Control-character-only correlation header yields an **empty** `correlationId` everywhere instead of the documented server-generated fallback
`AspNetCoreFrontend.cs:415` (emptiness tested on the **raw** value, before normalization at `:418`). **Severity: High** (reviewer would accept Medium on plausibility, not below).

Scenario with `CorrelationIdHeader = "x-correlation-id"`:
- Input `x-correlation-id: \t` — a single horizontal tab, a *legal* HTTP field-value character, so it reaches the server over a real socket (unlike bare CR/LF).
- `string.IsNullOrEmpty(correlationId)` at `:415` sees `"\t"` → false → client branch taken at `:418`.
- `Normalize("\t", 255)` → `SanitizeCorrelationIdForLog` → `safeCount == 0` → `LogSanitizer.cs:114` returns `string.Empty`.
- Result: `TraceId.Value == ""`. Every error body carries `"correlationId": ""`; every request log event carries `TraceId=""`.

Why wrong rather than merely unfortunate:
1. Contradicts documentation shipped in this same diff — `docs/CONFIGURATION.md:21`: *"When this setting is non-empty and the named header is present with a non-empty value, that value identifies the request… **otherwise the server-generated trace identifier is used**."* An empty emitted value matches neither branch.
2. Internally inconsistent — a header sent *empty* correctly falls back to `HttpContext.TraceIdentifier` (pinned by `ExtractTraceIdFromTests.cs:194`); a header sent as a tab does not. Same client-visible outcome, two behaviors.
3. Client-controlled suppression of the operational identifier: a caller can deterministically blank the `TraceId` on its own log line. ~15 E2E scenarios encode non-emptiness as a product expectation (e.g. `Features/Authorization/RelationshipsWithEdOrgsOnly.feature:176`).

Fix: re-test emptiness *after* normalization at the ingestion point and fall through to the `TraceIdentifier` branch. Keeps `Normalize` pure; leaves the settled order and allowlist untouched. Needs an `ExtractTraceIdFromTests` case for a control-character-only header value — none exists today.

### F-2 — Log/response parity on the 429 path is asserted for the body only, not the log
`CorrelationIdParityTests.cs:84` (`CreateFactory(loggerProvider: null, rateLimited: true)`) and `:105` (`_loggedTraceIds` snapshotted before the rate-limited requests are sent). **Severity: Low**.

`LoggingMiddleware` is registered at `Program.cs:150`, before `UseRateLimiter()` at `:203`, so the rate-limited request *does* produce a log event carrying a `TraceId` — it is simply not captured. Undetected regression: if `WriteRateLimitRejectionAsync` (`WebApplicationBuilderExtensions.cs:238`) were later changed to build its own `TraceId` from `httpContext.TraceIdentifier` — precisely the bug fixed at `HealthCheckEndpointModule.cs:110` — the body assertion at `:164` would still pass while parity broke. §6 case 9 still satisfied by the other three codes, hence Low.

### F-3 — `It_uses_the_server_generated_trace_identifier_instead` asserts only non-emptiness
`CorrelationIdParityTests.cs:412`. **Severity: Low**.

`NotBeNullOrEmpty()` passes for any non-empty string. Undetected regression: if `MapFallback` regressed to a literal placeholder (`"unknown"`, `"-"`) or the tenant name, both this test and its sibling at `:406` (`NotBe(ClientSuppliedValue)`) would still pass, and the 404 would no longer be correlatable to the log line.

## On the two failing `LoggingMiddlewareTests`
Both new behaviors are **correct**; both old expectations encode the pre-FR-LOG-3 strict allowlist. Correct disposition is to update the expected literals, not delete or skip.

## Reviewer's own caveats
The 6 `Core.Tests.Unit` failures were not independently confirmed against a fully-restored checkout; E2E/integration suites were not run (need Docker). Review was AI-assisted; a human should confirm F-1's plausibility judgment.

---

# Raw report 2 — Clean-code reviewer

> Narrative preambles condensed; all findings reproduced with `file:line`, severity and scenario.

**Verification run:** `dotnet csharpier check src/dms` clean (2779 files). New tests 43 pass, including all 11 in-process parity tests (confirmed they actually execute, not silently filtered). `LoggingMiddlewareTests` exactly 2 failures, both the adjudicated pair, both failing only on `{`/`}` surviving. `LoggingSanitizerTests` (16), HealthCheck/Endpoints (15), `RateLimitTests` pass. **No test weakened** — `git diff 5202c06..HEAD --numstat` on test files shows `99/0`, `414/0`, `243/0`, `251/0`: pure additions, zero deletions, no `[Ignore]`/`[Explicit]`/`Assert.Pass`.

### Medium 1 — `SanitizeForLog`'s entire body is copied into `SanitizeCorrelationIdForLog`
`LogSanitizer.cs:85-141` vs `:18-74`, differing only in which predicate is called. It mirrors the neighbour's shape *exactly* (leading `ReplaceLineEndings`, two-pass count-then-`string.Create`, early return of the original instance when clean, `#pragma warning disable S3267`) — the finding is that it mirrors by **copy** rather than extraction. 55 lines of allocation-sensitive logic now exist twice in one file with no cross-reference. Scenario: any future change to that algorithm (a `SearchValues` fast path, the `safeCount == 0` short-circuit, surrogate handling) applied to one method silently leaves the other behind — **already happening**, see Medium 2. Deduplicable with no added allocation via `string.Create<TState>` generic state (struct tuple, no boxing) and cached `static readonly Func<char,bool>` predicates. Distinct from D-5 (three pre-existing *cross-assembly* copies of the strict allowlist); this copy is new in this diff and inside a single file.

### Medium 2 — the `ReplaceLineEndings` comment is factually wrong, and the effective allowlist deviates from the one documented in three other places
`LogSanitizer.cs:92-95`. Verified on the .NET 10 runtime in this sandbox:

```
IsControl U+2028 (LINE SEPARATOR):      False
IsControl U+2029 (PARAGRAPH SEPARATOR): False
"a b c".ReplaceLineEndings("") → "abc"   (5 chars → 3)
```

`string.ReplaceLineEndings` recognises LF, CR, CRLF, FF, NEL U+0085, **LS U+2028 and PS U+2029**, but `char.IsControl` is true only for U+0000–U+001F / U+007F–U+009F. So for the correlation-ID variant the leading call is **not** "behaviorally redundant with the allowlist below" — it is load-bearing, and the effective allowlist is "all printable non-control characters *except U+2028 and U+2029*". That contradicts `LogSanitizer.cs:78-83`, `LoggingSanitizer.cs:27-31`, `CorrelationIdNormalizer.cs:20`, and `docs/LOGGING.md:206-209`.

Scenario: a maintainer trusting the "behaviorally redundant" comment deletes the `ReplaceLineEndings` call as dead work and silently re-admits U+2028/U+2029 to log sinks and response bodies — the line-forging class the filter exists to block for line-oriented consumers and JS-based log viewers. The comment was *accurate* in `SanitizeForLog:25-27` (U+2028 also fails `IsLetterOrDigit`); it became false the moment the predicate changed. Fix is comment + docs (and optionally a test pinning U+2028/U+2029 removal), **not** a behavior change — current behavior is the safer one. Not covered by D-7: this is not an argument to narrow the allowlist, it is that the implemented allowlist is *narrower* than what every doc states.

### Low 3 — non-positive-`maxLength` fallback now exists in two places
`LoggingMiddleware.cs:28-37` + `CorrelationIdNormalizer.cs:48`. `_correlationIdMaxLength`'s only remaining consumer is `:221`, which passes it straight to `Normalize`, and `Normalize` applies the identical `> 0 ? configured : Default` rule itself. Scenario: if the fallback policy changes (e.g. clamp to a floor of 8), a maintainer editing `CorrelationIdNormalizer` reasonably believes they changed it everywhere while `LoggingMiddleware` keeps a stale copy.

### Low 4 — `Normalize` called once per branch instead of once on the selected value
`CorrelationIdNormalizer.cs` / `AspNetCoreFrontend.cs:407-421`. Collapsing to one call would make AD-1's "normalize once" literally true in the code's shape and shorten the 110-column `:420`. Scenario: someone adding a third source (a second fallback header, a `traceparent` reader) adds a `return new TraceId(...)` and forgets the wrapper — exactly the omission class this ticket exists to fix.

### Low 5 — the two assertions covering the removed hardcoded `"correlationid"` fallback are negative-only
`CorrelationIdParityTests.cs:404-413`. `It_uses_the_server_generated_trace_identifier_instead` asserts only `NotBeNullOrEmpty()`; would pass for `"unknown"`, an echoed `Host` header, an unrelated GUID. Only `It_ignores_the_hardcoded_correlationid_header_name` pins the requirement, and negatively. (To its credit the pair *does* fail if the feature is reverted — weak, not vacuous.)

### Low 6 — assertion on a constant declared in the same file
`CorrelationIdParityTests.cs:195` — `ExpectedCorrelationId.Should().Contain("{").And.Contain("}")` exercises no product code and can only fail if someone edits the test's own literal (declared `:61`). `:196` is the real assertion. Scenario: inflates apparent FR-LOG-3 evidence — a reader credits the fixture with two braces-survive proofs when it has one.

### Low 7 — three tests in one fixture cannot fail independently
`CorrelationIdNormalizerTests.cs:140-157`. `Should().Be("abcdefghi")` (`:143`) already entails `NotBe("abcdefghij")` (`:149`) and `Length == 9` (`:156`). Cost: three tests to edit for one policy change.

### Low 8 — the same edge cases are asserted at three or four layers with duplicated literals
`LoggingSanitizerTests.cs:25-122`, `CorrelationIdNormalizerTests.cs:17-250`, `ExtractTraceIdFromTests.cs:72-156` + `222-242`, `CorrelationIdParityTests.cs` each re-assert "punctuation survives", "control chars removed", "over-length truncated"; the order is pinned three times, twice with identical fixture data (`CorrelationIdNormalizerTests.cs:129-130`, `ExtractTraceIdFromTests.cs:231-232`). Scenario: a sanctioned future policy change requires coordinated edits to four files holding copies of the same literals; a partial update leaves two green tests asserting contradictory policy. The genuinely layer-specific value in `ExtractTraceIdFromTests` is header selection, the empty-setting disable path, and the fallback branch (`:51-70`, `:158-220`) — new, unique, and the plan's §6 gap.

### Low 9 — truncation cuts on a UTF-16 code unit, so it can split a surrogate pair
`CorrelationIdNormalizer.cs:50`; the allowlist then keeps the orphan (`char.IsControl` is false for surrogates). Verified `System.Text.Json` does not throw on a lone surrogate — it writes `�` — so no crash. But the response body then carries U+FFFD while the log sink receives the raw unpaired surrogate, so the two values are no longer byte-identical and copy/pasting the ID from the response no longer finds the log line, which is the one thing FR-LOG-6 guarantees. Reachability narrow: Kestrel decodes request headers as Latin-1 by default, so a surrogate requires a host-installed UTF-8 `RequestHeaderEncodingSelector`. Pre-existing behavior relocated from `LoggingMiddleware`; order is settled, only whether the cut avoids splitting a pair.

### Nits
- **10** `CorrelationIdParityTests.cs:63-75` uses `null!` for uninitialized fixture fields; neighbouring `RateLimitTests.cs:32-37` uses `default!`.
- **11** `CorrelationIdNormalizer.cs:41` — `Normalize` has no `<returns>` element, while both `LoggingSanitizer` members it wraps document one.
- **12** `ExtractTraceIdFromTests.cs` is named after a method; `AGENTS.md` asks for "filenames named like the code area being tested", and the three sibling files are `AspNetCoreFrontendRequestHeaderTests.cs` / `…RequestBodyTests.cs` / `…ResponseHeaderTests.cs`.

### Explicitly NOT findings (so the lead need not re-derive)
- `CorrelationIdNormalizer` **justifies** being a new type; exactly one new type, matching §1's budget. It is the only place knowing both the order and the frontend's `DefaultCorrelationIdMaxLength`, neither of which can live in `Core.External` (AD-2) or `LogSanitizer`. Taking `maxLength` as a parameter keeps it pure and testable.
- **Normalization is single-sourced.** Production call sites `AspNetCoreFrontend.cs:418`, `:420`, `LoggingMiddleware.cs:221` — three calls to one function; the third is the `OptionsValidationException` path where `ExtractTraceIdFrom` is unusable by construction. Nothing scattered.
- The `IOptions<AppSettings>` threading through `HealthCheckEndpointModule` **is** idiomatic here (matches `TrackedChangesEndpointModule`, `DiscoveryEndpointModule`, `XsdMetadataEndpointModule`, `TokenEndpointModule`); the `AppSettings` alias is required and matches existing usage. `Program.cs`'s `GetRequiredService` is forced by the `MapFallback(RequestDelegate)` overload.
- **No needless allocation** — clean values return the same instance on the common path.
- **Test-style conventions met** throughout.

### Coverage verdict
Strong and materially better than the branch point (`ExtractTraceIdFrom` went from zero tests to nine; the parity fixture boots the real pipeline across four status codes and three layers with a genuinely hostile header). Every §6 case has a real assertion. Two soft spots: §6 case 10 is only nominally covered — the test asserts four hardcoded statuses and its comment *claims* an A/B comparison it never performs — and the removed-hardcoded-header fix rests on negative-only assertions. Dominant weakness is redundancy rather than gaps. Nothing weakened, deleted, skipped, or had assertions removed.

---

# Compiled, de-duplicated finding list with my adjudication

Reviewer severities re-confirmed against §9.3 myself; changes are marked with a reason.

| ID | Source | Finding | Reviewer | **Mine** | Decision |
| --- | --- | --- | --- | --- | --- |
| **R1-01** | Func F-1 | Control-char-only header → empty `correlationId` everywhere instead of `TraceIdentifier` fallback (`AspNetCoreFrontend.cs:415`) | High | **High** | **Fix now** |
| **R1-02** | Team lead | Two `LoggingMiddlewareTests` expectations encode the superseded strict allowlist (`:269`, `:430`) | — | **High** | **Fix now** — update literals only |
| **R1-03** | Clean 2 | `ReplaceLineEndings` comment false for the correlation variant; 3 doc sites overstate preservation (U+2028/U+2029) | Medium | **Medium** | **Fix now** — comment + docs + pinning test |
| **R1-04** | Func §6-10 + Clean coverage verdict | FR-LOG-5 "same status as a clean ID" only indirect; the test's comment overclaims an A/B it never performs | (implied) | **Medium** ↑ | **Fix now** — plan §5 traceability demands exactly this evidence |
| **R1-05** | Clean 1 | 55-line body duplicated between the two `LogSanitizer` methods | Medium | **Low** ↓ | **Decline** → ledger D-9 |
| **R1-06** | Clean 9 | Truncation can split a surrogate pair; orphan survives the allowlist | Low | **Low** | **Fix now** — 1 line, closes an FR-LOG-6 parity hole |
| **R1-07** | Func F-2 | 429 log parity asserted body-only (`CorrelationIdParityTests.cs:84`, `:105`) | Low | **Low** | **Fix now** |
| **R1-08** | Func F-3 = Clean 5 | Server-generated-fallback assertion is non-emptiness only (`:412`) | Low | **Low** | **Fix now** — assert against the captured logged `TraceId`, not an ASP.NET format regex |
| **R1-09** | Clean 3 | Redundant non-positive-`maxLength` ternary in `LoggingMiddleware` | Low | **Low** | **Fix now** |
| **R1-10** | Clean 4 | `Normalize` called per-branch rather than once on the selected value | Low | **Low** | **Fix now** — folds naturally into R1-01 |
| **R1-11** | Clean 6 | Assertion on a same-file constant (`:195`) | Low | **Low** | **Fix now** — delete the line |
| **R1-12** | Clean 8 | Order-pinning duplicated with identical literals across files | Low | **Low** | **Fix now, partially** — drop the duplicated order fixture from `ExtractTraceIdFromTests`, keep layer-specific cases |
| **R1-13** | Clean 7 | Three tests assert one fact | Low | **Nit** ↓ | **Fix now** — cheap consolidation |
| **R1-14** | Clean 10 | `null!` vs `default!` | Nit | **Nit** | **Fix now** |
| **R1-15** | Clean 11 | Missing `<returns>` on `Normalize` | Nit | **Nit** | **Fix now** |
| **R1-16** | Clean 12 | Test file named after a method | Nit | **Nit** | **Decline** → ledger D-10 |
| **R1-17** | Implementer | Health endpoint now honors the configured correlation header — behavior change beyond normalization | — | **Medium** (decision) | **Accept as intended** → ledger D-11; must be called out in the PR |
| **R1-18** | Implementer | No E2E negative scenarios added | — | **Low** | **Decline/defer** → ledger D-12 |

## Severity re-confirmations, with reasons

- **R1-05 Medium → Low.** `todo.md` Task 1 explicitly directed mirroring the reference implementation's shape, and AD-3 directs leaving the strict path alone. A delegate-based extraction rewrites allocation-sensitive code on the hot path shared by *every* `Method`/`Path` log call, for zero behavior change. The concrete harm the reviewer cites (comment drift) is real but is fixed directly by R1-03. Genuine smell, wrong ticket.
- **R1-04 upgraded to Medium.** Neither reviewer filed this as a numbered finding, but plan §5's traceability table demands, verbatim, "A test that a hostile ID yields the **same HTTP status** as a clean one" as FR-LOG-5's evidence. A test asserting four hardcoded statuses with a comment claiming an A/B it never performs does not supply that. A misleading comment over missing evidence is worse than a gap.
- **R1-13 Low → Nit.** Redundant-but-correct assertions are taste, not a maintenance hazard likely to cause a defect.
- **R1-01 held at High, not raised to Critical.** It is not an FR violation — FR-LOG-5 asks for a deterministic adjustment and empty *is* deterministic, and parity (FR-LOG-6) still holds because both sinks get the same empty value. It is a real bug on a reachable path that contradicts docs shipped in the same diff.

## Round 1 stop-condition evaluation

**Not converged.** FR verdicts are all "met", but findings above Nit remain (2 High, 2 Medium, 7 Low). Per §9.4(a) convergence needs all-Nit from both reviewers. Proceeding to round 2 with the fix-now list above as the correction list.

## Test-integrity audit for this round (§9.4 gate 4)

`git diff 5202c06..HEAD --numstat` on all `*Tests*` files: `99/0`, `414/0`, `243/0`, `251/0` —
**pure additions, zero deletions.** No `[Ignore]`, no `[Explicit]`, no `Assert.Pass`, no
removed assertion. Independently confirmed by the team lead and by the clean-code reviewer.
The two failing tests were left failing and escalated rather than edited — the correct
behavior under §9.6.
