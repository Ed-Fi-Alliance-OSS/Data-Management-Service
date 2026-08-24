---
jira: DMS-1428
source_spike: DMS-1246
epic: DMS-1308
related:
  - DMS-1245
  - DMS-1311
  - DMS-1314
  - DMS-1316
  - DMS-1317
  - DMS-1323
---

# Story: Add a DocumentCache Administration CLI

## Design References

- **Administrative serialization and state-row fencing**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#administrative-serialization-and-state-row-fencing
- **Baseline, rebuild, deactivation, and scrub**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md#baseline-rebuild-deactivation-and-scrub
- **Projection administration**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-administration
- **Security, telemetry, and operations**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#security-telemetry-and-operations

The referenced design sections and the E18 implementation stories own the lifecycle
semantics. This story adds the supported command-line operator surface over those existing
administrative services.

## Outcome

Deliver a supported non-interactive DocumentCache administration CLI for PostgreSQL and SQL
Server targets. The CLI must let operators inspect target status and run the existing
DocumentCache administrative commands, including online cache rebuild, without starting an
ad hoc DMS web process or duplicating provider-specific rebuild logic.

## Dependencies

- Depends on 18-01 for stable JSON-facing administrative contracts and target-key
  serialization.
- Depends on 18-04 for the command runner, administrative mutex, lifecycle transitions,
  bounded clearing, baseline seeding, work draining, and failure classifications.
- Depends on 18-06 for status, health, caught-up, queue, and bounded diagnostic
  observations.
- Informs 18-07 and 19-07 operator runbooks. Coordinate naming and bootstrap integration
  with 19-04, but do not make Kafka connector setup part of this story.

## Implementation Scope

- Add a new .NET command-line application for DocumentCache administration under
  `src/dms/clis`, packaged as a .NET tool with installed command name
  `dms-document-cache`.
- Reuse the same provider adapters, target resolution, effective settings, command runner,
  administrative mutex, telemetry, and JSON contracts used by DMS runtime services. Do not
  implement separate SQL-only lifecycle or rebuild paths in the CLI.
- Load configuration from the normal DMS configuration sources plus explicit command-line
  overrides needed for non-hosted execution. Resolve configured targets by
  `tenantKey`/`dataStoreId` using the same target registry semantics as DMS.
- Support read-only target inspection for lifecycle, cache-ahead latch, provider
  eligibility, physical source fingerprint, queue presence, oldest work, active command,
  last-ended diagnostics, and bounded document-scoped failure diagnostics.
- Support the existing administrative commands: guarded new-empty activation, offline
  activation, offline deactivation, online cache rebuild, explicit integrity scrub, and
  internal-only cache-ahead recovery.
- Reuse and, where needed for CLI-required acknowledgement, extend the shared
  administrative request DTOs and result DTOs rather than creating CLI-only request/result
  wrappers. Preserve lower-camel JSON enum values for JSON request input and `--json`
  output. Human-readable output may be added, but automation must be able to consume stable
  JSON without parsing prose.
- Require explicit non-interactive acknowledgement for destructive or writer-fenced
  commands, including exact `offlineWriterAdmission` confirmation tokens where the existing
  command contracts require them.
- Expose `expectedPhysicalSourceFingerprint` as an optional guard on every mutating
  command that supports it, and fail closed on mismatch before mutation.
- Publish a stable exit-code mapping for completed, rejected-no-mutation,
  failed-no-mutation, incomplete-retryable, argument, configuration, and unexpected
  failures.
- Handle cancellation, command timeouts, provider command timeouts, mutex acquisition
  cancellation, session loss, and retryable incomplete results without reconnecting under
  presumed mutex ownership.
- Emit sanitized structured logs and metrics consistent with the runtime projection and
  administration meters. Never write connection strings, secrets, unsanitized target input,
  or unbounded document identifiers to routine output.
- Add command help, examples, and runbook cross-links for safe activation/deactivation,
  online rebuild, reset/rebuild crash retry, cache-ahead recovery routing, and persistent
  poison remediation.

## Resolved CLI Scope and Command Contract

### Project and Packaging Boundary

