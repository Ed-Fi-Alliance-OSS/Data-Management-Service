---
jira: DMS-1023
jira_url: https://edfi.atlassian.net/browse/DMS-1023
---

# Story: Cross-Engine Parity Tests and Shared Fixtures

## Description

Ensure the relational redesign behaves consistently across PostgreSQL and SQL Server:

- Behavioral parity holds across both dialects: equivalent PostgreSQL and SQL Server cases exercise the same production boundary and assert the same externally visible and authoritative storage semantics through provider-neutral shared contracts and thin per-engine adapters, rather than sharing identical fixture instances. This covers the shared runtime scenario baselines from `reference/design/backend-redesign/epics/07-relational-write-path/03-persist-and-batch.md` and `reference/design/backend-redesign/epics/07-relational-write-path/03b-profile-aware-persist-executor.md`.
- Differences are intentional and documented (e.g., error messages where dialect limits differ).
- This story owns the compact shared profile scenario matrix that keeps `DMS-1106`, `DMS-1105`, `DMS-984`, `DMS-1124`, `DMS-1104`, `DMS-1022`, and `DMS-1023` aligned on fixture names and coverage expectations.

## Authoritative catalog and coverage layers

The machine-readable source of truth for cross-engine parity is the C# catalog under `src/dms/backend/EdFi.DataManagementService.Backend.Tests.Common/Parity/` (`ParityScenarioCatalog*.cs`, with the typed model in `ParityScenarioModel.cs` and the structural rules in `ParityCatalogInvariants.cs`). This document is the architectural narrative and index for that catalog; it is not a second row-by-row copy. Each catalog row records a stable scenario id, the behavioral mechanic (production seam) it exercises — recorded as `ProductionBoundary`, which is the mechanic the assertions pin rather than the invocation entry point, and which belongs to exactly one layer — per-engine coverage and test locations, the reusable assertion/helper entry point named as concrete `Type.Method` member(s) (recorded separately from the test locations), any intentional dialect difference, a classification, and per-engine gap ownership. `Backend.Tests.Unit/Parity` asserts the catalog is complete and internally consistent.

Every row resolves to exactly one **effective** reusable assertion/helper entry point, in one of three modes (`EntryPointKind`): **Direct** — the row names its own provider-neutral shared contract in `Backend.Tests.Common` (`SharedEntryPoint`) as concrete `Type.Method` member(s) (a bare type name is rejected), or, for an API row, a `Type.Method` in the API test assembly; **Inherited** — a supporting-smoke or variant row reuses the shared contract of the scenario it defers to (`CoveredByScenarioId`) or of its canonical family (`CanonicalIdOf`); **ProviderSpecific** — no provider-neutral shared contract applies (a profile scenario whose per-engine fixtures share no `Backend.Tests.Common` contract, or an already-covered cross-engine `Both` row with no extracted shared contract), so the row's existing per-engine or unit test locations are the effective entry points, justified by a recorded `ProviderSpecificEntryPointRationale`. Profiled runtime execution is owned by `DMS-1124`, but that ownership does not itself force `ProviderSpecific`: a profile row whose PostgreSQL and SQL Server fixtures do consume a shared `Backend.Tests.Common` contract (for example `ProfileCollectionAlignedExtensionScenarios` or `ProfileNestedCollectionScenarios`) is recorded `Direct` against that contract. `ParityEntryPointResolution.ResolveEffectiveEntryPoint` computes the mode; a unit invariant enforces that every row resolves to one, that a provider-specific rationale appears only on provider-specific rows, and that a `Direct` entry point names a concrete member; reflection meta-tests validate that Direct/Inherited shared entry points and unit locations name real members in their owning assemblies (the per-engine backend and API locations are validated by the existing per-assembly resolution meta-tests); and a source-tree scan validates that every recorded location file exists — the `File` field is otherwise diagnostic, with the fixture type and `[Test]` method the authoritative location.

The catalog spans three coverage layers:

