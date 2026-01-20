# `dms.ReferentialIdentity` Test Plan (Backend-Redesign Alternatives)

This test plan targets the correctness risk described in `reference/design/backend-redesign/strengths-risks.md` (“ReferentialIdentity Incorrect Mapping”): a valid `ReferentialId` resolving to the wrong `DocumentId`, or a `DocumentId` having an incorrect/stale `ReferentialId`.

The alternatives in `reference/design/backend-redesign/alternatives/` generally assume `dms.ReferentialIdentity` is maintained transactionally (e.g., row-local triggers + cascades). These tests aim to ensure that maintenance is correct, fails closed on “impossible” states, and remains correct under batching and concurrency.

## Execution matrix (minimum)

- Run on both PostgreSQL and SQL Server.
- Run both single-row and multi-row statements (multi-row coverage is especially important on SQL Server triggers).
- Include at least one concurrency fixture (2+ concurrent writers).
- For tests that require deliberate corruption, run them in an isolated database and assert “fail closed” behavior (no silent writes to the wrong document).

---

## Wrong on the `DocumentId` side

These tests detect cases where a (correct/valid) `ReferentialId` resolves to the wrong *existing* `DocumentId` (the highest risk case because it can be silently “valid but wrong”).

- **Swap test (two docs)**: create two documents of the same resource type with distinct identities; compute both `ReferentialId`s; assert each resolves to its own `DocumentId` (not swapped).
- **Row-shape invariant**: per `DocumentId`, assert `dms.ReferentialIdentity` has exactly 1 row (non-subclass) or 2 rows (subclass), and the `ResourceKeyId`s are exactly the expected concrete + optional superclass key.
- **Cross-resource disambiguation**: create two documents of *different* resource types whose identity values could otherwise “look the same”; assert their `ReferentialId`s differ and never resolve across types (guards `ResourceInfoString`/`ResourceKeyId` mistakes).
- **Cross-project/instance disambiguation** (if applicable in a shared DB): create the “same identity” in two projects/instances; assert no cross-project resolution (guards a missing/incorrect project component in the referential-id computation).
- **Descriptor type disambiguation**: create two different descriptor types with the same URI string; assert their computed `ReferentialId`s differ and resolve to the correct descriptor documents (guards discriminator/type omissions).
- **Multi-row DML test (SQL Server trigger semantics)**: insert/update identities for N documents in one statement; assert every `ReferentialId → DocumentId` mapping is correct (guards mis-joins against `inserted`/`deleted`).
- **Cascade fan-out test**: create a “hub” referenced by many parents; change hub identity to trigger cascades; assert no parent’s identity maintenance writes a `ReferentialId` row that points at another parent’s (or the hub’s) `DocumentId`.
- **Polymorphic/alias mapping test**: for a subtype with alias behavior, assert all alias rows map to the same `DocumentId` as the primary row, and never to another document.
- **Abstract/polymorphic reference safety test**: resolve an abstract reference and then attempt a write that requires a specific subtype; assert the system rejects an “existing but wrong-type `DocumentId`” (membership/compatibility validation for polymorphic targets).
- **Collision-as-incident / fail-closed test** (deliberate corruption): manually corrupt `dms.ReferentialIdentity` so a known `ReferentialId` points to a different existing `DocumentId`; then POST/PUT by that natural key; assert the transaction fails loudly and does not modify the wrong document.
- **Subject upsert compatibility guard** (deliberate corruption): corrupt resolution so a request for resource type `R` resolves to a `DocumentId` whose `dms.Document.ResourceKeyId` (and/or alias rules) is incompatible with `R`; assert the write fails closed.
- **Bigint truncation/overflow test**: ensure `DocumentId` values exceed 2^31 and run identity maintenance paths; assert mappings still point at the correct high `DocumentId`s (guards `int`/`smallint` temp structures/parameters).
- **Concurrent writers cross-wire test**: run 2+ concurrent upserts/identity updates (including some that trigger cascades); assert no `ReferentialId` ever resolves to a different document than the one whose identity produced it.

---

## Wrong on the `ReferentialId` side

These tests detect cases where the `DocumentId` is correct but the `ReferentialId` is incorrect/stale (which can later cause “not found” behavior, uniqueness conflicts, or unexpected reads).

- **Parity test (Core vs DB compute)**: for representative resources (self-contained + reference-bearing), compute expected `ReferentialId` via Core logic from stored identity values; assert it equals the row(s) in `dms.ReferentialIdentity`.
- **Cross-engine equivalence test**: run the same fixture on PostgreSQL and SQL Server; assert the resulting `ReferentialId`s are byte-for-byte identical (validates UUIDv5 implementation + concatenation rules).
- **Formatting/canonicalization edge cases**: identities containing dates/times, decimals, leading zeros, whitespace, and collation-sensitive strings; assert DB-computed UUIDv5 matches Core exactly.
- **Unicode/encoding test**: non-ASCII identity values produce the same UUIDv5 between Core and the database (guards UTF-8/UTF-16 and normalization differences).
- **SQL Server collation edge cases**: case-only and trailing-space-only changes to string identity parts trigger the expected recompute and match Core-computed UUIDv5 (guards “comparison says no change but stored value changed”).
- **Per-identity-part coverage**: for a resource, update each identity column independently and assert the `ReferentialId` changes; update only non-identity columns and assert it does not (guards missing columns in change detection and missing trigger firing).
- **Identity update test**: update identity values for an existing document; assert:
  - the new `ReferentialId` resolves to the document,
  - the old `ReferentialId` no longer resolves,
  - alias row(s) (if any) update consistently.
- **No-op update test**: update only non-identity fields; assert `dms.ReferentialIdentity` does not change.
- **Cascaded-update trigger firing**: change an upstream identity that propagates into dependent identity columns (e.g., via `ON UPDATE CASCADE`); assert dependents’ `ReferentialId`s recompute (guards “cascade updated columns didn’t trigger recompute”).
- **Cascade recompute convergence test**: in one transaction, perform an upstream identity change that forces dependent recomputes; assert all impacted documents have the correct post-commit `ReferentialId` (no stale window).
- **Delete cleanup test**: delete a document; assert its `dms.ReferentialIdentity` rows are removed (trigger/`ON DELETE CASCADE` correctness).
- **Uniqueness conflict test**: attempt an identity change that would collide with another document’s identity; assert the transaction fails and `dms.ReferentialIdentity` is unchanged (no partial state).
- **Rebuild/backfill idempotence test** (if repair tooling exists): run the recompute/repair routine twice; assert the second run produces zero changes and the final state matches Core-computed ids.
- **Generator determinism test**: snapshot/contract-test the generated identity concatenation order (identity json paths ordering) and the UUIDv5 helper so regeneration cannot silently change referential-id computation.

