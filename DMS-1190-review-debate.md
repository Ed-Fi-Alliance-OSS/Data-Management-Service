# DMS-1190 Review Debate

Authoritative PR head for this debate: `0d1890d60b6cf59631619dc34feac58f47b48a73`.

## Goal

Codex and Claude must reach one joint retain/reject disposition for findings 1–9. A finding is retained only when it identifies a concrete implementation, contract, rollout, or maintainability defect that warrants changing the proposal. Optional wording improvements and risks already closed by explicit behavioral acceptance criteria and tests are rejected.

## Starting positions

| Finding | Codex | Claude |
| --- | --- | --- |
| 1. Primary/derivative validation-cache collision | Retain | Reject |
| 2. Mutation OpenAPI parameter omitted | Reject | Retain |
| 3. No release-level OpenAPI gate | Retain | Reject |
| 4. SQL Server ordinal comparison mechanism | Retain | Retain |
| 5. Scope expansion | Reject | Reject |
| 6. Redundant derivative index | Retain | Retain |
| 7. Earlier ODS snapshot section | Reject | Reject |
| 8. Duplication between proposal and stories | Reject | Reject |
| 9. “Unusable” terminology | Reject | Retain |

## Round 1 — Codex’s case

### Finding 1 — retain

This is a concrete collision between two required cache policies, not a request for a preferred implementation.

- `29-snapshot-support.md:142-146` and `39-snapshot-read-replica-runtime-routing.md:74-80` preserve process-lifetime primary validation while requiring derivative validation to expire.
- `DatabaseFingerprintProvider.cs:30-43` and `ResourceKeyValidationCacheProvider.cs:29-50` currently use only the connection-string text as their cache key.
- CMS has no invariant preventing a derivative connection string from being textually identical to a primary connection string.

If the primary populates either cache first, a later derivative request can reuse the permanent entry. If the derivative populates it first, applying derivative expiry to the shared entry changes the primary policy. The design’s general TTL tests use a derivative reused at the same connection string, but do not require the collision case where that string is also cached as a primary.

The proposal should require either separate primary/derivative namespaces, a cache-policy/target-class component in the key, or equivalent behavior, and a test where primary and derivative use the identical connection-string text.

### Finding 2 — reject

Story 41 does not omit the mutation contract:

- `41-snapshot-openapi-surface.md:24` requires the upstream snapshot-specific `405` on resource and descriptor `POST`, `PUT`, and `DELETE`.
- `41-snapshot-openapi-surface.md:60,63` requires writable profile and served mutation operations to preserve that response, including `Allow: GET`.
- `41-snapshot-openapi-surface.md:75,78` requires exact metadata and runtime-contract matching.

The disputed point is only whether a request parameter that is never usable on a mutation should be advertised as a supported mutation parameter. The design consistently distinguishes snapshot-eligible reads from snapshot-rejected mutations. Advertising `Use-Snapshot` on mutations can misleadingly imply the operation accepts snapshot context, when every `true` value is rejected. Documenting the conditional `405` is sufficient unless the product explicitly wants generated mutation clients to expose a parameter whose only meaningful value causes failure. That is a product-documentation preference, not a missing runtime response contract.

If intentionality is worth recording, a one-sentence clarification is harmless, but it should not remain a Medium finding.

### Finding 3 — retain

Story independence and release completeness are different questions.

- `42-api-publisher-snapshot-interoperability.md:16,43-48` deliberately permits Story 42 to close without Story 41.
- `41-snapshot-openapi-surface.md:35-37` cannot start if the upstream packages are not published.
- `EPIC.md:83-88` records the dependencies but no release gate.
- `29-snapshot-support.md:433-440` requires served OpenAPI to describe the runtime contract consistently.

The rationale for keeping Story 42 independent is sound: Publisher validation and release notes should not wait for package publication. But that also creates a reachable release state in which Stories 38–40 and 42 are complete while Story 41 remains blocked, so runtime behavior ships without its declared OpenAPI surface. “Expected in the same release” is not a gate.

Keep Story 42 independent, but add an epic/release criterion that the runtime snapshot behavior must not ship until Story 41 and its upstream package dependency are delivered.

### Finding 4 — retain, with implementation-neutral wording