- **API (`DMS-1022`).** Representative HTTP CRUD/query/profile scenarios driven through the real DMS pipeline against both engines (`src/dms/tests/EdFi.DataManagementService.Tests.Integration/`).
- **Profile (`DMS-1124`).** Profile-aware relational-write scenarios (the feature matrix below) on the profile persist-executor boundary.
- **No-profile (`DMS-984`).** Full-surface relational-write scenarios on the no-profile persister/merge boundary.

Canonical identifiers are preserved verbatim and never renamed; variants are recorded as `<CanonicalId>/<PascalCaseVariant>`. There are **9 canonical profile ids** — `ProfileVisibleRowUpdateWithHiddenRowPreservation`, `ProfileVisibleRowDeleteWithHiddenRowPreservation`, `ProfileVisibleButAbsentNonCollectionScope`, `ProfileHiddenInlinedColumnPreservation`, `ProfileRootCreateRejectedWhenNonCreatable`, `ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable`, `ProfileHiddenExtensionRowPreservation`, `ProfileHiddenExtensionChildCollectionPreservation`, `ProfileUnchangedWriteGuardedNoOp` — and **8 canonical no-profile ids** — `NoProfileWriteBehavior`, `FullSurfaceCollectionReorder`, `NoProfileFullSurfaceCreate`, `NoProfileChangedPutOmissionSemantics`, `NoProfileGuardedNoOp`, `NoProfileMultiBatchCollection`, `NoProfilePostAsUpdate`, `NoProfileRollbackSafety`.

The feature matrix below is retained as the canonical profile feature-by-scenario map. Its `NoProfileWriteBehavior` and `FullSurfaceCollectionReorder` rows are **no-profile-layer** (`DMS-984`) scenarios; the remaining nine rows are the profile-layer canonical ids.

## Shared Profile Scenario Matrix

Use these scenario names verbatim in fixtures, helper APIs, acceptance criteria, and parity assertions.

| Scenario | Semantic merge and ordering | Non-collection visibility | Hidden-member overlay | `_ext` preservation | Creatability and failure path | Guarded no-op |
| --- | --- | --- | --- | --- | --- | --- |
| `NoProfileWriteBehavior` | Control path for full-surface writes |  |  |  |  |  |
| `FullSurfaceCollectionReorder` | Primary |  |  |  |  |  |
| `ProfileVisibleRowUpdateWithHiddenRowPreservation` | Primary |  | Primary | Variant coverage |  |  |
| `ProfileVisibleRowDeleteWithHiddenRowPreservation` | Primary |  | Primary | Variant coverage when `_ext` collections participate |  |  |
| `ProfileVisibleButAbsentNonCollectionScope` |  | Primary |  |  |  |  |
| `ProfileHiddenInlinedColumnPreservation` |  | Primary | Primary |  |  |  |
| `ProfileRootCreateRejectedWhenNonCreatable` |  |  |  |  | Primary |  |
| `ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable` |  | Variant coverage for new visible scopes |  | Variant coverage for extension scopes/items | Primary |  |
| `ProfileHiddenExtensionRowPreservation` |  |  | Primary | Primary |  |  |
| `ProfileHiddenExtensionChildCollectionPreservation` | Primary |  | Primary | Primary |  |  |
| `ProfileUnchangedWriteGuardedNoOp` | Compare/post-merge shape reused | Compare/post-merge shape reused | Hidden preservation must survive compare | Extension rows participate under the same compare rules |  | Primary |

Variant families carried under the shared scenario names:

- `NoProfileWriteBehavior`: one omitted non-collection scope case and one no-profile `_ext` case in addition to the ordinary changed-write control path.
- `ProfileVisibleRowUpdateWithHiddenRowPreservation`: no-previously-visible rows, interleaved update-plus-insert, nested collection scope, root-level extension child collection, and collection-aligned extension child collection.
- `ProfileVisibleRowDeleteWithHiddenRowPreservation`: delete-all-visible-while-hidden-rows-remain.
- `ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable`: new visible 1:1 scope, nested/common-type scope, collection/common-type item, extension scope, extension collection item, and a three-level chain where an existing visible middle-level parent still allows descendant update/create while a new visible middle-level parent is rejected because a required member is hidden and therefore blocks descendant extension-child creation.

