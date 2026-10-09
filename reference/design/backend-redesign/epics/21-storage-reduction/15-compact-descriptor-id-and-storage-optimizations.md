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
- Update affected data-loading and carry-forward tooling, including
  `eng/northridge/Copy-NorthridgeDataForward.ps1` and its tests, for the compact descriptor schema.
  Legacy dumps carry descriptor DocumentIds in resource references and include the removed
  Discriminator and stored Uri columns. Stage the source shape, allocate independent compact IDs,
  remap affected stored descriptor references, and initialize and verify the new descriptor
  allocation sequence. Translate descriptor tracked-change type identity to ResourceKeyId using
  the source discriminator and provisioned resource-key catalog, including tombstones whose live
  descriptor/document rows are gone. Preserve document IDs, RI rows, stamps, and the existing copy
  integrity checks; do not narrow or reuse global DocumentIds as compact IDs. DMS-1401 owns the
  later removal of this tool's document-timestamp dependency.

Current runtime contracts sometimes call a descriptor DocumentId a DescriptorId. Make the distinction explicit throughout the affected code. The PostgreSQL reference lookup already joins dms.Descriptor, allowing it to return both IDs without another round trip.

DMS-1404 is independently deliverable against the current RI-based runtime and supported database versions. It has no dependency on another story in this epic, including DMS-1447, and does not share the DMS-1443–DMS-1456 same-release atomicity requirement. Preserve existing normalization, RI matching and maintenance, provider collation behavior, and descriptor POST/PUT behavior. Natural-key resolution, RI removal, new validation and equality rules, expanded Unicode alias matching, platform upgrades, and stored-wins descriptor semantics remain in their existing stories.

Abstract-identity discriminator replacement is explicitly out of scope. Existing abstract-identity discriminator columns, union-view discriminator outputs, and their authorization behavior remain unchanged. This exclusion does not exempt descriptor FK columns in those structures from the compact-ID conversion where applicable.

## Implementation order and handoff to DMS-1401

Implement and validate this story first with `dms.Document.ContentVersion` and
`dms.Document.ContentLastModifiedAt` still present. Adapt descriptor stamping, no-op guards,
restamping, cache paths, and UUID lookups only as required by the separate IDs and removed
descriptor columns; retain the current document-to-root/descriptor stamp ownership. Timestamp
ownership moves in DMS-1401.

The handoff is a working RI-based runtime on freshly provisioned compact-descriptor schemas on
both engines, regenerated fixtures, executable descriptor catalog assertions, and working affected
load/copy tooling. Exercise descriptor metadata, stamping, restamping, and cache operations with
unequal DescriptorId and DocumentId values before handing off. DMS-1401 must build on these ID
roles and retain this story's descriptor behavior and physical-schema checks.

DMS-1401 then removes the document timestamp and completes the combined template build/restore
matrix and affected consumer-pin updates. The shared package exercise is deferred; the runtime,
tooling, fixture, and fresh-schema checks needed to complete DMS-1404 are not. If DMS-1401 does
not follow immediately, DMS-1404 can still close after its checks, but template-backed deployment
requires compatible templates to be rebuilt and verified before use.

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
- Namespace authorization preserves descriptor resources' own `Namespace` and regular resources'
  root namespace values. Dereferencing a descriptor reference for NamespaceBased is deferred to a
  separate story and fails closed with a Security Configuration Error. The custom-view
  `DescriptorId` to owning `DocumentId` bridge remains in scope.
