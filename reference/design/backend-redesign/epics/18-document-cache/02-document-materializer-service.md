---
jira: DMS-1312
source_spike: DMS-1246
epic: DMS-1308
related:
  - DMS-1245
---

# Story: Add Reusable Caller-Agnostic Document Materialization

## Design References

- **Cached document contract**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#cached-document-contract
- **Freshness and reconciliation**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#freshness-and-reconciliation
- **Topic and message contract**: reference/design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md

The referenced design sections define the materialized representation and coherence rules.
This story is only the work package for implementing them.

## Outcome

Add the reusable cache-projection materializer used by durable-work processing, optional
direct fill, and CDC fixtures.

## Dependencies

- Depends on 18-00 plus the relational read/reconstitution and update-tracking services.
- Unblocks 18-03 through 18-05 and supplies representative records to E19 tests.

## Implementation Scope

- Add the materializer interface, result model, and runtime implementation.
- Reuse compiled read plans, reconstitution, and the shared served-ETag composer.
- Add source-coherence and result-invariant validation at the materializer boundary.
- Accept a selected durable work item and materialize the latest coherent canonical source
  for its document; the selected worker-local version never overrides the final
  current-state classification.
- Add representative materialized-document fixtures for projection and CDC verification.

## Resolved Materializer Scope and Runtime Contract

### Component Boundary

- Add one reusable `IDocumentCacheMaterializer`-style application service plus small
  provider adapters needed to hydrate a single document by `DocumentId`. The exact type
  names may follow local conventions, but the boundary is one caller-agnostic materializer,
  not separate projector, direct-fill, and CDC materializers.
- The service is target-context scoped. It consumes the resolved connection, selected
  mapping set, effective-schema hash, resource-key lookup, compiled read/reconstitution
  plans, and shared served-ETag composer already established by earlier backend-redesign
  work.
- It does not resolve `DocumentCache:Targets`, validate SQL Server RCSI or `nested
  triggers`, inspect lifecycle state, page durable work, write `DocumentCache`, delete
  `DocumentProjectionWork`, set the cache-ahead latch, or shape Kafka envelopes. Those
  responsibilities remain in 18-01, 18-03, 18-04, 18-05, and E19.
- The materializer performs no request authorization and applies no readable profile. It is
  an internal projection of a document already selected by the caller's authorized read
  path, durable work row, baseline page, scrub/rebuild page, direct-fill path, or fixture.

### Inputs and Result Model

- Inputs are the resolved target context, `DocumentId`, optional selected durable-work
  `RequiredContentVersion`, materialization purpose for diagnostics, and cancellation token.
  The selected work version is only worker-local context. It never gates hydration and is
  not returned as current source evidence.
- A successful result returns the cache-row candidate fields owned by the cached-document
  contract:
  - `DocumentId`;
  - canonical `DocumentUuid`;
  - `ProjectName`, `ResourceName`, and `ResourceVersion` from the selected mapping/resource
    key;
  - current `ContentVersion`;
  - current `ContentLastModifiedAt` as cache `LastModifiedAt`;
  - `StreamEtag`; and
  - `DocumentJson` as a JSON object ready for `dms.DocumentCache`.
- The result does not include `ComputedAt`. Cache DML owns the provider timestamp used for
  insert/update operational metadata.
- Expected non-success results are limited to materializer-owned facts: source row missing,
  source changed during hydration, mapping/resource-plan unavailable, or invariant
  violation. Cancellation, transient database failures, and other provider/runtime failures
  may use the existing exception flow and are handled by the caller's retry/backoff policy;
  they are not converted into cache candidates.

### Source Read and Coherence

- Hydrate from the canonical relational source by `DocumentId`, not from
  `DocumentProjectionWork`, `DocumentCache`, `ContentVersion` scans, or public
  `DocumentUuid` lookup. The materializer may use the same compiled single-document
  hydration plan shape as GET-by-id after the document id has already been resolved.
- The first source observation reads `dms.Document` joined to immutable resource-key
  metadata. If no canonical row exists, return a missing-source outcome without attempting
  cache repair or work acknowledgement.
- Reconstitute the body without holding a work-row lock or deliberate write-conflicting
  source-row lock. Use ordinary target read semantics: PostgreSQL read-committed statement
  snapshots and SQL Server's target-validated RCSI path.
- After hydration, re-read the canonical source metadata for the same `DocumentId` and
  require it to still match the observed `DocumentUuid`, `ResourceKeyId`, `ContentVersion`,
  and `ContentLastModifiedAt`. A missing or different row returns source-changed/missing
  and produces no candidate. The later 18-03 writer still performs the authoritative
  source/cache/work classification and repeats all DML predicates.
