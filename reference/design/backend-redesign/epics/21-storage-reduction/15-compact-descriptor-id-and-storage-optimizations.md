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
- Remove stored Uri and its (Uri, Discriminator) uniqueness constraint. Enforce (ResourceKeyId, Namespace, CodeValue) uniqueness and reconstruct URIs wherever needed, including identity verification and response materialization.
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
- Enforce uniqueness on (ResourceKeyId, Namespace, CodeValue).
- Preserve agreement between descriptor and owning document ResourceKeyId.
- Descriptor types from different projects remain distinguishable, including types with identical resource names. The same namespace/code value can belong to different descriptor types without conflating them.

### 3. Remove stored URI

- Remove dms.Descriptor.Uri and the (Uri, Discriminator) unique constraint.
- Construct descriptor URIs from Namespace + "#" + CodeValue wherever needed.
- Preserve case-insensitive reference resolution and canonical, original-case URI output.
- Preserve existing duplicate-detection behavior within each descriptor type.

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

### 7. Storage and performance validated

- Regenerated DS 5.2 and extension fixtures demonstrate the new column types, keys, constraints, and indexes.

### 8. Provisioning and scope documented

- Document that existing databases require deliberate reprovisioning.
- Follow the repository's RelationalMappingVersion release cadence. For the current 8.1 release target, retain v3; do not add another mapping-version bump.
- Abstract-identity discriminator columns and behavior remain unchanged.
- Update affected backend-redesign documentation to reflect the separate DescriptorId and DocumentId roles, compact descriptor foreign keys, descriptor natural-key uniqueness, URI reconstruction, and ResourceKeyId-based descriptor change tracking. Reconcile overlapping storage-reduction stories and design documents so they do not prescribe conflicting final schemas. Preserve the exclusion of abstract-identity discriminator replacement.

## Clarifying Questions and Answers

### Questions 1

1. Where does DMS-1404 sit relative to DMS-1443–DMS-1456: must implementation start from the completed natural-key/ReferentialIdentity-removal schema, or must it also support the current RI-based runtime? Which stories block it, and does it share their same-release atomicity requirement?

2. What provider-specific unique-index and probe contract replaces `UX_Descriptor_UriLowered_ResourceKeyId` when uniqueness moves to `(ResourceKeyId, Namespace, CodeValue)`? In particular, must both components retain the existing engine-side `LOWER` semantics (`pg_c_utf8` on PostgreSQL and explicit SQL Server identity collation), and are the old lowered-URI index and SQL Server `UriLowered` computed column removed or retained in reconstructed form?

3. How should raw descriptor-reference URIs map to the new component key while preserving existing duplicate and lookup behavior? What delimiter rule or existing validation guarantees that `Namespace + "#" + CodeValue` is unambiguous, and how should component-wise comparisons handle values that compare differently from the former whole-URI comparison (for example, SQL Server trailing spaces immediately before `#`)?

4. Does acceptance criterion 7 require measured storage and query/join performance evidence, or only regenerated schema fixtures? If measurements gate completion, what populated dataset, baseline, workloads, database engines, and regression thresholds should the implementation tasks use?

### Answers 1

1. **Requires human decision:** Approve DMS-1404's implementation baseline and release relationship to DMS-1443–DMS-1456. The epic explicitly places DMS-1404 outside the T1–T14 chain, and its same-release rule names only those fourteen stories; neither it nor this story assigns DMS-1404 blockers. Recommend making DMS-1456 the direct prerequisite (therefore inheriting the full chain), implementing against the completed natural-key/RI-free runtime without a second RI-compatible path. Under that sequencing, T1–T14 retain their existing atomicity requirement, while DMS-1404's schema and affected runtime changes ship together as a subsequent coherent change; T1–T14 need not wait for DMS-1404. This is a proposed dependency/release decision, not an existing agreement. Once approved, record it in the story and epic dependency documentation. For the stated 8.1 target, retain `v3` and require deliberate reprovisioning; the contrary future-bump instruction in T14 must be reconciled with the repository's release-cadence rule.

2. Preserve the engine-owned folding contract from `natural-key-resolution.md` and DMS-1448/DMS-1449: both components must use SQL `LOWER`, with no application lowercasing or Unicode normalization. The concrete component-key design is a PostgreSQL unique expression index on `(ResourceKeyId, lower(Namespace COLLATE "pg_c_utf8"), lower(CodeValue COLLATE "pg_c_utf8"))`; SQL Server uses non-persisted `NamespaceLowered` and `CodeValueLowered` computed columns, each defined with `LOWER(component COLLATE SQL_Latin1_General_CP1_CI_AS)`, and a unique index on `(ResourceKeyId, NamespaceLowered, CodeValueLowered)`. Every probe must use the same expressions, including the explicit collation inside each parameter-side fold. Apply this consistently to upsert/PUT identity verification, reference resolution, query preprocessing, and Change Query recreation detection; project both IDs from the existing lookup. This replacement removes `UX_Descriptor_UriLowered_ResourceKeyId` and SQL Server `UriLowered`, rather than retaining an additional reconstructed-URI index. **Requires human decision:** This physical design is conditional on resolving question 3; the available context does not establish that component equality satisfies the required preservation of whole-URI identity behavior. Do not finalize the index/probe tasks until that contract is settled.

3. **Requires human decision:** Define the accepted URI component grammar and approve an equality contract that reconciles component-key uniqueness with preservation of existing whole-URI duplicate/lookup behavior. The design specifies raw URI lookup, existing trimming rules, NUL rejection, and engine-owned folding, but establishes no first/last-`#` splitting rule or universal prohibition on `#` in the individual components. Current descriptor extraction carries the entire URI, so it supplies no such guarantee. Without one, distinct component pairs can reconstruct the same URI. Also, rejecting whitespace in descriptor writes does not settle reference/query behavior: splitting a raw `namespace #code` input makes the space trailing in the namespace operand, potentially changing SQL Server equality compared with the former whole-URI probe. Do not silently trim split components, introduce delimiter restrictions, or assume two component comparisons preserve whole-string equality. The decision must specify the split rule, the verdict for ambiguous delimiters and spaces immediately before `#`, and which acceptance text changes if compatibility cannot be preserved. Then apply that single contract to writes, references, filters, and tombstone probes, with explicit provider regression cases.

4. Criterion 7 requires regenerated schema fixtures only. Its sole acceptance bullet names DS 5.2 and extension fixtures; the rationale explicitly distinguishes schema coverage from measured savings and calls performance improvements unestablished. Do not create a populated-data benchmark or numerical performance gate for this story: no dataset, baseline, workload, or threshold is specified, and the natural-key design's prototype measurements concern a different optimization. Generate and verify both providers' fixtures for compact descriptor columns, keys, constraints, and indexes, alongside the behavior and round-trip tests required elsewhere in this story. Later rename criterion 7 to “Schema storage changes validated” so its heading matches its actual requirement; measured storage and query/join benefits remain unproven.