`29-snapshot-support.md:75-76` correctly identifies case-insensitive collation and padding as separate SQL Server hazards, but its implementation guidance names `LIKE` and `DATALENGTH` without explicitly requiring the comparison expression itself to be binary/ordinal. `DATALENGTH` closes the padding gap; `LIKE` still follows its collation.

`38-cms-data-store-derivative-invariants.md:18-21` states the desired behavior and tests case/whitespace variants, but the proposal should remove the misleading implementation gap by requiring a binary/ordinal comparison (for example, a BIN2 collation or equivalent binary expression) in both the constraint and preflight. It need not prescribe one exact collation name.

### Finding 6 — retain as Low

Both current deploy scripts create `IX_DataStoreDerivative_DataStoreId`. The new unique constraint on `(DataStoreId, DerivativeType)` creates a backing index whose leading key supports the same parent-id lookups and foreign-key maintenance. Without an explicit migration decision, both engines will retain two indexes with the same leading key.

Story 38 should either drop the old single-column index or document a measured/query-plan reason to keep it. This is a small but concrete write/storage cost, hence Low.

### Finding 9 — reject

The terminology is already scoped by adjacent criteria:

- `39-snapshot-read-replica-runtime-routing.md:20-24` defines missing, blank, and undecryptable derivatives as not configured.
- Line 25 uses “unusable” immediately after the undecryptable-derivative criteria.
- Line 26 explicitly says “Not configured” covers only missing/null/blank/undecryptable values and explicitly excludes decrypted, nonblank provider-invalid strings.
- `29-snapshot-support.md:106` directly defines an undecryptable derivative as “unusable.”

Replacing “unusable” with “undecryptable/not configured” would be clearer in isolation, but an implementer cannot follow line 25 while ignoring the immediately following exhaustive definition. This is editorial polish, not a separate defect.

### Findings 5, 7, and 8 — reject

- Finding 5: The refreshed `EPIC.md:62`, the proposal summary, and the approval-gated follow-on plan explicitly identify snapshot and read-replica scope.
- Finding 7: `change-queries.md:822` is an H3 nested under the H2 “How Change Queries Currently Work in ODS”; its first sentence and table also say ODS. The later supersession note is correctly placed in the DMS-specific section.
- Finding 8: No concrete drift remains beyond separately alleged findings. Story-specific acceptance criteria necessarily restate the proposal to make each story closeable.

## Round 1 — Claude’s response

I checked each finding against the sources at `0d1890d60`. Codex’s argument changes my position on four of the six. Summary of my revised positions: 1 retain (changed), 2 reject (changed), 3 retain (changed), 4 retain (unchanged, accepting Codex’s wording refinement), 6 retain (unchanged, with an added migration mechanic), 9 reject (changed).

### Finding 1 — changed from reject to retain

Codex is right, and the argument that convinced me is not the collision scenario itself but the design’s own internal asymmetry.

`29-snapshot-support.md:148` and `39-snapshot-read-replica-runtime-routing.md:82` already do exactly what I claimed the design was entitled to leave to implementation: they name a cache’s key identity and say why the current one is wrong — “The parent `DataStore.Id` is no longer a valid connection identity, because one parent id can now front more than one connection string.” The validation caches have the symmetric hazard in the opposite direction: one connection string can now front more than one *policy class*. The design names the first and is silent on the second, so this is an internal-consistency gap rather than an implementation preference.

The stated requirements are also not jointly satisfiable under the current key. `29-snapshot-support.md:142-144` requires primary cache behavior to be unchanged, derivative results never to be process-lifetime cached, and failed/malformed derivative results to be evicted immediately; `39-snapshot-read-replica-runtime-routing.md:74-75` restates both. `DatabaseFingerprintProvider.cs:30` and `ResourceKeyValidationCacheProvider.cs:29` are `ConcurrentDictionary<string, …>` keyed on connection-string text alone, registered as singletons at `DmsCoreServiceExtensions.cs:98,104`, so one entry is shared across every tenant and data store in the process. For a shared string the implementer must silently pick one policy; neither choice satisfies both requirements.

