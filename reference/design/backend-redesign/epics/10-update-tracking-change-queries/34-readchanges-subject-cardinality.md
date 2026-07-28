---
jira: TBD
jira_url: TBD
---

# Story: Expose `ReadChanges` Authorization Subject Cardinalities

## Description

Spike DMS-1185 found that the per-resource tracked-change person and EdOrg auth indexes are plan hazards on PostgreSQL as long as the `ReadChanges` predicate is `c."OldX" IN (SELECT ... FROM <UNION view> ...)`.
PostgreSQL estimates the `*IncludingDeletes` views' deduplicated output at the default 200 distinct rows (measured actuals 20k-80k); with the indexes present, the resulting misestimates flip plans to per-row nested loops (18x regression on `/keyChanges` at 10M tombstones) and join-filter anti-joins (~4x on narrow-window `/deletes`).
The Tier-1 indexes are proposed separately in `33-tracked-change-index-emission.md`; its five-PA covering category is expected to be blocked on PostgreSQL by the narrow-window `/deletes` anti-join flip until this story's shape fix removes it.
Per-resource EdOrg/person index emission is a separate downstream story so its A/B result can be isolated from the runtime rewrite.
Tracked namespace indexes are also separate because their PostgreSQL blocker is predicate/operator-class compatibility, not subject cardinality.
This story depends on Story 33's candidate-evaluation phase for its checked-in harness and result matrix, but not on successful Tier-1 production emission.

This story selects a provider-appropriate runtime `ReadChanges` SQL shape that exposes real subject-set cardinalities where evidence shows the current shape is unsafe.
The leading PostgreSQL candidate is resolving the claim's subject sets before composing the main query (person `DocumentId` sets and hierarchy-expanded EdOrg sets), binding them as parameters; alternatives (lateral/EXISTS restructuring, statistics on the view inputs) may be evaluated.
SQL Server may retain its current shape if the decision checkpoint demonstrates that it already produces stable plans at district scale.
The selected per-provider design must be recorded in `change-queries.md` and must handle subject sets of tens of thousands of ids.

## Acceptance Criteria

- Before implementation, complete a decision checkpoint that measures the current and candidate query shapes independently on both providers and records the selected mechanism per provider in `change-queries.md` § Authorization. Include how district-scale sets (50k+ subjects) bind, and how the `KeyChanges are always authorized based on the old values` peculiarity is preserved. Pre-resolution, PostgreSQL arrays, SQL Server TVPs, lateral/EXISTS restructuring, and retaining the current provider shape are candidates rather than preselected requirements.
- When a selected provider shape splits subject resolution from the main query, it must be transactionally consistent or fail closed: today authorization and data retrieval execute as a single command (`RelationalChangeQueryRepository`), so the split must guarantee that both phases observe one consistent snapshot (or an equivalent fail-closed contract). Document the selected consistency semantics and their interplay with the preserved peculiarities (person subjects retain access by design via the `*IncludingDeletes` views; the EdOrg hierarchy is current-only and is the freshness-sensitive input), and cover concurrent-revocation behavior on every provider that uses a split.
- `/deletes` and `/keyChanges` relationship authorization use the selected provider shape. `NamespaceBased` and `NoFurtherAuthorizationRequired` are unchanged by this story.
- Functional authorization correctness is covered through each selected provider path: all six supported strategy shapes (including hierarchy direction for `RelationshipsWithEdOrgsOnlyInverted`), `keyChanges` old-value authorization, AND-within-strategy / OR-across-strategies / namespace-AND composition, paging and `totalCount` ordering unchanged, and the selected provider parameterization boundaries.
- Preserve the distinction between dynamic emptiness and static unrepresentability:
  - zero claim EdOrg ids or a valid tracked subject that resolves to no ids produces the dialect match-nothing shape and a successful response, not a 500 and not a fail-open predicate; normal AND-within-strategy and OR-across-strategies composition still applies;
  - a declared securable that has no representable tracked old-value column remains a security-configuration failure;
  - only-array-nested EdOrg securables and mixed root/array EdOrg securables exercise that security-configuration outcome on both providers, while a valid root subject with a dynamically empty resolved set exercises the successful-empty outcome;
  - cover the matrix for both `/deletes` and `/keyChanges`.
- The direct EdOrg claim-match fallback of `auth.md` § "Direct EdOrg claim match" survives every selected shape and is tested explicitly: a claim EdOrg id with no matching row in `auth.EducationOrganizationIdToEducationOrganizationId` still authorizes rows whose securable EdOrg value equals the claim, for both normal and inverted strategies, on both providers; the test data must not rely on hierarchy self-rows, which would mask loss of the direct arm.
- Re-run Story 33's checked-in DMS-1185 read matrix without adding the deferred per-resource indexes, in two configurations where they differ: once against the production DDL (only Story 33's emitted categories present) to hold the general 1.20 median regression gate for the selected shape on both providers, and once with Story 33's pinned five-PA candidate overlay applied to demonstrate that the narrow-window `/deletes` anti-join flip is gone on all five PrimaryAssociation fixtures. Passing the candidate-overlay rerun unlocks Story 33's PA-category emission gates; the overlay itself remains a benchmark input, unauthorized for production, until those gates pass. Record result counts and plans so the downstream EdOrg/person index story starts from a fixed runtime baseline.
- This story does not change derived index inventory, DDL, manifests, or `RelationalMappingVersion`.