- Add one tool project named `EdFi.DataManagementService.DocumentCacheAdmin` under
  `src/dms/clis`, plus matching unit and integration test projects. Add the tool and tests
  to `EdFi.DataManagementService.sln` and include the tool in the DMS package build beside
  SchemaTools.
- Package the tool as `EdFi.Api.DocumentCacheAdmin` with
  `ToolCommandName=dms-document-cache`. Use `net10.0`, `System.CommandLine`, nullable
  references, `System.Text.Json`, and the existing CLI logging pattern that keeps
  structured or diagnostic logs off stdout when stdout carries machine-readable output.
- Publish it through the same NuGet package flow used for SchemaTools: extend
  `build-dms.ps1 Package` with `PackageTarget=DocumentCacheAdmin` and include it in
  `PackageTarget=All`, producing `EdFi.Api.DocumentCacheAdmin.<version>.nupkg` at the repo
  root. Extend `build-dms.ps1 Push` only as needed to accept that package file; the push
  continues to use the configured `EdFiNuGetFeed` and `NuGetApiKey`. Feed-view promotion,
  signing, and release orchestration remain owned by the existing package-release pipeline,
  not by this CLI story.
- The CLI builds a non-web generic-host/service-provider graph from shared DMS
  registrations. It must not start Kestrel, map HTTP endpoints, start the ordinary
  all-target projector supervisor, or fork an ad hoc DMS web process. It may run only the
  target-scoped services required by the requested status or administrative command.
- If the existing runtime registrations cannot be reused without starting hosted services,
  refactor them into shared registration methods consumed by both runtime DMS and this CLI.
  Do not copy provider adapters, target-resolution rules, status classifiers, command
  coordinators, JSON DTOs, or mutex code into the CLI project.

### Configuration and Target Resolution

- Load configuration through the same DMS configuration pipeline used by runtime services,
  with CLI-only non-secret overrides for `--settings <path>`, `--environment <name>`,
  `--datastore postgresql|sqlserver`, timeouts, and the command target key. Connection
  strings, CMS credentials, client secrets, and other secret-bearing values come from the
  normal configuration/secret providers, not convenience command-line options.
- Every invocation targets either one explicit target key supplied as
  `--tenant-key <value>` plus required `--data-store-id <positive integer>`, or one
  `targetKey` supplied by JSON request input. The default tenant is the empty string, the
  same wire value used by the 18-01 DTO contract. Mutating commands reject missing,
  multiple, malformed, or unresolved targets before attempting mutation, using argument
  errors for malformed input and typed no-mutation results for target-level classifications
  from the registry.
- Treat the invocation target as the CLI process's explicit one-entry
  `DocumentCache:Targets` membership for that run. This does not mutate runtime
  configuration, does not require the target to be present in a running DMS replica, and
  does not infer target membership from HTTP requests, JWTs, CMS inventory, connection
  aliases, or the most recent successful status call.
- Resolve the target through the 18-01 target registry semantics, including tenant-key
  normalization, CMS refresh behavior, provider metadata compatibility, effective-schema
  and resource-key compatibility, provider inventory, SQL Server prerequisite observations,
  and physical-source fingerprint calculation. A mutating command always re-resolves and
  revalidates under the 18-04 command runner; status output from an earlier invocation is
  advisory only.
- Expose `--expected-physical-source-fingerprint <opaque value>` on every mutating command.
  The CLI passes it through to the shared command DTO and fails closed before mutation when
  the current target observation or required downstream-history observation is missing or
  does not match.

### Command Surface

- The v1 command names are fixed as:
  - `dms-document-cache status`;
  - `dms-document-cache activate-new-empty`;
  - `dms-document-cache activate-offline`;
  - `dms-document-cache deactivate-offline`;
  - `dms-document-cache rebuild-online`;
  - `dms-document-cache scrub`; and
  - `dms-document-cache recover-cache-ahead`.
- Command-line options are only a typed projection of the shared request DTOs. Also support
  `--request-json <path|->` for automation that wants to submit the exact shared JSON
  request shape. When `--request-json` is present, reject duplicate DTO fields supplied by
  command-specific options; only global configuration, logging, timeout, and output options
  may accompany it.
- `status --json` writes the 18-06 v1 status response shape to stdout, with a single target
  entry for the invocation target. Human status output is derived from the same DTO. Target
  states such as `unknown`, `nonOperational`, or `notCaughtUp` are status data and still
  return exit code `0` when the status command itself completed and serialized normally.
