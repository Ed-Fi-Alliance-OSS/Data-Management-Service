# Story: Derive `_etag`, `_lastModifiedDate`, and `ChangeVersion` on Reads

## Description

Implement read-time derivation per `reference/design/backend-redesign/update-tracking.md`:

- `_etag` is derived from local tokens + dependency identity tokens.
- `_lastModifiedDate` is derived from the max of local timestamps and dependency identity timestamps.
- `ChangeVersion` is derived as the max of local change and dependency identity versions.

Dependencies are non-descriptor document references and should be obtained from:
- `dms.ReferenceEdge` (recommended), and/or
- the reconstitution process as it projects references.

## Acceptance Criteria

- `_etag` changes when:
  - the document’s content changes,
  - the document’s identity projection changes,
  - or a referenced document’s identity projection changes.
- `_lastModifiedDate` changes when any of the above changes occur.
- `ChangeVersion` follows the formula in `update-tracking.md`.
- Derivation loads dependency tokens in bulk (no per-dependency queries).

## Tasks

1. Implement dependency enumeration using `dms.ReferenceEdge` (parent → children).
2. Implement batched dependency token reads from `dms.Document`.
3. Implement `_etag` hashing/encoding and timestamp derivation exactly per the design.
4. Add integration tests covering indirect representation changes (dependency identity change affects parent metadata).

