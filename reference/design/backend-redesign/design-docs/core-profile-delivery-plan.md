# Core Profile Support Delivery Plan

This document is the delivery plan for Core-owned profile support, produced by the spike story `DMS-1106` (`reference/design/backend-redesign/epics/07-relational-write-path/01a-core-profile-delivery-plan.md`).

It translates the ownership statements in `reference/design/backend-redesign/design-docs/profiles.md` into a concrete implementation plan, shared contract definitions, and follow-on story inventory.

- Profiles design doc: [profiles.md](profiles.md)
- Overview: [overview.md](overview.md)
- Flattening & reconstitution: [flattening-reconstitution.md](flattening-reconstitution.md)
- Compiled mapping set: [compiled-mapping-set.md](compiled-mapping-set.md)

## Table of Contents

- [Overview](#overview)
- [Current State Assessment](#current-state-assessment)
- [Profile Definition Input Contract](#profile-definition-input-contract)
- [Shared Compiled-Scope Adapter Contract](#shared-compiled-scope-adapter-contract)
- [Core Contract Types](#core-contract-types)
- [No-Profile Passthrough Path](#no-profile-passthrough-path)
- [Implementation Slices](#implementation-slices)
- [Follow-On Story Inventory](#follow-on-story-inventory)
- [Sequencing and Dependency Graph](#sequencing-and-dependency-graph)
- [Dependency Map Updates](#dependency-map-updates)

---

## Overview

### Purpose

This plan breaks the 15 Core-owned responsibilities enumerated in `profiles.md` §"Everything DMS Core Is Expected to Own" into concrete, testable implementation stories. Each story maps to specific responsibilities, defines inputs and outputs, and states test expectations.

### Relationship to profiles.md

`profiles.md` is the normative design document. This plan does not alter or extend the design; it organizes implementation work to deliver the design. Where this plan quotes contract shapes, algorithms, or rules, the canonical source is `profiles.md`.

### Core vs Backend Ownership Boundary

The boundary is defined in `profiles.md` §"Ownership Boundary":

- **Core** owns all profile semantics: metadata loading/validation, readable/writable interpretation, recursive member filtering, collection item value filtering, request validation, request shaping, stored-state projection, address derivation, visibility signaling, creatability analysis, readable projection, extension semantics, and structured error classification.
- **Backend** owns persistence mechanics: current-state loading, reference resolution, flattening, semantic-key matching, `CollectionItemId` reservation, DML execution, no-op detection, and concurrency enforcement.
- Core communicates to backend through `ProfileAppliedWriteRequest` and `ProfileAppliedWriteContext`. Backend validates Core-emitted addresses against compiled metadata before using them.

---

## Current State Assessment

### Existing Capabilities

- Profile metadata loading and readable/writable profile definitions exist in Core today.
- `WritableRequestBody` is produced by existing Core canonicalization + profile shaping.
- `VisibleStoredBody` concept exists but is not yet backed by the full structured contract.

### Gap Analysis Against 15 Responsibilities

| # | Responsibility | Current State | Gap |
| --- | --- | --- | --- |
| 1 | Profile metadata loading and validation | Partial — basic loading exists | Semantic identity compatibility validation missing |
| 2 | Readable vs writable profile interpretation | Partial — selection exists | Needs integration with adapter contract |
| 3 | Recursive member filtering | Partial — member selection exists | Must produce structured scope states, not just filtered JSON |
| 4 | Recursive collection item value filtering | Partial — predicates exist | Must produce `VisibleRequestCollectionItem` entries |
| 5 | Writable request validation | Partial — forbidden data rejection exists | Duplicate visible collection-item validation by compiled semantic identity missing |
| 6 | Creatability analysis | Missing | Full top-down creatability per profiles.md decision model needed |
| 7 | Writable request shaping | Partial — `WritableRequestBody` exists | Must also produce `RequestScopeStates` and `VisibleRequestCollectionItems` |
| 8 | Stored-state projection for writes | Missing | `StoredScopeStates`, `VisibleStoredCollectionRows`, `HiddenMemberPaths` not yet produced |
| 9 | Stable scope and row address derivation | Missing | `ScopeInstanceAddress` / `CollectionRowAddress` derivation per normative algorithm needed |
| 10 | Visibility signaling for all scopes | Missing | Structured per-scope visibility (`VisiblePresent` / `VisibleAbsent` / `Hidden`) needed |
| 11 | Collection visibility details | Missing | `VisibleStoredCollectionRows` keyed to compiled scope identity needed |
| 12 | Semantic identity compatibility validation | Missing | Pre-runtime gate for writable profiles hiding semantic identity fields needed |
| 13 | Read projection | Partial — readable filtering exists | Must apply after full relational reconstitution via Core-owned projector |
| 14 | Extension profile semantics | Partial — `_ext` filtering exists | Must cover visibility, creatability, and `HiddenMemberPaths` for extension scopes |
| 15 | Structured error classification | Missing | Typed failure categories needed |

---

## Profile Definition Input Contract

### Assumed Shape

All Core profile stories (C2–C8) take a "writable profile definition" or "readable profile definition" as input. This is the resolved profile metadata describing which members, collections, and extensions are included/excluded for the applicable profile. The assumed shape is the existing `ProfileDefinition` produced by Core's current profile metadata loading infrastructure.

### Resolution Path

Existing Core infrastructure already handles:
- Matching an inbound request to its applicable profile (by resource name and content type),
- Loading the profile XML definition,
- Parsing it into the structured `ProfileDefinition` consumed by downstream logic.

### Prerequisite, Not a New Story

Adapting the existing profile loading to produce a `ProfileDefinition` compatible with the new adapter-based contract (if any adaptation is needed) is a prerequisite task within the existing infrastructure, not a new story in this plan. The C2–C8 stories assume this input is available and correctly resolved. If the existing loading infrastructure requires changes to work with the compiled-scope adapter vocabulary, those changes are scoped to the integration layer and do not alter the Core profile semantics defined in `profiles.md`.

---

## Shared Compiled-Scope Adapter Contract

### Contract Definition

Per `profiles.md` §"Shared Compiled-Scope Adapter", the selected mapping set must expose an immutable resource-scoped compiled-scope catalog or equivalent adapter. Core consumes only this narrowed adapter for address derivation and canonical member-path vocabulary. Backend continues to use its full compiled plans for binding accounting, DML generation, and runtime validation.

### Minimum Surface (6 Fields per Scope)

| Adapter field | Required semantics |
| --- | --- |
| `JsonScope` | Exact compiled scope identifier used by `DbTableModel.JsonScope` / `TableWritePlan.TableModel.JsonScope` |
| `ScopeKind` | Distinguishes `Root`, `NonCollection`, and `Collection` so Core knows whether to emit `ScopeInstanceAddress` or `CollectionRowAddress` |
| `ImmediateParentJsonScope` | Compiled parent scope that directly owns this scope/item; collection-aligned `_ext` scopes point at the aligned base scope rather than an ordinal path |
| `CollectionAncestorsInOrder` | Compiled collection scopes on the path from the root-most collection ancestor to the immediate parent collection ancestor |
| `SemanticIdentityRelativePathsInOrder` | For persisted multi-item collection scopes, the non-empty compiled semantic identity member paths in `CollectionMergePlan.SemanticIdentityBindings` order |
| `CanonicalScopeRelativeMemberPaths` | Canonical scope-relative member-path vocabulary Core uses when emitting `SemanticIdentityPart.RelativePath` and `HiddenMemberPaths` |

### Lifecycle

- Backend builds the adapter from the same selected mapping set/resource plan instance used for write execution and read/write reconstitution.
- The adapter is an internal runtime contract. It may be cached, serialized, or code-generated with the mapping set, but it must stay version-aligned with the compiled plans it was derived from.
- Core and backend operate over the same compiled scope vocabulary.

### Construction Responsibility

C1 delivers the adapter contract types, the address derivation engine, and a test-only adapter factory (constructing adapter instances from hand-built test metadata). The production adapter factory — which populates adapter instances from the selected mapping set's `TableWritePlan`, `CollectionMergePlan`, and `DbTableModel` — is backend's responsibility. This factory is owned by DMS-1103 (`E07-S01b`) or a prerequisite task within it. C1 does not depend on backend compiled-plan types; backend depends on C1's contract types.

### Storage Topology Is Backend-Only

The adapter's `ScopeKind` distinguishes `Root`, `NonCollection`, and `Collection` but intentionally does not surface inlined-vs-separate-table storage topology. `profiles.md` defines different execution families for separate-table and inlined scopes, but this distinction is resolved by backend from its own `TableWritePlan` metadata at DML execution time. Core does not need it — Core emits visibility classification and `HiddenMemberPaths` for all `NonCollection` scopes uniformly. The adapter intentionally does not expose storage topology.

### Vocabulary Split

- Core emits `SemanticIdentityPart.RelativePath` and `HiddenMemberPaths` only in the canonical scope-relative vocabulary published by the adapter.
- Core must not synthesize alternate relative path strings ad hoc.
- Backend resolves those canonical member paths to physical bindings through `TableWritePlan`, `CollectionMergePlan`, `KeyUnificationWritePlan`, and related compiled metadata it already owns.

### Validation Contract

- Backend validates Core-emitted addresses and canonical member paths against its locally selected compiled plans and fails deterministically on drift rather than attempting best-effort coercion.
- The adapter keeps the ownership boundary narrow: Core needs compiled scope shape and canonical path vocabulary, but it does not need direct knowledge of column bindings, FK bindings, key-unification storage columns, `Ordinal` handling, or SQL plan shapes.

---

## Core Contract Types

### ProfileAppliedWriteRequest

Per `profiles.md` §"Minimum Core Write Contract", the implementation must be semantically equivalent to:

```
ProfileAppliedWriteRequest(
    WritableRequestBody,        // JsonNode — request after canonicalization + writable-profile shaping
    RootResourceCreatable,      // bool — Core-owned decision for profile-constrained creates
    RequestScopeStates,         // ImmutableArray<RequestScopeState> — per non-collection scope
    VisibleRequestCollectionItems  // ImmutableArray<VisibleRequestCollectionItem> — per visible collection item
)
```

### ProfileAppliedWriteContext

```
ProfileAppliedWriteContext(
    Request,                    // ProfileAppliedWriteRequest — the request-side contract
    VisibleStoredBody,          // JsonNode — current stored doc after writable-profile projection
    StoredScopeStates,          // ImmutableArray<StoredScopeState> — per non-collection scope, with HiddenMemberPaths
    VisibleStoredCollectionRows // ImmutableArray<VisibleStoredCollectionRow> — per visible stored row, with HiddenMemberPaths
)
```

### Supporting Types

- `ProfileVisibilityKind`: `VisiblePresent` | `VisibleAbsent` | `Hidden`
- `ScopeInstanceAddress(JsonScope, AncestorCollectionInstances)`: stable non-collection scope key
- `CollectionRowAddress(JsonScope, ParentAddress, SemanticIdentityInOrder)`: stable collection row key
- `AncestorCollectionInstance(JsonScope, SemanticIdentityInOrder)`: one ancestor in the collection chain
- `SemanticIdentityPart(RelativePath, Value, IsPresent)`: one identity member with canonical path
- `RequestScopeState(Address, Visibility, Creatable)`: request-side non-collection scope state
- `VisibleRequestCollectionItem(Address, Creatable)`: request-side visible collection item state
- `StoredScopeState(Address, Visibility, HiddenMemberPaths)`: stored-side non-collection scope state
- `VisibleStoredCollectionRow(Address, HiddenMemberPaths)`: stored-side visible collection row state

### Normative Requirements

All normative requirements are defined in `profiles.md` §"Minimum Core Write Contract". Key points:

- `RootResourceCreatable` applies only when the write would create a new document/root row.
- `RequestScopeState.Creatable` answers only the "create a new visible scope instance here" question.
- `VisibleRequestCollectionItems` must contain at most one item per `CollectionRowAddress`.
- Every `JsonScope` must equal the compiled scope identifier.
- `HiddenMemberPaths` are canonical scope-relative member paths.
- Backend must not infer hidden-vs-absent from projected JSON alone.

---

## No-Profile Passthrough Path

When no writable profile applies to a request, Core produces no `ProfileAppliedWriteRequest` and no `ProfileAppliedWriteContext`. Backend uses its existing non-profiled write path unchanged — the profile write state machine (creatability analysis, hidden-member preservation, binding-accounting) is bypassed entirely. Backend does NOT need to produce a degenerate "all visible" contract. The absence of a Core profile contract is the signal to use the standard non-profiled path.

Similarly, when no readable profile applies, the full reconstituted JSON is returned without readable-profile projection.

---

## Implementation Slices

### C1: Shared Compiled-Scope Adapter Contract + Address Derivation Engine

**Responsibility mapping:** #9 (stable scope and row address derivation)

**Inputs:**
- Compiled scope metadata from the selected mapping set (per profiles.md §"Shared Compiled-Scope Adapter")
- JSON data (request body or stored document)

**Outputs:**
- Adapter contract types: `JsonScope`, `ScopeKind`, `ImmediateParentJsonScope`, `CollectionAncestorsInOrder`, `SemanticIdentityRelativePathsInOrder`, `CanonicalScopeRelativeMemberPaths`
- Derived addresses: `ScopeInstanceAddress`, `CollectionRowAddress`, `AncestorCollectionInstance`, `SemanticIdentityPart`

**Test expectations:**
- Address derivation for root, 1:1, collection, nested collection, and `_ext` scopes against a test adapter
- Request-side and stored-side derivation produce identical addresses for the same scope/item

**Story file:** `reference/design/backend-redesign/epics/07-relational-write-path/01a-c1-compiled-scope-adapter-and-address-derivation.md`

### C2: Semantic Identity Compatibility Validation

**Responsibility mapping:** #12 (semantic identity compatibility validation)

**Inputs:**
- Compiled scope adapter from C1
- ProfileDefinition (writable profile)

**Outputs:**
- Pre-runtime gate: accept or reject writable profile definitions
- Structured error for profiles hiding compiled semantic-identity fields on persisted multi-item collections

**Test expectations:**
- Valid profiles pass validation
- Profiles hiding identity fields for persisted multi-item collection scopes fail with structured errors
- Profiles on single-item or non-persisted scopes are not incorrectly rejected

**Story file:** `reference/design/backend-redesign/epics/07-relational-write-path/01a-c2-semantic-identity-compatibility-validation.md`

### C3: Request-Side Visibility Classification + Writable Request Shaping

**Responsibility mapping:** #2 (readable/writable selection), #3 (recursive member filtering), #4 (collection item value filtering), #7 (writable request shaping), #10 (visibility signaling — request side), #14 (extension semantics — request side)

**Inputs:**
- Canonicalized request body
- Writable profile definition
- Compiled scope adapter from C1

**Outputs:**
- `WritableRequestBody` (filtered/canonicalized request JSON)
- `RequestScopeState` entries for all non-collection scopes with `VisiblePresent` / `VisibleAbsent` / `Hidden` visibility
- `VisibleRequestCollectionItem` entries (without `Creatable` flag) for each visible collection item, with `CollectionRowAddress` derived using C1's engine — C4 enriches these with creatability
- Per-scope visibility classification

**Test expectations:**
- Shaping for root, 1:1, collection, nested, and `_ext` scopes
- `IncludeOnly`, `ExcludeOnly`, and `IncludeAll` filter modes
- Correct visibility classification for present, absent, and hidden scopes
- `VisibleAbsent` scopes are correctly classified when a writable-profile-visible scope has no data in the request, enabling backend to clear stored data for that scope
- Extension scopes follow the same visibility rules as base data

**Story file:** `reference/design/backend-redesign/epics/07-relational-write-path/01a-c3-request-visibility-and-writable-shaping.md`

### C4: Request-Side Creatability Analysis + Duplicate Collection-Item Validation

**Responsibility mapping:** #5 (writable request validation — duplicates), #6 (creatability)

**Inputs:**
- Compiled scope adapter from C1
- Writable profile definition
- Visibility classification and `VisibleRequestCollectionItem` entries (without `Creatable`) from C3
- Semantic identity compatibility from C2
- Effective schema metadata (existing Core infrastructure) — for creation-required member determination
- Stored-side existence information: the orchestrating caller supplies a lightweight address-level lookup answering "does a visible stored scope/item exist at this address?" This uses C1's address derivation engine against the full stored document combined with C3's visibility rules. C4 does NOT require the full C6 stored-state projection.

**Outputs:**
- `RootResourceCreatable` decision
- `RequestScopeState.Creatable` for each non-collection scope
- `VisibleRequestCollectionItem` entries with `Creatable` flag
- Rejection of duplicate visible items by `CollectionRowAddress` before backend

**Test expectations:**
- Three-level chain creatability (existing root → middle collection → descendant extension child collection)
- Update-allowed/create-denied pairing: existing visible scope update remains allowed while new visible scope creation is rejected because required members are hidden
- Duplicate visible collection items by compiled semantic identity within the same stable parent are rejected
- Storage-managed values are not treated as creation-required members

**Story file:** `reference/design/backend-redesign/epics/07-relational-write-path/01a-c4-request-creatability-and-collection-validation.md`

### C5: Assemble ProfileAppliedWriteRequest

**Responsibility mapping:** #7 (final assembly of the request-side contract)

**Inputs:**
- `WritableRequestBody` from C3
- `RootResourceCreatable` from C4
- `RequestScopeStates` from C3 + C4
- `VisibleRequestCollectionItems` from C4

**Outputs:**
- `ProfileAppliedWriteRequest(WritableRequestBody, RootResourceCreatable, RequestScopeStates, VisibleRequestCollectionItems)`

**Test expectations:**
- Integration test: full assembly from profile definition + adapter + request JSON produces correct composite contract
- No-profile path returns no request (backend treats all scopes as visible)

**Story file:** `reference/design/backend-redesign/epics/07-relational-write-path/01a-c5-assemble-profile-applied-write-request.md`

### C6: Stored-State Projection + HiddenMemberPaths Computation

**Responsibility mapping:** #8 (stored-state projection), #10 (visibility — stored side), #11 (collection visibility details), #14 (extension semantics — stored side)

**Inputs:**
- Full current stored JSON (from backend write-side loader, before readable-profile filtering)
- Compiled scope adapter from C1
- Writable profile definition
- Visibility classification rules from C3

**Outputs:**
- `VisibleStoredBody` (stored JSON after writable-profile projection)
- `StoredScopeStates` with `VisiblePresent` / `VisibleAbsent` / `Hidden` and `HiddenMemberPaths`
- `VisibleStoredCollectionRows` with `HiddenMemberPaths`
- Assembled `ProfileAppliedWriteContext`

**Test expectations:**
- Visible, absent, and hidden non-collection scopes produce correct `StoredScopeState` entries
- `HiddenMemberPaths` emitted for hidden scalars, hidden references, hidden extension members
- Nested collection rows produce correct `VisibleStoredCollectionRow` entries with semantic identity
- `HiddenMemberPaths` use canonical vocabulary from `CanonicalScopeRelativeMemberPaths`
- Visible stored collection rows with no matching request item are included in `VisibleStoredCollectionRows`, enabling backend delete-vs-preserve decisions
- Extension scopes follow the same rules as base data

**Story file:** `reference/design/backend-redesign/epics/07-relational-write-path/01a-c6-stored-state-projection-and-hidden-member-paths.md`

### C7: Readable Profile Projection After Reconstitution

**Responsibility mapping:** #13 (read projection), #14 (extension semantics — reads)

**Inputs:**
- Full reconstituted JSON (from backend relational reconstitution, including references, descriptors, collections, `_ext`)
- Readable profile definition

**Outputs:**
- Profile-filtered JSON suitable for GET/query response

**Test expectations:**
- Readable projection removes hidden members, collections, and `_ext` data
- Present members are preserved; absent sections are omitted
- Backend does not reimplement readable profile filtering
- Extension data under readable profiles follows the same rules as base data

**Story file:** `reference/design/backend-redesign/epics/07-relational-write-path/01a-c7-readable-profile-projection.md`

### C8: Typed Profile Error Classification

**Responsibility mapping:** #15 (structured error classification)

**Inputs:**
- Profile validation results from C2, C3, C4
- Runtime execution context

**Outputs:**
- Typed failure categories:
  - Invalid profile definition (e.g., hides compiled semantic-identity fields)
  - Invalid profile usage (e.g., wrong profile mode for the operation)
  - Writable-profile validation failure (e.g., submitted forbidden member/value)
  - Creatability violation (e.g., new visible scope with hidden required members)
  - Core/backend contract mismatch (e.g., unknown `JsonScope`, ancestor-chain mismatch)
  - Binding-accounting failure (e.g., profiled binding cannot be classified)

**Test expectations:**
- Each category produces correct typed failure
- Failures short-circuit before DML
- Matched visible scope/item updates are not misclassified as creatability failures

**Story file:** `reference/design/backend-redesign/epics/07-relational-write-path/01a-c8-typed-profile-error-classification.md`

---

## Follow-On Story Inventory

| Story | Title | Tier | Dependencies | Unblocks |
| --- | --- | --- | --- | --- |
| C1 | Shared Compiled-Scope Adapter Contract + Address Derivation Engine | 0 | — | C2, C3, C6, C7 |
| C2 | Semantic Identity Compatibility Validation | 1 | C1 | C4 |
| C3 | Request-Side Visibility Classification + Writable Request Shaping | 1 | C1 | C4, C5, C6, C8 |
| C4 | Request-Side Creatability Analysis + Duplicate Collection-Item Validation | 2 | C1, C2, C3 | C5, C8 |
| C5 | Assemble ProfileAppliedWriteRequest | 2 | C3, C4 | C6, DMS-1103 (via C6) |
| C6 | Stored-State Projection + HiddenMemberPaths Computation | 3 | C1, C3, C5 | DMS-1103, DMS-1105 |
| C7 | Readable Profile Projection After Reconstitution | 3 | C1 | DMS-990 |
| C8 | Typed Profile Error Classification | 3 | C3, C4 | DMS-1104 |

### Per-Story Details

**C1** — `01a-c1-compiled-scope-adapter-and-address-derivation.md`
- Description: Define the shared compiled-scope adapter contract types and implement the normative address derivation algorithm from `profiles.md` §"Scope and Row Address Derivation".
- Acceptance criteria: Adapter surface matches profiles.md §"Shared Compiled-Scope Adapter" exactly; derivation produces correct addresses for root, 1:1, collection, nested collection, and `_ext` scopes.
- Jira: TBD

**C2** — `01a-c2-semantic-identity-compatibility-validation.md`
- Description: Implement the pre-runtime gate that rejects writable profiles hiding compiled semantic-identity fields for persisted multi-item collections.
- Acceptance criteria: Valid profiles pass; invalid profiles produce structured errors before runtime.
- Jira: TBD

**C3** — `01a-c3-request-visibility-and-writable-shaping.md`
- Description: Implement request-side visibility classification and writable request shaping, producing `WritableRequestBody` and `RequestScopeState` entries.
- Acceptance criteria: Correct shaping and visibility for all scope types and filter modes.
- Jira: TBD

**C4** — `01a-c4-request-creatability-and-collection-validation.md`
- Description: Implement top-down creatability analysis per profiles.md §"Creatability Decision Model" and duplicate visible collection-item validation.
- Acceptance criteria: Three-level chain creatability, update-allowed/create-denied pairing, duplicate rejection.
- Jira: TBD

**C5** — `01a-c5-assemble-profile-applied-write-request.md`
- Description: Thin integration story composing C3 + C4 outputs into `ProfileAppliedWriteRequest`.
- Acceptance criteria: Full assembly from profile + adapter + request JSON produces the correct composite contract.
- Jira: TBD

**C6** — `01a-c6-stored-state-projection-and-hidden-member-paths.md`
- Description: Implement the Core-owned stored-state projector callback and `HiddenMemberPaths` computation, assembling `ProfileAppliedWriteContext`.
- Acceptance criteria: Correct stored-side visibility, `HiddenMemberPaths` in canonical vocabulary, full context assembly.
- Jira: TBD

**C7** — `01a-c7-readable-profile-projection.md`
- Description: Implement readable profile projection applied after full relational reconstitution. Backend does not reimplement.
- Acceptance criteria: Correct readable projection for all scope types including `_ext`; backend invokes Core projector.
- Jira: TBD

**C8** — `01a-c8-typed-profile-error-classification.md`
- Description: Define the typed failure type hierarchy for all six error categories. Implement detection logic for categories 1–4 (invalid profile definition, invalid profile usage, writable-profile validation failure, creatability violation). Categories 5–6 (contract mismatch, binding-accounting failure) are type definitions only — backend implements detection.
- Acceptance criteria: Each category produces correct typed failure; matched visible updates are not misclassified.
- Jira: TBD

---

## Sequencing and Dependency Graph

### Dependency Graph

```
C1 ──┬──> C2 ──> C4 ──┬──> C5 ──> C6 ──> [DMS-1103, DMS-1105]
     │              ↑  │
     ├──> C3 ──────┘   └──> C8 ──> [DMS-1104]
     │
     └──> C7 ──> [DMS-990]

Additional edges not shown above (would create crossing lines):
  C1 ──> C6  (adapter for stored-side address derivation)
  C1 ──> C7  (adapter for readable scope identification)
  C3 ──> C6  (shared visibility classification rules)
  C3 ──> C8  (writable validation failures feed error classification)
```

### Tiers

- **Tier 0 (Foundation):** C1 — adapter contract and address derivation
- **Tier 1 (Validation + Shaping):** C2, C3 — semantic identity compatibility validation and request-side visibility/shaping (parallel after C1)
- **Tier 2 (Request Assembly):** C4, C5 — creatability analysis and request assembly (after C2+C3)
- **Tier 3 (Stored Side + Cross-Cutting):** C6, C7, C8 — stored-state projection, readable projection, and error classification (C7 and C8 can start earlier based on their minimal dependencies, but all are Tier 3 for scheduling simplicity)

### Minimum Unblock Path

The shortest path to unblock the four hard-blocked backend stories:

1. **C1** → Foundation adapter and address derivation
2. **C3** → Request-side visibility and shaping (depends on C1)
3. **C2** → Semantic identity compatibility validation (depends on C1, can parallel with C3)
4. **C4** → Creatability + duplicate validation (depends on C1, C2, C3)
5. **C5** → Assemble `ProfileAppliedWriteRequest` (depends on C3, C4)
6. **C6** → Stored-state projection + `ProfileAppliedWriteContext` (depends on C1, C3, C5) — **unblocks DMS-1103 and DMS-1105**

Then in parallel:
- **C8** → Error classification (depends on C3, C4) — **unblocks DMS-1104**
- **C7** → Readable projection (depends on C1 only) — **unblocks DMS-990**

### Parallelization Opportunities

- C2 and C3 can run in parallel after C1 completes.
- C7 depends only on C1 and can run in parallel with all write-side work (C2–C6).
- C8 depends on C3 and C4, so it can start once Tier 1 request-side work completes, in parallel with C6.

---

## Dependency Map Updates

### Changes Needed in DEPENDENCIES.md

New Core story entries must be added to the E07 section. The new stories use IDs `E07-S01a-C1` through `E07-S01a-C8`.

New dependency edges:

| Dependency Type | Blocker | Blocked | Rationale |
| --- | --- | --- | --- |
| Hard | `E07-S01a-C1` | `E07-S01a-C2` | C2 consumes adapter from C1 |
| Hard | `E07-S01a-C1` | `E07-S01a-C3` | C3 consumes adapter from C1 for address derivation |
| Hard | `E07-S01a-C1` | `E07-S01a-C6` | C6 consumes adapter from C1 for stored-side derivation |
| Hard | `E07-S01a-C1` | `E07-S01a-C7` | C7 consumes adapter from C1 for readable projection |
| Hard | `E07-S01a-C2` | `E07-S01a-C4` | C4 assumes semantic identity compatibility is validated |
| Hard | `E07-S01a-C3` | `E07-S01a-C4` | C4 consumes visibility classification from C3 |
| Hard | `E07-S01a-C3` | `E07-S01a-C5` | C5 consumes `WritableRequestBody` and `RequestScopeStates` from C3 |
| Hard | `E07-S01a-C3` | `E07-S01a-C6` | C6 uses the same visibility classification rules as C3 |
| Hard | `E07-S01a-C3` | `E07-S01a-C8` | C8 classifies errors from C3 validation |
| Hard | `E07-S01a-C4` | `E07-S01a-C5` | C5 consumes creatability and collection items from C4 |
| Hard | `E07-S01a-C4` | `E07-S01a-C8` | C8 classifies creatability violations from C4 |
| Hard | `E07-S01a-C5` | `E07-S01a-C6` | C6 includes the assembled request in the write context |
| Hard | `E07-S01a-C6` | `E07-S01b` | DMS-1103 consumes `ProfileAppliedWriteContext` from C6 |
| Hard | `E07-S01a-C6` | `E07-S01c` | DMS-1105 hands off to C6 for stored-state projection |
| Hard | `E07-S01a-C7` | `E08-S01` | DMS-990 invokes the readable projector from C7 |
| Hard | `E07-S01a-C8` | `E07-S05b` | DMS-1104 classifies errors defined by C8 |

### Blocked Story Updates

The blocked backend/read-path stories should update their dependency notes from the generic spike reference to specific Core stories:

| Story | Current dependency | Updated dependency |
| --- | --- | --- |
| `E07-S01b` (DMS-1103) | Blocked on `01a-core-profile-delivery-plan.md` | Blocked on C1 (adapter), C5 (request assembly), C6 (stored-state projection) |
| `E07-S01c` (DMS-1105) | Blocked on `01a-core-profile-delivery-plan.md` | Blocked on C1 (adapter), C6 (stored-state projection) |
| `E07-S05b` (DMS-1104) | Blocked on `01a-core-profile-delivery-plan.md` | Blocked on C8 (typed error classification) |
| `E08-S01` (DMS-990) | Blocked on `01a-core-profile-delivery-plan.md` | Blocked on C7 (readable profile projection) |

---

## Responsibility Coverage Verification

Every one of the 15 Core responsibilities from `profiles.md` §"Everything DMS Core Is Expected to Own" is covered:

| # | Responsibility | Covered by |
| --- | --- | --- |
| 1 | Profile metadata loading and validation | C2 (compatibility validation), existing Core infrastructure |
| 2 | Readable vs writable profile interpretation | C3 (request-side selection) |
| 3 | Recursive member filtering | C3 (request shaping) |
| 4 | Recursive collection item value filtering | C3 (request shaping), C4 (duplicate validation) |
| 5 | Writable request validation | C4 (duplicate collection-item validation) |
| 6 | Creatability analysis | C4 (full top-down creatability) |
| 7 | Writable request shaping | C3 (produce `WritableRequestBody`), C5 (assemble full request) |
| 8 | Stored-state projection for writes | C6 (stored-state projection + context assembly) |
| 9 | Stable scope and row address derivation | C1 (address derivation engine) |
| 10 | Visibility signaling for all scopes | C3 (request side), C6 (stored side) |
| 11 | Collection visibility details | C6 (`VisibleStoredCollectionRows`) |
| 12 | Semantic identity compatibility validation | C2 (pre-runtime gate) |
| 13 | Read projection | C7 (readable profile projection) |
| 14 | Extension profile semantics | C3 (request `_ext`), C6 (stored `_ext`), C7 (read `_ext`) |
| 15 | Structured error classification | C8 (typed error categories) |
