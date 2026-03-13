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

## Example: Collection Table Write Plan

```json
{
  "table": { "schema": "sample", "name": "SchoolExtensionAddress" },
  "column_bindings_in_order": [
    { "column_name": "School_DocumentId", "write_value_source": { "kind": "parent_key_part", "index": 0 } },
    { "column_name": "Ordinal", "write_value_source": { "kind": "ordinal" } },
    { "column_name": "Zone", "write_value_source": { "kind": "scalar", "relative_path": "$.zone" } }
  ]
}
```

```sql
  DELETE FROM "sample"."SchoolExtensionAddress"
  WHERE
      ("School_DocumentId" = @school_DocumentId)
  ;

  INSERT INTO "sample"."SchoolExtensionAddress"
  (
      "School_DocumentId",
      "Ordinal",
      "Zone"
  )
  VALUES
  (
      @school_DocumentId,
      @ordinal,
      @zone
  )
  ;
```
---

## Example: Read Plan - Root Table + Collection Table

```json
{
  "table_plans_in_dependency_order": [
    {
      "table": { "schema": "edfi", "name": "School" },
      "select_list_columns_in_order": ["DocumentId", "NameOfInstitution", "SchoolId"],
      "order_by_key_columns_in_order": ["DocumentId"]
    },
    {
      "table": { "schema": "edfi", "name": "SchoolAddress" },
      "select_list_columns_in_order": ["School_DocumentId", "Ordinal", "City"],
      "order_by_key_columns_in_order": ["School_DocumentId", "Ordinal"]
    }
  ]
}
```

```sql
SELECT
    r."DocumentId",
    r."NameOfInstitution",
    r."SchoolId"
FROM "edfi"."School" r
INNER JOIN "page" k ON r."DocumentId" = k."DocumentId"
ORDER BY
    r."DocumentId" ASC
;

SELECT
    t."School_DocumentId",
    t."Ordinal",
    t."City"
FROM "edfi"."SchoolAddress" t
INNER JOIN "page" k ON t."School_DocumentId" = k."DocumentId"
ORDER BY
    t."School_DocumentId" ASC,
    t."Ordinal" ASC
;
```
---

## What's Next

- This epic builds the compilation layer, the runtime executor is next
- PostgreSQL is the only runtime path wired here
- AOT mapping-pack compilation is not implemented yet
