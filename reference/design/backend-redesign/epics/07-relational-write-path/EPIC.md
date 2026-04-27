---
jira: DMS-980
jira_url: https://edfi.atlassian.net/browse/DMS-980
---


# Epic: Relational Write Path (POST/PUT)

## Description

Implement the relational write pipeline for POST upsert and PUT by id, per:

- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md` (flattening design + plan shapes)
- `reference/design/backend-redesign/design-docs/transactions-and-concurrency.md` (reference validation, transaction boundary, propagation/stamping)
- `reference/design/backend-redesign/design-docs/extensions.md` (write-time `_ext` handling)

Core produces validated JSON + extracted references; the backend:
- resolves references/descriptors in bulk,
- consumes `WritableRequestBody` when Core applied a writable profile,
- loads/reconstitutes the current stored document for profiled update/upsert flows before invoking the Core-owned stored-state projector,
- flattens JSON into relational row buffers plus logical collection candidates (root + child tables + extensions),
- for update flows, compares request-derived rowsets to current persisted rowsets and short-circuits successful no-op updates using shared executor logic,
- invokes the Core-owned stored-state projector when profile-constrained merge semantics apply,
- persists changed rows in a single transaction using a shared stable-identity merge executor foundation for full-surface writes and a profiled follow-on that adds hidden-data preservation and creatability semantics,
- surfaces profile contract/runtime failures through dedicated typed error classification rather than DB constraint mapping, and
- relies on database-driven maintenance for derived artifacts required for correctness (propagated reference identity columns, `dms.ReferentialIdentity`, and update-tracking stamps).

Authorization remains out of scope; however, the roundtrip and transaction structures built in this epic should accommodate future authorization queries being batched into the same DB roundtrips. See `reference/design/backend-redesign/design-docs/auth.md` §"Performance improvements over ODS" for the expected roundtrip layout per operation.

Critical-path note: `reference/design/backend-redesign/epics/DEPENDENCIES.md` remains the source of truth for story blocking, but runtime collection-merge execution in this epic does not start until `DMS-1108` / `E15-S04b` (`reference/design/backend-redesign/epics/15-plan-compilation/04b-stable-collection-merge-plans.md`) lands. Re-scoped `DMS-984` consumes that retrofitted merge-plan contract to establish the request-scoped transaction boundary, stable-identity merge executor, batching, and guarded no-op freshness for full-surface writes; it must not be implemented against the earlier delete-by-parent / `Ordinal`-based write-plan shape. Profile-aware executor behavior is split to `DMS-1124`, which builds on `DMS-984` plus the Core/backend profile hand-off stories. `DMS-1106` is also blocked on `E15-S04b`, because its production compiled-scope adapter factory populates `SemanticIdentityRelativePathsInOrder` from `CollectionMergePlan.SemanticIdentityBindings`. The profile-dependent backend stories in this epic are blocked on specific Core profile stories produced by the delivery plan spike (`01a-core-profile-delivery-plan.md`): `DMS-1106` on C1, C5, C6, C8, and `E15-S04b`; `DMS-1105` on C1 and C6; `DMS-1104` on C8 plus `DMS-1124`. Readable profile projection in `DMS-990` is blocked on C7. DMS-983 split: re-scope `DMS-983` to the no-profile/backend-mechanics flattener thin slice, restore `WritableRequestBody` hand-off in follow-on `DMS-1123` / `02b-profile-applied-request-flattening.md`, then layer profiled persist behavior in `DMS-1124`; backend still must not guess the missing Core-side profile outputs.

## Stories

- `DMS-981` — `00-core-extraction-location.md` — Core emits concrete JSON locations for document references
- `DMS-982` — `01-reference-and-descriptor-resolution.md` — Bulk resolve `ReferentialId → DocumentId` and validate descriptors
- `DMS-1110` — `01a-core-profile-delivery-plan.md` — Core profile support delivery plan spike (creates the follow-on Core stories that block profiled write/read integration)
- `DMS-1111` — `01a-c1-compiled-scope-adapter-and-address-derivation.md` — Shared Compiled-Scope Adapter Contract + Address Derivation Engine (Core, Tier 0)
- `DMS-1114` — `01a-c2-semantic-identity-compatibility-validation.md` — Semantic Identity Compatibility Validation (Core, Tier 1)
- `DMS-1115` — `01a-c3-request-visibility-and-writable-shaping.md` — Request-Side Visibility Classification + Writable Request Shaping (Core, Tier 1)
- `DMS-1116` — `01a-c4-request-creatability-and-collection-validation.md` — Request-Side Creatability Analysis + Duplicate Collection-Item Validation (Core, Tier 2)
- `DMS-1117` — `01a-c5-assemble-profile-applied-write-request.md` — Orchestrate Profile Write Pipeline + Assemble ProfileAppliedWriteRequest (Core, Tier 2)
- `DMS-1118` — `01a-c6-stored-state-projection-and-hidden-member-paths.md` — Stored-State Projection + HiddenMemberPaths Computation (Core, Tier 3)
- `DMS-1113` — `01a-c7-readable-profile-projection.md` — Readable Profile Projection After Reconstitution (Core, Tier 0 — independent, no C-story dependencies)
- `DMS-1112` — `01a-c8-typed-profile-error-classification.md` — Typed Profile Error Classification (Core, Tier 0 — shared type contract)
- `DMS-1144` — `01d-profile-namespace-and-server-generated-fields.md` — Enforce the Core profile namespace rule so server-generated fields (`id`, `link`, `_etag`, `_lastModifiedDate`) are not profile-addressable, passing through readable-profile projection by construction; blocks the link-injection implementation story (`06a-link-injection-implementation.md`, DMS-1145)
- `DMS-1106` — `01b-profile-write-context.md` — Integrate the Core/backend profile write contract
- `DMS-1105` — `01c-current-document-for-profile-projection.md` — Load/reconstitute the current stored document for profiled update/upserts
- `DMS-983` — `02-flattening-executor.md` — Flatten validated write bodies into row buffers and collection candidates using compiled mapping (thin slice)
- `DMS-1123` — `02b-profile-applied-request-flattening.md` — Integrate `ProfileAppliedWriteRequest.WritableRequestBody` into the flattener after `DMS-1106`
- `DMS-984` — `03-persist-and-batch.md` — Persist rows with stable-identity merge semantics, batching, and guarded no-op freshness for the shared/no-profile executor foundation (pgsql + mssql); blocked on `DMS-1108` for the executor-facing collection merge-plan contract
- `DMS-1124` — `03b-profile-aware-persist-executor.md` — Apply profile-aware merge, hidden-data preservation, and creatability in the persist executor after `DMS-984`, `DMS-1106`, `DMS-1105`, and `DMS-1123`; planning recut on this branch is split into serial slice docs under `03b-profile-aware-persist-executor/`
- `DMS-985` — `04-propagated-reference-identity-columns.md` — Populate propagated reference identity columns (no reverse-edge table)
- `DMS-986` — `05-write-error-mapping.md` — Map DB constraint errors to DMS error shapes (consistent across dialects)
- `DMS-1104` — `05b-profile-error-classification.md` — Classify and map profile write failures to DMS error shapes
- `DMS-987` — `06-descriptor-writes.md` — Descriptor POST/PUT: maintain `dms.Descriptor` + descriptor referential identities (no per-descriptor tables)
- `DMS-1132` — `07-semantic-identity-presence-fidelity.md` — Close the presence-sensitive semantic identity fidelity gap exposed by `DMS-1124` so the merge never depends on upstream null-pruning invariants to distinguish absent vs explicit-null identity members

## Review Aid

- `review-checklist-profile-aware-write-path.md` — reviewer checklist for profile-aware shared write-path stories, including a short PR checklist for `DMS-1124`-class work
