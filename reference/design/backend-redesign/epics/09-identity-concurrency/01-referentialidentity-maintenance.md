---
jira: DMS-997
jira_url: https://edfi.atlassian.net/browse/DMS-997
---

# Story: Maintain `dms.ReferentialIdentity` (Primary + Superclass Alias Rows)

> **Historical delivery boundary.** DMS-997 is complete and owns row-local, value-diff-driven referential-identity
> maintenance. DMS-1274 owns post-key-unification effective dependency promotion and ensures storage-promoted canonical
> identity changes reach this existing maintenance contract. The current wording below is an integration invariant, not
> new scope attributed to DMS-997.

## Description

Maintain `dms.ReferentialIdentity` as the canonical identity index:

- Insert the primary `ReferentialId` for each document.
- For subclass resources, also insert the superclass/abstract alias `ReferentialId` row (preserving polymorphic reference behavior).
- Ensure updates replace old identity rows transactionally and uniqueness is enforced.

Align with `reference/design/backend-redesign/design-docs/data-model.md` (`dms.ReferentialIdentity` invariants).

## Acceptance Criteria

- After a successful insert, `dms.ReferentialIdentity` contains:
  - one primary row for the document’s concrete `ResourceKeyId`,
  - and (when applicable) one alias row for the superclass/abstract `ResourceKeyId`.
- On identity change, old referential identity rows are removed and new ones inserted within the same transaction.
- Uniqueness violations surface as deterministic conflicts (no silent overwrites).

## Tasks

1. Implement referential identity maintenance (insert/replace) used by normal writes and every actual identity-projection
   value change, including cascaded canonical updates through authored or storage-promoted effective dependencies
   (trigger-driven), using the engine UUIDv5 helper (`E02-S06`) for any DB-side `ReferentialId` recomputation.
2. Implement alias-row logic for subclass resources based on ApiSchema subclass metadata.
3. Add unit/integration tests covering:
   1. insert primary + alias rows,
   2. replace on identity change,
   3. conflict on duplicate identity.
