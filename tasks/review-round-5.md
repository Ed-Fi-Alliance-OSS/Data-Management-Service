# Review Round 5 — DMS-1457 / FR-LOG-3..6 — **CONVERGED**

Team lead record per `tasks/plan.md` §9.2 Step 4.

- **Branch point:** `5202c06` · R1 `7835227` · R2 `be941dd` · R3 `63d7bd9` · R4 `48d029f` · **R5 `c526453`**
  (records: `29c02e3`, `65279c8`, `010fb2f`, `72bc5d4`)
- **Roles:** fresh implementation sub-agent; two fresh, tool-restricted read-only reviewers
  (different agents). Both read D-1..D-17 first.

## Stop condition: §9.4(a) CONVERGENCE

All four FRs reported **met**, and **both reviewers returned only Nit-level observations** —
the functionality reviewer returned none at all, and the clean-code reviewer returned two
Nits in a test-file doc comment. This is the desired exit, reached on the final round the
budget allowed rather than by exhausting it.

---

# Raw report 1 — Functionality reviewer (round 5)

## Round 5 verified non-functional
`git show --stat c526453` = 5 files. The only non-test, non-doc file is `LoggingSanitizer.cs`,
and filtering that file's diff for `^[+-]` lines shows **every one begins with `///`**. No
executable statement in production code changed.

The fixture move did not alter what the OAuth tests assert:
`git diff -w --ignore-blank-lines 48d029f..c526453 -- OAuthManagerTests.cs` reduces the change to
the class XML doc, the two `default!` fields, and one `A.CallTo(...)` line-wrap. `[TestFixture]`,
`[Parallelizable]`, `UpstreamCorrelationId`, the whole `Setup` body and both `[Test]` bodies are
**byte-identical**. Nesting is inert for NUnit: the outer `OAuthManagerTests` has no attributes,
fields, `[Test]` or `[SetUp]`, so there is nothing to inherit or be wrapped by, and the new
fixture does not derive from `When_Getting_An_Access_Token`. No CI job filters by test name
(`on-dms-pullrequest.yml` uses a solution filter; only `Category=` filters appear), and the
fixture name is referenced nowhere outside its own file.

Empirical: `--filter FullyQualifiedName~OAuthManager` → **27/0** (was 25 — the claim is exact);
Frontend **556/0**; Backend **3785/0**; `csharpier check src/dms` clean (2780).

## Both doc corrections match the code
- `LoggingSanitizer.cs:20-21` — correct: `char.IsControl` is `UnicodeCategory.Control` (Cc),
  precisely U+0000–U+001F ∪ U+007F–U+009F. **And the correction is substantive, not cosmetic:**
  Kestrel decodes header bytes as Latin-1, so bytes `0x80`–`0x9F` arrive as U+0080–U+009F, and it
  is the C1 half of the range that removes them — which the old "ASCII < 32" wording denied.
- `docs/CONFIGURATION.md:22` — now names both causes, matching `CorrelationIdNormalizer.cs:74-82`
  and parallel to `docs/LOGGING.md:233-238`. "One further character" is arithmetically exact.

## Round 4's two load-bearing conclusions re-verified with an independent parser
- **(a)** Brace-matching parse of all **713** `Log*(` calls in `src/dms` production, checking
  argument lists (not templates) for `TraceId` and for `traceId` not followed by `.Value`:
  **zero** bare-struct hits. The three `traceId` hits all resolve to `string`
  (`LoggingMiddleware.cs:154`, `:184` from `ExtractTraceId`'s `string` return; `Handler/Utility.cs:90`
  from `ResiliencePropertyKey<string>`). Both `BeginScope` dictionaries carry strings. The
  structural backstop at `CorrelationIdRecordingLoggerProvider.cs:52-53` (`value.Value is string`)
  really would drop the count and fail `HaveCount(...)` on a regression.
- **(b)** ~200 strict-sanitizer arguments enumerated: **not one** is trace- or correlation-shaped.
  All **36** correlation-sanitizer sites take a `.TraceId.Value`, the normalizer's `truncated`, or a
  `string` local derived from `.TraceId.Value`. The cumulative swap is a clean 1:1 and adds **zero
  new `Log*` call sites**.
  - **One repo-wide match remains, and it is `src/config`:**
    `src/config/.../RequestLoggingMiddleware.cs:25` passes a trace value into the strict sanitizer.
    CMS, pre-existing, forbidden by AD-4, declined as **D-1**. Carried into the final report so it
    is visible rather than buried.