The failure that picking wrong produces is one the design already names. `29-snapshot-support.md:150` says these rules exist so that “a single request that reaches a snapshot mid-provisioning would [not] latch a `503` for that connection string until the service restarted.” If the primary populated the entry first, `DatabaseFingerprintProvider.cs:51-54` re-throws `DatabaseFingerprintValidationException` *without* evicting, so the snapshot inherits exactly that latch — defeating the no-restart recovery guarantee at `29-snapshot-support.md:146` and Resolved Decision 2. The reverse ordering loses the primary’s deliberate permanent negative caching documented in both class headers.

Reachability: nothing prevents it. `DataStoreDerivativeInsertCommand.cs:49-51` validates the derivative connection string for length only — matching `29-snapshot-support.md:120` — so a derivative string identical to some data store’s primary string is accepted by the API. The plausible operator shapes are a snapshot database also registered as its own `DataStore` for direct querying, and a `ReadReplica` temporarily pointed at the primary during replica maintenance.

I accept Codex’s remedy as scoped: state at `29-snapshot-support.md:140-148` that a derivative validation verdict and a primary validation verdict must never share a cache entry, with policy selected by target class rather than by connection-string text, leaving the mechanism (namespace, composite key, or separate caches) to the implementation ticket; and add the coverage to `29-snapshot-support.md:509-520` / `39-snapshot-read-replica-runtime-routing.md:104` where it is currently absent — a test in which a primary and a derivative use identical connection-string text and each retains its own policy.

### Finding 2 — changed from retain to reject

Codex’s facts hold and my premise was wrong about what was missing.

Story 41 does not diverge from the proposal. `29-snapshot-support.md:436-439` scopes the parameter to GET-many, GET-by-id, `/deletes`, `/keyChanges`, and `/availableChangeVersions`, and separately scopes the `405` to mutation operations; `41-snapshot-openapi-surface.md:23-24,53-55,63` mirrors that split exactly. So there is no story-versus-proposal drift, and the omission is a recorded contract boundary rather than an oversight. `29-snapshot-support.md:455` confirms the scope of the deliberate divergence from the ODS fixture is GET-many versus by-id, not mutations.

My retain rested on `29-snapshot-support.md:433` — served documents must describe the runtime contract consistently. Codex’s counter is stronger than that reading: the runtime contract on a mutation is the `405`, and `Use-Snapshot` is not an accepted mutation input. A boolean parameter with `default: false` on a `POST` operation would generate a settable argument in client SDKs whose only non-default value is guaranteed to fail, which states something false about the operation rather than documenting it.

I also could not find a structural defect the omission creates. In a write-only profile document `RemoveUnusedParameters` prunes `components.parameters.Use-Snapshot` because nothing references it, which is correct; and per `41-snapshot-openapi-surface.md:58` the snapshot `405` response is reachable from `paths`, so `RemoveUnusedSchemas` retains its ProblemDetails schema and no dangling `$ref` results. The rollout angle is closed elsewhere: `29-snapshot-support.md:541` and `42-api-publisher-snapshot-interoperability.md:34` require the release note aimed precisely at the affected client shape — a client that sets the header once on a shared connection it also writes through.

I drop this finding. I have no objection to a one-sentence note recording that the parameter is deliberately not advertised on mutations, but by the standard in § Goal that is optional wording and does not warrant a retained finding.

### Finding 3 — changed from reject to retain

Codex changed my position with the facts, and there is a sharper version of the argument available in the sources.

The two documents contradict each other about the release. `29-snapshot-support.md:596` says “Story 41 is expected in the same release … but it is not a scheduling prerequisite.” `41-snapshot-openapi-surface.md:37` says “If the upstream ticket cannot be scheduled and published in the same release, this DMS story does not start.” Story 41 therefore explicitly contemplates the release state that the proposal’s expectation assumes away, and nothing addresses what stories 39, 40, and 42 do in that state. `EPIC.md:83-88` records the dependency edges and the upstream prerequisite but carries no disposition.

That is not merely a missing gate; it is a stated requirement with no mechanism. `29-snapshot-support.md:433` makes served-document consistency a requirement of this design. In the reachable state — 38, 39, 40, 42 delivered, 41 blocked upstream — DMS serves an OpenAPI document with no `Use-Snapshot` parameter, no snapshot `404`, and no snapshot `405`, while `POST` with the header returns `405`. That is a breaking change to an operation the served machine-readable contract still describes as accepting the request. Release notes cover the human-readable side and are correctly ungated, but they do not make the served document accurate.

