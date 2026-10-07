---
jira: DMS-1404
jira_url: https://edfi.atlassian.net/browse/DMS-1404
epic: DMS-1402
---

# Story: Give descriptors a compact int surrogate, plus other optimizations

## Problem and rationale

DMS currently uses the eight-byte document ID for descriptor foreign keys throughout resource tables and indexes. Introduce a separately allocated four-byte descriptor ID to reduce that repeated storage while retaining the document ID for document metadata and concurrency operations.

The checked-in DS 5.2 PostgreSQL and SQL Server relational-model manifests reviewed on October 6, 2026 contain 595 stored descriptor FK columns and 879 indexes with at least one such key column, all currently Int64. These update the earlier spike's inventory of 594 columns and 837 indexes. The counts describe schema coverage, not measured storage savings; actual savings depend on populated rows and physical layout. Join and index performance improvements are expected benefits to validate, not established results.

This implements the descriptor optimization identified in DMS-1398. ODS's compact integer descriptor identity provides the precedent.

## Implementation scope

- Add an independently generated int DescriptorId primary key to dms.Descriptor. Retain DocumentId as a unique, non-null FK to dms.Document. Use the compact ID for stored resource descriptor references.
- Remove the redundant descriptor Discriminator string and use the existing ResourceKeyId smallint for descriptor type identity.
- Remove stored Uri and its (Uri, Discriminator) uniqueness constraint. Enforce uniqueness by ResourceKeyId and the engine-lowered, reconstructed whole URI (Namespace + "#" + CodeValue). Reconstruct URIs wherever needed, including identity verification and response materialization; retain the folded URI in the lookup index only.
- Use ResourceKeyId instead of string discriminators in descriptor tracked-change records and their query and authorization paths.
- Update both dialects, model derivation, DDL, compiled plans, reference-resolution contracts, flattening, query preprocessing, hydration, cache integration, change-tracking joins, and affected fixtures/tests.

Current runtime contracts sometimes call a descriptor DocumentId a DescriptorId. Make the distinction explicit throughout the affected code. Reference resolution can return both IDs in the existing probe; the PostgreSQL lookup already joins dms.Descriptor. Keep document metadata and concurrency operations tied to DocumentId.

Abstract-identity discriminator replacement is explicitly out of scope. Existing abstract-identity discriminator columns, union-view discriminator outputs, and their authorization behavior remain unchanged. This exclusion does not exempt descriptor FK columns in those structures from the compact-ID conversion where applicable.

## Acceptance criteria

### 1. Compact descriptor identity

- dms.Descriptor has an independently generated int DescriptorId primary key, using its own allocation sequence or identity rather than narrowing the global DocumentId.
- DocumentId remains a unique, non-null FK to dms.Document.
- Every stored descriptor-reference column uses int and references DescriptorId, including descriptor references participating in composite keys or constraints.
- Updating an existing descriptor preserves both IDs.

### 2. Descriptor type and natural key

- Remove dms.Descriptor.Discriminator; use its existing ResourceKeyId to identify descriptor type.
- Enforce uniqueness by ResourceKeyId and the engine-lowered, reconstructed whole URI, using the provider-specific expressions and collations in answer 2. Do not enforce component-wise Namespace/CodeValue uniqueness.
- Preserve agreement between descriptor and owning document ResourceKeyId.
- Descriptor types from different projects remain distinguishable, including types with identical resource names. The same namespace/code value can belong to different descriptor types without conflating them.

### 3. Remove stored URI

- Remove dms.Descriptor.Uri and the (Uri, Discriminator) unique constraint.
- Construct descriptor URIs from Namespace + "#" + CodeValue wherever needed.
- Use a PostgreSQL expression index and a SQL Server non-persisted UriLowered computed column with a unique index over the reconstructed URI and ResourceKeyId. No stored Uri or persisted UriLowered column is required.
- Preserve case-insensitive reference resolution and canonical, original-case URI output.
- Preserve existing duplicate-detection behavior within each descriptor type.
- Compare incoming descriptor-reference URIs as whole strings without splitting on "#", adding delimiter restrictions, or introducing component trimming. Preserve existing input validation and trimming rules.

### 4. Correct runtime use of both IDs

- Reference resolution returns DescriptorId and DocumentId without an additional database round trip.
- Resource writes, query filters, hydration, and joins from stored descriptor references use DescriptorId.
- Document metadata, locking, concurrency, and cache operations continue using the appropriate DocumentId.
- Tests deliberately use unequal DescriptorId and DocumentId values to detect accidental interchange.

### 5. Descriptor change tracking

- Descriptor tracked-change records and their query/authorization paths use ResourceKeyId instead of string discriminators.
- Resource change-tracking joins resolve descriptor references through DescriptorId.
- Delete-history responses retain the correct descriptor type, namespace, and code value after the live descriptor and document rows are deleted.

### 6. Behavior preserved on both database engines

- PostgreSQL and SQL Server tests cover descriptor CRUD, referenced-descriptor deletion protection, resource writes and reads, descriptor query filters, namespace authorization, and cache materialization.
- Existing identity-update rules, no-op detection, ETags, and change-version behavior remain unchanged.
- Regression coverage includes mixed-case descriptor references, identical URI values across descriptor types, and descriptor types with identical resource names across projects.
- Both providers have regression cases for distinct component pairs that reconstruct the same URI and for spaces immediately before "#" in references and descriptor-valued filters. Writes, identity lookups, and tombstone recreation probes must apply the same whole-URI equality contract.

### 7. Storage and performance validated