- An authorized descriptor POST matching an existing descriptor through RI preserves both IDs and applies the incoming Namespace and CodeValue. Changed components, including casing changes, receive normal update stamps and ETag changes even when descriptive fields are unchanged.
- Database equality alone does not establish a successful RI POST match. Preserve existing conflict behavior.
- Descriptor PUT retains its ordinal whole-URI identity guard. Different URI text, including case-only changes, returns 400 for immutable identity, with stored values and stamps unchanged.
- Distinct component pairs producing exactly the same URI pass the PUT identity guard; accepted component changes remain real updates under existing stamping and ETag rules.
- An unchanged POST returns HTTP 200 and an unchanged PUT returns HTTP 204; both preserve descriptor and document IDs, ETag, content stamps, and change-query state.
- Preserve existing authorization, descriptive-field update behavior, regular-resource identity-update rules, no-op detection, ETags, and change-version behavior.
- Regression coverage includes mixed-case descriptor references, identical URI values across descriptor types, and descriptor types with identical resource names across projects.
- Both providers have regression cases for distinct component pairs that reconstruct the same URI and for spaces immediately before "#" in references and descriptor-valued filters.
- Regression coverage verifies existing RI matching for repeated POSTs and document references whose identities contain descriptors, including mixed-case descriptor values reached through document-reference identity paths.
- Replacing stored URI reads with reconstruction preserves each existing path's comparison and normalization behavior, including RI hashing, PUT identity checks, and tombstone recreation detection.
- Descriptor stamping, representation restamping, and cache materialization continue to use the owning DocumentId for document stamps and the correct descriptor row when the two IDs differ. These checks pass with the document timestamp still present, before DMS-1401 changes timestamp ownership.

### 7. Schema storage changes validated

- Regenerated DS 5.2 and extension fixtures demonstrate the new column types, keys, constraints, and indexes for both providers.
- DMS-1404 owns the descriptor-specific physical-schema expectations used to verify combined template builds: the independently generated int DescriptorId primary key, unique non-null DocumentId FK, compact stored descriptor FKs, removal of descriptor Discriminator, required reconstructed-URI storage/index shape, and ResourceKeyId-based descriptor change tracking. When DMS-1401 follows this story, its shared package rebuild evidence must also demonstrate these expectations; timestamp-column absence alone does not prove the compact descriptor schema. These expectations apply to builds containing DMS-1404 and do not make compact descriptor IDs a prerequisite for DMS-1401's standalone acceptance.
- Provide reusable provider-catalog assertions for those descriptor expectations and exercise them against fresh DMS-1404 schemas on both engines while the document timestamp remains present. DMS-1401 reuses them against both source and restored catalogs in the combined template gate. Expected schema features come from the implementation baseline, not from detecting which features an older source or restored database happens to contain.
- Carry-forward regression coverage proves that a legacy dump loads into the compact schema with correctly remapped descriptor references, ResourceKeyId-based descriptor history, unchanged document IDs and stamps, valid foreign keys, and a descriptor allocation sequence ready for the next insert. Use representative legacy-dump fixtures with unequal descriptor and document IDs in the target. This descriptor conversion is complete before DMS-1401 removes the document timestamp; the full Northridge dataset run and shared template rebuilds are not DMS-1404 closure gates.
- Measured storage savings and performance benchmarks are not completion requirements.

### 8. Provisioning and scope documented

- Document that existing databases require deliberate reprovisioning. Validate on the currently supported PostgreSQL and SQL Server environments; no database-version upgrade is required by this story.
- Follow the repository's RelationalMappingVersion release cadence. For the current 8.1 release target, retain v3; do not add another mapping-version bump.
- For the planned DMS-1404 then DMS-1401 implementation sequence on this branch, DMS-1401 owns the shared Minimal and Populated template rebuilds, restore-verification matrix on both engines and supported template Data Standards, and affected consumer package-version updates for the combined schema. DMS-1401 also owns removal of dms.Document.ContentLastModifiedAt and its physical-column restore gate. This schedules artifact delivery without adding an implementation prerequisite to DMS-1404.
- DMS-1404 may close once its implementation, regenerated fixtures, required behavior checks, descriptor-specific schema expectations, and reprovisioning documentation are complete. Shared template build-and-restore evidence is required before DMS-1401 closes. Consumers must use databases freshly provisioned from DMS-1404's changed DDL or compatible rebuilt templates before running its changed runtime; defer template-backed deployment until the shared rebuilds succeed, or produce and verify compatible templates earlier if deployment is needed. Retaining v3 does not make an older physical schema compatible.
- Final v8.1 template production, publication, release-view promotion, and deployment package-version pins remain in the release workflow. Verify packages built from the final release schema against this story's descriptor expectations alongside DMS-1401's timestamp requirements when both changes are included.
- Abstract-identity discriminator columns and behavior remain unchanged.
- Update affected backend-redesign documentation to reflect the separate DescriptorId and DocumentId roles, compact descriptor foreign keys, descriptor uniqueness by ResourceKeyId and reconstructed whole URI under existing provider collation behavior, URI reconstruction, and ResourceKeyId-based descriptor change tracking. Reconcile overlapping storage-reduction stories and design documents so subsequent natural-key work builds on this storage schema while retaining ownership of its equality and platform changes. Preserve the exclusion of abstract-identity discriminator replacement.

