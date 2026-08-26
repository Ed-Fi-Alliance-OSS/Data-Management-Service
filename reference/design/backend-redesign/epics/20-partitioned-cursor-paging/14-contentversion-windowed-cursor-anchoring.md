---
jira: DMS-1394
epic: DMS-1348
status: implemented
related:
  - DMS-1298
---

# Story: ContentVersion-Anchored Windowed Cursor Paging

## Outcome

For max-bearing change-version windows, anchor cursor pages, partition boundaries, and
continuation tokens on `ContentVersion` instead of `DocumentId`. A max-bearing window has
`maxChangeVersion`, with or without `minChangeVersion`.

This change turns windowed cursor walks and partitions into `ContentVersion` index range seeks.
It removes the walk-entry dead run, makes page cost depend on page size rather than window
position, and allows `Next-Page-Token` on `ContentVersion`-ordered pages.

Min-only and unfiltered walks remain anchored on `DocumentId`.

## Design References

- [`Page-selection ordering`](../../design-docs/change-queries.md#page-selection-ordering)
- [`Consistency Under Writes`](../../design-docs/partitioned-cursor-paging.md#consistency-under-writes)

## Dependencies

- Hard dependencies: DMS-1386 for cursor execution and DMS-1387 for the partition pipeline.
- DMS-1298 supplies conditional change-window ordering, including the `PageOrderingMode` signal
  and `ContentVersion` ordering SQL.

This is post-epic follow-up work. Evaluate it against the evidence recorded by the DMS-1348
acceptance gates instead of incorporating it into those gates retroactively.

## Implementation Scope

- **Match traditional page selection.** Use `ContentVersion` anchors exactly when traditional
  paging uses `ContentVersion` ordering: when `maxChangeVersion` is present.
- **Keep min-only walks on `DocumentId`.** In an open-ended min-only window, an update moves a
  row past the current `ContentVersion` anchor while the row remains eligible. The walk could
  then return that row twice. A stable `DocumentId` anchor avoids this duplication.
- **Treat max-bearing windows as monotonic-escape windows.** An update advances the row beyond
  the maximum, so the row leaves the window instead of moving past the anchor within it.
- **Assume `ContentVersion` is unique.** Anchors and tokens carry one `ContentVersion`, not a
  `(ContentVersion, DocumentId)` pair. The shared sequence assigns distinct values, including
  for multi-row writes. If uniqueness is violated, a token boundary can skip a row. Enforcing
  uniqueness in the schema remains deferred.
- **Record the ordering mode in each token.** Tokens do not store the request filters. The server
  infers anchor semantics from the filters that clients repeat on each request. Without an
  ordering marker, changing `maxChangeVersion` mid-walk could reinterpret a `ContentVersion`
  anchor as a `DocumentId`. Reject a marker/filter mismatch with the standard invalid-token
  response.
- **Balance windowed partitions by `ContentVersion`.** For a max-bearing `/partitions` request,
  calculate balanced `ContentVersion` subranges across the filtered and authorized candidate
  set. Include the ordering-mode marker in every partition token.
- **Align indexes with runtime predicates.** Resource tables use their `ContentVersion` index.
  Descriptor queries filter by the authoritative `ResourceKeyId` and use
  `(ResourceKeyId, ContentVersion, DocumentId)`, not `(Discriminator, ContentVersion)`.

- For max-bearing cursor walks, seek `ContentVersion > @anchor`, retain the window maximum,
  order by `ContentVersion`, and apply the page-size limit. Compose authorization exactly as the
  `DocumentId` cursor query does.
- Extend the token codec with an ordering-mode marker and `ContentVersion` bound encoding.
  Reject requests whose filters do not match the token's ordering mode.
- Calculate max-bearing partition boundaries over `ContentVersion`.
- Emit `Next-Page-Token` for `ContentVersion`-ordered pages. Use the page's highest selected
  `ContentVersion` as the next anchor.
- Do not change hydration, within-page `DocumentId` ordering, or Total-Count behavior.

## Acceptance Evidence and Test Expectations

- Token round-trip tests cover the ordering marker and reject mismatches in both directions:
  replaying a windowed token without `maxChangeVersion`, and replaying a `DocumentId` token with
  `maxChangeVersion`.
- PostgreSQL and SQL Server integration tests show that a bounded cursor walk returns every
  member of a stable fixture exactly once. Include concurrent updates that move rows beyond the
  window maximum.
- Min-only integration tests retain `DocumentId` anchors and return no duplicates during
  mid-walk updates.
- Windowed partition boundaries are balanced by `ContentVersion`; together they cover the
  window without overlap.
- Query plans on both providers show that an upper-tail first page uses the appropriate
  `ContentVersion` index without a dead-run scan. Include both a regular resource and a
  descriptor resource.

## Cross-Provider and Authorization Responsibilities

- PostgreSQL and SQL Server use the same ordering-mode, token, cursor, and partition semantics.
- Regular resources and descriptors expose equivalent cursor and partition behavior while using
  provider-appropriate SQL and indexes.
- Existing authorization determines the candidate set before cursor anchors or partition
  boundaries are calculated. This story introduces no new authorization strategy or public
  authorization contract.
- Integration and plan tests cover authorized regular-resource and descriptor requests on both
  providers.

## Explicit Exclusions / Not Assigned

- Snapshot data sources, which are deferred beyond DMS v1.0.
- A schema constraint that enforces `ContentVersion` uniqueness.
- Any change to unfiltered or min-only cursor behavior.
