# DMS-1318: Representation Restamp Utility — Design Specification

## Status and authority

**Status:** approved design for implementation planning; no implementation is included in this document.

The authoritative work package is [08-representation-restamp-utility.md](../../../reference/design/backend-redesign/epics/18-document-cache/08-representation-restamp-utility.md). Its referenced CDC, ETag, Change Query, and Kafka designs define the safety and compatibility rules. The two DMS-1318 pre-specs in `.plans/` were reviewed as supporting evidence only; they do not override the story.

Facts marked **Verified** were read in the repository at commit `0bd0392c`. Items marked **Proposed** are DMS-1318 design decisions approved during brainstorming.

## Goal, scope, and non-goals

### Goal

Provide a supported, non-interactive PostgreSQL and SQL Server administrative utility that advances the representation stamps of selected current documents after an offline correction changes served, materialized, or stream representation bytes without changing education/domain state.

### In scope

- Two DocumentCacheAdmin commands: preview and execute/resume.
- Explicit, typed affected-document scope; preview; confirmation; durable operation manifests; resume; progress and final reporting.
- Database-scoped administrative mutex use for the whole invocation.
- Atomic canonical document, root/descriptor mirror, Tracking enqueue, and manifest-progress updates.
- PostgreSQL and SQL Server implementations, generated DDL, automated tests, and operator documentation.

### Explicit non-goals

- HTTP administration, request-path restamping, automatic admission, arbitrary SQL, or a shell wizard.
- Domain-field, identity-key, `DocumentUuid`, delete-history, or key-change-history updates.
- ETag hashing, ETag wire-format changes, Change Query contract changes, Kafka message/topic changes, or direct Kafka publication.
- Kafka containment, purge, connector/topic retirement, replacement namespace, or stream-contract migration. A correction requiring removal of prior sensitive Kafka values is rejected operationally and routed to E19 containment.
- An additional mutex, alternate lock identity, or transparent reconnect after mutex-session loss.

## Current behavior

### Existing administrative foundation — Verified

`src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/DocumentCacheAdminCommandSurface.cs` exposes flat, non-interactive DocumentCache commands. `DocumentCacheAdminMutatingCommandRequests.cs`, `DocumentCacheAdminMutatingCommandContracts.cs`, and `DocumentCacheAdminMutatingCommandDispatcher.cs` map CLI input or `--request-json` into shared DTOs. JSON mode writes one machine-readable result to stdout; the README documents diagnostics/progress separately.

`src/dms/backend/EdFi.DataManagementService.Backend/DocumentCacheAdministrativeCommandRunner.cs#DocumentCacheAdministrativeCommandRunner` resolves and pins one target, acquires `IDocumentCacheAdministrativeMutex`, retains the dedicated physical session, creates `DocumentCacheAdministrativeCommandExecutionContext`, and treats session loss as terminal. `DocumentCacheAdministrativeWorkflow.ExecuteInTransactionWithProviderConcurrencyRetryAsync` runs bounded transaction retries on that same mutex session.

`src/dms/core/EdFi.DataManagementService.Core/DocumentCache/DocumentCacheAdministrativeContracts.cs` contains the shared command, confirmation, phase, classification, diagnostic, and result contracts. It does not yet contain a representation-restamp value.

`src/dms/backend/EdFi.DataManagementService.Backend/DocumentCacheAdministrativePrimitives.cs#IDocumentCacheAdministrativePrimitives` and the baseline/scrub workflows establish the existing deterministic page and retry convention. Page size comes from `DocumentCacheOptions.ProjectorPageSize`.

### Existing stamp and projection semantics — Verified

- `dms.Document.ContentVersion` is allocated from `dms.ChangeVersionSequence`; `dms.GetMaxChangeVersion()` reads the sequence high-water on both providers (`PgsqlDialect.CreateGetMaxChangeVersionFunction` and `MssqlDialect.CreateGetMaxChangeVersionFunction`).
- `dms.Document.DocumentUuid` is unique (`CoreDdlEmitter.EmitDocumentTable`); `RelationalDocumentUuidLookupSupport.TryResolveByDocumentUuidAsync` resolves it to document ID and resource key.
- The generated root/descriptor stamping model records its mirror target in `DbTriggerInfo.MirrorStampTargetTable` (`DerivedRelationalModelSetContracts.cs`). Normal trigger DML uses captured canonical stamps to keep the root or `dms.Descriptor` mirror equal to `dms.Document`.
- The `dms.Document` enqueue trigger records a changed required version in `dms.DocumentProjectionWork` in `Tracking`; it intentionally returns without work-table DML in `Disabled`. Therefore restamp must update `dms.Document`, not insert application-level queue work.
- `DocumentCacheAdministrativeWorkflow` already commits or rolls back the active database transaction before accepting cancellation at a page boundary.