- Do not compare current source state to the selected work item's
  `RequiredContentVersion` inside this service. If work is behind, ahead, absent, or has
  advanced since selection, 18-03's current source/cache/work statement classifies the
  relationship and either writes, acknowledges, leaves work pending, or latches cache-ahead
  according to the ADR.

### Representation Shape

- Build `DocumentJson` by reusing the compiled relational reconstitution path and
  `System.Text.Json`/`Utf8JsonWriter`. If the existing API materializer always emits
  `_etag`, factor the shared body/metadata writer so cache projection emits `id` and
  `_lastModifiedDate` but omits `_etag`; do not introduce a trigger-side JSON builder,
  provider-specific JSON composition query, post-parse string surgery, or `Newtonsoft.Json`
  dependency.
- The projection is the caller-agnostic full stored representation before readable-profile
  projection. It includes stable top-level `id`, `_lastModifiedDate`, and compiled reference
  `link` subtrees when the read plan emits them. It excludes API client identity,
  authorization arrays, EdOrg hierarchy payloads, and readable-profile-specific filtering.
- `DocumentJson` stored in `dms.DocumentCache` must not contain `_etag`. The materializer
  returns `StreamEtag` separately. E19's `DocumentState` transform injects that opaque value
  into the public `document._etag` field when shaping Kafka upsert values.
- Compose `StreamEtag` with the same served-ETag composer used by the API, using current
  `ContentVersion`, selected effective-schema hash/schema epoch, JSON format, no readable
  profile, identity content coding, and the fixed stream link mode. Ordinary resource
  projections use the link-bearing stream context; descriptor projections use the
  descriptor stream context defined by the message contract.
- Cache `LastModifiedAt` retains provider timestamp precision from `dms.Document`.
  `DocumentJson._lastModifiedDate` uses the existing whole-second UTC DMS formatter without
  rounding. Fractional precision remains database metadata, not public JSON text.

### Invariant Validation and Failure Handling

- Before returning a success result, validate:
  - `DocumentJson` is a JSON object;
  - `DocumentJson.id` exactly matches the canonical `DocumentUuid` string emitted by the API
    reconstitution path;
  - `DocumentJson._lastModifiedDate` exactly matches formatted `ContentLastModifiedAt`;
  - `DocumentJson` has no `_etag`;
  - `StreamEtag` equals the shared composer output for the fixed stream representation; and
  - denormalized resource metadata matches the selected `ResourceKey`/compiled plan.
- An invariant failure returns or throws a deterministic projection-processing failure, emits
  bounded sanitized diagnostics, and produces no cache candidate. It must leave durable work
  visible for retry or operator diagnosis and must not be treated as a successful
  stale-candidate suppression.
- Missing canonical rows are not materializer errors. They are ordinary delete/post-delete
  races fenced by foreign keys and handled by the cache-write/acknowledgement component.
- A materialized candidate is never an authorization, freshness, caught-up, or cache-write
  decision. It is only an optimistic current-source candidate that 18-03 may attempt to
  publish under its own lifecycle lock, monotonic write, and conditional acknowledgement
  rules.

### Fixture Boundary

- Add shared fixtures at the materializer boundary: canonical source setup,
  materializer result, expected cache-row JSON without `_etag`, expected `StreamEtag`, and
  the companion public CDC document shape only where needed to prove handoff to E19.
- Include at least one ordinary link-bearing resource, one descriptor/no-link stream
  context, one extension or nested-collection case, and one invariant-failure fixture.
  Provider-specific cache DML, Debezium raw records, and Kafka envelope assertions remain in
  18-03 and E19.

## Acceptance Evidence

- Unit and provider integration tests cover every materializer state and invariant owned by
  the referenced design sections.
- Concurrency fixtures exercise source changes at the materializer boundary.
- Representation fixtures are shared with the CDC test work rather than redefining the
  public message contract in this story.

## Not Assigned to This Story

- Cache persistence and reconciliation scheduling are assigned to 18-03 and 18-04.
- Kafka envelope shaping is assigned to E19.

## Clarifying Questions and Answers

### Questions 1

1. For a materializer invariant failure, should the materializer contract return a typed non-success result, or throw a deterministic projection-processing exception? The story currently allows "returns or throws", but the choice affects the result model, caller retry/backoff behavior, and acceptance tests.
2. When `dms.Document.ResourceKeyId` is present but the selected mapping set has no matching resource key, read plan, or descriptor materialization path, should that remain a per-document materialization outcome that leaves work visible, or should it fail the target execution context as a target/mapping invariant violation?
3. What durable format and repository location should the shared materialized-document fixtures use so E19 can consume the same source setup, cache-row JSON, `StreamEtag`, and companion public CDC shape without copying .NET-only test builders?

### Answers 1

