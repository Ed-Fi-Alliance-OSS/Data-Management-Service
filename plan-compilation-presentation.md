# Plan Compilation Epic

## Plan Compilation

- The relational model is the database-shaped version of an Ed-Fi resource schema.
- It defines the tables, columns, keys, and relationships needed to store and read that resource.
- Plan compilation uses it to generate deterministic read and write plans.
### &nbsp;
### &nbsp;
### &nbsp;
### &nbsp;
---

## Plan Compilers

- Compile executor-ready plans, SQL with placeholders
- Removes runtime guesswork and SQL parsing
- Reuse the same plan shape across runtime and future AOT work
### &nbsp;
### &nbsp;
### &nbsp;
### &nbsp;
---

## When They Run

- Query plan compiler runs at request time because filters, operators, and paging can change per request
- Write and read plan compilers run at startup when the `MappingSet` is compiled for the current effective schema
- Requests then reuse the cached read and write plans and only bind request values at execution time
### &nbsp;
### &nbsp;
### &nbsp;
### &nbsp;
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
### &nbsp;
### &nbsp;
### &nbsp;
### &nbsp;
---

## Write Plan Compilation

- The compiler emits the SQL needed to write each relational table in a resource
- Root and 1:1 tables can use `InsertSql` and `UpdateSql`
- Child and collection tables use `DeleteByParentSql` plus insert for replace semantics
- The plan also carries binding metadata such as `DocumentId -> @documentId`, `NameOfInstitution -> @nameOfInstitution`, `SchoolId -> @schoolId`, and `City -> @city`
### &nbsp;
### &nbsp;
### &nbsp;
### &nbsp;
---

## Example: Collection Table Write Plan
Note the relational model already has:
```
edfi.School.SchoolId -> $.schoolId
edfi.School.NameOfInstitution -> $.nameOfInstitution
edfi.SchoolAddress.City -> $addresses[*].city
```
Plan Objects
```json
{
  "table_plans_in_dependency_order": [
    {
      "table": { "schema": "edfi", "name": "School" },
      "column_bindings_in_order": [
        { "column_name": "DocumentId", "parameter_name": "documentId", "write_value_source": { "kind": "document_id" } },
        { "column_name": "NameOfInstitution", "parameter_name": "nameOfInstitution", "write_value_source": { "kind": "scalar", "relative_path": "$.nameOfInstitution" } },
        { "column_name": "SchoolId", "parameter_name": "schoolId", "write_value_source": { "kind": "scalar", "relative_path": "$.schoolId" } }
      ]
    },
    {
      "table": { "schema": "edfi", "name": "SchoolAddress" },
      "column_bindings_in_order": [
        { "column_name": "School_DocumentId", "parameter_name": "school_DocumentId", "write_value_source": { "kind": "parent_key_part", "index": 0 } },
        { "column_name": "Ordinal", "parameter_name": "ordinal", "write_value_source": { "kind": "ordinal" } },
        { "column_name": "City", "parameter_name": "city", "write_value_source": { "kind": "scalar", "relative_path": "$.city" } }
      ]
    }
  ]
}
```
Plan SQL
```sql
INSERT INTO "edfi"."School"
(
    "DocumentId",
    "NameOfInstitution",
    "SchoolId"
)
VALUES
(
    @documentId,
    @nameOfInstitution,
    @schoolId
)
;

UPDATE "edfi"."School"
SET
    "NameOfInstitution" = @nameOfInstitution,
    "SchoolId" = @schoolId
WHERE
    ("DocumentId" = @documentId)
;
```
```sql
DELETE FROM "edfi"."SchoolAddress"
WHERE
    ("School_DocumentId" = @school_DocumentId)
;

INSERT INTO "edfi"."SchoolAddress"
(
    "School_DocumentId",
    "Ordinal",
    "City"
)
VALUES
(
    @school_DocumentId,
    @ordinal,
    @city
)
;
```
### &nbsp;
### &nbsp;
### &nbsp;
### &nbsp;
---

## Example: Collection Table Read Plan
Plan Objects
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
Plan SQL
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
### &nbsp;
### &nbsp;
### &nbsp;
### &nbsp;
---

## What's Next

- The runtime executor is next
- AOT mapping-pack compilation is probably not V1
