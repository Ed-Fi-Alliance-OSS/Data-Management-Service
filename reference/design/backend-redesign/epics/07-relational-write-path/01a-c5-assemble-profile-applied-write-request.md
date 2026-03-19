---
jira: TBD
---

# Story: Orchestrate Profile Write Pipeline + Assemble ProfileAppliedWriteRequest

## Description

Orchestrate the end-to-end Core profile write pipeline and assemble the `ProfileAppliedWriteRequest` and `ProfileAppliedWriteContext` contracts. C5 owns the call sequence that threads intermediate results between the individual profile processing steps (C2, C3, C4, C6), the construction of the stored-side existence lookup required by C4, and the no-profile short-circuit.

Align with:

- `reference/design/backend-redesign/design-docs/profiles.md` §"Minimum Core Write Contract"

Delivery plan: `reference/design/backend-redesign/design-docs/core-profile-delivery-plan.md`

Depends on:
- C1 (`01a-c1-compiled-scope-adapter-and-address-derivation.md`) — adapter for stored-side existence lookup construction (step 4)
- C2 (`01a-c2-semantic-identity-compatibility-validation.md`) — C5 directly invokes C2 as an orchestration step (step 2)
- C3 (`01a-c3-request-visibility-and-writable-shaping.md`) — `WritableRequestBody`, `RequestScopeStates` (without creatability)
- C4 (`01a-c4-request-creatability-and-collection-validation.md`) — `RootResourceCreatable`, creatability flags, `VisibleRequestCollectionItems`

**Core responsibility coverage:** #7 (writable request shaping — final assembly), pipeline orchestration

This story produces the `ProfileAppliedWriteRequest` that backend consumes in `E07-S01b` (DMS-1103). It also invokes C6 for update/upsert flows to produce `ProfileAppliedWriteContext`.

### Orchestration Sequence

The individual C stories (C2, C3, C4, C6) are pure processing steps. C5 is the orchestrator that calls them in order and manages data flow:

1. **No-profile short-circuit:** If no writable profile applies to the request, produce no `ProfileAppliedWriteRequest` and no `ProfileAppliedWriteContext`. Return immediately. Backend uses its non-profiled write path.
2. **C2 — Semantic identity compatibility validation:** Validate the writable profile + adapter. If the profile hides semantic-identity fields for a persisted multi-item collection, fail with a C8 typed error.
3. **C3 — Request-side visibility + shaping:** Produce `WritableRequestBody`, `RequestScopeStates` (without `Creatable`), and `VisibleRequestCollectionItem` entries (without `Creatable`).
4. **Build stored-side existence lookup:** For update/upsert-to-existing flows, run C1's address derivation engine against the full stored document and apply C3's visibility classification rules. The result is a lookup (predicate or set keyed by address) answering "does a visible stored scope/item exist at this address?" For a create (POST with no existing document), the lookup reports nothing exists. This lookup is C5's responsibility to construct — it is the "orchestrating caller" referenced by C4.
5. **C4 — Creatability + duplicate validation:** Pass C3's outputs plus the existence lookup. Produces `RootResourceCreatable`, enriched `RequestScopeStates` with `Creatable` flags, and enriched `VisibleRequestCollectionItems` with `Creatable` flags.
6. **Assemble `ProfileAppliedWriteRequest`** from the C3 + C4 outputs.
7. **C6 — Stored-state projection (update/upsert only):** For update/upsert flows, invoke C6 with the full stored document + adapter + writable profile + the assembled `ProfileAppliedWriteRequest`. C6 produces stored-side outputs and assembles the complete `ProfileAppliedWriteContext` (C6 owns the context assembly; C5 owns calling C6 at the right point in the pipeline).

## Acceptance Criteria

### Assembly

- `ProfileAppliedWriteRequest` is assembled with:
  - `WritableRequestBody` from C3,
  - `RootResourceCreatable` from C4,
  - `RequestScopeStates` from C3 with `Creatable` flags populated by C4, and
  - `VisibleRequestCollectionItems` from C4.
- The assembled contract is semantically equivalent to the shape defined in `profiles.md` §"Minimum Core Write Contract".

### Orchestration

- C5 owns the call sequence: no-profile check → C2 → C3 → existence lookup → C4 → assembly → (C6 for updates).
- The stored-side existence lookup is constructed by C5 from C1's address derivation engine + the full stored document + C3's visibility rules. C4 receives this as an input parameter.
- For update/upsert flows, C5 invokes C6, passing the assembled `ProfileAppliedWriteRequest`. C6 returns the complete `ProfileAppliedWriteContext`.
- For create flows (no existing document), C5 supplies an empty existence lookup to C4 and does not invoke C6.

### No-Profile Passthrough

- When no writable profile applies, no `ProfileAppliedWriteRequest` is produced (backend treats all scopes as visible). See "No-Profile Passthrough Path" in the delivery plan: the absence of a profile contract bypasses the entire profile write state machine — creatability analysis, hidden-member preservation, and binding-accounting are all skipped. Backend does NOT produce a degenerate "all visible" contract.
- The no-profile path is tested: absence of a writable profile produces no request contract.

### Testing

- Integration test: given a writable profile definition + compiled-scope adapter + request JSON, the full pipeline (C2 → C3 → C4 → C5 assembly) produces the correct composite `ProfileAppliedWriteRequest`.
- Update flow integration test: full pipeline including C6 invocation produces correct `ProfileAppliedWriteContext`.
- Stored-side existence lookup correctly reports visible stored scopes/items for C4 creatability decisions.
- The no-profile path produces no contracts.

## Tasks

1. Implement the orchestration entry point that accepts a canonicalized request body, an optional writable profile definition, a compiled scope adapter, and (for update/upsert flows) the full current stored JSON.
2. Implement the no-profile short-circuit that returns no contracts when no writable profile applies.
3. Wire the call sequence: C2 validation → C3 shaping → existence lookup construction → C4 creatability → assembly.
4. Implement the stored-side existence lookup: run C1's address derivation against the stored document, classify each scope/item using C3's visibility rules, and produce a lookup keyed by address that C4 consumes.
5. Assemble `ProfileAppliedWriteRequest` from C3 + C4 outputs.
6. For update/upsert flows, invoke C6 with the stored document + adapter + writable profile + the assembled `ProfileAppliedWriteRequest`. C6 returns the complete `ProfileAppliedWriteContext`.
7. Add integration tests proving the full pipeline, the update flow with C6, the existence lookup, and the no-profile path.