1. Throw a deterministic projection-processing exception for materializer invariant failures. Typed non-success results should cover ordinary materializer observations such as missing source and source-changed-during-hydration; invariant failures are projection bugs, mapping defects, or corrupted source/projection state. The exception should carry a bounded reason code and sanitized metadata, produce no cache candidate, cause the projector/direct-fill caller to use its existing failure/backoff path, and leave durable work visible for retry or operator diagnosis. Acceptance tests should assert the exception path, no cache-writer call, and bounded diagnostics.
2. Fail the target execution context as a target/mapping invariant violation. Runtime mapping selection and `dms.ResourceKey` validation already guarantee that a selected target context matches the database's effective schema and resource-key seed; a missing `ResourceKeyId`, read plan, or descriptor materialization path means the target cannot safely project that database. Do not treat it as a normal per-document outcome. Surface a deterministic target/mapping failure with the target key, mapping-set key, `DocumentId`, and `ResourceKeyId` in sanitized diagnostics; pause projection eligibility for that target until configuration, provisioning, or mapping-pack contents are corrected. The story should later narrow any "mapping/resource-plan unavailable" result wording to this target-fatal classification.
3. Store the shared fixtures as checked-in, language-neutral JSON under `src/dms/backend/Fixtures/document-cache/materialized-documents/<case-name>/`. Each case should have a `fixture.json` manifest with relative paths to provider-neutral canonical source setup, expected materializer/cache-row output, expected `StreamEtag`, and, where needed, the expected public CDC document value after `DocumentState` injects `_etag`. Use JSON objects and arrays only, with no C# builders, serialized .NET types, provider-specific SQL, or Debezium raw-record shapes as the source of truth. E18 materializer tests and E19 transform/message tests should both load these files; E19 may add provider raw-record fixtures derived from them, but must not copy or redefine the cache-row JSON or public document expectation.

### Questions 2

1. Should the cache projection shape be implemented as a first-class read/materialization mode or options object for both ordinary resources and descriptors, rather than post-processing existing external-response or stored-document modes? The cache mode would need to suppress readable profiles and `_etag`, emit `id` and `_lastModifiedDate`, force the fixed stream link behavior for ordinary resources, and compose `StreamEtag` separately.
2. For the shared materialized-document fixtures, should `canonical source setup` seed the database through normal write-path/API operations, or through deterministic direct seed rows with fixed `DocumentId`, `DocumentUuid`, `ContentVersion`, and `ContentLastModifiedAt` values? This determines whether expected cache JSON and `StreamEtag` are stable checked-in values or are normalized from provider-generated stamps at test time.
3. If the first metadata read finds a `dms.Document` row, but hydration finds no matching concrete resource root row or descriptor row for that same `DocumentId`, should the materializer return a typed missing/source-changed outcome or throw the deterministic invariant/projection-processing exception?

### Answers 2

1. Yes. Add a first-class cache-projection materialization mode or options object consumed by both ordinary resource and descriptor materialization, and have `IDocumentCacheMaterializer` request that mode directly. It should not post-process `ExternalResponse` or `StoredDocument` output. The mode emits the caller-agnostic full stored representation with `id` and `_lastModifiedDate`, suppresses readable-profile projection and `_etag`, always uses the link-bearing fixed stream context for ordinary resources independent of `ResourceLinks:Enabled`, uses the descriptor no-link stream context for descriptors, and returns `StreamEtag` as a separate value composed by the shared served-ETag composer. Acceptance tests should pin that cache projection is not implemented by string surgery over an external response and that ordinary resource, descriptor, and readable-profile inputs all produce the fixed cache/stream representation.
2. Use deterministic direct seed rows as the source of truth for the shared materialized-document fixtures. The fixture manifest should define provider-neutral source rows with explicit `DocumentId`, `DocumentUuid`, `ResourceKeyId`, `ContentVersion`, `ContentLastModifiedAt`, and the required concrete root, child, extension, descriptor, reference, and referential-identity rows. A test-only seeder can translate that manifest into provider-specific setup and set final stamps after any write-path triggers run, but the checked-in expected cache-row JSON, expected `StreamEtag`, and expected public CDC document value should be stable files. Normal API/write-path setup belongs in separate integration or E2E coverage; it should not be the source of truth for these cross-E18/E19 representation fixtures.
3. Treat the final metadata re-read as the discriminator. If the final read shows the `dms.Document` row disappeared or its `DocumentUuid`, `ResourceKeyId`, `ContentVersion`, or `ContentLastModifiedAt` changed, return the typed source-changed or missing-source outcome and produce no candidate. If the same canonical metadata is still present but the concrete resource root row or `dms.Descriptor` row cannot be hydrated for that `DocumentId`, throw the deterministic projection-processing exception. Under supported writes, a stable `dms.Document` row without its body row is corruption, unsupported direct mutation, or a mapping/provisioning defect, not an ordinary stale-candidate suppression.
