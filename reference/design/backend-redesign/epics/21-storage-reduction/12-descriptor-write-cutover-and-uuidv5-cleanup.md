---
jira: DMS-1454
jira_url: https://edfi.atlassian.net/browse/DMS-1454
epic: DMS-1402
---

# Story: Cut Over Descriptor Writes and Remove Core UUIDv5 Contracts

## Outcome

Move descriptor writes to natural-key probes, remove the last document-level referential-ID
consumers, and establish the re-provision rollback boundary.

## Design References

- [Natural-key resolution](../../design-docs/natural-key-resolution.md)
- [E21 dependency chain](EPIC.md#dependency-chain)

## Dependencies

- Depends on [DMS-1452 — POST upsert cutover and stored-identity rebind](10-post-upsert-natural-key-cutover-and-stored-identity-rebind.md).
- Depends on [DMS-1453 — collection duplicate detection and conflict fallback](11-collection-duplicate-detection-and-conflict-fallback.md).
- Blocks DMS-1455 (its cross-engine fixture matrix and `Turkish_100_CS_AS` live fixture exercise the
  descriptor write/upsert path delivered here).
- Together with DMS-1455, this story blocks DMS-1456.

## Implementation Scope

- Replace descriptor upsert detection with lowered-URI + `ResourceKeyId` probes.
- Implement stored-wins descriptor identity for descriptor writes, including persisted-identity
  binding, the split no-op comparer, and the provider-authoritative PUT identity guard.
- Remove `DescriptorWriteRequest.ReferentialId` and stop writing `dms.ReferentialIdentity` from the
  descriptor handler.
- Delete `DocumentInfo.ReferentialId`, `SuperclassIdentity.ReferentialId`, `ReferentialId`,
  `ReferentialIdFactory`, `ReferentialIdCalculator`, `No.ReferentialId`, Core extraction-time
  referential-ID calculation, and the UUIDv5 package dependency if it has no remaining consumers
  (DMS-1451 removes the resolver-facing members; this story removes the remaining Core carriers and
  the type itself).
- Update every production and test compile-time consumer affected by these contract/type removals.
  DMS-1456 fixture cleanup must not be needed to make the solution compile or these suites pass.

## Acceptance Criteria

- Descriptor write and stamping suites pass.
- SQL Server write/upsert SQL applies the explicit identity collation to each URI input inside
  `LOWER`.
- Provider stored-wins tests include SQL Server identity aliases accepted by the configured collation.
- The four `DescriptorCaseInsensitiveValidation.feature` E2E scenarios (the ODS-derived casing
  artifact) are tagged `@MssqlRepresentative` and pass on the SQL Server lane; today they carry only
  `@e2e-ci-shard-2` and never run against SQL Server.
- Targeted source scans over Core models, extraction/middleware, resolver/query contracts, and write
  request contracts find no `ReferentialId`, `ReferentialIds`, `ReferentialIdFactory`, or
  `ReferentialIdCalculator`.
- Remaining referential-ID text is confined to the combined DMS-1456 fixture/schema-removal lane.
- The story documents that descriptor RI writes stop here. Reverting DMS-1451 or later after this point
  requires re-provisioning or an approved backfill rather than assuming RI rows are current.
