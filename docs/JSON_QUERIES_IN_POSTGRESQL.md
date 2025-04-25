# Notes on JSON Queries within PostgreSQL

## Enablement

Need to run this when using PostgreSQL as the back-end query handler:

```sql
CREATE EXTENSION IF NOT EXISTS btree_gin;
CREATE INDEX IF NOT EXISTS IX_Document_GIN on dms.document USING gin(EdfiDoc jsonb_path_ops, ResourceName);
```

Perhaps this can be done with the PostgreSQL setup script, receiving the query
handler environment variable.

## Observations

* NPgsql does not parameterize values in a JSON query; the values need to be
  inserted directly into the SQL statement. That means we need to take extra
  precautions against SQL injection attacks.
* Need to use proper dates types - won't find 2024 if searching for "2024".
* Will need to use JSON paths for references that are part of a natural key.
* With `ORDER BY CreatedAt` in the query, does `CreatedAt` need to be part of
  the index?

## Query Styles

The following three queries are equivalent:

```sql
explain analyze 
SELECT EdfiDoc
FROM dms.Document
WHERE ResourceName = 'SchoolYearType' AND EdfiDoc @> '{"schoolYear": 2020}';

explain analyze
SELECT EdfiDoc
FROM dms.Document
WHERE ResourceName = 'SchoolYearType' AND EdfiDoc @> json_build_object('schoolYear', 2020)::jsonb;

explain analyze 
SELECT EdfiDoc
FROM dms.Document
WHERE ResourceName = 'SchoolYearType'
  AND jsonb_path_exists(EdfiDoc, '$.schoolYear ? (@ == 2020)');
```

With a small database (56 records), the query plan analysis makes it look like
these would have differing performance, but the actual time to retrieve data is
about the same in all cases. Need to perform more intensive testing to see if
one approach is more productive than another.

## Authorization

GitHub Copilot prompt, with the OpenSearch `QueryDocuments` open:

> Convert function QueryDocuments to equivalent SQL statement for use with PostgreSQL.

Response:

> Converting the QueryDocuments function to an equivalent SQL statement for
> PostgreSQL involves translating the OpenSearch query DSL into SQL syntax.
> Below is an example of how the logic in QueryDocuments can be expressed as a
> SQL query:

```sql
WITH authorization_filters AS (
    SELECT *
    FROM securityelements
    WHERE (
        -- Namespace filters
        securityelements."Namespace" = ANY(ARRAY[
            'namespace_value_1', 'namespace_value_2' -- Replace with actual namespace filter values
        ])
        OR
        -- EducationOrganization filters
        securityelements."EducationOrganizationId" IN (
            SELECT hierarchy.array
            FROM edfi_dms_educationorganizationhierarchytermslookup
            WHERE id = '6001010' -- Replace with actual filter value
        )
    )
)
SELECT edfidoc.*
FROM edfidoc
JOIN authorization_filters ON edfidoc."SecurityElementId" = authorization_filters."Id"
WHERE (
    -- Match phrase filters
    edfidoc."schoolYearDescription" = 'Year 2025'
    AND edfidoc."currentSchoolYear" = FALSE
    AND (
        -- OR condition for multiple document paths
        edfidoc."DocumentPath1" = 'value1'
        OR edfidoc."DocumentPath2" = 'value2'
    )
)
ORDER BY edfidoc."Id" ASC -- Default sort order
LIMIT 20 OFFSET 100; -- Pagination parameters
```

> Notes [from Copilot]:
>
> * Replace placeholders like 'namespace_value_1', 'value1', and '6001010' with
>   actual values from the queryRequest.
> * Ensure that the table and column names (edfidoc, securityelements, etc.)
>   match your PostgreSQL schema.
> * If the edfidoc table contains nested JSON fields, you may need to use
>   PostgreSQL's JSON/JSONB functions (e.g., ->, ->>, jsonb_array_elements) to
>   query them.
