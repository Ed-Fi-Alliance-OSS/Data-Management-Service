# Review Round 5 — DMS-1457

> [!WARNING]
> The files in this `tasks` directory are ephemeral, committed only for the duration
> of the autonomous coding run. `plan.md`, `todo.md`, `declined-findings.md`,
> and all `review-round-*.md` files must be removed with `git rm` before final merge
> by a human.

## Trigger

A human follow-up review on PR #1232
([comment](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1232#issuecomment-5594473367))
re-verified rounds 3-4 against the branch (not the self-reports) and found round 4's
"converged, only Low findings" declaration was incorrect:

- 3 Medium findings from the original external review were never fixed **and** never
  recorded in `tasks/declined-findings.md`, so they went from open to invisible without
  anyone deciding anything (a direct violation of plan §9.5's purpose).
- The PR description still lacked both mandated D-8 lines (the public-docs TODO and the
  DMS-1518 note), and still narrated "converged at round 2," never mentioning rounds 3-4.
- Several genuine Low/Nit items from the original review remain untouched; the reviewer
  correctly noted round 4 overstated "no Nit even, this round."

This is round 5, the last round of the plan's 5-round budget (§9.4(b)).

## Findings adjudicated this round

### Fixed

1. **Medium — correlation ID normalizing to empty is unrecorded.** `NormalizeTraceId` was
   byte-identical, with no doc comment and no test distinguishing "all-control-character
   input strips to empty" from the already-tested "null/empty input." Fixed:
   `AspNetCoreFrontend.cs` now carries an XML doc comment stating this is deliberate
   (FR-LOG-5 "never reject"), and
   `ExtractTraceIdFromTests.It_returns_empty_when_every_character_is_a_control_character`
   pins the behavior so it can't silently flip.
2. **Medium — `Sanitize`'s predicate-purity contract is undocumented.** The private
   two-pass `Sanitize` method in `LogSanitizer.cs` invokes its `isAllowedChar` predicate
   twice per character (once to size the buffer, once to fill it) and never stated that
   the predicate must be pure. Fixed with an XML doc comment on the method explaining the
   contract and the failure mode if it's violated. No behavior change — both current
   predicates (`IsAllowedChar`, the correlation-ID lambda) are already pure.

### Declined (recorded, not silently dropped)

3. **Medium — `CorrelationIdMaxLength` floor.** See `tasks/declined-findings.md` D-9.
   Choosing a specific minimum above the existing `> 0` validation is a product decision
   outside FR-LOG-3..6's text; recording the decision (rather than leaving it unrecorded)
   is what this round fixes, per the follow-up review's actual complaint.

### Backfilled ledger (process fix)

`tasks/declined-findings.md` now has explicit "Round 3", "Round 4", and "Round 5"
subsections under "Round-by-round declines," closing the process gap the follow-up
review flagged (no round-3/4 entries existed at all).

### Not fixed this round (Low/Nit, out of the 5-round budget)

The remaining Low/Nit items catalogued in the original external review (the redundant
`using` alias in `HealthCheckEndpointModule.cs`, the single-caller `internal`
`ExtractTraceIdFrom` overload, the `SanitizeCorrelationId`/`SanitizeForCorrelationId`
naming asymmetry, `WriteAsJsonAsync` overwriting `application/problem+json` on the
catch-all 404, `CorrelationIdMaxLength` absent from deployment artifacts, the
triplicated startup filter, and untested 403/whitespace/negative-length/multi-valued-
header edge cases) remain open. None violate an FR; per §9.3 they are Low, and per
§9.4(b) the loop stops at the round-5 budget regardless of remaining findings below
Medium.

## PR description gap

The mandated D-8 lines (public-docs TODO, DMS-1518 note) and a rounds-3-5 narrative were
drafted and pushed via `report_progress` in an earlier session, but **the live GitHub PR
body was not updated** — `report_progress` commits and pushes code, it does not rewrite
the PR's title/body fields, and no available tool in this environment can edit an
already-open PR's description. This is a confirmed, repeated tool limitation, not a
one-off failure. The corrected description text has been provided to the requesting
human directly (chat response) and posted as a PR comment for convenience; a maintainer
with `gh pr edit` or GitHub UI access needs to apply it.

## Verification gates (this round)

- [x] `dotnet csharpier format src/dms` — no diff beyond the intentional edits
- [x] `dotnet build src/dms/EdFi.DataManagementService.sln -c Debug` — 0 warnings, 0 errors
- [x] `EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit` — 524/524 (523 + 1 new)
- [x] `EdFi.DataManagementService.Core.Tests.Unit` — 5093/5093
- [x] `EdFi.DataManagementService.Backend.Tests.Unit` — 3785/3785
- [x] `git diff --stat -- src/config` — empty (untouched)
- [x] No existing test assertions weakened; only one new test added

## Stop condition

Per §9.4(b): **5 rounds of Step 1 have completed. Stopping at the budget**, not early
convergence — round 4's convergence claim was premature (this round proves it). No
Critical or High findings are outstanding; the sole open item is the Low/Nit backlog
above and the still-unresolved PR-description-editing tool gap. Per §9.4, both must be
stated plainly rather than presented as a clean pass: **this is a budget-exhausted stop
with known, recorded, non-blocking Low findings remaining — not a Critical/High-free
"success" declaration.**