## Implementation and verification coverage

The task IDs below refer to this story's repository implementation checklist. The coverage review
connects all eight acceptance sections to implementation and required verification:

| Acceptance section | Implementation tasks | Verification tasks |
|---|---|---|
| 1. Compact identity | 01, 03, 04, 06: native allocation, separate document association, Int32 stored/copy/unified bindings and two-ID resolution | 16 independent allocation/catalogs; 17–18 unequal-ID runtime; 20 authoritative DS/extension inventories |
| 2. Type and natural key | 01–02, 07: ResourceKeyId type, agreement enforcement, whole-URI uniqueness and CRUD | 16 provider collations/type drift/collisions; 17 cross-type/project conflict/CRUD; 19 history routing |
| 3. Removed stored URI | 02, 05, 07–09, 11–13: reconstructed URI in RI, writes, filters, reads, cache and history | 16 physical/session checks; 17–19 mixed-case, equal-whole-URI component pairs, delimiter spaces and retained comparisons |
| 4. Correct ID roles | 04, 06, 08–11, 13–14: resolver/replay, writes, filters, hydration, auth bridges, metadata/cache/restamp and seed helpers | 11 document-owned cache/restamp; 17–19 HTTP/backend unequal-ID scenarios; 20 sample smoke; 24 loader smoke |
| 5. Change tracking | 12–13: ResourceKeyId history, compact resource joins and document membership bridges | 19 deleted-owner type/UUID/components, recreation, namespace/custom-view authorization and exact old/new snapshots |
| 6. Preserved behavior | 05–11, 13–14: current RI, ordinal PUT, incoming-component POST, no-op/ETag/auth/cache contracts | 11 both-provider cache/CLI/restamp/CDC; 17 descriptor CRUD; 18 in-process API/backend resource/query/FK/authorization; 19 history; parity catalog |
| 7. Storage validation | 15–16, 20: focused/authoritative provider models, DDL/plans and reusable baseline-driven catalog assertions; 24–25 compatible loaders and copy-forward conversion | 16 fresh/old catalogs and independent sessions; 20 both-provider authoritative sample smoke; 24 both-provider Explicit loader smoke; 25 actual representative legacy-dump conversion |
| 8. Provisioning/scope | 21–23: schema/runtime/future-story docs, deliberate reprovisioning, v3 release cadence and DMS-1401 handoff | Generated-artifact/link/contract review; retained abstract discriminators; final acceptance/task review |

Required provider cases in tasks 11 and 16–19 and loader cases in task 24 must execute successfully
on both PostgreSQL and SQL Server; zero selected tests or skips do not satisfy those gates. Task 11
includes descriptor resource/UUID restamp scopes, cache-admin CLI seed consumers and the existing
PostgreSQL/SQL Server CDC representation-restamp fixtures, with both document stamps retained.
Task 18 uses the existing in-process API/backend suites as its single verification path, including
authorization row-set correctness; it adds no separate timing or measurement harness.

Task 25 executes the real representative legacy-dump conversion, covering every writable stored
reference once, deleted-row type/history conversion, preserved document/RI/stamp values and the
next native descriptor allocation. Tasks 16 and 25 consume the same implementation-generated
exact-schema relational manifest through `ExpectedModelManifestPath` and the shared inventory
reader. Task 22 includes profiles and partitioned cursor paging. The combined template matrix and
consumer pins remain DMS-1401 gates; full-dataset conversion, benchmarks, measured savings,
generic migration frameworks and AOT are outside this story's completion requirements.
