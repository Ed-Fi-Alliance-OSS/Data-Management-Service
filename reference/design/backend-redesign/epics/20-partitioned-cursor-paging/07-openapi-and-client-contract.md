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

- Hard dependency: E20-S00 for the approved public parameter, response, and runtime-default
  contracts.
- Soft dependencies: E20-S01 and E20-S06 for final route/operation integration.
- E20-S08 consumes the published contract for API/E2E parity coverage.

## Implementation Scope

- Augment eligible collection GET operations after fragment merge with `pageToken`, `pageSize`,
  and `Next-Page-Token` metadata.
- Generate sibling partition GET operations for core, extension, and descriptor collections with
  partition-specific summaries/descriptions and `application/json` token responses.
- Append `Partitions` to the exact base collection `operationId`, preserving extension prefixes.
- Publish runtime page-size and partition-count defaults and the page-size maximum.
- Copy only eligible resource/change-version filters, security, tags, and domain metadata.
- Associate profile partition paths explicitly with readable base resources without rewriting the
  token response to a profile media type.

## Acceptance Evidence and Test Expectations

- OpenAPI unit/snapshot tests cover core, extension, descriptor, excluded-domain, and
  readable/write-only profile documents.
- Tests assert exact `operationId`, summary, description, parameter references/defaults, response
  header, `application/json` schema, tags, and security.
- Negative tests prove no augmentation of item-by-id, composite, `/deletes`, `/keyChanges`,
  discovery, or management paths.
- OpenAPI generator integration tests produce the same augmented contract as runtime assembly.

## Cross-Provider and Authorization Responsibilities

- OpenAPI is provider-independent and must not vary between PostgreSQL and SQL Server.
- Security metadata follows the base collection operation. Profile filtering retains partitions
  only for resources with readable content.

## Explicit Exclusions / Not Assigned

- Runtime cursor and partition execution belong to E20-S04 through E20-S06.
- SDK generation changes outside DMS and per-resource ApiSchema path duplication are not assigned.
- Descriptor operations are not added to resource-derived profile documents.
