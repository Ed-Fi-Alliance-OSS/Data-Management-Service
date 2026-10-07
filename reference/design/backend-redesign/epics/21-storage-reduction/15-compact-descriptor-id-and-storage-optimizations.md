---
jira: DMS-1404
jira_url: https://edfi.atlassian.net/browse/DMS-1404
epic: DMS-1402
---

# Story: Give descriptors a compact int surrogate, plus other optimizations

## Problem and rationale

DMS currently uses the eight-byte document ID for descriptor foreign keys throughout resource tables and indexes. Introduce a separately allocated four-byte descriptor ID to reduce that repeated storage while retaining the document ID for document metadata and concurrency operations.

The checked-in DS 5.2 PostgreSQL and SQL Server relational-model manifests reviewed on October 6, 2026 contain 595 stored descriptor FK columns and 879 indexes with at least one such key column, all currently Int64. These update the earlier spike's inventory of 594 columns and 837 indexes. The counts describe schema coverage, not measured storage savings; actual savings depend on populated rows and physical layout. Join and index performance improvements are potential benefits, not established results.

This implements the descriptor optimization identified in DMS-1398. ODS's compact integer descriptor identity provides the precedent.

## Implementation scope

- Add an independently generated int DescriptorId primary key to dms.Descriptor. Retain DocumentId as a unique, non-null FK to dms.Document. Use the compact ID for stored resource descriptor references.
- Remove the redundant descriptor Discriminator string and use the existing ResourceKeyId smallint for descriptor type identity.
- Remove the physically stored Uri and its (Uri, Discriminator) uniqueness constraint. Enforce uniqueness by ResourceKeyId and the reconstructed, unlowered whole URI (Namespace + "#" + CodeValue), preserving the existing provider collation behavior. Reconstruct URIs wherever previously stored URI values were consumed, including hashing, identity verification, and response materialization.
- Use ResourceKeyId instead of string discriminators in descriptor tracked-change records and their query and authorization paths.
- Update both dialects, model derivation, DDL, compiled plans, reference-resolution contracts, flattening, query preprocessing, hydration, cache integration, change-tracking joins, and affected fixtures/tests.

Current runtime contracts sometimes call a descriptor DocumentId a DescriptorId. Make the distinction explicit throughout the affected code. The PostgreSQL reference lookup already joins dms.Descriptor, allowing it to return both IDs without another round trip.

DMS-1404 is independently deliverable against the current RI-based runtime and supported database versions. It has no dependency on another story in this epic, including DMS-1447, and does not share the DMS-1443–DMS-1456 same-release atomicity requirement. Preserve existing normalization, RI matching and maintenance, provider collation behavior, and descriptor POST/PUT behavior. Natural-key resolution, RI removal, new validation and equality rules, expanded Unicode alias matching, platform upgrades, and stored-wins descriptor semantics remain in their existing stories.

Abstract-identity discriminator replacement is explicitly out of scope. Existing abstract-identity discriminator columns, union-view discriminator outputs, and their authorization behavior remain unchanged. This exclusion does not exempt descriptor FK columns in those structures from the compact-ID conversion where applicable.

## Acceptance criteria

### 1. Compact descriptor identity

- dms.Descriptor has an independently generated int DescriptorId primary key, using its own allocation sequence or identity rather than narrowing the global DocumentId.
- DocumentId remains a unique, non-null FK to dms.Document.
- Every stored descriptor-reference column uses int and references DescriptorId, including descriptor references participating in composite keys or constraints.
- Updating an existing descriptor preserves both IDs.

### 2. Descriptor type and natural key

- Remove dms.Descriptor.Discriminator; use its existing ResourceKeyId to identify descriptor type.
- Enforce uniqueness by ResourceKeyId and the reconstructed, unlowered whole URI, preserving the former stored Uri column's provider collation behavior. Use the indexes specified in criterion 3; do not enforce component-wise Namespace/CodeValue uniqueness.
- Preserve agreement between descriptor and owning document ResourceKeyId.
- Descriptor types from different projects remain distinguishable, including types with identical resource names. The same namespace/code value can belong to different descriptor types without conflating them.

### 3. Remove stored URI