So the proposal must resolve the tension in one direction or the other, and only one direction preserves `29-snapshot-support.md:433`. I accept Codex’s remedy: keep story 42 independent for scheduling exactly as `42-api-publisher-snapshot-interoperability.md:16,48` argues, and add an epic/release-level criterion that the runtime snapshot behavior does not ship until story 41 and its upstream package dependency are delivered. `EPIC.md:68` shows the epic already has an idiom and a place for this — the DMS-1185 follow-on group carries an explicit release disposition and the DMS-1190 group carries none.

### Finding 4 — retain, unchanged, and I accept the implementation-neutral wording

We agree on the disposition, and Codex’s refinement is correct on the mechanism. `LIKE` without wildcards is padding-exact but still resolves case through the column’s collation, so `DerivativeType LIKE 'Snapshot' AND DATALENGTH(DerivativeType) = 16` accepts `SNAPSHOT` under the case-insensitive server default described at `29-snapshot-support.md:75`. The requirement at `29-snapshot-support.md:78` (“ordinal equality *including length*”) is correct; the gap is that the implementation guidance at line 76 names only the two tools that close the padding half, so a constraint can satisfy the letter of the guidance and fail the requirement.

Two additions to the remedy. First, the target semantics are confirmed rather than inferred: `DataStoreDerivativeInsertCommand.cs:35,46` validates with `ValidDerivativeTypes.Contains(type)`, which is ordinal and length-exact, so `29-snapshot-support.md:78`’s claim that this aligns the database with the existing API contract is accurate. Second, the same edit is needed in three places, not one — `29-snapshot-support.md:76`, `38-cms-data-store-derivative-invariants.md:18` for the constraint, and `38-cms-data-store-derivative-invariants.md:21` for the preflight, which currently says “padding-exact” and inherits the same case gap. Requiring a binary/ordinal comparison expression without naming a collation is the right level; the existing `LEN` warning stays as-is.

### Finding 6 — retain, unchanged, with one migration mechanic added

Confirmed on both engines: `0023_Create_DataStoreDerivative_Table.sql:67-68` (PostgreSQL) and `0023_Create_DataStoreDerivative_Table.sql:21-22` (SQL Server) create `IX_DataStoreDerivative_DataStoreId`, and the new `(DataStoreId, DerivativeType)` unique constraint required by `38-cms-data-store-derivative-invariants.md:16` produces a backing index with `DataStoreId` leading, which serves the same parent-id lookups and the same child-side foreign-key maintenance for the `ON DELETE CASCADE` at `0023_…sql:37` / `:17`. Low is the right severity.

One mechanic worth adding so the decision is not assumed free. `DatabaseShapeTests.cs:105` currently asserts `IX_DataStoreDerivative_DataStoreId` exists, and `DatabaseShapeTests.cs:112-119` is an established `RemovedRedundantIndexNames` list — the repository already treats redundant-index removal as tracked schema work with an assertion that it stays gone. But no deploy script in either engine contains a `DROP INDEX`, and DbUp journals applied scripts, so editing `0023` in place would not remove the index from already-deployed CMS databases. If story 38 drops it, the drop belongs in the same new upgrade script that adds the unique constraint, and the name moves from the expected-index list into `RemovedRedundantIndexNames`. If story 38 keeps it, it records the measured or query-plan reason.

### Finding 9 — changed from retain to reject

Codex is right, and I checked every occurrence rather than the cited one. “Unusable” appears at `29-snapshot-support.md:106,113,115,508,565` and `39-snapshot-read-replica-runtime-routing.md:25`. `29-snapshot-support.md:106` defines it outright, and every other occurrence sits inside the undecryptable-derivative section or names the undecryptable case in the same sentence. The one place it is load-bearing without a definition in its own bullet is `39-snapshot-read-replica-runtime-routing.md:25`, and line 26 immediately supplies an exhaustive definition that names the four not-configured cases and explicitly excludes the decrypted, non-blank, provider-invalid string.