Story alignment:

- `DMS-1106` consumes the contract-heavy scenarios: `ProfileVisibleRowUpdateWithHiddenRowPreservation`, `ProfileVisibleButAbsentNonCollectionScope`, `ProfileHiddenInlinedColumnPreservation`, `ProfileRootCreateRejectedWhenNonCreatable`, and `ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable`.
- `DMS-1105` reuses nested and `_ext` fixtures from `ProfileVisibleRowUpdateWithHiddenRowPreservation` and `ProfileHiddenExtensionChildCollectionPreservation`.
- `DMS-984` owns runtime execution for `NoProfileWriteBehavior` and `FullSurfaceCollectionReorder`.
- `DMS-1124` owns runtime execution for the profiled scenario set.
- `DMS-1104` owns the failure semantics for `ProfileRootCreateRejectedWhenNonCreatable` and `ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable`, plus invalid usage/forbidden-data cases that are not separate matrix scenarios.
- `DMS-1022` executes the API matrix on both engines. `DMS-1023` establishes the authoritative catalog and the provider-neutral shared no-profile contracts, and delivers the PostgreSQL executions and proofs; the SQL Server no-profile executions are owned by `DMS-1285`.

## Acceptance Criteria

- A shared fixture set exists that can run the same CRUD/query and shared profile scenario matrix above on pgsql and mssql.
- Parity assertions cover:
  - response bodies (JSON semantics),
  - update-tracking metadata behavior (`_etag/_lastModifiedDate/ChangeVersion` served from stored stamps),
  - paging determinism,
  - `NoProfileWriteBehavior`, including one omitted non-collection scope case, one no-profile `_ext` case, and `FullSurfaceCollectionReorder` with semantic-identity-based visible-row matching rather than request ordinal,
  - hidden-data preservation, hidden inlined-member preservation, hidden extension-column preservation on matched visible rows, key-unified canonical storage preservation, synthetic presence-flag preservation, hidden reference/descriptor FK preservation, and delete/clear behavior for profiled non-collection scopes across `ProfileVisibleRowUpdateWithHiddenRowPreservation`, `ProfileVisibleRowDeleteWithHiddenRowPreservation`, `ProfileVisibleButAbsentNonCollectionScope`, `ProfileHiddenInlinedColumnPreservation`, `ProfileHiddenExtensionRowPreservation`, and `ProfileHiddenExtensionChildCollectionPreservation`, and
  - the distinction between update-of-existing-visible-data and create-of-new-visible-data for profiled non-collection scopes and collection items, including `ProfileRootCreateRejectedWhenNonCreatable` and `ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable`, plus the three-level parent-create-denied/child-denied chain, and
  - the deterministic profile-scoped sibling-order rule "start from the current full sibling sequence for that scope instance, replace the visible-row subsequence with the merged visible rows in request order, preserve hidden rows in their existing relative gaps, append extra visible inserts after the last previously visible row for that scope instance (or at the end when there was no previously visible row), and renumber `Ordinal` contiguously", including the ordering variants nested under `ProfileVisibleRowUpdateWithHiddenRowPreservation` and `ProfileVisibleRowDeleteWithHiddenRowPreservation`, and
  - `ProfileUnchangedWriteGuardedNoOp`, and
  - profile-based validation/creatability failure semantics.
- The matrix above is the source of truth for shared fixture identifiers and scenario naming reused by `DMS-1106`, `DMS-1105`, `DMS-984`, `DMS-1124`, `DMS-1104`, and `DMS-1022`.
- Any dialect-specific differences are explicitly documented and tested.

## Tasks