### Existing observable behavior — Verified

The strong ETag is derived from ContentVersion plus its representation variant key, as defined in `reference/adr-etag-from-content-version.md`. Change Query live-resource filtering consumes root/descriptor `ContentVersion` mirrors, as defined in `reference/design/backend-redesign/design-docs/change-queries.md`. The Kafka v1 design uses `contentVersion` as its state-ordering value and permits a compatible higher-version correction, but does not certify a replacement baseline or purge prior records.

The CLI README currently declares representation restamp out of scope. The 18-06 design reserves the future shared observation value `representationRestamp`; the current enum must be extended by this story.

## Proposed behavior

### Operator prerequisites and admission

The target datastore is offline before preview and remains offline through execution: all DMS replicas, API readers/writers, projectors, direct-fill/bulk loaders, administrative peers, and external writers are stopped outside the utility. The utility requires the existing exact `closedAndDrained` acknowledgement; it does not try to prove the external fence.

The command acquires the existing provider-specific, database-scoped administrative mutex on a dedicated connection and holds it for the complete preview or execute invocation. Every coordinator-issued restamp DML uses that connection. If the session is lost, the invocation stops and performs no later mutation. A later invocation must reacquire the mutex.

Before creating a manifest, starting a draft, or resuming an incomplete operation, durable lifecycle is re-read on the mutex session:

| Requested mode | Required durable state | Meaning |
| --- | --- | --- |
| `tracking` | `Tracking` with a clear cache-ahead latch | projection/publication mode |
| `disabled` | `Disabled` with a clear cache-ahead latch | canonical-only mode |

`Resetting`, `Rebuilding`, a set latch, missing/unreadable lifecycle, or any mismatch rejects before canonical stamping. Resume uses the manifest's selected mode; it cannot change mode. The manifest fingerprint must equal the current physical-source fingerprint and the supplied target must equal the manifest target.

### Typed scope

**Proposed:** A preview request carries exactly one of the following canonical scope forms:

1. `resource`: one fully qualified resource (`projectName` and `resourceName`). It selects every current document whose compiled `ResourceKeyId` equals that resource's authoritative key.
2. `documentUuids`: a set of distinct document UUIDs. It selects those current `dms.Document` rows regardless of resource type. The set is sorted lexicographically before manifest serialization and may contain at most the target's configured `ProjectorPageSize` UUIDs.

The canonical scope JSON is persisted verbatim after validation. UUID scope uses the unique `dms.Document.DocumentUuid` lookup and then compiled `MirrorStampTargetTable` metadata for each resolved resource; resource scope resolves the same metadata from the selected resource. Unknown resources, duplicate UUIDs, UUIDs that resolve to no current document, unsupported/missing mapping metadata, or a missing/ambiguous mirror are errors. No SQL fragment, predicate, implicit “all documents,” internal `DocumentId`, or table name is accepted from the operator.

### Command and request contract

**Proposed:** Follow the existing flat CLI convention with:

- `dms-document-cache restamp-preview`
- `dms-document-cache restamp-execute`

`restamp-preview` takes a shared typed request through `--request-json`. It contains exactly one target, optional expected physical-source fingerprint, `offlineWriterAdmission: "closedAndDrained"`, `mode`, non-empty `reason` up to 1,024 characters, and the typed scope. It creates a durable draft manifest but does not change canonical stamps, mirrors, projection work, cache rows, or Kafka state.

`restamp-execute` takes one target, one opaque operation ID, `offlineWriterAdmission: "closedAndDrained"`, and exact confirmation `representationRestamp`. It accepts a draft operation or an incomplete operation and has no scope, mode, reason, or boundary input. It cannot create a new operation. As with existing commands, request JSON is the only command-DTO input source when used; duplicate CLI request fields are rejected.

`DocumentCacheAdministrativeCommand` gains `RepresentationRestamp`, `DocumentCacheAdministrativeCommandConfirmation` gains `RepresentationRestamp`, and the shared phase enum gains `CreateManifest`, `SelectDocuments`, and `StampDocuments`. All serialize lower-camel as `representationRestamp`, `createManifest`, `selectDocuments`, and `stampDocuments`.

### Durable manifest