## Per-FR verdict on `c526453` — all four **met**
| FR | Verdict | Key evidence |
| --- | --- | --- |
| **FR-LOG-3** | **met** | Bare `IsAllowedCorrelationIdChar` `LogSanitizer.cs:161` + `ReplaceLineEndings` `:99` for U+2028/29; applied `:85`; surfaced `LoggingSanitizer.cs:50`; reached from `CorrelationIdNormalizer.cs:87` before any use. Structurally distinct from the untouched strict `IsAllowedChar` `:152`. Documented `docs/LOGGING.md:212-231`. Differential test `LoggingSanitizerTests.cs:64` fails if the allowlists ever converge. |
| **FR-LOG-4** | **met** | Cap `CorrelationIdNormalizer.cs:66-82`; default `AppSettings.cs:14`; override `:29` + env; startup rejection `:80-85`. Also applies to the server-generated branch `AspNetCoreFrontend.cs:433` and the config-failure fallback `LoggingMiddleware.cs:212-215`. Tests `CorrelationIdNormalizerTests.cs:110,116`; `ConfigurationTests.cs:528`. |
| **FR-LOG-5** | **met** | No throw/reject path `CorrelationIdNormalizer.cs:59-88`; non-positive `maxLength` degrades `:66`. Order pinned `CorrelationIdNormalizerTests.cs:141` and end-to-end `CorrelationIdParityTests.cs:62`. Differential arm `:232`. |
| **FR-LOG-6** | **met** | One ingestion point `AspNetCoreFrontend.cs:415-437`, sole reachable `new TraceId(...)` at `:436` (only other is `No.cs:106`, D-3). Every responder routes through it: `Program.cs:222`, `HealthCheckEndpointModule.cs:110`, `WebApplicationBuilderExtensions.cs:238`, `TokenEndpointModule.cs:87`, `LoggingMiddleware.cs:38`. Both raw-`TraceIdentifier` reads normalized. Tests span 404/401/403/429/500/502 plus the Core layer. |

Additional requirement-scoped checks outside round 4's list: no correlation ID in any response
header; `LoggingConfigurator.cs:150` has only `Enrich.FromLogContext()` and **no**
`UseSerilogRequestLogging`, so no framework identifier competes; the `WriteTo.OpenTelemetry` sink
receives the normalized scope value; every `FrontendRequest.Headers` consumer reads a specific
named header, none of them the correlation header, and none enumerates or logs the collection.