- Regenerated DS 5.2 and extension fixtures demonstrate the new column types, keys, constraints, and indexes.

### 8. Provisioning and scope documented

- Document that existing databases require deliberate reprovisioning.
- Follow the repository's RelationalMappingVersion release cadence. For the current 8.1 release target, retain v3; do not add another mapping-version bump.
- Abstract-identity discriminator columns and behavior remain unchanged.
- Update affected backend-redesign documentation to reflect the separate DescriptorId and DocumentId roles, compact descriptor foreign keys, descriptor uniqueness by ResourceKeyId and reconstructed whole URI, URI reconstruction, and ResourceKeyId-based descriptor change tracking. Reconcile overlapping storage-reduction stories and design documents so they do not prescribe conflicting final schemas. Preserve the exclusion of abstract-identity discriminator replacement.

## Clarifying Questions and Answers

### Questions 1

1. Where does DMS-1404 sit relative to DMS-1443–DMS-1456: must implementation start from the completed natural-key/ReferentialIdentity-removal schema, or must it also support the current RI-based runtime? Which stories block it, and does it share their same-release atomicity requirement?

2. What provider-specific unique-index and probe contract applies after removing stored `Uri`? Should uniqueness move to `(ResourceKeyId, Namespace, CodeValue)`, or should `UX_Descriptor_UriLowered_ResourceKeyId` and SQL Server `UriLowered` be retained in reconstructed form with the existing engine-side `LOWER` semantics (`pg_c_utf8` on PostgreSQL and explicit SQL Server identity collation)?

3. How should raw descriptor-reference URIs be compared while preserving existing duplicate and lookup behavior? What is the contract for distinct component pairs that reconstruct the same `Namespace + "#" + CodeValue` URI, and for values whose component-wise comparisons could differ from whole-URI comparison (for example, SQL Server trailing spaces immediately before `#`)?

4. Does acceptance criterion 7 require measured storage and query/join performance evidence, or only regenerated schema fixtures? If measurements gate completion, what populated dataset, baseline, workloads, database engines, and regression thresholds should the implementation tasks use?

### Answers 1

1. DMS-1404 has no dependency on any other story in this epic and does not share the DMS-1443–DMS-1456 same-release atomicity requirement. Implement it against the current runtime, reusing existing work and completing within this story any descriptor-specific prerequisites needed to satisfy its acceptance criteria. Preserve the functionality of ReferentialIdentity paths that remain active; removing that infrastructure remains outside this story. Reconcile overlapping sibling stories and design documents so subsequent work builds on the descriptor schema and behavior established here.

2. Preserve the whole-URI, engine-owned folding contract from `natural-key-resolution.md` and DMS-1448/DMS-1449, with no application lowercasing or Unicode normalization. PostgreSQL uses a unique expression index on `("ResourceKeyId", lower(("Namespace" || '#' || "CodeValue") COLLATE "pg_c_utf8"))`. SQL Server uses a non-persisted `UriLowered` computed column defined as `LOWER(([Namespace] + N'#' + [CodeValue]) COLLATE SQL_Latin1_General_CP1_CI_AS)`, with a unique index on `(ResourceKeyId, UriLowered)`. Retain `UX_Descriptor_UriLowered_ResourceKeyId` in reconstructed form; do not add component-wise unique indexes or `NamespaceLowered`/`CodeValueLowered` columns. The folded URI is stored only in the index, not as a duplicate in the base row. Every probe compares the whole input URI using the matching expression, including the explicit collation inside each parameter-side fold: `lower(@uri COLLATE "pg_c_utf8")` or `LOWER(@uri COLLATE SQL_Latin1_General_CP1_CI_AS)`. Apply this consistently to upsert/PUT identity verification, reference resolution, query preprocessing, and Change Query recreation detection; project both IDs from the existing lookup.

3. Preserve descriptor identity as the complete `Namespace + "#" + CodeValue` string, scoped by `ResourceKeyId`. Do not split incoming descriptor-reference URIs or introduce additional delimiter restrictions or component trimming. Existing input validation and trimming rules remain in force; the natural-key design's NUL rejection and engine-owned folding contract remain applicable. Multiple `#` characters retain their existing meaning: distinct component pairs that reconstruct the same URI identify the same descriptor within a descriptor type and cannot create separate identities. Spaces immediately before `#` remain inside the compared string; for example, `namespace #code` must not acquire a match to `namespace#code` through SQL Server's trailing-space treatment of a separately compared namespace. Remove the stored `Uri` column, but enforce uniqueness and perform identity lookups using the reconstructed whole URI and the expressions in answer 2. Apply this single contract to writes, references, descriptor-valued filters, and tombstone recreation probes, with explicit provider regression cases for delimiter collisions and spaces before `#`. The implementation scope and acceptance criterion 2 require uniqueness by descriptor type and reconstructed whole URI, replacing the proposed component-wise uniqueness requirement.

4. Criterion 7 requires regenerated schema fixtures only. Its sole acceptance bullet names DS 5.2 and extension fixtures; the rationale explicitly distinguishes schema coverage from measured savings and calls performance improvements unestablished. Do not create a populated-data benchmark or numerical performance gate for this story: no dataset, baseline, workload, or threshold is specified, and the natural-key design's prototype measurements concern a different optimization. Generate and verify both providers' fixtures for compact descriptor columns, keys, constraints, and indexes, alongside the behavior and round-trip tests required elsewhere in this story. Later rename criterion 7 to “Schema storage changes validated” so its heading matches its actual requirement; measured storage and query/join benefits remain unproven.
