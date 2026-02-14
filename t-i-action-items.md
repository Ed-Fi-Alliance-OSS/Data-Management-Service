# Trigger + Index Action Items (prep for Key Unification)

This is a recommended action list to address the gaps in `trigger-index-findings.md` and make the index/trigger
inventory + DDL emission compatible with `reference/design/backend-redesign/design-docs/key-unification.md`.

## A) Locked decisions (chosen approach)

1. **SQL Server propagation strategy**
   - Use **“always `ON UPDATE NO ACTION` on MSSQL + always trigger-based propagation fallback”** for v1
     correctness/determinism (align docs to match).

2. **Query predicate strategy for unified aliases**
   - Implement **predicate rewrite** (binding alias → canonical + presence gate) so existing indexes on canonical
     storage columns remain usable.
   - Do **not** extend the index inventory model for filtered/partial indexes as part of this workstream.

3. **Propagation-trigger granularity**
   - Use **one propagation trigger per referenced table** (fan-out to all referrers) to reduce trigger count.
   - Update the trigger inventory contract so it can represent “one trigger updates many tables” without overloading
     unrelated fields.

## B) Fix the trigger inventory contract (must happen before key unification lands)

4. **Make “where the trigger is created” explicit**
   - Replace the ambiguous `DbTriggerInfo.Table`/`TargetTable` semantics with explicit fields, e.g.:
     - `TriggerTable` (table the trigger is created on / fires on)
     - `AffectedTable` (table updated by the trigger), and
     - for propagation: explicit “join columns” (referenced key vs referencing FK) + explicit “columns to update”.
   - For propagation triggers specifically, avoid overloading `KeyColumns`/`IdentityProjectionColumns` for unrelated
     meanings; add dedicated propagation fields such as:
     - `ReferencingFkColumn` (the referrer `..._DocumentId` column),
     - `ReferencedDocumentIdColumn` (typically `DocumentId` on the target),
     - `IdentityColumnPairs` (ordered `(referrer_storage_column, referenced_storage_column)` pairs to copy old→new).
   - Update `DerivedRelationalModelSetContracts.cs` docs so `DbTriggerInfo` is unambiguous for
     `DbTriggerKind.IdentityPropagationFallback`.

5. **Treat `IdentityProjectionColumns` as a value-diff compare set (not “UPDATE OF columns”)**
   - Update the story/doc wording + contracts so consumers implement identity-change detection as:
     - `IS DISTINCT FROM`/null-safe value compare of `deleted` vs `inserted` values (or equivalent),
     - not `UPDATE(column)` / `UPDATE OF (...)` gating (which breaks under computed unified aliases).

6. **Ensure identifier-shortening updates all new trigger fields**
   - If `DbTriggerInfo` gains new `DbTableName`/`DbColumnName` fields, extend
     `ApplyDialectIdentifierShorteningPass` to shorten them deterministically.

## C) Re-derive propagation fallback triggers correctly (direction + storage targeting)

7. **Fix propagation trigger direction**
   - Propagation fallback triggers must fire on the **referenced (target) table** and update **referrer rows**.
   - Rewrite `DeriveTriggerInventoryPass` propagation derivation accordingly (and update unit tests that currently
     lock in the inversion). Recommendation: derive propagation edges from the FK inventory (post key-unification)
     rather than from `DocumentReferenceMapping` alone, so non-root/reference-site variations are naturally covered.

8. **Update canonical storage columns (never binding/alias columns)**
   - Under key unification, the per-site identity-part columns become persisted computed aliases and are read-only.
   - Propagation fallback SQL must update the referrer’s **canonical storage columns** for each propagated identity
     part, after mapping binding columns → storage columns and de-duplicating duplicates.
   - Ensure the propagation intent captures the storage-column mapping explicitly (or is derived after mapping),
     because alias columns may not be writable or even legal FK targets.

9. **Make propagation fallback set-based + idempotent under convergence**
   - Ensure the propagation trigger plan (and eventual SQL) uses `inserted`/`deleted` to map old→new values and
     updates only rows that still match the old composite key, so multi-path convergence does not thrash rows.

10. **Decide and implement coverage beyond root tables**
   - Key-unification and the overall redesign allow references in child/collection tables and `_ext` tables.
   - If MSSQL uses triggers for propagation, inventory derivation must cover reference bindings on **all tables**
     (root + child + extension), not root-only.

## D) Fix identity projection column derivation (stamping + referential identity maintenance)

11. **Define identity projection columns independently from the root UNIQUE constraint**
   - Current derivation reuses the natural-key UNIQUE binding logic, which intentionally substitutes reference
     identity paths with `..._DocumentId` (stable key). That is not sufficient for:
     - DB-side referential-id recomputation (UUIDv5 over identity values), or
     - correct `IdentityVersion` bumping when cascades / propagation triggers update propagated identity values.

12. **Include propagated identity values for identity-component references**
   - For every `DocumentReferenceBinding` with `IsIdentityComponent == true`, include its locally stored identity-part
     values (`IdentityBindings[*].Column`) in the identity projection compare set.
   - Under key unification, treat those as binding columns whose values may be computed aliases; use value-diff
     semantics rather than “updated column” semantics.

13. **Validate identity projection coverage with new fixtures**
   - Add/adjust unit fixtures so at least one resource has:
     - an identity-component reference with propagated identity parts, and
     - a key-unification class that turns those propagated parts into aliases,
     and assert the derived trigger inventory still identifies identity projection changes correctly.

## E) Index inventory adjustments for key unification

14. **Guarantee FK-support index derivation uses final FK storage columns**
   - Ensure FK constraints in the derived model reference **storage columns only** (after key unification mapping and
     canonical de-duplication), so `DeriveIndexInventoryPass` naturally derives correct FK-support indexes.
   - Add a safety validation: no FK constraint may reference a `UnifiedAlias` binding column.

15. **If filtered/partial indexes are needed, extend the contract (otherwise don’t)**
   - Only if predicate rewrite is not implemented and performance requires it:
     - extend `DbIndexInfo` with a dialect-neutral “filter predicate” model (e.g., `PresenceColumn IS NOT NULL`), and
     - emit `WHERE` (Pgsql) / `WHERE` (MSSQL filtered index) accordingly.

## F) Docs + tests cleanup (to prevent drift)

16. **Update design docs to match the chosen MSSQL strategy**
   - Align `key-unification.md`, `transactions-and-concurrency.md`, and
     `epics/01-relational-model/07-index-and-trigger-inventory.md` with the actual propagation approach.

17. **Update key-unification docs for pass naming**
   - Replace references to `IndexAndTriggerInventoryPass` with `DeriveIndexInventoryPass` +
     `DeriveTriggerInventoryPass` and confirm ordering relative to `KeyUnificationPass`.

18. **Rewrite the unit tests that currently “lock in” incorrect propagation semantics**
   - Update `DeriveTriggerInventoryPassTests` to assert:
     - propagation triggers are created **on the referenced table**, and
     - they update/refers-to the correct referrer tables + canonical storage columns.
