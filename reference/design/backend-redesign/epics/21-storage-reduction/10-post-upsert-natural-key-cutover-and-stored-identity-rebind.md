---
jira: DMS-1452
jira_url: https://edfi.atlassian.net/browse/DMS-1452
epic: DMS-1402
---

# Story: Cut Over POST Upsert Detection and Rebind SQL Server Stored Identity

## Outcome

Remove referential IDs from POST target detection and make case-variant SQL Server POST-as-update
bind the existing stored identity before authorization and no-op detection.

## Design References

- [Natural-key resolution](../../design-docs/natural-key-resolution.md)
- [Transactions and concurrency](../../design-docs/transactions-and-concurrency.md)
- [Flattening and reconstitution](../../design-docs/flattening-reconstitution.md)
- [E21 dependency chain](EPIC.md#dependency-chain)

## Dependencies

- Depends on [DMS-1451 — resolver and Core contract cutover](09-natural-key-resolver-and-core-contract-cutover.md).
- Together with DMS-1453, this story blocks DMS-1454.

## Implementation Scope

- Replace the capture-predicate hash subselect with the inline natural-key
  RefKey/lowered-descriptor predicate on both write paths: statement 1 of the composite command and
  the first command of the ordered-segments path (`ResolveInOrderedSegmentsAsync`). Do not
  resequence that path; it captures before it resolves references today and keeps that order.
  Since DMS-1535 it is no longer a rare fallback. The path is taken, for example, when split
  Create/Update POST policies set `PostTargetAuthorizationBundles.RequiresOrderedSegments` (a branch
  whose result is owed right after capture, such as a not-permitted branch, or a create-new
  relationship result) or carry a stored custom view on the Update branch, or when any POST owes a
  deferred stored-ownership failure.
- Preserve POST action selection (DMS-1535) and ownership authorization (DMS-1060). The captured
  target picks the Create or Update branch (`PostTargetAuthorizationBundles.Select`,
  `PostTargetAction.For`), and an existing target is authorized as Update, including
  stored-ownership checks. The natural-key capture predicate must produce the same target-context
  shape the RI capture produces today, plus `ExistingDocument` for identities that are equal under
  the SQL Server identity collation but differ in casing. Today the referential ID hashes
  exact-case values (only descriptor values are folded), so such a case variant misses, resolves to
  `UpsertTargetAction.Create`, and selects the `CreateNew` branch; after this story it resolves to
  the existing document and selects the Update branch.
- Delete `RelationalWriteTargetRequest.Post.ReferentialId` and the resource-POST capture predicate
  builder (`BuildPostCaptureTargetPredicate`), which the natural-key capture replaces. Leave
  `ResolveForPostAsync` and the lookup service in place: the descriptor handler still calls them,
  and DMS-1454 removes them with the descriptor cutover.
- Bind target resolution from `DocumentInfo.DocumentIdentity` and compiled own-key probe metadata.
- On SQL Server, rebind merged root rows to stored identity before proposed-value authorization and
  no-op detection. Use the DMS-1443 schema comparer in the identity-stability guard
  (`RelationalWriteIdentityStability`, `object.Equals` today) so CI-equal identity values are
  "unchanged", and rebind them per column.
- Extend the same comparer and rebind to collection semantic keys: the merge match
  (`RelationalWriteNoProfileMerge` and the profile merge path, `ObjectValueArrayComparer` /
  presence-aware key today) compares local string semantic-key members with the schema comparer,
  keeps resolved-id equality for reference/descriptor members, and rebinds a comparer-equal but
  byte-different member to the stored row's value so the row keeps its `CollectionItemId` and hidden
  profile columns. Values the comparer does not consider equal keep delete + insert semantics.
- Update the write-flow design sketches to show the stored-identity rebind in the correct sequence.

## Acceptance Criteria

- Command-stream tests show unchanged round-trip counts; POST create remains two commands on the
  co-batched path, and the ordered-segments path keeps its current command count with the
  natural-key capture as its first command. The ordered-segments fixtures must actually reach
  `ResolveInOrderedSegmentsAsync`, through `RequiresOrderedSegments` or an Update-branch custom
  view, and assert it through the command-stream shape (the capture runs as its own first
  command).
- Resource POST target lookup has zero RI command classifications; the create stream classifies
  exactly one natural-key capture/lookup command (`WriteSessionCommandStreamScenarios` create-stream
  expectations move from RI = 1 to RI = 0, natural-key = 1) and the update stream keeps RI = 0.
- SQL Server case-variant POST tests prove HTTP 200, stored casing in the response, guarded no-op, no
  referrer rewrite, no key-change row, and no `ContentVersion` increment.
- SQL Server case-variant POST tests with differing Create and Update policies prove the request is
  authorized as Update: a client with only Create permission gets the Update denial (403), not a
  create or a 409, and ownership-based authorization is evaluated against the stored row.
- SQL Server case-variant PUT tests prove a casing-only identity change is not a key change and a
  mixed PUT cascades only the genuinely changed column.
- SQL Server collection tests prove a PUT whose item differs from the stored row only in the casing
  of a local string semantic-key member keeps the same `CollectionItemId`, serves the stored casing,
  is a guarded no-op when otherwise identical, and preserves hidden columns under a profile-scoped
  write.
- PostgreSQL behavior remains unchanged, including delete + insert for case-variant collection
  items.
- Zero-, one-, and multiple-match target lookup tests preserve the DMS-1449 invariant.
- Write suites pass and design sketches place stored-identity rebind before authorization, no-op
  detection, and writer DML.
