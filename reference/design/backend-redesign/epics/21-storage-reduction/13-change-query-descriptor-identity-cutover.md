---
jira: DMS-1455
jira_url: https://edfi.atlassian.net/browse/DMS-1455
epic: DMS-1402
---

# Story: Cut Over Change Query Descriptor Identity Resolution

## Outcome

Make Change Query recreated-row detection resolve descriptor identity from the live descriptor table
using the same provider equality contract as the natural-key resolver.

## Design References

- [Natural-key resolution](../../design-docs/natural-key-resolution.md)
- [Change queries](../../design-docs/change-queries.md) (custom-view `ReadChanges` authorization)
- [E21 dependency chain](EPIC.md#dependency-chain)

## Dependencies

- Depends on [DMS-1445 — natural-key probe metadata](03-natural-key-probe-metadata.md).
- Depends on [DMS-1448 — descriptor validation, index, and FK foundations](06-descriptor-validation-index-and-fk-foundations.md).
- Depends on [DMS-1454 — descriptor write cutover and UUIDv5 cleanup](12-descriptor-write-cutover-and-uuidv5-cleanup.md):
  the fixture matrix below exercises the descriptor write/upsert, reference-resolution, and
  query-filter probe surfaces, not only Change Queries.
- The Change Query cutover work may start once DMS-1445 and DMS-1448 are complete; the story closes
  only after DMS-1454.
- Together with DMS-1454, this story blocks DMS-1456.

## Implementation Scope

- For descriptor `/deletes`, probe the live descriptor table by lowered URI plus the descriptor
  resource's compile-time `ResourceKeyId`.
- Use the same lookup for descriptor-valued identity joins in resource `/deletes`.
- Use the same descriptor identity contract in custom-view `ReadChanges` authorization
  (`TrackedChangeAuthorizationSqlEmitter`; DMS-1193 added custom views to it). It compares
  descriptor identity in four places, and all four move off exact `Namespace`/`CodeValue` equality:
  - the live-seek join (`ReadChangesCustomViewDescriptorKeyPair`) and the `DescriptorSeek` `EXISTS`
    predicate, which join live `dms.Descriptor` by the qualified `ResourceKeyId` plus exact
    `Namespace`/`CodeValue` equality. Move both to lowered URI while retaining the descriptor
    resource's compile-time `ResourceKeyId`. `ReadChangesCustomViewPlanner` resolves that `ResourceKeyId` from
    `MappingSet.ResourceKeyIdByResource` and carries it on `ReadChangesCustomViewDescriptorKeyPair`
    and `ReadChangesCustomViewBasis.DescriptorSeek`; the emitter renders it as a literal and does
    not take a `MappingSet`.
  - the live-seek tombstone probe arm (`BuildProbeArm`, `ReadChangesCustomViewProbeDescriptorKeyPair`)
    and the `DescriptorSeek` tombstone probe arm (`BuildDescriptorSeekPredicate`). Both compare old
    `Namespace`/`CodeValue` values stored on two tracked-change rows with a plain `=` and no
    `dms.Descriptor` join (the contract comment in `ReadChangesAuthorizationContracts.cs` and the
    `TrackedChangeAuthorizationSqlEmitterTests` pin record this). Compare the lowered
    `<namespace>#<codeValue>` of both sides under the same per-engine fold the live probes use
    (`lower(… COLLATE "pg_c_utf8")` on PostgreSQL, `LOWER` under the DMS identity collation on SQL
    Server), and update that contract comment and the pinning tests. The `DescriptorSeek` probe arm
    keeps `ResourceKeyId` as its routing predicate over the shared descriptor tracked-change table.
- Keep shared descriptor history routing by stored `ResourceKeyId` and the endpoint's compile-time
  qualified resource key, including tombstones whose live descriptor/document rows are gone.
  DMS-1404 already removed live/history descriptor discriminator storage and
  `IX_Descriptor_Discriminator_ContentVersion`; no removal or string-routing transition remains here.
- Resource identity joins compare compact descriptor FKs to live `DescriptorId`; custom-view
  membership bridges to owning `DocumentId`. Preserve document/UUID/component history snapshots
  and ResourceKeyId/DocumentId paging and ResourceKeyId/ContentVersion/DocumentId live indexes.
- Preserve descriptor route, response, and authorization contracts.
- Own the cross-engine Unicode verdict fixture matrix (moved here from DMS-1447): live-database
  fixtures that record, per engine, both the collation verdict and the `OrdinalIgnoreCase` verdict
  for at minimum `ß`/`ss`, width-variant values, dotted `İ`/`i`, precomposed `é` vs `e` + combining
  acute, `Ǹ`/`ǹ`, unweighted supplementary characters (`A`/`A😀`, `A😀`/`A😁`), and the
  comparer-boundary candidates `ſ`/`s`, dotless `ı`/`i`, and Kelvin `K` (U+212A)/`k`. Companion
  fixtures prove uniqueness, reference/upsert resolution, stored-wins rebinding, and recreated-row
  detection follow the same per-engine verdicts.
- Own the focused SQL Server `Turkish_100_CS_AS` database-default live fixture, reusing the
  SchemaTools alternate-collation provisioning that DMS-1443 extends: write/upsert,
  reference-resolution, query-filter, descriptor-valued identity, Change Query recreated-row, and
  custom-view `ReadChanges` descriptor-seek probes must resolve an existing `I`-bearing descriptor
  through `UX_Descriptor_UriLowered_ResourceKeyId` rather than missing and attempting a duplicate
  insert (unqualified `LOWER(N'I')` under that default yields dotless `ı`).

## Acceptance Criteria

- The SQL pinned by `TrackedChangeQueryPlannerTests` and `TrackedChangeAuthorizationSqlEmitterTests`
  contains no live-descriptor `Discriminator` predicate, including the custom-view `ReadChanges`
  authorization SQL emitted by `TrackedChangeAuthorizationSqlEmitter`.
- Custom-view `ReadChanges` authorization keeps the verdicts it has after DMS-1443, with these
  changes. On PostgreSQL, where all four comparisons are exact today, descriptor values that are
  equal after `pg_c_utf8` lowercasing now match. On SQL Server, DMS-1443's identity collation
  already makes all four comparisons case-insensitive, so verdicts stay as DMS-1443 left them,
  plus the pairs where `LOWER` and the collation's comparison weights disagree (dotted `İ`/`i`),
  which now match. The criterion holds for both basis kinds, each with a live basis row and with a
  deleted basis row reached through its tombstone probe arm:
  - a descriptor basis (`DescriptorSeek`);
  - a resource basis whose identity includes a descriptor (live seek).
- Derived index inventories, manifests, and generated DDL contain no live-descriptor
  `IX_Descriptor_Discriminator_ContentVersion` index.
- Every SQL Server descriptor probe applies the explicit identity collation to its input inside
  `LOWER`.
- Every PostgreSQL descriptor probe lowers both the live reconstructed
  `Namespace || '#' || CodeValue` and any tombstoned
  `<namespace>#<codeValue>` expression under `COLLATE "pg_c_utf8"`, never an unqualified `lower()`.
- Under the `Latin1_General_100_CS_AS_SC_UTF8` SQL Server database default (reusing the
  alternate-collation provisioning), a descriptor deleted and recreated with only casing changed is
  suppressed without a collation-conflict error (the descriptor counterpart of DMS-1443's pin), and
  so is one recreated with dotted `İ` for `i`, which only the `LOWER`-based probe equates.
- An equal descriptor recreation suppresses the old tombstone on both providers, including
  case-only recreation and SQL Server aliases accepted by the configured collation.
- The same suppression behavior applies to descriptor-valued resource `/deletes` identity joins.
- The same URI under another `ResourceKeyId` does not suppress the tombstone.
- Unequal-ID and deleted-owner fixtures retain ResourceKeyId routing and compact resource joins
  for identically named descriptor types in different projects. Abstract discriminators and their
  authorization behavior remain unchanged.
- Descriptor route, response, and authorization behavior remains unchanged, except for the
  custom-view verdict changes and the `/deletes` tombstone suppression changes above.
- The engine-divergence fixture matrix pins both verdicts for every listed pair on both engines; a
  comparer-looser pair, if one ever appears, surfaces as a fixture diff rather than a production
  discovery.
- Under a `Turkish_100_CS_AS` SQL Server database default, every descriptor probe surface resolves
  the existing `I`-bearing descriptor through the computed-column index (no duplicate insert, no
  unique violation) and recreated-row suppression holds.