- Standalone status mode reports only observations available to this CLI process plus the
  direct durable/provider observation. Because 18-06 active-command and last-ended command
  fields are process-local and not durable, the CLI must not fabricate command activity
  from another DMS replica or another CLI process. If no current-process observation exists,
  emit the documented `notObserved`/`null` fields from the 18-06 contract.
- Standalone status mode is a CLI-specific exception to the 18-06 endpoint composition rule
  that skips durable observation when runtime is `notObserved`. After target resolution,
  inventory, provider metadata, physical-source fingerprint, and provider prerequisites
  succeed, the CLI still runs the provider current-source observation and populates
  lifecycle, cache-ahead, queue presence, oldest-work fields, and `durableObservedAt` from
  that read-only statement. This direct durable observation is inspection data only: when no
  current 18-04 runtime observation exists, `operationalHealth` and `caughtUp` remain
  `unknown` with reason `runtimeNotObserved`.
- Mutating commands invoke the shared 18-04 administrative command runner and return the
  shared command result DTO. The CLI may add human summaries, but JSON output must not be a
  CLI-only wrapper that forces automation to parse different fields than runtime or
  bootstrap callers.
- Do not add a v1 dry-run, interactive prompt, HTTP status proxy, dashboard, shell wizard,
  direct SQL escape hatch, or per-command provider-specific SQL path. Operators use
  `status`, explicit confirmations, expected-fingerprint guards, and the command result
  classifications instead.

### Confirmation and Safety Gates

- The CLI is non-interactive. Missing required acknowledgements are argument errors, not
  prompts. Confirmation values are exact, case-sensitive tokens so scripts cannot pass a
  generic boolean by accident.
- Every mutating command requires `--confirm <token>`, where `<token>` is one of
  `newEmptyActivation`, `offlineActivation`, `offlineDeactivation`,
  `onlineCacheRebuild`, `integrityScrub`, or `internalCacheAheadRecovery` as appropriate
  for the command.
- Offline activation, offline deactivation, and cache-ahead recovery also require
  `--offline-writer-admission closedAndDrained`, mapped to the shared
  `offlineWriterAdmission` request field. This is an operator acknowledgement that external
  write admission is closed and in-flight canonical transactions have drained; it is not a
  database-derived proof and it does not replace the command runner's preflight locks and
  rechecks.
- Guarded new-empty activation requires the `newEmptyActivation` confirmation and uses the
  18-04 guarded workflow, including the writer-blocking `dms.Document` lock, empty
  canonical/cache/work checks, exclusive state-row lock, racing-insert safety, and SQL
  Server prerequisite revalidation.
- Offline activation, offline deactivation, and cache-ahead recovery require the trusted
  18-01 downstream-publication-history abstraction to report `internalOnly` for the same
  normalized target key and physical-source fingerprint. Do not accept a CLI boolean,
  operator-entered text, stopped connector, or removed `DocumentCache:Targets` entry as
  internal-only proof. Until E19 supplies durable binding/history evidence, the production
  default remains `unknown` and these commands reject.
- Online rebuild, scrub, and cache-ahead recovery must preserve the fail-closed latch
  routing from the design. A set cache-ahead latch rejects `rebuild-online` before any
  mutation; `scrub` is admitted only from clear-latch `Tracking` and may set but never clear
  the latch; `recover-cache-ahead` is the only CLI command that may clear the latch, and
  only through the proven-internal-only offline recovery workflow.

### Output, Exit Codes, and Failure Classification

- In `--json` mode, stdout contains exactly one JSON document and no prose. Logs, progress,
  warnings, and sanitized diagnostics go to stderr or the configured log sink. In human
  mode, command output still avoids secrets and unbounded identifiers.
- Serialize JSON with lower-camel property names, lower-camel enum strings, the 18-01
  nested `targetKey` shape, UTC timestamps with `Z`, numeric duration fields in seconds,
  and no numeric enum values. Reject malformed JSON request input, unknown command names,
  command/request mismatches, and duplicate option/request fields with the argument exit
  code.
