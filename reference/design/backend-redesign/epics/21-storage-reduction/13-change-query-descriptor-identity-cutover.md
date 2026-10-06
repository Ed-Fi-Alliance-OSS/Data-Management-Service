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
  (`TrackedChangeAuthorizationSqlEmitter`; DMS-1193 added custom views to it). It compares descriptor identity in
  four places, and all four move off exact `Namespace`/`CodeValue` equality:
  - the live-seek join (`ReadChangesCustomViewDescriptorKeyPair`) and the `DescriptorSeek` `EXISTS`
    predicate, which join live `dms.Descriptor` by `Discriminator IN (...)` plus exact
    `Namespace`/`CodeValue` equality. Move both to lowered URI plus the descriptor resource's
    compile-time `ResourceKeyId`. The planner resolves that `ResourceKeyId` from
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
    keeps `Discriminator` as its routing predicate over the shared descriptor tracked-change table.
- Keep shared-tombstone `Discriminator` as a routing predicate only.
- Remove the unused live `IX_Descriptor_Discriminator_ContentVersion` index.
- Preserve descriptor route, response, and authorization contracts.
- Own the cross-engine Unicode verdict fixture matrix (moved here from DMS-1447): live-database
  fixtures that record, per engine, both the collation verdict and the `OrdinalIgnoreCase` verdict
  for at minimum `ß`/`ss`, width-variant values, dotted `İ`/`i`, precomposed `é` vs `e` + combining
  acute, `Ǹ`/`ǹ`, unweighted supplementary characters (`A`/`A😀`, `A😀`/`A😁`), and the
  comparer-boundary candidates `ſ`/`s`, dotless `ı`/`i`, and Kelvin `K` (U+212A)/`k`. Companion
  fixtures prove uniqueness, reference/upsert resolution, stored-wins rebinding, and recreated-row
  detection follow the same per-engine verdicts.
- Own the focused SQL Server `Turkish_100_CS_AS` database-default live fixture, reusing DMS-1443's
  alternate-default provisioning: write/upsert, reference-resolution, query-filter, descriptor-valued
  identity, Change Query recreated-row, and custom-view `ReadChanges` descriptor-seek probes must
  resolve an existing `I`-bearing descriptor
  through `UX_Descriptor_UriLowered_ResourceKeyId` rather than missing and attempting a duplicate
  insert (unqualified `LOWER(N'I')` under that default yields dotless `ı`).

## Acceptance Criteria

- SQL snapshots contain no live-descriptor `Discriminator` predicate, including the custom-view
  `ReadChanges` authorization SQL emitted by `TrackedChangeAuthorizationSqlEmitter`.
- Custom-view `ReadChanges` authorization keeps its current verdicts, except that descriptor values
  differing only in casing now match on both providers. Today all four comparisons are
  case-sensitive on PostgreSQL, and on SQL Server under a case-sensitive database default. The
  criterion holds for both basis kinds, each with a live basis row and with a deleted basis row
  reached through its tombstone probe arm:
  - a descriptor basis (`DescriptorSeek`);
  - a resource basis whose identity includes a descriptor (live seek).
- Derived index inventories, manifests, and generated DDL contain no live-descriptor
  `IX_Descriptor_Discriminator_ContentVersion` index.
- Every SQL Server descriptor probe applies the explicit identity collation to its input inside
  `LOWER`.
- Every PostgreSQL descriptor probe lowers both the live `Uri` and any tombstoned
  `<namespace>#<codeValue>` expression under `COLLATE "pg_c_utf8"`, never an unqualified `lower()`.
- Under the `Latin1_General_100_CS_AS_SC_UTF8` SQL Server database default (reusing DMS-1443's
  provisioning), a descriptor deleted and recreated with only casing changed is suppressed without a
  collation-conflict error.
- An equal descriptor recreation suppresses the old tombstone on both providers, including
  case-only recreation and SQL Server aliases accepted by the configured collation.
- The same suppression behavior applies to descriptor-valued resource `/deletes` identity joins.
- The same URI under another `ResourceKeyId` does not suppress the tombstone.
- Descriptor route, response, and authorization behavior remains unchanged, except for the
  case-only custom-view matches above.
- The engine-divergence fixture matrix pins both verdicts for every listed pair on both engines; a
  comparer-looser pair, if one ever appears, surfaces as a fixture diff rather than a production
  discovery.
- Under a `Turkish_100_CS_AS` SQL Server database default, every descriptor probe surface resolves
  the existing `I`-bearing descriptor through the computed-column index (no duplicate insert, no
  unique violation) and recreated-row suppression holds.
