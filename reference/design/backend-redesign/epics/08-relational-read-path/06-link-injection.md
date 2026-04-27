---
jira: DMS-622
jira_url: https://edfi.atlassian.net/browse/DMS-622
---

# Story: Design Link Injection

## Description

Design spike to produce the link-injection contract for DMS GET responses against the relational
backend. Deliverable is the approved design document at
`reference/design/backend-redesign/design-docs/link-injection.md`. Implementation is tracked
separately in `06a-link-injection-implementation.md`.

The contract is scoped to document references backed by `..._DocumentId`. Descriptor references
remain on their existing canonical-URI string surface.

Align with:

- ODS reference implementation (parity target): `Application/EdFi.Ods.Standard/Standard/5.2.0/Resources/Resources.generated.cs`
  at v7.3.
- `reference/design/backend-redesign/design-docs/flattening-reconstitution.md` — reconstitution
  engine and `DocumentReferenceBinding`
- `reference/design/backend-redesign/design-docs/data-model.md` — `dms.Document`,
  `dms.ResourceKey`, abstract identity tables
- `reference/design/backend-redesign/design-docs/extensions.md` — read-time `_ext` overlay and
  collection-aligned extension scope shape
- `reference/design/backend-redesign/design-docs/profiles.md` — readable-profile projection
  ownership boundary and profile namespace
- `reference/design/backend-redesign/design-docs/compiled-mapping-set.md` §4.3 step 6 —
  descriptor URI auxiliary-result-set pattern this design reuses
- `reference/design/backend-redesign/design-docs/update-tracking.md` — `_etag` derivation from the
  served body
- `reference/design/backend-redesign/design-docs/auth.md` — authorization strategy families

Blocks:

- `06a-link-injection-implementation.md` (DMS-1145) — runtime implementation of the contract
  defined by this spike.

## Acceptance Criteria

- The design document at `reference/design/backend-redesign/design-docs/link-injection.md` exists,
  is reviewed, and is approved.
- The design document covers each of these topics:
  - link shape and emission gate,
  - `rel` and `href` resolution (including GUID format and abstract-reference handling),
  - auxiliary `dms.Document` lookup (including join-column derivation, boundary condition, and
    zero-bindings case),
  - compiled read-plan extensions,
  - JSON reconstitution integration,
  - feature flag (including ODS divergence),
  - cache and `_etag` behavior,
  - authorization (including profile namespace interaction),
  - collection-response behavior,
  - out-of-scope and deferred follow-on work, and
  - testing strategy for the implementation story.
- Cross-referenced design documents touched by the contract remain consistent with the approved
  design.

## Tasks

1. Author `reference/design/backend-redesign/design-docs/link-injection.md`.
2. Update cross-referenced design documents as needed so they remain consistent with the approved
   contract.
3. Review the design with stakeholders; incorporate feedback; iterate to approval.
4. On approval, open a Jira ticket for `06a-link-injection-implementation.md` and populate its
   `jira:` / `jira_url:` frontmatter.