- Publish and pin this exit-code mapping:

  | Exit code | Meaning |
  | --- | --- |
  | `0` | Status or administrative command completed according to its result DTO. |
  | `10` | Administrative command was rejected before mutation by a known guard or preflight rule. |
  | `11` | Administrative command or status observation failed before mutation; retry may be safe but the result did not enter an incomplete durable workflow. |
  | `12` | Administrative command is incomplete and retryable after possible mutation; reissue the same command with the same target and guards. |
  | `64` | Command-line argument, confirmation, or JSON request validation error. |
  | `78` | Process-wide configuration error before the target registry or shared command contract could be built. |
  | `1` | Unexpected or unclassified CLI/runtime failure. |

- Exit-code selection is derived from typed result classifications, not message text.
  Rejected no-mutation results include lifecycle/latch/fingerprint/provider-prerequisite
  guards, unresolved targets, nonempty guarded activation, provider mismatch,
  downstream-history ineligibility, and explicit scrub/rebuild admission rejection.
  Operational failures before mutation, such as mutex acquisition timeout, provider
  observation timeout, or status-provider failure, use the no-mutation failure
  classification when the runner can prove no mutation occurred.
- Cancellation, command timeout, provider command timeout, mutex-session loss, or process
  termination after a command may have entered `Resetting`, cleared rows, seeded baseline
  work, or changed lifecycle returns the incomplete-retryable result and exit code `12`.
  The runner must release or lose the mutex session, perform no later mutation on a
  replacement connection, and require a later invocation to reacquire the mutex and
  revalidate durable state.

### Telemetry and Documentation Boundary

- Reuse the E18 DocumentCache meters and bounded target surrogate labels from 18-06 for CLI
  command attempts, mutex acquisition, phases, outcomes, durations, and failures. Do not
  introduce metric labels containing connection strings, database names, tenant display
  names, raw target JSON, `DocumentUuid`, document bodies, or unbounded resource names.
- Use the existing logging sanitizer for every provider, target-resolution, CMS, and command
  diagnostic emitted by the CLI. Command examples must show environment/settings-file
  secret loading rather than secret-bearing command-line flags.
- The CLI README and 18-07/19-07 runbook cross-links should document the shipped command
  names, package-install command from the Ed-Fi NuGet feed, confirmation tokens, JSON
  request/output examples, exit-code table, retry guidance for `Resetting`/`Rebuilding`, and
  routing from set-latch rebuild rejection to internal-only recovery or publication
  containment.

## Acceptance Evidence

- CLI parser and serialization tests pin command names, options, required confirmations,
  `--json` request/result shapes, lower-camel enum values, and exit-code mapping.
- Build-script tests prove `PackageTarget=All` creates the API, SchemaTools, and
  DocumentCacheAdmin packages, `PackageTarget=DocumentCacheAdmin` creates only the CLI
  package, and the documented install command uses the same package ID and tool command
  name as the project file.
- PostgreSQL and SQL Server integration tests execute the CLI against real target
  databases for status, guarded new-empty activation, online cache rebuild, offline
  activation/deactivation, explicit scrub admission/rejection, and internal-only
  cache-ahead recovery rejection/admission paths.
- Online rebuild tests prove the CLI invokes the shared 18-04 coordinator: pending work is
  preserved while cache is cleared, the baseline is bounded/backpressured, work drains
  before returning to `Tracking`, `Rebuilding` resumes without repeating cache clearing, and
  a set cache-ahead latch rejects with no lifecycle, cache, work, or latch mutation.
- Mutex tests prove two CLI invocations targeting aliases of the same physical database
  serialize through the shared provider mutex, while different physical databases can be
  administered concurrently according to the existing design.
- Cancellation, timeout, and session-loss tests prove mutated incomplete states return
  retryable results and that rerunning the same command revalidates durable state before
  resuming.
- Documentation tests exercise help output and shipped runbook commands so examples cannot
  drift from the implemented CLI.

## Not Assigned to This Story

- New lifecycle semantics, table shapes, queue algorithms, baseline cursor persistence, or
  cache writer behavior. Those remain owned by the existing E18 design and implementation
  stories.
- Kafka connector setup, connector teardown, source replacement, binding retirement, topic
  management, or CDC bootstrap orchestration. Those are E19 responsibilities.
