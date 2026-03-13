# Plan Compilation Epic

## Plan Compilation

- The relational model is the database-shaped version of an Ed-Fi resource schema.
- It defines the tables, columns, keys, and relationships needed to store and read that resource.
- Plan compilation uses it to generate deterministic read and write plans.

---

## Plan Compilers

- Removes runtime guesswork and SQL parsing
- Compile executor-ready plans
- Reuse the same plan shape across runtime and future AOT work

---

## When Each Compiler Runs

- Query plan compiler runs at request time because filters, operators, and paging can change per request
- Write and read plan compilers run at startup when the `MappingSet` is compiled for the current schema, dialect, and mapping version
- Requests then reuse the cached read and write plans and only bind request values at execution time

---

## Example: Query Plan

- From SQL compiler, given schoolYear and studentUniqueId as query parameters for StudentSchoolAssociation
- SchoolYear filter, StudentUniqueId key unification lookup, and paging parameters

```sql
SELECT r."DocumentId"
FROM "edfi"."StudentSchoolAssociation" r
WHERE
    (r."SchoolYear" = @schoolYear)
    AND (r."Student_DocumentId" IS NOT NULL AND r."StudentUniqueId_Unified" = @studentUniqueId)
ORDER BY r."DocumentId" ASC
LIMIT @limit OFFSET @offset
;
```

---

## Write Plan Compilation

- The compiler emits the SQL needed to write each relational table in a resource
- Root and 1:1 tables can use `InsertSql` and `UpdateSql`
- Child and collection tables use `DeleteByParentSql` plus insert for replace semantics
- The plan also carries binding metadata such as `SchoolId -> @schoolId`, `SchoolYear -> @schoolYear`, and `StudentUniqueId -> @studentUniqueId`

---

## 7. Example: Collection Table Write Plan

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

## 8. Example: Read Plan with Multi-column Identity & Descriptor

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

## 9. Runtime Integration

- Compile active mapping set from effective schema
- Cache once per process
- Log `Compiled`, `Joined in-flight`, or `Reused completed`

---

## 10. Evidence

- Golden tests for SQL and manifest output
- Authoritative DS 5.2 fixture coverage
- Determinism tests for repeatability
- Cache tests for compile-once concurrency

---

## 11. Scope Boundaries

- This epic builds the compilation layer, not the full executor story
- PostgreSQL is the runtime path wired here
- AOT mapping-pack compilation is not implemented yet

---

## Close

- Deterministic plans now exist for reads and writes
- The runtime can compile and cache them safely
- The output is testable, reusable, and ready for executor implementation
