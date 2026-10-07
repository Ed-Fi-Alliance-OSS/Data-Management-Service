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
- Remove the physically stored Uri and its (Uri, Discriminator) uniqueness constraint. Enforce uniqueness by ResourceKeyId and the reconstructed, unlowered whole URI (Namespace + "#" + CodeValue), preserving the existing provider collation behavior. Use a PostgreSQL expression index and a SQL Server non-persisted computed Uri column with a unique index. Reconstruct URIs wherever needed, including existing hashing, identity verification, and response materialization.
- Use ResourceKeyId instead of string discriminators in descriptor tracked-change records and their query and authorization paths.
- Update both dialects, model derivation, DDL, compiled plans, reference-resolution contracts, flattening, query preprocessing, hydration, cache integration, change-tracking joins, and affected fixtures/tests.

Current runtime contracts sometimes call a descriptor DocumentId a DescriptorId. Make the distinction explicit throughout the affected code. Reference resolution can return both IDs in the existing probe; the PostgreSQL lookup already joins dms.Descriptor. Keep document metadata and concurrency operations tied to DocumentId.

DMS-1404 changes descriptor storage representation, not descriptor equality or resolution semantics. Preserve existing normalization, ReferentialIdentity matching, provider collation behavior, and POST/PUT behavior. Reconstruct whole URIs wherever previously stored URI values were consumed. The PostgreSQL upgrade and engine-owned descriptor equality contract remain outside this story; DMS-1404 has no dependency on DMS-1447. Natural-key resolution, new validation and Unicode alias behavior, and stored-wins descriptor semantics remain in their existing stories.

Abstract-identity discriminator replacement is explicitly out of scope. Existing abstract-identity discriminator columns, union-view discriminator outputs, and their authorization behavior remain unchanged. This exclusion does not exempt descriptor FK columns in those structures from the compact-ID conversion where applicable.

## Acceptance criteria

### 1. Compact descriptor identity

- dms.Descriptor has an independently generated int DescriptorId primary key, using its own allocation sequence or identity rather than narrowing the global DocumentId.
- DocumentId remains a unique, non-null FK to dms.Document.
- Every stored descriptor-reference column uses int and references DescriptorId, including descriptor references participating in composite keys or constraints.
- Updating an existing descriptor preserves both IDs.

### 2. Descriptor type and natural key

- Remove dms.Descriptor.Discriminator; use its existing ResourceKeyId to identify descriptor type.
- Enforce uniqueness by ResourceKeyId and the reconstructed, unlowered whole URI, preserving the current Uri column's provider collation behavior as specified in Answer 1.2. Do not introduce new folding rules or component-wise Namespace/CodeValue uniqueness.
- Preserve agreement between descriptor and owning document ResourceKeyId.
- Descriptor types from different projects remain distinguishable, including types with identical resource names. The same namespace/code value can belong to different descriptor types without conflating them.

### 3. Remove stored URI

- Remove the physically stored dms.Descriptor.Uri and the (Uri, Discriminator) unique constraint. A non-persisted computed Uri column is permitted on SQL Server for indexing.
- Construct descriptor URIs from Namespace + "#" + CodeValue wherever needed.
- Use a PostgreSQL unique expression index on ResourceKeyId and the unlowered reconstructed URI, and a SQL Server unique index on ResourceKeyId and the non-persisted computed Uri. Do not add UriLowered or persist a duplicate URI in the base row.
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
- Existing identity-update rules, no-op detection, ETags, and change-version behavior remain unchanged: descriptor POST remains request-wins, and PUT retains its ordinal whole-URI identity guard, as specified in Answer 2.2.
- Preserve existing RI-based descriptor and regular-resource POST matching, descriptor-reference resolution, document-reference resolution, and descriptor-valued query preprocessing, including current normalization and mixed-case behavior. New engine-owned equality and expanded alias matching are deferred to the natural-key cutover, as specified in Answer 2.1.
- Regression coverage includes mixed-case descriptor references, identical URI values across descriptor types, and descriptor types with identical resource names across projects.
- Both providers have regression cases for distinct component pairs that reconstruct the same URI and for spaces immediately before "#" in references and descriptor-valued filters. Replacing stored URI reads with reconstruction must preserve each existing path's comparison and normalization behavior, including RI hashing, PUT identity checks, and tombstone recreation detection. Do not split URI inputs or introduce a new shared engine-folded equality contract.