- The representation restamp utility, which remains owned by 18-08.
- HTTP administration endpoints, dashboards, or cloud-provider-specific automation.

## Clarifying Questions and Answers

### Questions 1

1. For the `status` command, should target-level durable/provider observation failures that serialize as 18-06 `unknown` status still return exit code `0`, leaving exit code `11` only for failures that prevent a status DTO from being produced, or should specific serialized status reasons also map to exit code `11`?
2. Which timeout overrides are part of the stable v1 CLI surface, including option names, units, defaults, and mapping to command timeout, provider command timeout, mutex acquisition, status observation, and overall process/endpoint budgets?
3. Before E19 supplies durable downstream-publication history, should CLI integration tests for internal-only activation/deactivation and cache-ahead recovery admission invoke the CLI host in-process with a fake trusted history provider, or must the packaged command support a non-production registration/configuration mechanism for that proof?

### Answers 1

1. `status` should return exit code `0` whenever the CLI can produce and serialize the 18-06 status DTO, including a single target whose durable facts are serialized as `unknown` for `statusObservationTimeout`, `providerObservationFailed`, or `statusEndpointTimeout`. Do not map any serialized target-level status reason to exit code `11`; automation must inspect the JSON status fields for target health. Use exit code `11` only when the status command itself cannot produce the DTO after arguments and process configuration are valid, such as a failure in the shared status pipeline or serialization before a complete status document is available. Argument errors remain `64`, process-wide configuration errors remain `78`, and unexpected unclassified failures remain `1`.
2. Use positive numeric seconds for all stable CLI timeout overrides, accepting fractional seconds where the underlying `TimeSpan` accepts them and rejecting zero, negative, malformed, or overflow values with exit code `64`. Mutating commands expose one stable option, `--command-timeout-seconds`, default `86400`, mapped to `DataManagement:DocumentCache:Administration:WorkflowTimeout` and also used as the CLI's outer cancellation budget from target resolution through mutex acquisition and command completion. There is no separate v1 mutex-acquisition timeout option: waiting for the provider mutex is bounded by that outer command budget and a timeout before mutation returns the shared no-mutation mutex/cancellation classification. There is no separate v1 provider-command-timeout option: provider command timeouts are classified by the shared provider adapters and remain derived from the operation's active bounded budget. The `status` command exposes `--status-observation-timeout-seconds`, default `5`, mapped to `DataManagement:DocumentCache:Status:StatusObservationTimeout` and the provider current-source command timeout, plus `--status-timeout-seconds`, default `30`, mapped to `DataManagement:DocumentCache:Status:EndpointTimeout` as the total CLI status evaluation budget. Do not add additional v1 timeout aliases such as `--timeout`, `--provider-command-timeout`, `--mutex-timeout`, or per-phase rebuild/scrub timeouts.
3. CLI admission-path integration tests before E19 should invoke the CLI host in-process with an explicit fake implementation of the trusted 18-01 downstream-publication-history abstraction. The packaged command must not ship a non-production command-line switch, appsettings flag, environment variable, or plug-in proof mechanism that can make a target appear `internalOnly`. Packaged-command coverage should prove the production default `unknown` rejection path; admitted offline activation/deactivation and cache-ahead recovery paths may be covered through the in-process test host using the same parser, DTOs, command runner, provider adapters, and fake trusted history provider.

### Questions 2

1. Should `status` support `--request-json <path|->`, and if so what stable JSON request shape should it accept given that 18-06 defines a response contract but no request body: only `{ "targetKey": { "tenantKey": "", "dataStoreId": 1 } }`, target plus status timeout fields, or should `--request-json` be limited to mutating administrative command DTOs?
2. Does this story own adding DocumentCacheAdmin to package/release and PR verification workflows alongside SchemaTools, including pack, SBOM, provenance, publish, release attachment, package promotion, and installed-tool smoke verification, or is scope intentionally limited to `build-dms.ps1 Package/Push` plus local package-build tests?

### Answers 2