## Findings: **none**
Two things looked at and deliberately not reported: `default!` means a `Setup` failure surfaces as
a `NullReferenceException` rather than a FluentAssertions message (test-diagnostics nuance, and
raising it would re-litigate R4-06's whole purpose); and the U+007F–U+009F half of the new claim
has no direct test literal (it is a statement about BCL semantics, correct as written, and
asserting BCL behavior is not this repo's job).

> "**I have no finding above Nit — in fact no finding at all.** All four FRs are met on `c526453`
> with the evidence above. Round 5 is verifiably non-functional. **Round 5 converges.**"

---

# Raw report 2 — Clean-code reviewer (round 5)

## Round 5's claims all hold
5 files; the only non-test, non-doc diff sits inside `/// <summary>` at `LoggingSanitizer.cs:20-21`;
zero executable lines changed. `git diff -w --ignore-blank-lines` reduces the fixture move to
exactly four items (the brace move, the summary rewrite, two `default!`, one line-wrap) **and
nothing else** — name, attributes, `[SetUp]`, the literal, both `It_` methods and both assertions
byte-identical. The line-wrap is cosmetic (`csharpier check` clean). `default!` is behaviourally
inert because NUnit fails a test outright if `[SetUp]` throws. `--filter ~OAuthManager` → 27/0.

Each rewrite checked against the code, including one the lead did not ask about: the "two places"
comment is **exactly right, not an undercount** — the third `switch` arm
(`Unauthorized` → `GenerateUnauthorizedResponse`, `OAuthManager.cs:90-117`) contains **no log site**.

## Independent cumulative pass — structural claims re-derived, not read
- Single ingestion point holds: `new TraceId(` has exactly two production hits; `TraceIdentifier`
  exactly two, both immediately wrapped in `Normalize`.
- Non-conflation holds both directions (enumerated independently).
- `HealthCheckEndpointModule.cs:14`'s `using AppSettings = …` alias is **necessary, not clutter** —
  line 6 imports `Core.Configuration`, which also defines `AppSettings`; a plain namespace import
  would be CS0104 and no import would silently bind the *wrong* type.
- No dead code or leftover scaffolding: `_appSettings` still used `:203`; round 3's field removal
  left no orphan; `SanitizeCorrelationIdForLog` has one caller **by design** (the facade, mirroring
  `SanitizeForLog` per `todo.md` Task 1); the recording provider has two consumers.
- Comment-vs-code coherence spot checks passed on the ones most likely to have drifted over five
  rounds: `CustomResourceValidationMiddleware.cs:28-35`'s "three ways" (verified at `:36`, `:138`,
  `:242`/`:248`), `LoggingMiddleware.cs:35-37`, `LoggingSanitizerTests.cs:74-81`.
- `docs/LOGGING.md:197-262`'s three-step ordering matches `Normalize` step for step, including the
  surrogate back-off being *inside* truncation rather than a separate pass.
- AGENTS.md: zero added `== null`/`!= null`, zero `Newtonsoft`, file-scoped namespaces, primary
  constructors on new types, `Given_`/`Setup`/`It_` throughout, `System.Text.Json` only.

## Findings — two Nits
- **C5-01 (Nit)** `ExtractTraceIdFromTests.cs:27-29` — the new `<remarks>` says the order is "the
  one thing not repeated here", but four other normalizer behaviours are also not re-pinned in this
  file (surrogate-split, idempotence, non-positive `maxLength`, null/empty). A maintainer trusting
  it literally would believe surrogate handling is covered at this seam. **Direction is now right** —
  round 4's Low claimed the opposite and *was* misleading; this residue only over-promises coverage
  the reader would not act on.
- **C5-02 (Nit)** same, `:23-25` — "the same literals" is approximate for one of the three: the
  upstream-punctuation literal at `:86` drops the `\"p'q` suffix present in
  `CorrelationIdNormalizerTests.cs:46`. Control characters and both bounds are literally identical.

> "Both are wording in a test-file doc comment with no correctness impact. **Neither justifies a
> round 6.**"

## Verdicts
- **Ready to merge.** "The cumulative change reads as one coherent thing rather than five accreted
  ones." The two places a reviewer will pause — truncation in the Frontend assembly while the
  allowlist lives in `Backend.External`, and the D-11 health-endpoint behaviour change — are both
  explained at the site. No comment contradicting final code, no drifted doc claim, no dead code,
  no abstraction failing to earn its place.
- **Test coverage adequate**; nothing vacuous anywhere in the cumulative diff. The redundancy
  C5-01/C5-02 describe is deliberate seam-pinning, not padding.
- **Test integrity clean**, verified independently and enumerated per commit: exactly **7 removed
  lines** in all test files across the whole cumulative diff, all expected-literal swaps. Zero
  `[Test]`/`[TestCase]`/`[TestFixture]` net deletions across five rounds; zero `[Ignore]`,
  `[Explicit]`, `Assert.Pass`, `Assert.Inconclusive`, `Skip`, `#if DEBUG` added. `src/config`
  untouched. `CorrelationId.feature` untouched. Only the two authorized pre-existing test files
  were ever edited.

---

# My adjudication

| ID | Source | Finding | Reviewer | **Mine** | Decision |
| --- | --- | --- | --- | --- | --- |
| **R5-01** | Clean C5-01 | The new `<remarks>` over-promises which normalizer behaviours are not re-pinned here | Nit | **Nit** | **Accept as-is** — see below |
| **R5-02** | Clean C5-02 | "the same literals" is approximate for one of three | Nit | **Nit** | **Accept as-is** |

**Both accepted as-is rather than fixed, deliberately.** §9.4(a) convergence explicitly tolerates
Nit-level observations, and §9.4(b) caps the loop at five rounds of Step 1 — which round 5 was. A
round 6 to reword two sentences in a test-file doc comment would breach the budget for no
correctness gain, and the reviewer that raised them said plainly that neither justifies one. They
are recorded here and named in the final report so a human can take them in the same pass as the
`tasks/` cleanup if they wish. Note the direction of travel: round 4's version of that comment was
*actively misleading* (a Low) and is now merely *incomplete* (a Nit).

## Severity trend across the loop

| Round | Critical | High | Medium | Low | Nit |
| --- | --- | --- | --- | --- | --- |
| 1 | 0 | 2 | 2 | 7 | 7 |
| 2 | **1** | 0 | 1 | 6 | 5 |
| 3 | 0 | **1** | 1 | 3 | 5 |
| 4 | 0 | 0 | 0 | 4 | 5 |
| **5** | **0** | **0** | **0** | **0** | **2** |

The two mid-loop spikes are both cases where a reviewer refused to accept the previous round's
framing: round 2's Critical came from looking past the frontend assembly, and round 3's High from
sweeping for a mechanism nobody had considered. Neither would have surfaced from a review that
took the prior round's inventory as its starting point.

## Test-integrity audit (§9.4 gate 4) — final
Independently confirmed by me and by both round-5 reviewers, who enumerated it per commit: across
the entire five-round cumulative diff, `git diff 5202c06..HEAD -- '*Tests*'` contains **exactly 7
removed lines, all expected-literal swaps** (5 in `LoggingMiddlewareTests.cs`, 2 in
`RequestResponseLoggingMiddlewareTests.cs`). Only those two pre-existing test files were ever
edited. Zero test/fixture deletions, zero `[Ignore]`/`[Explicit]`/`Assert.Pass`/`Assert.Inconclusive`,
zero weakened matchers. `src/config` and `CorrelationId.feature` untouched. Net +6 tests.