### 7. Schema storage changes validated

- Regenerated DS 5.2 and extension fixtures demonstrate the new column types, keys, constraints, and indexes.

### 8. Provisioning and scope documented

- Document that existing databases require deliberate reprovisioning. Validate on the currently supported PostgreSQL and SQL Server environments; no database-version upgrade is required by this story.
- Follow the repository's RelationalMappingVersion release cadence. For the current 8.1 release target, retain v3; do not add another mapping-version bump.
- Abstract-identity discriminator columns and behavior remain unchanged.
- Update affected backend-redesign documentation to reflect the separate DescriptorId and DocumentId roles, compact descriptor foreign keys, descriptor uniqueness by ResourceKeyId and reconstructed whole URI under existing provider collation behavior, URI reconstruction, and ResourceKeyId-based descriptor change tracking. Reconcile overlapping storage-reduction stories and design documents so subsequent natural-key work builds on this storage schema while retaining ownership of its equality and platform changes. Preserve the exclusion of abstract-identity discriminator replacement.

## Clarifying Questions and Answers

### Questions 1

1. Where does DMS-1404 sit relative to DMS-1443–DMS-1456: must implementation start from the completed natural-key/ReferentialIdentity-removal schema, or must it also support the current RI-based runtime? Which stories block it, and does it share their same-release atomicity requirement?

2. What provider-specific unique-index and lookup contract applies after removing stored `Uri`? How can this preserve existing behavior without importing the natural-key design's lowered-URI indexes and new collation contract?

3. How should raw descriptor-reference URIs be compared while preserving existing duplicate and lookup behavior? What is the contract for distinct component pairs that reconstruct the same `Namespace + "#" + CodeValue` URI, and for values whose component-wise comparisons could differ from whole-URI comparison (for example, SQL Server trailing spaces immediately before `#`)?

4. Does acceptance criterion 7 require measured storage and query/join performance evidence, or only regenerated schema fixtures? If measurements gate completion, what populated dataset, baseline, workloads, database engines, and regression thresholds should the implementation tasks use?

### Answers 1

1. DMS-1404 has no dependency on another story in this epic, including DMS-1447, and does not share the DMS-1443–DMS-1456 same-release atomicity requirement. Implement against the current RI-based runtime and supported database versions, limiting this story to descriptor storage changes and their required adaptations. Preserve existing normalization, RI matching and maintenance, provider collation behavior, and descriptor POST/PUT behavior. Natural-key resolution, RI removal, new validation and equality rules, PostgreSQL upgrades, and stored-wins semantics remain in their existing stories. Reconcile overlapping design documents so subsequent work builds on the descriptor storage schema established here.

2. Preserve the existing Uri uniqueness comparison while replacing Discriminator with ResourceKeyId as the descriptor-type key. PostgreSQL uses a unique expression index on `("ResourceKeyId", ("Namespace" || '#' || "CodeValue"))`. SQL Server uses a non-persisted computed `Uri AS ([Namespace] + N'#' + [CodeValue])` with a unique index on `(ResourceKeyId, Uri)`. In both providers, preserve the old Uri column's collation behavior in the reconstructed expression; do not adopt a new identity collation. The URI remains in the index but is no longer duplicated in the base row. Do not add `LOWER` to these uniqueness expressions, `UriLowered`, component-wise unique indexes, or separate lowered component columns. Keep existing RI-based lookups and their normalization. Where hashing, verification, or another existing path reads Uri, substitute the reconstructed whole URI and retain that path's existing transformations and comparisons. Return DescriptorId and DocumentId from the existing lookup without another round trip. This story does not introduce raw-URI natural-key probes or the future engine-owned folding contract.

3. Preserve descriptor identity as the complete `Namespace + "#" + CodeValue` string, scoped by descriptor type. Do not split incoming descriptor-reference URIs or introduce delimiter restrictions, component trimming, Unicode normalization, or new validation rules. Existing input validation and normalization remain in force. Distinct component pairs that reconstruct exactly the same URI cannot create separate identities within a descriptor type. Spaces immediately before `#` remain inside the whole string; `namespace #code` must not acquire a match to `namespace#code` through a separately compared namespace. Replace stored URI reads with reconstruction while preserving each consumer's existing behavior, including RI hashing, reference and query resolution, the ordinal PUT guard, and tombstone recreation detection. Include provider regression cases for delimiter collisions and spaces before `#`. The new uniqueness index preserves existing provider collation behavior; it does not establish a new shared equality contract across these paths.

