# Relational document property tests (DMS-1684, location 3)

This implements recommended location 3 from [DMS-1684](https://edfi.atlassian.net/browse/DMS-1684)
in the existing Backend.Plans NUnit unit-test project. It uses the centrally pinned CsCheck
5.0.1 version introduced for location 1, adding the package reference and lock-file entry.
The existing `focused-stable-key/positive/extension-child-collections` School schema fixture
is compiled through the normal relational model builder and read/write plan compilers for
both PostgreSQL and SQL Server. No database or Docker setup is required.

## Implemented coverage and oracles

- `DocumentReconstituterPropertyTests.cs`: exact expected API values, page/single-document
  equivalence, page metadata order, isolation from other documents' root/child rows, nested
  parent attachment, root and collection-aligned extensions, and projected program reference
  identities. Each sample checks a three-document page, each single-document entry point,
  and isolated pages. Page metadata must include all hydrated roots; the single-document
  entry point accepts the full page and must isolate the requested document.
  Reversed row arrival, descending stable IDs, and increasing gapped stored ordinals make
  arrival order, stable-ID order, and stored-ordinal order distinguishable. Source row buffers
  must remain unchanged.
- `RelationalWriteFlattenerPropertyTests.cs`: exact values in compiled column-binding order,
  root scalars, root extension values, address/period/aligned-extension/sponsor hierarchies,
  request ordinals and ordinal paths, semantic identities, resolved program FKs and propagated
  reference identity values. New roots retain unresolved root IDs; existing roots carry their
  supplied IDs. Every collection row must have a distinct unresolved persistence token, and
  children/aligned scopes must reuse the correct parent's token. Input JSON is unchanged.
- `RelationalDocumentPropertyScenarios.cs`: immutable semantic descriptions, a direct JSON
  document oracle, and an independently constructed set of hydrated rows with explicit
  fixture column names. Expected JSON is never produced by the flattener or reconstituter.
  Write assertions specify expected named column values and relationships independently.

These are separate read and write contracts, not a full write/read round trip. The read
harness supplies persistence-assigned stable IDs and hydrated reference identities. The
write harness uses the fixture's canonical root-level mirror for collection extensions
(`$._ext.sample.addresses[*]._ext.sample`); read output attaches extensions under the
corresponding base address. The write harness supplies a resolved-reference set through `Backend.Tests.Common`'s
`ResolvedReferenceSets` helper. Read output omits empty optional collections; write input
also exercises explicit empty arrays. The link-disabled reconstitution overload returns
resource values, before server metadata and response-boundary link processing.

## Bounds and distributions

There are 36 NUnit cases and 3,600 generated evaluations per successful focused run:

- Read: two dialects × three collection shapes × extensions absent/present × 100 samples.
- Write: the same matrix × new/existing root × 100 samples.

Empty shapes have no addresses. Mixed shapes have three to five addresses, force both
absent and present aligned extensions, and force one empty and one three-item period
collection; other period/sponsor counts range from zero to three. Dense shapes have three
to five addresses with three periods and three sponsors per address when extensions are
selected. Root extensions are explicitly absent or present. Shared program references
across parents exercise repeated natural keys; period payloads identify the owning parent
and document. Strings include ordinary text, Unicode, escaped characters and newlines.
All generated strings remain within the fixture's declared lengths. Document/stable/reference
IDs exceed Int32, and school IDs vary within the positive Int32 domain.

Sampling is single-threaded and uses no discard filters. Every evaluation and shrink attempt
builds fresh mutable JSON, row buffers, reference sets and flattener instances. Compiled plans
are reused within a fixture. Each test reports its shape/dialect/extension/root-state counts.

## Execution and replay

```bash
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit \
  --no-restore --filter 'Category=RelationalDocumentProperty' --logger 'console;verbosity=normal'

dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit --no-restore
```

The category runs in ordinary unfiltered execution as well. CsCheck reports the replay seed,
shrink counts and serialized immutable scenario alongside the NUnit assertion failure.
Retain the test parameters, source revision, framework version and full failure output.
Replay using `CsCheck_Seed` and the same test filter; seed meaning depends on an unchanged
generator and framework. Fixed `iter: 100` bounds execution and is not overridden by
`CsCheck_Iter`. Convert any real defect to a deterministic NUnit regression using the reduced
semantic scenario, not only the seed.

## Validation results

The focused run passed all 36 cases (3,600 generated evaluations) in 6.66 seconds of
reported test time, excluding build time. That iteration used `-p:RunAnalyzers=false`.
After formatting and restoring the mutations below, the unfiltered project run with normal
analyzers passed all 2,707 tests with zero failures or skips in 29 seconds. The recorded
sorting seed then passed both selected read cases against the restored, analyzer-built
assembly in 0.82 seconds using the replay command below. These are local measurements,
not CI runtime guarantees. CSharpier formatted all three new C# files; `git diff --check`
passed. Only the test package reference/lock file, new tests and this document are changed.

## Mutation checks and replay evidence

Two temporary production mutations verified the assertions against broken behavior:

| Mutation | Selected cases | Result |
| --- | --- | --- |
| Remove `rows.Sort` in `HydratedRowOrdering.EnsureOrdinalOrder` | Read fixture, dense collections, extensions absent, both dialects | Both failed with reversed address/period output. The first failure reported seed `4hRJiUA3q5H1`, five shrinks, 100 attempts. |
| Return `UnresolvedRootDocumentId.Instance` instead of the collection parent's token in `RelationalWriteFlattener.ResolveParentKeyPart` | Write fixture, mixed collections, both extension/root states and dialects | All eight failed on exact parent-token assertions. First reported seed: `6MbNR3N9f2B1`. |

The sorting failure reproduced with the mutation still applied:

```bash
CsCheck_Seed=4hRJiUA3q5H1 dotnet test \
  src/dms/backend/EdFi.DataManagementService.Backend.Plans.Tests.Unit \
  --no-restore --no-build \
  --filter 'FullyQualifiedName~Given_Generated_Hydrated_Documents&Name~dense&Name~False' \
  --logger 'console;verbosity=normal'
```

Both replay cases failed. One retained the exact reduced scenario and original seed; the
other shrank once further. The original reduced scenario was:

```json
{"SchoolId":34405229,"IdOffset":4016,"Text":"plain","Addresses":[{"Periods":3,"Sponsors":3,"Extension":true},{"Periods":3,"Sponsors":3,"Extension":true},{"Periods":3,"Sponsors":3,"Extension":true}]}
```

This is an observed reduced input, not a claim of global minimality. Extensions are disabled
by the test parameter in this replay, regardless of the generated per-address flags.
Mutation builds used `-p:RunAnalyzers=false`; all temporary production edits were restored
before final validation. No production defects were found; initial harness failures were
corrected to respect complete page metadata and the fixture's mirrored write paths.

## Limits and follow-up

This representative fixture covers the shapes requested for location 3. It does not claim
coverage of all schemas or all relational types. Follow-up properties can generate the
fixture's root-extension interventions and nested visits, descriptor references, nullable
scalars, date/time/decimal normalization, key unification and reference-derived composite
identities. Each needs a fixture-specific semantic oracle and explicit resolved/hydrated
reference values; the existing bounded scenario pattern can be extended without new tooling.

A provider-backed round trip belongs in the PostgreSQL/SQL Server integration suites. It must
execute persistence to allocate IDs, resolve references against seeded target documents,
hydrate through the real reader, and compare resource values after documented normalization.
Server metadata and link emission need their own expectations. This adds database leases,
DDL provisioning and provider-specific runtime costs; it is not direct composition of the
two unit-test entry points.

Keep the bounded properties alongside existing examples. The main maintenance cost is the
explicit fixture-to-oracle mapping, which intentionally fails when fixture columns change.
No production code or `RelationalMappingVersion` change is required for these tests.
