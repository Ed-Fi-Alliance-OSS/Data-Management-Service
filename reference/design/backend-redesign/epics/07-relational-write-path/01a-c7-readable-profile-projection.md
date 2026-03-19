---
jira: TBD
---

# Story: Readable Profile Projection After Reconstitution

## Description

Implement the Core-owned readable profile projection applied after full relational reconstitution. Backend does not reimplement readable profile filtering; it passes the full reconstituted document to this Core projector.

Align with:

- `reference/design/backend-redesign/design-docs/profiles.md` §"Everything DMS Core Is Expected to Own" responsibilities #13, #14
- `reference/design/backend-redesign/design-docs/profiles.md` §"Read Path Under Profiles"

Delivery plan: `reference/design/backend-redesign/design-docs/core-profile-delivery-plan.md`

Depends on: C1 (`01a-c1-compiled-scope-adapter-and-address-derivation.md`) — adapter for scope identification.

**Core responsibility coverage:**
- #13 (read projection)
- #14 (extension profile semantics — reads)

This story unblocks `DMS-990` (`reference/design/backend-redesign/epics/08-relational-read-path/01-json-reconstitution.md`), which invokes this projector after full JSON reconstitution. It depends only on C1 and can run in parallel with all write-side work (C2–C6, C8).

## Acceptance Criteria

- The readable projector accepts:
  - full reconstituted JSON (including references, descriptors, collections, nested collections, and `_ext`), and
  - a readable profile definition.
- The projector produces profile-filtered JSON suitable for GET/query responses.
- Hidden members, hidden collections, hidden `_ext` data, and hidden nested scopes are removed from the output.
- Present members are preserved; absent sections are omitted rather than emitted as empty/null.
- Backend does not reimplement readable profile filtering; readable profiles are applied only by this Core projector after full reconstitution.
- Extension data under readable profiles follows the same filtering rules as base resource data.
- The projector does not alter the input document; it produces a new filtered document.
- Unit tests cover:
  - readable projection removes hidden scalar members,
  - readable projection removes hidden collection scopes,
  - readable projection removes hidden `_ext` data,
  - present members are preserved intact,
  - absent sections produce no output (not empty objects/arrays),
  - extension data follows base-data filtering rules, and
  - full round-trip: reconstituted JSON with all data → projector → correctly filtered output.

## Tasks

1. Implement the readable profile projector that walks the reconstituted JSON and applies readable-profile member filtering recursively across root, embedded objects, collections, common types, and extensions.
2. Ensure hidden data is removed and absent sections produce no output.
3. Expose the projector as a callable entry point that DMS-990 invokes after reconstitution. The projector API must accept full reconstituted JSON and a readable profile definition, returning the filtered result. Wiring the invocation into the backend read path is owned by DMS-990, not this story.
4. Add tests validating readable projection for representative fixtures covering scalar hiding, collection hiding, `_ext` hiding, and round-trip correctness.