**Proposed:** Add `dms.RepresentationRestampOperation` as generated core DDL in both providers. This is the authoritative resume/audit record, not an exported JSON file. It has:

| Column | Purpose |
| --- | --- |
| `OperationId` (UUID/uniqueidentifier, primary key) | opaque resume locator |
| `ContractVersion` (integer, currently `1`) | durable schema/serialization version |
| `TenantKey`, `DataStoreId` | configured target identity that requested the operation |
| `PhysicalSourceFingerprint` | pinned physical-source identity |
| `ScopeJson` | validated canonical typed scope |
| `Reason` | bounded operator rationale |
| `Mode` | `Tracking` or `Disabled`; immutable after preview |
| `PreRestampBoundary` | captured `dms.GetMaxChangeVersion()` value, normalized to zero only when the provider reports no current sequence value |
| `PreviewDocumentCount`, `CommittedDocumentCount` | selected total and atomically committed progress |
| `State` | `Draft`, `Incomplete`, or `Completed` |
| `CreatedAt`, `UpdatedAt` | durable audit timestamps |

All immutable fields, including scope, reason, target, fingerprint, mode, boundary, and preview count, are written by preview and never updated. An execute page updates only committed count, state, and `UpdatedAt` in the same transaction as that page's canonical/mirror changes. `Completed` is terminal; a completed ID cannot execute again. A rejected execution before its first restamp page leaves the manifest unchanged.

The manifest table is a DMS-managed core table but is not a CDC source table. `CdcDmsManagedTableInventoryBuilder` must recognize it as managed so source-inventory validation remains complete, without adding it to `CoreDdlEmitter.BuildCdcSourceInventory()`.

### Preview flow

1. Resolve/pin the configured target, validate provider and optional expected fingerprint, acquire the mutex, and re-read lifecycle/latch.
2. Validate scope via the active mapping set and resolve deterministic selected current documents.
3. Capture `dms.GetMaxChangeVersion()` on the mutex session. Because the required offline fence forbids other writers, this is the global pre-restamp boundary. It is not recalculated on resume.
4. Count the selected documents whose current ContentVersion is at or below the captured boundary.
5. Insert the immutable draft manifest and return its operation ID, boundary, selected count, mode, scope summary, fingerprint, and state.

Preview output is machine-readable in `--json` mode and contains no document body or unbounded UUID list. Its only database write is the auditable draft manifest.

### Execute/resume flow

1. Resolve/pin the supplied target and acquire the same existing mutex.
2. Load the operation by ID. Verify contract version, supplied target, physical fingerprint, exact mode/lifecycle/latch eligibility, and permitted state.
3. Repeatedly process deterministic `DocumentId`-ordered pages of selected documents whose current ContentVersion is at or below `PreRestampBoundary`. The page size is the target's existing `ProjectorPageSize`.
4. Each page runs at serializable isolation through `DocumentCacheAdministrativeWorkflow.ExecuteInTransactionWithProviderConcurrencyRetryAsync` on the mutex-owning session. The page:
   1. re-reads lifecycle/latch and requires the manifest mode;
   2. selects its page and validates its mapping/mirror routes;
   3. allocates exactly one fresh `dms.ChangeVersionSequence` value per selected document and updates `dms.Document.ContentVersion` and `ContentLastModifiedAt`;
   4. captures the resulting document ID/version/timestamp tuples and updates each root mirror or `dms.Descriptor` mirror to exactly those tuples;
   5. relies on the existing `dms.Document` trigger for Tracking work enqueue, or the trigger's Disabled no-work branch;
   6. increments `CommittedDocumentCount` and writes `Incomplete` progress in the same transaction; and
   7. commits only if every selected canonical row and every expected mirror row was changed exactly once.
5. After no eligible rows remain, re-count current selected rows and require `CommittedDocumentCount + remainingEligibleCount == PreviewDocumentCount`. If it is true, atomically mark the manifest `Completed`; if false, return an incomplete/failure result without declaring success. This detects selection drift or a violated offline fence.

Committed rows have fresh versions above the immutable boundary and are ineligible on retry. A rolled-back page leaves no document/mirror/work/manifest progress; sequence gaps are expected and are never used as a count. The manifest does not store an unbounded per-document completion list.

### Mode completion and claims

In `Tracking`, successful execution means **canonical restamp complete and projection work queued**. It does not drain work itself. Per the authoritative CDC procedure, operators next start only corrected DMS/projector instances, observe normal cache catch-up, then verify affected public records have the higher `contentVersion` and changed ETag. The utility neither verifies Kafka delivery nor claims a new exact CDC baseline.

