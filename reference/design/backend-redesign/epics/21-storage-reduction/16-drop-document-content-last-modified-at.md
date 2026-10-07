---
jira: DMS-1401
jira_url: https://edfi.atlassian.net/browse/DMS-1401
epic: DMS-1402
---

# Story: Drop the denormalized ContentLastModifiedAt from dms.Document

## Outcome

Keep `ContentVersion` in `dms.Document`, remove `ContentLastModifiedAt` from that table, and make
the existing timestamp on each resource root authoritative. For descriptors, the authoritative
timestamp remains on `dms.Descriptor`.

This preserves `dms.Document` as the universal lock and version-check table while reducing
duplicated storage. Concurrency-sensitive operations continue to use
`dms.Document.ContentVersion` without requiring a root-table probe solely for version checks.
Moving `ContentVersion` out of `dms.Document` is outside this story's scope.

## Dependencies and independent delivery

DMS-1401 has no prerequisite among the other DMS-1402 child stories. It must pass on PostgreSQL
and SQL Server against the existing descriptor and ReferentialIdentity implementation and may be
merged and closed independently.

This story does not depend on DMS-1403's creation-time changes, DMS-1404's compact descriptor IDs,
or the DMS-1443 through DMS-1456 natural-key/ReferentialIdentity removal workstream. It must not
require those stories' new keys, lookup contracts, schema invariants, or PostgreSQL version
upgrade. The fourteen-story release-atomicity requirement in DMS-1402 applies to that natural-key
workstream and does not include DMS-1401.

Shared DDL emitters, generated fixtures, and descriptor code may require merge coordination.
That overlap does not make the other stories prerequisites. DMS-1401 owns all changes needed to
keep its implementation and affected tooling working against the current schema.

The planned implementation sequence runs DMS-1401 after DMS-1404 on the same branch, allowing
one shared template rebuild exercise to validate both physical changes. This coordinates artifact
delivery without making DMS-1404 an implementation prerequisite. DMS-1404 retains ownership of
its descriptor-specific schema expectations; when those changes are present in the implementation
baseline, the combined package evidence must also demonstrate them. DMS-1401's timestamp-column
check alone does not establish that the compact descriptor schema is present.

## Implementation scope

- Remove `dms.Document.ContentLastModifiedAt` from generated PostgreSQL and SQL Server DDL.
  Retain the root/descriptor timestamp and the existing content-version columns.
- Update timestamp generation as well as readers. Generated triggers currently obtain both
  stamps from `dms.Document` and copy them to the root. Preserve transactional consistency between
  the document version, authoritative root timestamp, and resource representation after this
  ownership change.
- Preserve stamping for root changes, child inserts/updates/deletes, reference-identity cascades,
  and descriptors. No-op writes must retain existing stamp behavior. Preserve SQL Server trigger
  recursion guards and the seek/locking protections used to avoid concurrent-write deadlocks.
- Update response materialization, single-document lookup, page metadata, descriptor reads, cache
  source metadata, and projection materialization to obtain the timestamp from the appropriate
  resource root.
- Separate the metadata needs of read and write callers of the shared UUID lookup. PUT version
  checks and descriptor delete-target lookup must retain document-only access when no timestamp
  is needed. Do not introduce root joins or extra root queries solely to satisfy a shared result
  contract. Preserve GET-by-id's existing wrong-resource UUID behavior.
- Update representation restamping on both database engines. It currently updates both stamps on
  `dms.Document` and distributes them to resource roots; version and timestamp updates must remain
  consistent within the transaction.
- Keep projection enqueueing driven by changes to `dms.Document.ContentVersion`; removing the
  timestamp should not require changing that contract.
- Update affected model terminology, contracts, design documentation, generated fixtures, and
  tests so the timestamp is no longer described as a mirror of `dms.Document`.
- Update `eng/northridge/Copy-NorthridgeDataForward.ps1` and its tests. Its stamp-distribution
  check explicitly reads `dms.Document.ContentLastModifiedAt`; remove that document-column
  dependency while retaining checks that authoritative root and descriptor timestamps survive
  the copy unchanged. Verify carry-forward from an older dump into the new schema without
  requiring the other storage-reduction stories.
- Regenerate affected CDC inventories, SQL Server capture-column expectations, and fixtures from
  the new physical schema. Verify CDC bootstrap and Kafka streaming against the changed schema.
- Rebuild **Minimal and Populated** database-template packages on PostgreSQL and SQL Server.
  Both kinds contain the full generated physical schema. Use fresh databases provisioned from
  the changed DDL, the existing template workflows, and current supported engine versions. Cover
  every supported template Data Standard and its configured core/extension schema set; this does
  not require the PostgreSQL upgrade or DMS-1271's future bootstrap-restore implementation.
- Extend `eng/DatabaseTemplates/verify-template-restore.ps1` with provider-catalog assertions
  before the API probes. In both the source and restored databases, require `dms.Document` to
  exist, `ContentLastModifiedAt` to be absent, `ContentVersion` to remain, and the expected
  resource roots and `dms.Descriptor` to retain their timestamp columns. Preserve the existing
  schema, data, and serveability checks and Populated verification's `RequirePopulatedData`.
  Add regression coverage for pre-change and post-change physical schemas with the same `v3`
  fingerprint.
- Update affected fixed repository consumer package-version pins to verified post-change
  prereleases. Retain `DATABASE_TEMPLATE_PACKAGE` package ids and their Data Standard suffixes;
  those suffixes identify the Data Standard. Consumers using the changed runtime must resolve
  compatible rebuilt packages or databases freshly provisioned from the changed DDL.

## Read-cost and storage expectations