1. Define and maintain the shared profile scenario matrix above as the canonical feature-by-scenario fixture map, including the ordering variants nested under `ProfileVisibleRowUpdateWithHiddenRowPreservation` and `ProfileVisibleRowDeleteWithHiddenRowPreservation`, plus the creatability variants nested under `ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable`.
2. Provide thin per-engine adapters that execute each scenario independently against its own engine through the provider-neutral shared contracts, plus per-assembly catalog-resolution meta-tests that prove each engine's declared coverage resolves to real tests. The engines execute independently; there is no in-process cross-engine comparison harness or byte-for-byte provider-output comparison.
3. Add documentation for expected/allowed differences and how to add new parity cases using the shared scenario names, including which ordering variants belong under `ProfileVisibleRowUpdateWithHiddenRowPreservation` and `ProfileVisibleRowDeleteWithHiddenRowPreservation`, and which creatability variants belong under `ProfileVisibleScopeOrItemInsertRejectedWhenNonCreatable`.

## Contributor workflow

To add or extend a parity scenario:

1. Define or extend the shared, provider-neutral scenario under a durable canonical id (or `<CanonicalId>/<PascalCaseVariant>`) in `Backend.Tests.Common`.
2. Add the thin provider entry points that consume it — one per engine — and, for the API layer, the mirrored HTTP wrappers.
3. Record the scenario in `ParityScenarioCatalog` with its behavioral mechanic (`ProductionBoundary`, valid for the row's layer), per-engine test locations (each `File` must name a real source file), coverage, classification, its reusable assertion/helper entry point (a direct `SharedEntryPoint` naming concrete `Type.Method` member(s), an inherited family/covered-by contract, or a provider-specific entry point with a `ProviderSpecificEntryPointRationale`), and — for any gap — the owning ticket.
4. Tag each new SQL Server backend fixture with exactly one `MssqlCiShards.Shard1..4` category; `Given_Mssql_Ci_Shard_Guardrails` enforces exactly one per fixture/method.
5. Run the catalog unit tests and the relevant PostgreSQL and SQL Server integration lanes.

Do not rename a canonical id, and do not hand-maintain a row-by-row copy of the catalog in this document.

## Allowed dialect differences and the parity rule

Parity is behavioral and **per-mechanic at the same production boundary**: equivalent PostgreSQL and SQL Server cases must exercise the same boundary and assert the same externally visible and authoritative storage semantics. Identical filenames, fixture counts, SQL text, or error wording are not required, and a mechanic covered on both engines through different resources still counts as parity.

Intentional dialect differences are recorded on the catalog row with a rationale. The primary example is no-profile multi-batch collection writes: PostgreSQL reserves collection ids via `generate_series` and caps at 65535 parameters / 1000 rows, whereas SQL Server has no `generate_series` equivalent and caps at 2100 parameters / 1000 rows. The asserted parity is the persisted rowset, contiguous 0-based ordinals, and batch partition counts — not the emitted SQL text.

## CI categories, shards, and configured-dependency semantics

- Backend integration fixtures carry `DatabaseIntegration` plus `PostgresqlIntegration` or `MssqlIntegration`; SQL Server backend fixtures also carry exactly one `MssqlCiShards.Shard1..4`. API integration tests carry `ApiIntegration` plus `PostgresqlIntegration`/`MssqlIntegration`.
- A missing local SQL Server admin connection string (`ConnectionStrings__MssqlAdmin`) may skip locally via `Assert.Ignore`, but a configured CI lane without it fails (`MssqlConnectionStringGuard.RequireConfiguredForCiOrSkipLocally`). A present-but-unreachable connection fails during leased-database setup, before any request is issued. No convention may report successful parity after silently skipping a configured SQL Server dependency in CI.

## Gap ownership

- **`DMS-1285`** owns adding the missing SQL Server no-profile executions — the thin MSSQL wrappers and any bounded MSSQL production defects they expose. No-profile family rows are recorded `KnownGap` with `MssqlGapOwner = DMS-1285` until those twins land.
- **`DMS-1023`** closed the PostgreSQL standalone-extension-child-collection deletion proof — the changed-PUT case (`NoProfileChangedPutOmissionSemantics/DeletedStandaloneExtensionChildCollection`), added in this story's no-profile-contract work (`PostgresqlRelationalWriteStandaloneExtensionChildDeleteTests.cs`). That row is now PostgreSQL `Covered` with no PostgreSQL owner; it remains `KnownGap` only for SQL Server, with `MssqlGapOwner = DMS-1285`, until the SQL Server twin lands.
