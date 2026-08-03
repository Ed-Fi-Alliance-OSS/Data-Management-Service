---
jira: TBD
source_spike: DMS-1349
epic: DMS-1348
status: proposed
---

# E20-S07: OpenAPI and Client Contract

## Outcome

Publish the approved cursor parameters, conditional response header, and partition operations in
core, extension, descriptor, and profile OpenAPI documents without per-resource duplication.

## Design References

- [`Public API Contract`](EPIC.md#public-api-contract)
- [`OpenAPI assembly`](EPIC.md#openapi-assembly)
- [`Configuration`](EPIC.md#configuration)
- [`Non-Goals`](EPIC.md#non-goals)

## Dependencies

- Hard dependencies: E20-S00a for the approved public parameter, response, and runtime-default
  contracts; E20-S04 and E20-S05 for regular-resource and descriptor cursor execution; and
  E20-S06 for the active partition pipeline. No cursor parameter, response header, or partition
  path may be published before its runtime behavior is available.
- E20-S00b and E20-S01 route integration are consumed transitively through E20-S06.
- E20-S08b consumes the published contract for API/E2E parity coverage.

## Implementation Scope

- Augment eligible collection GET operations after fragment merge with `pageToken`, `pageSize`,
  and `Next-Page-Token` metadata.
- Generate sibling partition GET operations for core, extension, and descriptor collections only
  when E20-S06 activates the runtime route, with partition-specific summaries/descriptions and
  `application/json` token responses.
- Append `Partitions` to the exact base collection `operationId`, preserving extension prefixes.
- Publish runtime `MaximumPageSize` as both the default and maximum for `limit` and `pageSize`;
  publish the runtime partition-count default.
- Copy only eligible resource/change-version filters, security, tags, and domain metadata.
- Associate profile partition paths explicitly with readable base resources without rewriting the
  token response to a profile media type.

The client-facing documentation update is a separable delivery slice of this story. It may ship as
its own increment after the OpenAPI slice, and it must not gate OpenAPI acceptance:

- Update DMS client-facing paging documentation and examples for starting a cursor walk from a
  traditional response, consuming partition tokens, preserving filters, and terminal empty pages.
- Document that a partition response may contain fewer tokens than requested and never more, and
  that tokens are opaque and non-portable.

## Acceptance Evidence and Test Expectations

- OpenAPI unit/snapshot tests cover core, extension, descriptor, excluded-domain, and
  readable/write-only profile documents.
- Tests assert exact `operationId`, summary, description, parameter references/defaults, response
  header, `application/json` schema, tags, and security.
- Negative tests prove no augmentation of item-by-id, `/deletes`, `/keyChanges`, discovery, or
  management paths and no introduction of composite paths.
- Sequencing tests or review evidence prove cursor parameters and `Next-Page-Token` metadata are
  absent before E20-S04/E20-S05 and `/partitions` paths are absent before E20-S06, then are
  published atomically with their active runtime operations.
- OpenAPI generator integration tests produce the same augmented contract as runtime assembly.
- For the separable documentation slice, review confirms examples use `pageSize`, repeat filters,
  and treat tokens as opaque without implying snapshot consistency. This evidence is tracked
  separately from the OpenAPI evidence above.

## Cross-Provider and Authorization Responsibilities

- OpenAPI is provider-independent and must not vary between PostgreSQL and SQL Server.
- Security metadata follows the base collection operation. Profile filtering retains partitions
  only for resources with readable content.

## Explicit Exclusions / Not Assigned

- Runtime cursor and partition execution belong to E20-S04 through E20-S06.
- SDK generation changes outside DMS and per-resource ApiSchema path duplication are not assigned.
- Descriptor operations are not added to resource-derived profile documents.
