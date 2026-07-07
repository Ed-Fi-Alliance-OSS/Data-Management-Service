---
jira: TBD
source_spike: DMS-1246
epic: TBD
related:
  - DMS-1245
---

# Story: Add Reusable Caller-Agnostic Document Materialization

## Description

Add a reusable service that materializes the caller-agnostic full resource document used by `dms.DocumentCache`.

The service must use the same relational reconstitution rules as GET/query response assembly, compute the
full-resource `_etag` and `_lastModifiedDate`, and produce the metadata needed by the cache row and CDC stream.
Readable-profile projection and `ResourceLinks:Enabled` response stripping happen after cache retrieval and are
not part of the cached shape.

## Dependencies

- Depends on relational read/reconstitution and update-tracking metadata semantics.
- Unblocks `18-03-async-projector-worker.md`, `18-04-initial-backfill-and-rebuild.md`,
  `18-05-cache-backed-read-path.md`, and `18-06-cdc-pre-delete-materialization.md`.
- Provides realistic `DocumentJson`, `Etag`, and `LastModifiedAt` source data for
  `17-cdc-kafka/04-message-contract-tests.md` and `17-cdc-kafka/05-e2e-kafka-scenarios.md`.

## Acceptance Criteria

- Materialization accepts a current `DocumentId` and mapping-set/resource context and returns:
  - `DocumentUuid`,
  - `ProjectName`,
  - `ResourceName`,
  - `ResourceVersion`,
  - `ContentVersion`,
  - `Etag`,
  - `LastModifiedAt`,
  - `DocumentJson`.
- `DocumentJson` is the caller-agnostic pre-profile full resource document.
- When link injection is compiled into the read plan, `DocumentJson` includes `link` subtrees.
- `DocumentJson` does not include authorization arrays, EdOrg hierarchy JSON, API client identity, or
  readable-profile-specific projection.
- `Etag` is the DMS API base64 `SHA-256` `_etag` for the full resource state, not a hex digest.
- `LastModifiedAt` is sourced from `dms.Document.ContentLastModifiedAt`.
- Materialization fails cleanly when the document no longer exists or cannot be reconstituted.
- Unit/integration coverage includes at least one nested-array resource and one reference with link data.

## Tasks

1. Define the materialization service contract and result type.
2. Reuse existing read-plan/reconstitution code instead of duplicating JSON assembly rules.
3. Add metadata lookup for `DocumentUuid`, resource names, resource version, `ContentVersion`, and
   `ContentLastModifiedAt`.
4. Compute or reuse the full-resource `_etag` using the update-tracking rules.
5. Add tests for shape, metadata, `_etag`, `_lastModifiedDate`, links, and absence of authorization/profile data.

## Out of Scope

- Projector queueing and scheduling.
- Kafka envelope shaping.
- Readable-profile response projection.