1. `status` should support `--request-json <path|->`, but the accepted v1 JSON request shape is only the invocation target: `{ "targetKey": { "tenantKey": "", "dataStoreId": 1 } }`. This is a CLI input convenience for target selection, not an 18-06 HTTP request body and not a second status contract. Do not include status timeout fields in the JSON shape; `--status-observation-timeout-seconds` and `--status-timeout-seconds` remain CLI timeout overrides that may accompany `--request-json` under the existing global/logging/timeout/output option allowance. Reject missing `targetKey`, malformed tenant/data-store values, unknown fields, or duplicate target fields supplied by options with exit code `64`.
2. Scope this story to the tool project/package metadata, `build-dms.ps1 Package` and `Push` plumbing, `PackageTarget=DocumentCacheAdmin` and `PackageTarget=All`, local build-script/package tests, and a local generated-package install/help smoke that verifies `EdFi.Api.DocumentCacheAdmin` and `dms-document-cache`. Do not make this story add DocumentCacheAdmin to GitHub package/release or PR verification workflows for SBOM, provenance, publish, release attachment, feed-view promotion, signing, or workflow-orchestrated installed-tool smoke. Those remain owned by the existing package-release and CI pipeline work; this story should leave the package ready for that pipeline to consume.

### Questions 3

1. When a mutating command uses `--request-json <path|->`, should the destructive confirmation token and any required `offlineWriterAdmission` value be fields in the shared JSON request DTO, or should `--confirm` and `--offline-writer-admission` remain required command-line options even though command-specific DTO options are otherwise rejected with `--request-json`?
2. Should standalone CLI `status` run the provider current-source observation and populate lifecycle, cache-ahead, queue, oldest-work, and `durableObservedAt` fields even when no 18-04 runtime observation exists in the CLI process, or should it follow the 18-06 endpoint composition rule that treats `runtimeNotObserved` as process-ineligible and leaves durable fields unavailable?
3. For option-based target selection, is `--tenant-key` optional with an implicit empty-string default when `--data-store-id` is supplied, or must operators pass `--tenant-key ""` explicitly for the default tenant?
4. What is the default stdout mode when `--json` is omitted for `status` and mutating commands: human-readable output for every command, JSON for every command, or command-specific defaults; and should every command support `--json` with stdout containing exactly the shared DTO document?

### Answers 3

1. With `--request-json`, the JSON document is the only source for command-specific DTO fields. Extend the shared administrative request DTOs with a `confirmation` field for the destructive acknowledgement, using the same lower-camel token values accepted by `--confirm`, and put offline writer proof in `offlineWriterAdmission` when the command requires it. Reject `--confirm`, `--offline-writer-admission`, target options, and any other command DTO option when `--request-json` is present, because they duplicate request fields. Option-based invocation still uses `--confirm` and, where required, `--offline-writer-admission` as typed projections into those same DTO fields.
2. Standalone CLI `status` should run the provider current-source observation after target resolution, inventory, provider metadata, fingerprint, and prerequisite checks succeed, even when the CLI process has no 18-04 runtime worker observation. Populate lifecycle, cache-ahead, queue presence, oldest-work fields, and `durableObservedAt` from that direct read-only observation. Still serialize `executionState.status` as `notObserved`, `executionState.observedAt` as `null`, `activeCommand` as `null`, and `lastEndedDiagnostic` as `null` when no current-process observation exists. Do not claim runtime operational health from that durable read; `operationalHealth` and `caughtUp` should remain `unknown` with `runtimeNotObserved` unless the status call is running inside a process that has a current 18-04 runtime observation. This CLI-only exception to the 18-06 endpoint's skip-durable-observation composition rule is now part of this story's canonical scope.
3. For option-based target selection, `--data-store-id <positive integer>` is required and `--tenant-key` is optional. When omitted, `--tenant-key` defaults to the normalized default-tenant wire value, the empty string. Operators may still pass `--tenant-key ""`, but scripts should not be required to do so for the default tenant. Reject a missing or malformed `--data-store-id`, and apply the same 18-01 tenant-key normalization and validation to an explicitly supplied tenant key.
4. When `--json` is omitted, every command defaults to human-readable stdout derived from the same DTO that would be serialized as JSON. Every command, including `status` and all mutating commands, must support `--json`; in that mode stdout contains exactly one shared contract document with no CLI wrapper and no prose: the 18-06 v1 status response for `status`, and the shared administrative command result DTO for mutating commands. Logs, progress, warnings, and diagnostics remain on stderr or the configured log sink in both modes.