In `Disabled`, successful execution means **canonical-only restamp complete**. It runs no drain, makes no cache or Kafka claim, and operators start only corrected DMS API instances to verify ETags and Change Query behavior. A later ordinary activation/rebuild establishes projection state.

## Architecture and boundaries

### Proposed components

| Component | Responsibility | Dependencies |
| --- | --- | --- |
| CLI restamp request builder/dispatcher | Parse the two commands, load shared JSON DTOs, preserve one-target and stdout behavior | existing command surface and executor |
| `IRepresentationRestampCommand` (proposed) | Implements preview and execute workflows through the shared runner | runner, restamp store, mapping-set access |
| `IRepresentationRestampStore` (proposed) | Provider-neutral operations: create/load manifest, capture boundary, resolve/page scope, stamp pages, and complete manifest | mutex-session `IRelationalWriteSession`, compiled metadata |
| PostgreSQL/SQL Server restamp stores (proposed) | Provider SQL, parameter binding, sequence allocation, captured mirror updates, and manifest DML | current provider relational services |
| Existing `DocumentCacheAdministrativeCommandRunner` | target pinning, mutex lifecycle, session-loss handling, observations, result classifications | unchanged behavior; extended shared enums/request conversion |
| Existing `dms.Document` enqueue trigger | Tracking transactional work enqueue / Disabled no-work behavior | unchanged |

The restamp store is a sibling to `IDocumentCacheAdministrativePrimitives`, rather than an expansion of that generic lifecycle/clear/scrub interface. This isolates durable operation, scope, and mapping semantics while continuing to use the runner's session and transaction wrappers.

### Affected files and contracts

Existing paths below are verified. “New” means a proposed DMS-1318 file/contract, not a current file.

| Area | Files and symbols |
| --- | --- |
| Core administrative contracts | `src/dms/core/EdFi.DataManagementService.Core/DocumentCache/DocumentCacheAdministrativeContracts.cs` — add command, confirmation, phases, classifications/diagnostics necessary for manifest and scope validation, and typed restamp request/result records. |
| CLI command surface | `src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/DocumentCacheAdminCommandSurface.cs`, `DocumentCacheAdminMutatingCommandContracts.cs`, `DocumentCacheAdminMutatingCommandRequests.cs`, `DocumentCacheAdminMutatingCommandDispatcher.cs`, and request JSON registration — add preview/execute parsing and dispatch. |
| Shared workflow | `src/dms/backend/EdFi.DataManagementService.Backend/DocumentCacheAdministrativeCommandRunner.cs`, `DocumentCacheAdministrativeWorkflow.cs`, and `ReferenceResolverServiceCollectionExtensions.cs` — use the existing runner; register the proposed command without creating another mutex/session owner. |
| New focused workflow/store | **New:** `RepresentationRestampCommand.cs`, `RepresentationRestampContracts.cs`, and `RepresentationRestampStore.cs` under `src/dms/backend/EdFi.DataManagementService.Backend/`. |
| Provider implementations | **New:** `PostgresqlRepresentationRestampStore.cs` and `MssqlRepresentationRestampStore.cs` under their existing backend provider projects; register through `PostgresqlServiceExtensions.AddPostgresqlDocumentCacheRuntimeServices` and `MssqlServiceExtensions.AddMssqlDocumentCacheRuntimeServices`. |
| Metadata routing | `src/dms/backend/EdFi.DataManagementService.Backend.External/DerivedRelationalModelSetContracts.cs#DbTriggerInfo.MirrorStampTargetTable`, `src/dms/backend/EdFi.DataManagementService.Backend.External/MappingSetContracts.cs`, and `src/dms/backend/EdFi.DataManagementService.Backend/RelationalDocumentUuidLookup.cs` — consume existing authoritative resource/mirror data; do not infer names. |
| DDL/inventory | `src/dms/backend/EdFi.DataManagementService.Backend.Ddl/DmsTableNames.cs`, `DmsCoreTableDefinitions.cs`, `CoreDdlEmitter.cs`, and `CdcDmsManagedTableInventory.cs` — add and emit the manifest table and catalog it as non-source managed state. |
| Telemetry/status | `src/dms/backend/EdFi.DataManagementService.Backend/DocumentCacheProjectionTelemetry.cs`, command observations, and `DocumentCacheStatusContracts.cs` — reuse existing metrics and add the shared command/phase values only. |
| Operator documentation | `src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md`, `reference/design/backend-redesign/epics/18-document-cache/07-documentcache-integration-tests-and-runbooks.md`, and `reference/design/backend-redesign/epics/19-cdc-kafka/07-ops-docs-runbooks.md`. |