Some existing paths read only `dms.Document`, including UUID lookup and cache source metadata.
Ordinary hydration also has a separate document-metadata query. Access to the root is therefore
not automatically free: use resource-aware joins or batched reads and avoid a separate root query
per document in a page.

The expected saving is roughly eight bytes of raw timestamp payload per document, approximately
half of the raw payload of the two document stamp columns, not half of document-table or total
database storage. Alignment and page layout affect actual savings. The document version update
and root stamp maintenance remain necessary, so write-throughput improvement and negligible read
overhead remain hypotheses.

## Acceptance criteria

1. Fresh PostgreSQL and SQL Server schemas omit `dms.Document.ContentLastModifiedAt`, retain
   `dms.Document.ContentVersion`, and retain authoritative timestamps on resource roots and
   `dms.Descriptor`.
2. GET-by-id, GET-many, descriptor responses, and cached responses preserve existing last-modified
   metadata, ETag, and change-query semantics. Version, timestamp, and returned representation
   remain consistent under concurrent writes and cache materialization. Wrong-resource UUID
   handling remains unchanged.
3. Document locking and optimistic concurrency checks continue to use
   `dms.Document.ContentVersion`, without a new root probe solely for version checking. Targeted
   command-shape or round-trip assertions prove PUT version checks and descriptor delete-target
   lookup do not acquire a new timestamp-only root dependency.
4. Integration tests on both engines cover creation, root updates, child-only
   inserts/updates/deletes, identity cascades, descriptor changes, no-op writes, and representation
   restamping. Tests demonstrate that meaningful changes receive the correct stamps and no-op
   writes preserve them.
5. Projection enqueueing and cache freshness remain correct after normal writes and restamping.
   Concurrent-write tests retain coverage for SQL Server recursion and deadlock protections.
6. Generated fixtures and schema documentation match the new ownership model, and deployment
   instructions explicitly require reprovisioning affected databases and using rebuilt Minimal
   and Populated template packages on both engines.
7. The story compiles and passes its required PostgreSQL and SQL Server checks against the existing
   descriptor and ReferentialIdentity implementation, without implementation prerequisites from
   other DMS-1402 child stories.
8. Before story closure, fresh build-and-restore verification succeeds for Minimal and Populated
   packages on both engines for each supported template Data Standard (`5.2.0` and `6.1.0`:
   eight legs). Sources are freshly provisioned from the changed DDL and packages are restored
   into fresh verification databases. Record the producing commit, package id/version/hash,
   build run, and catalog, data, and API verification results for every artifact. Unpublished
   PR artifacts (`publish_package: false`) satisfy this evidence requirement; Populated
   verification retains `RequirePopulatedData`.
9. The shared restore verifier enforces the Document-column and root/descriptor timestamp
   requirements in criterion 1 against both source and restored catalogs before API probes.
   Regression coverage proves that a pre-change template with the same `v3` fingerprint fails
   the physical-column check and a post-change template passes. Hash equality, matching schema
   names, row counts, and successful reads alone do not satisfy physical-schema acceptance.
10. Any affected fixed repository consumer package-version pins select verified post-change
    prereleases before closure. Artifact verification records identify the exact package versions
    used; package ids retain their Data Standard suffixes.
11. When the implementation baseline includes DMS-1404, the combined rebuild evidence also
    demonstrates its compact descriptor primary key and foreign keys, separate DocumentId,
    ResourceKeyId-based descriptor typing and change tracking, and required URI storage/index
    shape. Use DMS-1404's descriptor-specific expectations. These conditional checks preserve
    DMS-1401's standalone acceptance against the descriptor implementation on its baseline.

## Release and deployment

This story targets Ed-Fi API v8.1. Per the repository release cadence, keep
`SchemaHashConstants.RelationalMappingVersion` at `v3` for changes landing before 8.1 ships. Do
not add another version bump within this release cycle. Because the effective schema hash does
not include generated DDL or mapping-set output, retaining `v3` means startup validation may not
detect databases provisioned before this physical change; those databases must be deliberately
reprovisioned.

Story closure requires the verified build/restore artifacts and affected prerelease consumer-pin
updates above. Stable v8.1 artifact production, publication, release-view promotion, and deployment
pin updates remain in the existing release workflow; closure does not wait for the release event.
Build from the final release commit and schema, including any subsequent mapping changes, and
repeat the physical-column restore gate for all eight legs before publication. Promotion uses
those verified artifacts. Where the final schema includes DMS-1404, retain its descriptor-specific
package verification as well.

Deployment must resolve the rebuilt packages, update fixed deployment pins to the verified release
versions, discard cached older artifacts, and deliberately reprovision affected databases before
serving traffic. A matching `v3` fingerprint does not establish physical compatibility.

## Implementation references

- `src/dms/backend/EdFi.DataManagementService.Backend.Ddl/RelationalModelDdlEmitter.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Ddl/CoreDdlEmitter.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend/RelationalDocumentUuidLookup.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend/RelationalWriteTargetLookupResolver.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend/DocumentCacheSourceMetadataReader.cs`
- `src/dms/backend/EdFi.DataManagementService.Backend.Plans/IPlanSqlDialect.cs`
- PostgreSQL and SQL Server `RepresentationRestampStore` implementations.
- `eng/northridge/Copy-NorthridgeDataForward.ps1` and `eng/northridge/tests`.
- CDC schema inventories and provider capture expectations.
- `eng/DatabaseTemplates/Template-Management.psm1` and `verify-template-restore.ps1`.
- Minimal and Populated template workflows and affected consumer package-version settings.

Scope and risks above are based on repository inspection; implementation, tests, and performance
measurements remain to be completed.