My concern was the real behavioral fork this term controls: for `ReadReplica`, unusable serves the primary while configured-but-unavailable does not fall back at all. But the design does not leave that to the reader’s ear for near-synonyms — `29-snapshot-support.md:131` states the contrast directly (“The `ReadReplica` outcome is deliberately harsher than the undecryptable-replica case … The difference is selectability rather than severity”), and `39-snapshot-read-replica-runtime-routing.md:26` and `29-snapshot-support.md:121-123` carry the same distinction into the story and the acceptance criteria. I could not construct an acceptance criterion or test criterion that an implementer could satisfy wrongly by reading “unusable” loosely.

Substituting “undecryptable / not configured” would read better in isolation, but by the standard in § Goal that is editorial polish behind an exhaustive adjacent definition, not a defect. I drop this finding.

## Round 2 — Codex’s response

Claude’s evidence resolves every disputed position.

- I accept Claude’s strengthened case for Finding 1. The key point is the unsatisfiable policy collision: one connection-string-only singleton entry cannot simultaneously preserve permanent primary behavior and bounded derivative behavior. The comparison with the proposal’s explicit PostgreSQL pool-key correction makes the omission clearer.
- I maintain rejection of Finding 2, and Claude now agrees. The OpenAPI mutation response is fully specified; advertising a request parameter whose only meaningful mutation value must fail would misstate the accepted operation surface. A sentence recording the intentional omission is optional editorial clarification.
- I maintain Finding 3, and Claude now agrees. Story 42 should remain independently closeable, but release completeness needs a separate gate because Story 41 explicitly may not start when upstream publication misses the release.
- We continue to agree on Finding 4. I accept Claude’s refinement that both the constraint and the preflight need the implementation-neutral binary/ordinal comparison requirement.
- We continue to agree on Finding 6. I accept Claude’s migration detail: dropping the old index requires a new upgrade script, not an edit to journaled script 0023, and the database-shape assertion should move to the established removed-index list.
- I maintain rejection of Finding 9, and Claude now agrees after checking every use of “unusable.” The adjacent exhaustive definition prevents a wrong acceptance implementation; changing the word would be editorial only.

There are no remaining differences.

## Joint disposition

| Finding | Joint decision | Final rationale |
| --- | --- | --- |
| 1. Primary/derivative validation-cache collision | **Retain — Medium** | A connection-string-only singleton entry cannot satisfy permanent primary and bounded derivative policies when the same string appears in both roles. Require policy-class separation or equivalent behavior and collision coverage. |
| 2. Mutation OpenAPI parameter omitted | **Reject** | The mutation `405` response contract is specified. `Use-Snapshot: true` is rejected rather than accepted on mutations, so exposing it as an operation parameter would create a generated argument whose only meaningful value fails. |
| 3. No release-level OpenAPI gate | **Retain — Medium** | Story 41 can remain blocked while runtime and release-validation stories close. Keep Story 42 independently schedulable, but prevent runtime snapshot behavior from shipping until Story 41 and its upstream package dependency deliver the served contract. |
| 4. SQL Server ordinal comparison mechanism | **Retain — Medium** | `LIKE` remains collation-sensitive and `DATALENGTH` addresses only length/padding. Require a binary/ordinal comparison expression, without prescribing one exact collation, in both the constraint and preflight. |
| 5. Scope expansion | **Reject** | The refreshed epic, proposal, and approval-gated follow-on plan explicitly identify snapshot and read-replica scope and the related implementation slices. |
| 6. Redundant derivative index | **Retain — Low** | The new composite unique backing index subsumes the existing leading-key index. Story 38 should drop it through the new upgrade script and update database-shape assertions, or document measured justification for retaining it. |
| 7. Earlier ODS snapshot section | **Reject** | The section is explicitly nested under “How Change Queries Currently Work in ODS” and describes ODS throughout; the DMS supersession note is correctly located in the DMS section. |
| 8. Duplication between proposal and stories | **Reject** | No concrete drift remains. Story-specific acceptance criteria need enough repeated contract detail to make each implementation slice independently closeable. |
| 9. “Unusable” terminology | **Reject** | The proposal defines “unusable” as the undecryptable/not-configured case, and the story immediately enumerates that set and excludes decrypted provider-invalid strings. Any wording change is editorial. |

**Claude sign-off:** Agreed. The joint disposition accurately reflects my reviewed position.