## Error handling and edge cases

| Condition | Required result |
| --- | --- |
| Missing/mismatched confirmation or offline acknowledgement | argument/preflight rejection; no manifest or canonical mutation |
| Wrong target, fingerprint, contract version, scope, or completed operation | rejected/no mutation |
| `Resetting`, `Rebuilding`, latch set, unavailable lifecycle, or manifest-mode mismatch | reject before a restamp page; a prior committed page makes the command incomplete/retryable |
| Manifest insert/update failure | rollback the relevant preview/page transaction; no partial stamped page |
| Missing or duplicated root/descriptor mirror | invariant failure and rollback of the page |
| Tracking enqueue-trigger failure | rollback canonical stamp, mirror update, work DML, and manifest increment together |
| Deadlock/transient provider conflict | retry the complete serializable page using existing bounded retry settings; retry exhaustion is incomplete if any prior page committed |
| Cancellation or timeout | do not interrupt an active transaction; stop before the next page and return retryable/incomplete after any committed page |
| Mutex session loss | abort; do not reconnect; active work rolls back or reports according to its actual commit outcome; resume is a new invocation |
| Preview-count drift at completion | do not mark complete; report incomplete/failure and preserve the manifest for diagnosis/resume after the offline fence is restored |
| Sequence gaps | allowed; never treated as omitted documents or used for committed count |
| Empty scope selection | create a draft with count zero; execute completes it after revalidation without canonical/work/cache change |

## Compatibility and security

- Preserve the strong ETag format and variant-key rules. ContentVersion changes are the invalidation mechanism; no hashing or special read path is introduced.
- Preserve Change Query behavior: mirrors receive the new value, current resource rows become visible in later live windows, and restamp generates no key-change or delete event.
- Preserve Kafka v1 keys, fields, types, ordering, topic, and connector behavior. Tracking is an eventual higher-version state replacement only. Disabled creates no publication expectation. Neither mode certifies a baseline or purges previous records.
- Preserve current provider mutex identity, target pinning, configuration/secret loading, timeout budget, exit-code mapping, JSON naming, and stdout purity.
- Treat `ScopeJson`, `Reason`, and manifest identity as operational audit data. Do not put them in metric labels or logs. Never store credentials, connection strings, document bodies, or response JSON in manifests, results, diagnostics, or examples.

## Observability

Reuse `IDocumentCacheProjectionTelemetry` and `DocumentCacheAdministrativeCommandExecutionContext` observations. The existing metric dimensions already contain provider, target surrogate, outcome, category, lifecycle, command, and phase; DMS-1318 adds only bounded enum values. It must not add operation ID, raw scope, resource names, UUIDs, reasons, database names, or document contents as metric labels.

The final typed result reports: operation ID, manifest state, command status/classification, selected mode, original boundary, preview count, committed count, remaining eligible count when calculated, physical fingerprint, lifecycle/latch observation, phase diagnostics, elapsed time, and a claim level of `canonicalOnlyComplete`, `projectionWorkQueued`, or `incomplete`. Claim levels never say Kafka published/verified.

## Test plan

### Unit and contract coverage

Extend the verified CLI unit project `src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin.Tests.Unit/`:

- `Given_DocumentCacheAdminCommandSurface.cs`: command names, required request fields, exact confirmation, offline acknowledgement, `--request-json` exclusivity, and help text.
- `Given_DocumentCacheAdminJsonContracts.cs`: canonical scope serialization, manifest/result JSON, lower-camel enum values, one-JSON stdout behavior, bounded diagnostics, and no sensitive fields.
- `Given_DocumentCacheAdminMutatingCommandRequests.cs` and dispatcher/registration tests: command-to-DTO-to-command routing and provider registrations.

Add shared-backend unit coverage next to `DocumentCacheAdministrativeCommandRunnerTests.cs` and `DocumentCacheAdministrativePrimitivesTests.cs` for scope validation, immutable manifest rules, lifecycle matrix, result classification, retry state, and no-reconnect behavior.

### Provider and CLI integration coverage

Add provider-backed restamp test fixtures to the existing DocumentCacheAdmin integration project and/or provider integration projects, following the `Given_...` NUnit naming convention. Each test runs on PostgreSQL and SQL Server and proves:

