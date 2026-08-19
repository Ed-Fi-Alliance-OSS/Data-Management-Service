---
jira: DMS-1451
jira_url: https://edfi.atlassian.net/browse/DMS-1451
epic: DMS-1402
---

# Story: Cut Over the Resolver, Core Contracts, and Raw Descriptor URI Handling

## Outcome

Atomically replace hash-based reference resolution with natural-key resolution and remove
referential IDs from resolver-facing contracts while preserving public API behavior.

## Design References

- [Natural-key resolution](../../design-docs/natural-key-resolution.md)
- [Test strategy](../../design-docs/natural-key-resolution.md#test-strategy)
- [E21 dependency chain](EPIC.md#dependency-chain)

## Dependencies

- Depends on [DMS-1446 — probe-based duplicate-identity and constraint diagnostics](04-probe-based-duplicate-identity-and-constraint-diagnostics.md).
- Depends on [DMS-1450 — natural-key resolver internal seam](08-natural-key-resolver-internal-seam.md).
- Blocks DMS-1452 and DMS-1453.

## Implementation Scope

- Compose `NaturalKeyReferenceResolver` in `Add{Postgresql,Mssql}ReferenceResolver()` and delete the
  previous provider lookup builders/strategies, result reader, corruption canary, and superseded test
  suites. Re-point composite-seam consumers to the new factory.
- Remove referential IDs from resolver-facing references, failures, lookup requests/results, resolved
  maps, and replay snapshots. Re-point `ResolvedReferenceSet.DocumentReferences` and its consumers
  (flattener, reference-lookup verification support, replay snapshots) to the DMS-1450
  `IResolvedDocumentReferenceMap`; no raw dictionary keyed by `ReferenceLookupKey` or `DocumentIdentity`
  remains on a public write-pipeline contract.
- Stop application-side descriptor URI lowercasing. Carry the raw validated URI and delegate folding
  and equality to the configured database expression/index and collation. Do not introduce Unicode
  normalization such as NFC, NFD, NFKC, or NFKD. Every C# lowercasing site goes in this story (the
  set verified on the branch): Core `IdentityValueCanonicalizer` (descriptor-valued identity members
  of regular resources), `DescriptorExtractor` (descriptor reference URI), and `DescriptorDocument`
  (the descriptor resource's own `namespace#codeValue` identity value); backend
  `RelationalQueryRequestPreprocessor` (query filter), `ReferenceResolver` (`NormalizedUri`),
  `ReferenceLookupVerificationSupport` (canary), and `FlatteningResolvedReferenceLookupSet`
  (`DescriptorUriLookupKey`). The RI triggers' SQL `lower()` leaves with the triggers in DMS-1456.
  Removing the Core sites that still feed active UUIDv5 hashes is deliberate — see "Known Transient
  Trunk State" below.
- Extend the write-session command-stream classifier (`WriteSessionCommandStreamScenarios` and the
  provider `WriteSessionCommandStreamTests`) with a natural-key lookup/capture classification that
  recognizes the new statement shapes (`unnest(` / `OPENJSON(` lookups and the own-natural-key
  capture subselect) alongside the existing `dms."ReferentialIdentity"` text match, so the round-trip
  pins keep asserting what each command is, not only how many there are. After this story the create
  stream classifies as one RI command (the capture subselect, until DMS-1452) that also carries the
  embedded natural-key lookup.
- Move descriptor query preprocessing to compiled metadata, add path-attributed malformed-value 400
  responses before resolver calls, and retain empty-page behavior for valid missing or wrong-type
  descriptor URIs.
- Move reference-array duplicate validation from referential IDs to the schema-derived structural
  comparer.

## Known Transient Trunk State

Removing application-side lowercasing here changes the input to Core's still-active UUIDv5 hashes
(`DocumentInfo.ReferentialId`, consumed by the RI-based POST capture predicate until DMS-1452, and
`DescriptorWriteRequest.ReferentialId`, consumed by descriptor RI writes until DMS-1454), while
`TR_<R>_ReferentialIdentity` triggers keep hashing descriptor URIs with SQL `lower()`. Between this
story and DMS-1452/DMS-1454, POST-as-update of a resource whose identity contains a mixed-case
descriptor URI, and case-variant descriptor re-POSTs, therefore miss the RI probe on trunk. This is
accepted: DMS-1443 through DMS-1456 ship in one release, so no deployed environment observes the
intermediate state. Do not "fix" it by re-adding lowercasing; DMS-1452 and DMS-1454 close it.

## Acceptance Criteria

- Resolver unit suites pass, including structurally identical `DocumentIdentity` arrays resolving to
  the same `DocumentId`.
- Descriptor query-filter tests cover exact and case-variant URIs, malformed URIs, nonexistent URIs,
  and wrong-type URIs, and pin the framework percent-decoding boundary: a percent-encoded non-ASCII
  URI (for example `%C3%A9`) resolves, `%00` returns the path-attributed 400, and a percent-encoded
  lone surrogate (`%ED%A0%80`) is treated as literal text and yields an empty page.
- Reference-array duplicate tests pass without referential IDs.
- A production-source scan finds no `ToLowerInvariant()`/`ToLower()` applied to a descriptor
  namespace, code value, or URI in Core or backend code.
- Command-stream pins classify the embedded natural-key lookup on both providers with unchanged
  round-trip counts.
- Reference-resolution-dependent integration tests pass on PostgreSQL and SQL Server using the new
  resolver.
- A full-pipeline E2E scenario POSTs a resource whose identity contains a descriptor URI (for
  example `programs` or `gradingPeriods`) twice with identical payloads and asserts 201 then 200. It
  is added here, may be marked as an expected transient failure only until DMS-1452 lands, and must
  pass unconditionally from DMS-1452 onward on both engines. It is tagged `@MssqlRepresentative`;
  without the tag the SQL Server E2E lane does not run it.
- Production composition contains no coexistence arm for the old hash resolver.
- If production-shaped performance evidence regresses, follow the documented capture-predicate
  contingency ladder; reverting composite write-path batching remains the last resort.
