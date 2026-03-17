# Story: Classify and Map Profile Write Failures to DMS Error Shapes

## Description

Provide consistent, non-DB error handling for profile-constrained writes across Core, backend, and the API layer.

This companion story covers the profile-specific failures required by:

- `reference/design/backend-redesign/design-docs/profiles.md`
- `reference/design/backend-redesign/design-docs/overview.md`
- `reference/design/backend-redesign/design-docs/transactions-and-concurrency.md`

It is distinct from `DMS-986`, which remains limited to database exception classification and mapping.

Profile-aware writes must surface deterministic failure shapes for:

- invalid profile definitions rejected before runtime,
- invalid profile usage,
- writable-profile validation failures when submitted data is forbidden by the profile, and
- creatability violations for a new resource instance, new non-collection scope, or new collection/extension item.

## Acceptance Criteria

- Invalid writable profile definitions that exclude compiled semantic-identity members required for persisted collection merge do not reach backend merge execution; the runtime surfaces a deterministic configuration/startup failure instead of a merge-time error.
- Invalid profile usage maps to a deterministic client-visible error without entering persistence DML.
- Writable-profile validation failures map to consistent validation/policy errors without partial writes.
- Creatability violations for:
  - a new resource instance,
  - a new non-collection scope, and
  - a new collection/common-type/extension item or scope
  map to consistent policy/validation errors without partial writes.
- Unit or integration tests cover representative cases for invalid usage, forbidden submitted data, and non-creatable create/scope/item failures.

## Tasks

1. Define or align the typed failure contract emitted across Core, backend, and the API layer for invalid profile definitions, invalid profile usage, writable-profile validation failures, and creatability violations.
2. Ensure the write pipeline short-circuits before DML on profile classification/validation/creatability failures and maps them to deterministic DMS/API responses.
3. Add tests covering:
   - root-resource create denied by writable profile,
   - non-creatable visible scope/item,
   - submitted forbidden member/value under a writable profile, and
   - invalid profile definition/usage behavior.