- resource and UUID scopes select only the intended rows; preview changes no document/mirror/work/cache stamp;
- manifest fields persist before any stamp and reject target/fingerprint/scope/mode/boundary tampering;
- root-resource and descriptor rows receive unique new versions and exact timestamps matching `dms.Document`;
- Tracking work is created/advanced by the trigger and an induced enqueue failure rolls back the whole page;
- Disabled updates stamps but creates/advances no work and invokes no drainer;
- failure after one committed page, cancellation, timeout, and mutex session loss resume with the original boundary and never re-stamp committed rows;
- aliases of one physical database serialize through the existing mutex while independent physical databases can run independently;
- `Resetting`, `Rebuilding`, latch set, mode mismatch, and source mismatch fail before a new page;
- completion detects preview-count drift instead of falsely succeeding.

Use the existing generated-DLL fixtures in `PostgresqlGeneratedDdlAuthoritativeSmokeTests.cs` and `MssqlGeneratedDdlAuthoritativeSmokeTests.cs` to verify emitted manifest DDL and root/descriptor stamp invariants. Reuse the repository's existing transaction-fault injection seams or add a narrowly scoped restamp page hook; do not simulate trigger atomicity only with mocks.

### Observable integration coverage

- Extend `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/Resources/etag.feature` and its existing step definitions to prove the prior strong ETag is not reused and `_lastModifiedDate` advances with unchanged domain data.
- Extend `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/ChangeQueries/LiveResourceChangeVersionFilter.feature` to show the restamped current document in a later live-resource Change Query window and prove no synthetic `/deletes` or `/keyChanges` record.
- Add a Tracking operator-follow-up fixture: start corrected projector/DMS after the command, process queued work, and verify cache catches up. It must report projection state separately from Kafka delivery.
- Add CDC semantic coverage under the existing `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/` project when its state-record fixture is added: assert unchanged v1 contract shape with higher `contentVersion`, and never assert purge/baseline certification. Disabled has no CDC expectation.

### Documentation coverage

Update CLI README examples for preview, execute, interruption/resume, Tracking verification, Disabled verification, rejected lifecycle/latch routing, E18 recovery, and E19 sensitive-data containment. Execute representative examples through the existing CLI process harness so command spelling, confirmation tokens, request JSON, exit codes, and stdout contracts cannot drift.

## Risks and mitigations

| Risk | Mitigation |
| --- | --- |
| A retry re-captures the boundary and double-restamps prior work | immutable database manifest boundary; retry uses only that value; committed-page resume tests |
| Canonical/mirror divergence | capture canonical update tuples and require exact mirror-row count in the same transaction |
| Continuing after mutex-session loss | retain existing terminal session-loss behavior; only a fresh invocation can resume |
| Mode changes partway through operation | immutable manifest mode plus pre-page lifecycle/latch revalidation |
| Over-broad selection churns unrelated ETags | no implicit all scope; typed resource or bounded UUID scope; preview count and reason |
| External writer violates the offline fence | acknowledgement plus mutex; completion count reconciliation detects selection drift but cannot certify an external fence |
| Same-topic restamp is misused as sensitive-data purge | explicit E19 routing in docs and rejection guidance; never make a purge claim |
| Manifest becomes an unbounded/sensitive data store | scope is resource metadata or bounded UUID list; no body/secrets; no raw scope/UUID telemetry labels |

## Acceptance-criteria traceability

| Story acceptance evidence | Design coverage |
| --- | --- |
| PG/SQL Server utility, scope, preview, confirmation, manifests, resume, progress, reports | typed two-command surface, `dms.RepresentationRestampOperation`, provider stores, page/result contracts, CLI/provider integration tests |
| Shared database mutex, serialization, session loss | shared runner and physical mutex session for every page; no reconnect; alias/session-loss integration cases |
| Tracking transactional enqueue and queue-drain follow-up | canonical update triggers existing enqueue in the same transaction; documented corrected-projector follow-up fixture; no in-command drain |
| Disabled canonical-only behavior | exact Disabled admission, trigger no-work behavior, no drain/publication claim tests |
| Transitional/latched/mode mismatch rejection | admission matrix before preview/execute and before each page; test matrix |
| Canonical/mirror stamps, Change Query, strong validators | captured same-transaction mirror updates; ETag and Change Query E2E coverage; no delete/key-change history |
| CDC v1 correction behavior | Tracking higher-version replacement only; CDC semantic coverage; no topic/schema/order change |
| Documentation preview/execute/resume/failure/verification/E18/E19 routing | README and 18-07/19-07 updates with executable examples |