- Remove the physically stored dms.Descriptor.Uri and the (Uri, Discriminator) unique constraint. A non-persisted computed Uri column is permitted on SQL Server for indexing.
- Construct descriptor URIs from Namespace + "#" + CodeValue wherever needed.
- PostgreSQL uses a unique expression index on `("ResourceKeyId", ("Namespace" || '#' || "CodeValue"))`.
- SQL Server uses a non-persisted computed `Uri AS ([Namespace] + N'#' + [CodeValue])`, with a unique index on `(ResourceKeyId, Uri)`.
- Preserve the former stored Uri column's collation behavior in both reconstructed expressions. The URI remains in the index without being duplicated in the base row. Do not add LOWER to the uniqueness expressions, UriLowered, separate lowered component columns, or a new identity collation.
- Preserve case-insensitive reference resolution and canonical, original-case URI output.
- Preserve existing duplicate-detection behavior within each descriptor type.
- Treat incoming descriptor-reference URIs as whole strings without splitting on "#", adding delimiter restrictions, introducing component trimming, or adding Unicode normalization. Preserve existing input validation and each consumer's existing normalization and comparisons.
- Distinct component pairs reconstructing exactly the same URI cannot create separate identities within a descriptor type. Spaces before "#" remain inside the whole string; `namespace #code` must not acquire a match to `namespace#code` through a separately compared namespace.

### 4. Correct runtime use of both IDs

- Reference resolution returns DescriptorId and DocumentId without an additional database round trip.
- Resource writes, query filters, hydration, and joins from stored descriptor references use DescriptorId.
- Document metadata, locking, concurrency, cache operations, and document-level RI operations continue using the appropriate DocumentId.
- Preserve existing RI calculation, matching, and maintenance for descriptor and regular-resource POSTs, descriptor references, document references, and descriptor-valued query preprocessing, including descriptors reached through document-reference identity paths. Adapt stored descriptor-reference joins to DescriptorId and reconstruct URI text wherever existing hashing or verification needs it, retaining each path's existing transformations and comparisons.
- Tests deliberately use unequal DescriptorId and DocumentId values to detect accidental interchange.

### 5. Descriptor change tracking

- Descriptor tracked-change records and their query/authorization paths use ResourceKeyId instead of string discriminators.
- Resource change-tracking joins resolve descriptor references through DescriptorId.
- Delete-history responses retain the correct descriptor type, namespace, and code value after the live descriptor and document rows are deleted.

### 6. Behavior preserved on both database engines

- PostgreSQL and SQL Server tests cover descriptor CRUD, referenced-descriptor deletion protection, resource writes and reads, descriptor query filters, namespace authorization, and cache materialization.
- An authorized descriptor POST matching an existing descriptor through RI preserves both IDs and applies the incoming Namespace and CodeValue. Changed components, including casing changes, receive normal update stamps and ETag changes even when descriptive fields are unchanged.
- Database equality alone does not establish a successful RI POST match. Preserve existing conflict behavior.
- Descriptor PUT retains its ordinal whole-URI identity guard. Different URI text, including case-only changes, returns 400 for immutable identity, with stored values and stamps unchanged.
- Distinct component pairs producing exactly the same URI pass the PUT identity guard; accepted component changes remain real updates under existing stamping and ETag rules.
- An unchanged descriptor body returns 200 without changing its ETag, stamps, or change-query state.
- Preserve existing authorization, descriptive-field update behavior, regular-resource identity-update rules, no-op detection, ETags, and change-version behavior.
- Regression coverage includes mixed-case descriptor references, identical URI values across descriptor types, and descriptor types with identical resource names across projects.
- Both providers have regression cases for distinct component pairs that reconstruct the same URI and for spaces immediately before "#" in references and descriptor-valued filters.
- Regression coverage verifies existing RI matching for repeated POSTs and document references whose identities contain descriptors, including mixed-case descriptor values reached through document-reference identity paths.
- Replacing stored URI reads with reconstruction preserves each existing path's comparison and normalization behavior, including RI hashing, PUT identity checks, and tombstone recreation detection.

### 7. Schema storage changes validated

- Regenerated DS 5.2 and extension fixtures demonstrate the new column types, keys, constraints, and indexes for both providers.
- Measured storage savings and performance benchmarks are not completion requirements.

### 8. Provisioning and scope documented

- Document that existing databases require deliberate reprovisioning. Validate on the currently supported PostgreSQL and SQL Server environments; no database-version upgrade is required by this story.
- Follow the repository's RelationalMappingVersion release cadence. For the current 8.1 release target, retain v3; do not add another mapping-version bump.
- Abstract-identity discriminator columns and behavior remain unchanged.
- Update affected backend-redesign documentation to reflect the separate DescriptorId and DocumentId roles, compact descriptor foreign keys, descriptor uniqueness by ResourceKeyId and reconstructed whole URI under existing provider collation behavior, URI reconstruction, and ResourceKeyId-based descriptor change tracking. Reconcile overlapping storage-reduction stories and design documents so subsequent natural-key work builds on this storage schema while retaining ownership of its equality and platform changes. Preserve the exclusion of abstract-identity discriminator replacement.
