# BACKEND-REDESIGN-1 Feedback

## Findings
- [High] Query planning only targets root-table columns, but `queryFieldMapping` can point to nested/array paths; without child-table joins/EXISTS, those queries will return incomplete results. Define child-table predicate generation or constrain `queryFieldMapping` to root-only paths. (BACKEND-REDESIGN-1.md:552, BACKEND-REDESIGN-1.md:556)
- [High] Reconstitution assumes a single referenced resource table for identity expansion; polymorphic references have no plan for determining concrete type or joining the right table, which breaks reference output and query filtering. Add a discriminator/union view/membership plan for abstract resources. (BACKEND-REDESIGN-1.md:540, BACKEND-REDESIGN-1.md:435)
- [High] Derived mapping only mentions `$ref` plus array/object/scalar traversal; it does not specify handling for `allOf`/`anyOf`/`oneOf`, nullable types, or `additionalProperties`, nor how to suppress columns for reference-object internals. Constrain ApiSchema to a supported subset or add a normalization pass before derivation. (BACKEND-REDESIGN-1.md:885, BACKEND-REDESIGN-1.md:986)
- [Medium] Paging order changes to `ORDER BY DocumentId`; current behavior orders by `CreatedAt`. This can change result ordering and paging semantics. Decide on the ordering contract (CreatedAt/LastModifiedAt) and index accordingly. (BACKEND-REDESIGN-1.md:562, src/dms/backend/EdFi.DataManagementService.Backend.Postgresql/Operation/SqlAction.cs:359)
- [Medium] Inlining every non-array object into the current table can hit SQL Server row/column limits for wide resources/extensions; there is no 1:1 split option for large object graphs. Consider relational metadata or auto-splitting to 1:1 tables when thresholds are exceeded. (BACKEND-REDESIGN-1.md:988, BACKEND-REDESIGN-1.md:414)
- [Medium] `ReferentialIdentity.IdentityRole` is required and unique per document, but roles are undefined; multi-level abstract hierarchies may need multiple alias identities. Define the role enumeration and how many ancestor referential IDs must be emitted. (BACKEND-REDESIGN-1.md:89, BACKEND-REDESIGN-1.md:83)
- [Low/Medium] The design removes partitioning while keeping OFFSET paging; large datasets may regress without a partitioning or keyset strategy. Consider partitioning by `(ProjectName, ResourceName)` or adopting keyset paging for large offsets. (BACKEND-REDESIGN-1.md:61, BACKEND-REDESIGN-1.md:559)

## Questions / Assumptions
- Are query fields expected to remain equal-only, or do any resources rely on range/partial matching that needs explicit relational support?
- Will ApiSchema be restricted to a subset of JSON Schema (no `oneOf`/`anyOf`), and if so, where is that enforced?
- Should polymorphic reference output include a discriminator or just the concrete reference identity fields?

## Suggested Validation / Tests
- Round-trip tests for flatten/reconstitute with nested arrays, descriptor refs, and polymorphic refs.
- Query tests that cover reference-based filters and any query fields that map to arrays/child tables.
- Cross-engine DDL validation for wide resources (column-count and row-size limits).
