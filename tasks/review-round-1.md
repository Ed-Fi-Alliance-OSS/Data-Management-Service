# Review Round 1 — DMS-1457 Correlation ID Normalization

## Round summary

Implementation sub-agent completed Tasks 1–7 in this round. Both reviewers ran
against the resulting uncommitted diff.

## Raw report — Functionality reviewer (fresh, read-only)

**Verdict: all four FRs MET. No Critical or High findings.**

- **FR-LOG-3 — MET.** `LogSanitizer.cs` new `SanitizeCorrelationId` uses bare
  `!char.IsControl(c)`, no positive enumeration. `SanitizeForLogging` unchanged.
  Punctuation `+={}@|,#()[]<>"'ß` preserved (`ExtractTraceIdFromTests.cs`,
  `LoggingSanitizerTests.cs`); control chars `\r\n\t\0` removed.
- **FR-LOG-4 — MET.** `AspNetCoreFrontend.NormalizeTraceId` truncates to configured
  max, falls back to `AppSettings.DefaultCorrelationIdMaxLength` when `<= 0`.
  Applied at every entry point (`Program.cs` MapFallback, `HealthCheckEndpointModule`,
  `LoggingMiddleware`). Docs updated.
- **FR-LOG-5 — MET.** Truncate-first-then-filter implemented and pinned by
  `It_truncates_before_removing_control_characters` (`"12\r{34}\t567890"`, max 8 →
  `"12{34}"`). No request ever rejected for a malformed ID. Idempotence test passes.
- **FR-LOG-6 — MET.** Single ingestion point (`ExtractTraceIdFrom` /
  `NormalizeTraceId`) reached by every production call site. Parity proven across
  401/404/429 in `Given_CorrelationIdNormalization_Parity.cs`.

All 10 §6 edge cases covered by real (non-vacuous) assertions.

Additional observations (informational, no action needed):
- Test value updates in `LoggingMiddlewareTests.cs` (e.g. `"traceidwithunsafe"` →
  `"traceidwith{unsafe}"`) correctly reflect the new, broader allowlist — not a
  weakening.
- "whitelist" wording remaining in comments is pre-declined as **D-6**.
- `ReplaceLineEndings` retained with its CodeQL-defense comment, as required.
- `Program.cs` MapFallback now uses DI (`IOptions<AppSettings>`) instead of reading
  config directly — noted as a stylistic improvement, not a finding.

## Raw report — Clean-code reviewer (fresh, read-only)

**Verdict: 2 findings, both Nit/Low. No Critical/High/Medium.**

1. `LoggingMiddleware.cs:179` — comment says "Applying the same logging whitelist
   and length cap to the response ensures log/response parity," which is misleading:
   the value is already normalized via the correlation-ID-specific allowlist before
   this line runs; no logging-allowlist (`Method`/`Path`'s stricter one) is applied
   here at all. **Reported severity: Nit.**
2. `LoggingMiddleware.cs:206,215` — `ExtractTraceId()` return type changed from
   `string?` to `string`, guaranteeing non-null via `NormalizeTraceId`. **Reported
   severity: Low** — flagged as a correct, beneficial design improvement, not a
   defect requiring action.

Confirmed clean: no new public type beyond what's needed; `NormalizeTraceId` takes
max length as a parameter (not reading config itself); `SanitizeCorrelationId`
mirrors `SanitizeForLog`'s shape (`ReplaceLineEndings`, two-pass `string.Create`,
`#pragma warning disable S3267`); no dead code left from the old
`LoggingMiddleware` truncate/filter logic; test naming follows `Given_*`/`It_*`;
no test assertions weakened, only two existing assertions' *expected values*
updated to match the intentionally broader allowlist (not narrowed/removed).

## Team lead adjudication

| # | Finding | Reported severity | Re-confirmed severity | Reason | Decision |
|---|---|---|---|---|---|
| 1 | `LoggingMiddleware.cs:179` comment describes the wrong allowlist as being applied at echo time | Nit | **Nit** — no failure scenario, purely descriptive text, but genuinely inaccurate (not just "whitelist" wording — implies the strict `Method`/`Path` allowlist governs the echoed value, which is wrong under AD-3) | Confirmed Nit per §9.3 ("consider..." / wording-only issues cap at Nit); does not affect behavior. | **Fix now** — one-line comment edit, zero risk, avoids future maintainer confusion about which allowlist governs correlation IDs. Round 2 correction list, item 1. |
| 2 | `LoggingMiddleware.cs:206,215` return-type tightening | Low | **No action** — this is a positive observation about existing code, not a defect or hazard | N/A | **No fix needed.** Not carried to declined-findings ledger since it isn't a declined defect; it's simply not a finding requiring any change. |

No new findings duplicate any of D-1 through D-8; none re-raised.

### Stop condition check (§9.4)

- (a) Convergence: **not yet** — FR-LOG-3..6 all MET, but one Nit-severity
  observation remains outstanding (comment fix), so this round does not yet meet
  "all Nit" trivially-closed criterion until the correction lands and is
  re-verified.
- (b) Budget: round 1 of 5 used.

**Decision: proceed to round 2** with a single, trivial correction (fix the
misleading comment at `LoggingMiddleware.cs:179`). No production logic changes
required. Re-run both reviewers after the fix to confirm convergence.

### Objective gates as reported by round 1 implementer (to be independently re-verified before final sign-off)

- [x] `dotnet csharpier format src/dms` — no diff on second run
- [x] `pwsh ./build-dms.ps1 Build` — exit 0
- [x] `pwsh ./build-dms.ps1 UnitTest` — exit 0
- [x] `git diff --stat -- src/config` — empty
- [x] No test assertions removed/weakened (only two expected-value updates,
      justified by the intentionally broadened allowlist)

## Correction list for round 2

1. **Fix the comment at `LoggingMiddleware.cs:179`** (currently: "Applying the same
   logging whitelist and length cap to the response ensures log/response parity").
   Replace with accurate wording: the value is already normalized (correlation-ID
   allowlist + length cap) at ingestion; this code path only echoes that
   already-normalized value into the response body to preserve log/response parity.
   Do not touch any other line. Do not modify production logic.
