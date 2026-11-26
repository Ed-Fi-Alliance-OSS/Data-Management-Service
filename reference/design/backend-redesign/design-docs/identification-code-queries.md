# Identification-Code Collection Queries

GET-many queries are compiled to predicates over resource root-table columns (see
[transactions-and-concurrency.md](transactions-and-concurrency.md#query)).
Identification-code collections are the one exception: a resource may be filtered on the properties
of its identification-code collection, which lives in a child table. This note describes that
exception, why it compiles to a correlated `EXISTS`, and where DMS deliberately differs from the
Ed-Fi ODS/API (pinned at tag `v7.3`, commit `e94f44775d2de3db70e7da80395762915d3a179f`), which
offers the same filters.

## Which resources and parameters

The filters are derived from the loaded ApiSchema by one rule
(`IdentificationCodeQueryFieldRule`), not read from `queryFieldMapping`, so no ApiSchema, MetaEd or
package change is involved and `EffectiveSchemaHash` is unaffected.

- A resource's identification-code collection is the **first top-level array property**, in
  `jsonSchemaForInsert` property order, whose items declare an `identificationCode` property. This
  is the ODS/API rule. Root-level, nested and extension properties are never considered.
- Every scalar property of those items is a query parameter, named as the property and typed as
  `queryFieldMapping` types scalars.
- A name that already names one of the resource's `queryFieldMapping` fields (case-insensitively) is
  not offered: the existing root field keeps its meaning.

Under Data Standard 5.2 this yields 15 resources: the nine education organization resources,
assessments, courses, courseTranscripts, learningStandards, staffs and
studentEducationOrganizationAssociations. Under Data Standard 6.1, which moved most identification
codes into their own resources, it yields assessments, courses, courseTranscripts and
learningStandards. On courseTranscripts, `assigningOrganizationIdentificationCode` and
`courseCatalogURL` are root query fields, so only `identificationCode` and
`courseIdentificationSystemDescriptor` filter the collection.

Upstream `queryFieldMapping` entries that cross an array boundary remain unsupported: they still
omit the resource's relational GET-many at mapping-set compilation.

## Semantics and SQL

Each derived field compiles to a collection-column target: the one collection table scoped to the
collection, its root locator column, and the one stored column at the item path (a descriptor FK
for a descriptor-valued property). A field that does not resolve omits the resource, as any other
unsupported field does.

At query time every identification-code predicate on a collection table is placed in **one**
correlated `EXISTS`, emitted after the root value predicates and before authorization filters:

```sql
-- PostgreSQL, schools; the descriptor FK column name is shortened by the DDL generator.
EXISTS (SELECT 1 FROM "edfi"."SchoolIdentificationCode" ci
        WHERE ci."School_DocumentId" = r."DocumentId"
          AND ci."EducationOrganizationIdentificationSystemDescriptor__f63fb21ede" = @educationOrganizationIdentificationSystemDescriptor
          AND ci."IdentificationCode" = @identificationCode)
```

- **Same element.** All predicates test the same collection row, so every supplied
  identification-code parameter must match one element. A code and a system that occur only on
  different elements do not match.
- **One row per resource.** `EXISTS` is a semi-join: a resource matched by several elements is
  selected once and counted once, without `DISTINCT` and without changing the keyset, cursor or
  partition anchors.
- **Scoped columns.** The subquery's columns are qualified by its own alias, so they cannot be
  confused with root or authorization columns of the same name.
- **Every mode.** The predicate is part of the shared `WHERE` builder, so offset pages,
  `totalCount`, cursor pages under either anchor, the unpaged candidate relation behind
  `/partitions`, and document-cache candidate selection all apply it, beside change-version windows
  and every authorization strategy.
- **No DDL change.** The correlation column leads each collection table's existing unique key,
  which supports indexed per-resource probes; the optimizer still chooses the access strategy.
  There is no index on `IdentificationCode` alone, so a filter on the code without a selective
  root predicate may scan the collection table.

Values are matched exactly with `=`; string comparison follows the column collation, as for root
string filters. Descriptor values are resolved in the same batched lookup as root descriptor
filters.

## OpenAPI

The parameters are added to each eligible collection GET after fragment merge and before cursor
augmentation, so `/partitions` publishes them too. The published set equals the set the runtime
accepts, for every resource, in every document that publishes the operation.

## Deliberate differences from the ODS/API

| | ODS/API v7.3 | DMS |
|---|---|---|
| DV1 | Joins the collection without `DISTINCT`; reading the source suggests a resource matched by two elements could be returned twice (not observed). | Each matching resource is returned and counted once. |
| DV2 | Offers courseTranscripts `assigningOrganizationIdentificationCode` and `courseCatalogURL` as collection parameters. | Those names keep their existing root-field meaning; the collection qualifiers are not offered. |
| DV3 | Binds the parameters as untyped strings; no length check is known. | A value longer than the item property's `maxLength` is rejected with 400, as is a SQL Server value wider than its column. |
| DV4 | Hides the parameters in profile documents that exclude the collection. | Publishes them wherever the GET-many operation is published; filtering applies regardless of profile, as at ODS/API runtime. |
| DV5 | Silently ignores names that are not parameters of the resource. | Ignores them and names them in `X-EdFi-Warning`. |
| DV6 | Rejects a descriptor value without `#` with 400. | Rejects only descriptor text containing NUL or an unpaired surrogate (400). Any other value, including one without `#`, is looked up; a miss returns 200 with an empty page, as for root descriptor filters. |

The resource-selection rule, same-element matching, an unresolved well-formed descriptor returning
an empty page, case-insensitive names and composition with authorization match the ODS/API.