4. Criterion 7 requires regenerated schema fixtures only. Its sole acceptance bullet names DS 5.2 and extension fixtures; the rationale explicitly distinguishes schema coverage from measured savings and calls performance improvements unestablished. Do not create a populated-data benchmark or numerical performance gate for this story: no dataset, baseline, workload, or threshold is specified, and the natural-key design's prototype measurements concern a different optimization. Generate and verify both providers' fixtures for compact descriptor columns, keys, constraints, and indexes, alongside the behavior and round-trip tests required elsewhere in this story. The heading “Schema storage changes validated” reflects that requirement; measured storage and query/join benefits remain unproven.

### Questions 2

1. DMS-1451 documents that removing current Core lowercasing breaks RI-based POST matching until later cutovers. What compatibility boundary should DMS-1404 preserve for descriptor and regular-resource POST matching, descriptor references, and document references whose identities contain descriptors? Does this story introduce raw-URI probes or expanded engine-equivalent alias matching?

2. Acceptance criterion 6 preserves existing identity-update, no-op, ETag, and change-version behavior, but `natural-key-resolution.md` and DMS-1454 deliberately change descriptor behavior: case-variant POST preserves stored identity instead of rewriting it, and case-only PUT becomes 200/no-op instead of 400. Which behavior must DMS-1404 deliver, including casing and delimiter-collision component pairs? Does database equality alone make an alias a successful POST match? Specify which component values remain stored and whether stamps change when all descriptive fields are unchanged.

3. The natural-key design's `pg_c_utf8` contract needs PostgreSQL 17+ and UTF-8, but this branch still pins PostgreSQL 16 in CI and compose. Does the descriptor storage change require that contract or a dependency on DMS-1447? Identify the delivery and validation boundary.

### Answers 2

1. Preserve existing RI-based resolution throughout. Keep current normalization and RI calculation, matching, and maintenance for descriptor and regular-resource POSTs, descriptor references, document references, and descriptor-valued query preprocessing, including descriptors reached through document-reference identity paths. Adapt joins from stored descriptor references to DescriptorId, keep document-level RI operations tied to DocumentId, and reconstruct URI text wherever existing hashing or verification needs it. Return both IDs from the existing lookup without another round trip. Do not add separate raw-URI descriptor probes or metadata-derived natural-key resolution here. Preserve existing mixed-case behavior with regression coverage. Expanded engine-equivalent alias matching belongs to the natural-key cutover; this story neither guarantees it nor accepts DMS-1451's transient broken POST state.

2. Preserve current descriptor POST, PUT, and no-op behavior; leave stored-wins semantics to DMS-1454. For an authorized POST matching an existing descriptor through the current RI path, retain both IDs and apply the incoming Namespace and CodeValue, including casing and distinct component pairs reconstructing the same URI. Changes to stored components remain real updates under existing stamping and ETag rules even when descriptive fields are unchanged. Database equality alone does not establish a successful RI POST match; do not add alias handling or change existing conflict behavior. PUT retains the current ordinal whole-URI identity guard: a case-only change or other alias with different URI text remains a 400 immutable-identity rejection, with stored values and stamps unchanged. Distinct component pairs producing exactly the same URI pass that guard; accepted component changes are real updates under existing rules, not no-ops. An unchanged body remains a 200 no-op with unchanged ETag, stamps, and change-query state. Preserve existing authorization and descriptive-field update behavior.

3. DMS-1404 does not depend on DMS-1447 and does not require `pg_c_utf8` or a PostgreSQL upgrade. Compact DescriptorId values, ResourceKeyId-based descriptor typing, and unlowered whole-URI expression/computed-column indexes can be delivered on the currently supported database versions. Validate regenerated fixtures and existing descriptor behavior on the current PostgreSQL and SQL Server environments. Leave new folding rules, explicit identity collations, expanded Unicode alias behavior, platform images and clients, version/encoding guards, template rebuild/publication, and upgrade/REINDEX work to their existing stories. DMS-1447 and the natural-key stories must later adapt their changes to the compact descriptor schema and reconstructed URI established here. Document deliberate reprovisioning for this storage change and retain RelationalMappingVersion v3.
