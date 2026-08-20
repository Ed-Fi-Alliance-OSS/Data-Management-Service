---
jira: DMS-1444
jira_url: https://edfi.atlassian.net/browse/DMS-1444
epic: DMS-1402
---

# Story: Add the Document/Resource Invariant and Abstract ResourceKeyId

## Outcome

Represent the resource type alongside every abstract identity so natural-key resolution can
disambiguate concrete targets without treating resource metadata as part of the abstract identity.

## Design References

- [Natural-key resolution](../../design-docs/natural-key-resolution.md)
- [E21 dependency chain](EPIC.md#dependency-chain)

## Dependencies

- Depends on [DMS-1443 — SQL Server identity collation contract](01-sql-server-identity-collation-contract.md).
- This story blocks DMS-1445 and, together with DMS-1443 and DMS-1447, DMS-1448.

## Implementation Scope

- Add `UX_Document_DocumentId_ResourceKeyId` to `dms.Document`.
- Add `ResourceKeyId smallint NOT NULL` to each abstract identity table and union view.
- Replace each existing foreign key with the exact `FK_<Abstract>Identity_Document` name shape and
  `DocumentId`-only column list with
  `FK_<Abstract>Identity_DocumentResourceKey` on `(DocumentId, ResourceKeyId)`.
- Populate `ResourceKeyId` from abstract-identity maintenance triggers using a typed
  `AbstractIdentityMaintenance.ResourceKeyIdValue` literal paired with the existing diagnostic
  `DiscriminatorValue`.
- Treat `ResourceKeyId` as target-disambiguation metadata, never as an abstract identity member.

## Acceptance Criteria

- Golden DDL and manifest diffs contain the document candidate key, abstract `ResourceKeyId`
  column/view/trigger value, and composite foreign key.
- Golden DDL pins, for every abstract identity table, `UX_<Abstract>Identity_NK` over exactly the
  abstract identity fields in `abstractResources[A].identityJsonPaths` order and
  `UX_<Abstract>Identity_RefKey` over those same fields plus trailing `DocumentId`, with
  `ResourceKeyId` and `Discriminator` absent from both key definitions.
- Derived models, manifests, and generated DDL contain no abstract-identity foreign key whose name
  ends in `Identity_Document` after the composite replacement.
- Abstract identity-column consumers exclude `ResourceKeyId` and `Discriminator` from identity
  equality.
- Parity and corruption tests cover insert, delete, identity rename, provider casing behavior, and
  document/resource-key drift attempts.
- Concrete `ResourceKeyId` population comes from compile-time metadata on both providers.
