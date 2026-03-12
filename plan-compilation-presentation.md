# Plan Compilation Epic

## Plan Compilation

- The relational model is the database-shaped version of an Ed-Fi resource schema.
- It defines the tables, columns, keys, and relationships needed to store and read that resource.
- Plan compilation uses it to generate deterministic read and write plans.

---

## Plan Compilation

- Removes runtime guesswork and SQL parsing
- Compile executor-ready plans once (caching)
- Reuse the same plan shape across runtime and future AOT work

---

## What The Epic Delivered

- Canonical SQL generation
- Stable read, write, and projection plan contracts
- Runtime mapping set compilation and cache

---

## The Core Output

- Compile target: `MappingSet`
- Cache key: schema hash + dialect + mapping version
- Result: immutable per-resource read and write plans

---

## 4. Example: Actual Query Plan

- Shows filter, unified alias rewrite, and paging parameters

```sql
SELECT r."DocumentId"
FROM "edfi"."StudentSchoolAssociation" r
WHERE
    (r."SchoolYear" >= @schoolYear)
    AND (r."Student_DocumentId" IS NOT NULL AND r."StudentUniqueId_Unified" = @studentUniqueId)
ORDER BY r."DocumentId" ASC
LIMIT @limit OFFSET @offset
;
```

---

## 5. Write Plan Compilation

- `InsertSql` for every table
- `UpdateSql` for applicable 1:1 tables
- `DeleteByParentSql` for replace semantics
- Example root bindings: `SchoolId -> @schoolId`, `SchoolYear -> @schoolYear`, `StudentUniqueId -> @studentUniqueId`

---

## 6. Example: Child Table Write Plan

- Shows parent key, ordinal, and scalar binding

```json
{
  "table": { "schema": "sample", "name": "SchoolExtensionAddress" },
  "delete_by_parent_sql_sha256": "8c202607...",
  "column_bindings_in_order": [
    { "column_name": "School_DocumentId", "write_value_source": { "kind": "parent_key_part", "index": 0 } },
    { "column_name": "Ordinal", "write_value_source": { "kind": "ordinal" } },
    { "column_name": "Zone", "write_value_source": { "kind": "scalar", "relative_path": "$.zone" } }
  ]
}
```

---

## 7. Example: Projection Read Plan

- Golden source: `projection/mappingset.manifest.json`
- Shows reference identity and descriptor projection metadata
- Good example of ordinal-driven reconstitution

```json
{
  "reference_object_path": "$.sessionTermReference",
  "fk_column_ordinal": 1,
  "identity_field_ordinals_in_order": [
    { "reference_json_path": "$.sessionTermReference.schoolId", "column_ordinal": 2 },
    { "reference_json_path": "$.sessionTermReference.schoolYear", "column_ordinal": 3 }
  ],
  "result_shape": { "descriptor_id_ordinal": 0, "uri_ordinal": 1 }
}
```

---

## 8. Runtime Integration

- Compile active mapping set from effective schema
- Cache once per process
- Log `Compiled`, `Joined in-flight`, or `Reused completed`

---

## 9. Evidence

- Golden tests for SQL and manifest output
- Authoritative DS 5.2 fixture coverage
- Determinism tests for repeatability
- Cache tests for compile-once concurrency

---

## 10. Scope Boundaries

- This epic builds the compilation layer, not the full executor story
- PostgreSQL is the runtime path wired here
- AOT mapping-pack compilation is not implemented yet

---

## Close

- Deterministic plans now exist for reads and writes
- The runtime can compile and cache them safely
- The output is testable, reusable, and ready for executor implementation
