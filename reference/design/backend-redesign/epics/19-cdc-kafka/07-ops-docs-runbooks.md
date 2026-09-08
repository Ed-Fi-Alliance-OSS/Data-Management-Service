---
jira: DMS-1326
source_spike: DMS-1245
epic: DMS-1309
---

# Story: Add CDC Setup, Monitoring, Recovery, and Security Runbooks

## Design References

- **Configuration, integration, readiness, and operations**: reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md
- **Topic and message contract**: reference/design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md
- **Projector and source decision**: reference/design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md

The referenced documents own the architecture, contracts, constraints, and deferrals. This
story documents the shipped implementation and must link to those owners rather than
restate them.

## Outcome

Publish verified operator guidance for the implemented relational CDC capability.

## Dependencies

- Depends on 18-07 and the completed E19 setup, status, and lifecycle tooling.
- Depends on the shipped downstream-publication-history provider behavior that either
  unlocks or intentionally keeps locked the E18 internal-only DocumentCache administrative
  commands.

## Implementation Scope

- Document local opt-in, production-like prerequisites, setup, observation, and
  troubleshooting for both providers.
- Document the shipped topic, connector, consumer, binding-state, security, retention,
  sizing, and telemetry operations.
- Document queue backlog/oldest work, poison failure remediation, lifecycle/configuration
  mismatch, activation/deactivation, `Resetting`, bounded rebuild, clear-latch `Tracking`
  admission for the explicit O(N) scrub, enqueue-failure diagnosis, and provider-specific
  per-write/queue-drain overhead.
- Document that current or historical CDC binding/consumer state disqualifies the simple
  read-acceleration activation/deactivation toggle in v1, and that stopping a connector or
  removing a runtime target is not clearing authority.
- Document the shipped availability of `activate-offline`, `deactivate-offline`, and
  `recover-cache-ahead`. These commands require the production downstream-history provider to
  report `internalOnly` for the same target and physical-source fingerprint, and no shipped
  provider reports it: the CDC records prove `active` and `historical`, and every other
  evidence shape is `unknown`. Document them as rejected in v1 and route operators to CDC
  containment/recovery instead of the simple read-acceleration toggles.
- Document the destructive-retirement operator judgements the commands put on the operator by
  name: the retire confirmation token, and `--connector-already-absent`, which is how a
  generation whose connector was never registered, or whose interrupted retirement had already
  removed it, stays retirable. What that assertion covers, and why the worker cannot make it,
  is owned by
  reference/design/backend-redesign/design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding.
- Document that a binding record spells the default tenant as `default` while every `cdc` verb
  takes the E18 tenant key, where the default tenant is the empty string. An operator driving a
  verb from a record maps it back, and a record's own `default` passed through as `--tenant-key`
  is rejected before dispatch. The token is reserved case-insensitively for binding records;
  the CDC CLI does not support a named tenant called `default`.
- State that projector downtime permits canonical writes to queue work, while enqueue
  failure rejects the complete canonical transaction. Projection status never gates
  ordinary API routing.
- Document only the implemented restart, recovery, containment, source-replacement, and
  destructive-retirement commands.
- Cross-link E18 projection/restamp guidance and the design-owned deferred workflows.
- Manually verify documentation against command help, templates, status output, and test
  fixtures. There must be no automated tests of documentation in this story.

## Resolved Runbook Scope and Implementation Guidance

This section resolves documentation placement, integration with completed work, and the
evidence needed for DMS-1326. The linked design owners remain authoritative; the deliverable
is executable operator guidance for the shipped surfaces, not another lifecycle controller
or a second specification of CDC behavior.

### Deliverables and Existing Documentation

- Add `reference/cdc-documentation/README.md` as the CDC operator entry point,
  `operations-runbook.md` in that directory for setup, observation, recovery, and security,
  and `cdc-inv-evidence.md` for runbook-to-test traceability. Keep common procedures in one
  runbook with provider-specific subsections rather than maintaining two complete copies.
- Reuse `reference/document-cache-documentation/operations-runbook.md` for queue, poison,
  lifecycle, rebuild, scrub, and SQL Server projection-prerequisite procedures. Update its
  CDC handoff links and its older default-provider explanation to describe the packaged
  CDC-backed downstream-history provider. Keep the E18 evidence matrix scoped to E18 and
  link to the new CDC evidence index for the downstream procedures.
- Keep installation, command syntax, confirmations, and exit-code reference in
  `src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md`. Extend
  `docs/CONFIGURATION.md` with the shipped `DataManagement:DocumentCache:Cdc` settings and
  link from the runbook instead of copying the complete options catalog into it.
- Update discovery links and stale CDC statements in `eng/docker-compose/README.md`,
  `docs/RELATIONAL-BACKEND.md`, the DMS E2E README, and the CLI README. Audit the other
  active setup references named by the design's
  [documentation disposition](../../design-docs/cdc/cdc-streaming.md#documentation-audit-and-disposition),
  including its own implementation-status descriptions. Correct claims that relational
  connector registration has not landed or that SQL Server cannot use Kafka; retain
  distinctions between a particular test lane and provider capability. Replace operator
  links to this story with links to the delivered runbook. Historical material stays
  historical and must not become a setup example.
- Scope refactoring to consolidating overlapping documentation and reusing existing test
  helpers. Provider provisioning, template rendering, binding persistence, command behavior,
  and orchestration remain in 19-00 through 19-04; message and API-driven coverage remain in
  19-05 and 19-06. A missing operational capability is documented as a limitation and routed
  to its owning work package, not implemented as an unreviewed shell workaround here.

### Runnable Examples and Setup Handoffs

- Use the packaged `dms-document-cache cdc` verbs as the operator surface and the shipped
  bootstrap/compose wrappers for local execution. Derive examples from
  `DocumentCacheAdminCommandSurface`, `DocumentCacheAdminCdcCommandDispatcher`,
  `CdcControlOptions`, the connector renderer, and the existing script tests. Do not maintain
  hand-authored connector JSON or invoke provider setup independently to bypass initial
  enablement. See [initial enablement](../../design-docs/cdc/cdc-streaming.md#enablement-and-initial-readiness-sequence).
- Give each executable procedure a stable anchor, starting directory, prerequisites,
  target/generation selection, commands, expected JSON outcome and exit code, verification
  step, and interruption/retry disposition. Separate diagnostic commands from mutations and
  identify the scope of destructive steps before the command. Use synthetic identifiers and
  named secret references; capture actual output from fixtures before sanitizing it.
- Provide one fresh local setup exercise per provider using the actual `-EnableKafkaCdc`
  wrapper and qualified image digest input, followed by status, an API upsert/delete smoke
  observation, planned stop, and guarded restart. Reuse 19-06's provisioned database and
  consumer helpers for API evidence. Explain the one-shot control-plane container's network
  context so host-side examples do not assume container-only database or broker names resolve.
- Distinguish local opt-in from published deployment configuration: the published start
  script does not accept the local CDC switch. Production-like guidance specifies the
  settings, credentials, connectivity, explicit projection target, status-endpoint role,
  provider setup authority, and durable state backend the deployment must supply. The
  shipped filesystem store is a single-controller implementation, not a delivered remote
  state adapter. Link to [local bootstrap](../../design-docs/cdc/cdc-streaming.md#local-bootstrap-and-ci)
  and [binding storage](../../design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding).
- Show how the same binding-state root reaches setup, status, stop, and retirement, using
  the shipped path precedence and mount handling. Include backup of binding, incident, and
  retirement records, permissions, and missing-state diagnosis. A backup or redacted
  connector manifest is not evidence to recreate offsets or edit binding identity; missing
  binding recovery uses `cdc adopt` with a complete operator-supplied record and live
  validation. Preserve retirement history when describing destructive local stack teardown.

### Monitoring and Incident Routing

- Present a short symptom-to-evidence-to-action table using actual fields/reasons from the
  shared projection status and CDC JSON contracts. Cover queue/poison failures, enqueue
  failures, provider prerequisites, heartbeat/barrier progress, connector snapshot/task
  state, lag, topic/ACL/offset-store mismatch, source mismatch, and continuity incidents.
  Link each row to its procedure and owning design section rather than copying readiness
  algorithms. Start from [projection and CDC status](../../design-docs/cdc/cdc-streaming.md#projection-health-and-deployment-owned-cdc-readiness).
- Show automation inspecting the returned outcome and component evidence: `cdc status`
  returns exit code zero for a successfully produced `notReady` or `unknown` answer, and a
  successful stop or restart does not establish end-to-end readiness. Distinguish DMS
  `status` / `/health/document-cache` from deployment-owned `cdc status`, and initial
  admission evidence from later observations using the
  [v1 readiness scope](../../design-docs/cdc/cdc-streaming.md#v1-readiness-scope).
- Document the shipped lag reader's Jolokia configuration and failure output, including
  `ConnectMetricsBaseUri`, alongside existing projection telemetry. Name real instruments,
  units, configuration keys, and bounded diagnostic fields; label unavailable observations
  explicitly. Routine polling must use the existing status and indexed queue observations,
  with scrub identified as a separate expensive operation. See
  [telemetry and operations](../../design-docs/cdc/cdc-streaming.md#security-telemetry-and-operations).
- Separate continuity `unknown`, terminal `lost`, missing deployment state, and physical
  source replacement in the incident table. Use guarded `cdc restart` only where its
  existing prerequisites pass. Describe `cdc replace-source` only for its shipped
  new-database workflow and pre-rotated source identity; it is not a restore utility or
  terminal-history-loss recovery. Do not supply manual identity rotation, offset reset,
  slot/capture recreation, or same-topic resnapshot recipes. Link to
  [continuity](../../design-docs/cdc/cdc-streaming.md#source-history-continuity) and
  [source replacement](../../design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding).
- For cache-ahead incidents, make the rejected packaged `activate-offline`,
  `deactivate-offline`, and `recover-cache-ahead` paths explicit, including why test-only
  `internalOnly` fixtures do not establish a production recovery option. Cross-link E18
  rebuild/scrub guards and the independently owned offline restamp guidance. A rebuild,
  scrub, or restamp example must not imply that it clears published-state risk or certifies
  a replacement baseline. See [repair operations](../../design-docs/cdc/cdc-streaming.md#contract-change-and-repair-operations).
- Give `cdc retire` a dedicated procedure showing target/generation verification,
  `cdcBindingRetirement`, the operator judgement behind `--connector-already-absent`, and
  `--source-connection-variable` for a retained generation's original physical database.
  Include default-tenant translation, partial-cleanup proof inspection, and same-operation
  retry. Identify per-binding artifacts, shared worker state, and retained retirement
  records using the existing cleanup proof; do not offer direct file deletion or broad
  topic/volume deletion as an alternative to guarded retirement. The
  [binding lifecycle](../../design-docs/cdc/cdc-streaming.md#deployment-owned-cdc-target-and-physical-source-binding)
  owns the cleanup order and authority.

### Security, Retention, and Capacity Guidance

- Explain each credential boundary using shipped configuration: database setup and
  connector principals, Kafka connector/worker/consumer principals, control-plane Kafka
  admin credentials, and the DMS status token. Distinguish Java connector security
  properties from `KafkaAdminClientSecurityProperties` for the .NET control plane.
  Include provider least-privilege verification and work-table capture/grant exclusion by
  linking to [provider setup](../../design-docs/cdc/cdc-streaming.md#connector-topology-and-provider-setup).
- Reuse the topic/ACL validator and template manifests to show effective-policy inspection
  for public, progress, shared offset, and SQL Server schema-history topics. Keep the policy
  values and consumer conformance rules in their owners:
  [topic contract](../../design-docs/cdc/0002-kafka-topic-and-message-contract.md#topic),
  [consumer bootstrap](../../design-docs/cdc/cdc-streaming.md#public-consumer-bootstrap), and
  [security](../../design-docs/cdc/cdc-streaming.md#security-telemetry-and-operations).
  Local ACL-disabled evidence must be labelled as such and cannot qualify production isolation.
- Cover PostgreSQL retained WAL/slot pressure, SQL Server capture/cleanup retention and
  row-version-store health, broker cleaner/retained-log volume, consumer barrier/checkpoint
  evidence, and the pipeline's record-size budget. Supply bounded inspection examples and
  tuning guidance using shipped settings. For record-size changes, identify the operator
  and tool steps actually available under the
  [coordinated increase procedure](../../design-docs/cdc/cdc-streaming.md#in-place-record-size-increase);
  do not invent a resize verb or claim a partial rollout succeeded.
- Separate tuning observations and small exercise results from representative production
  qualification. Neither the E18 component tests nor 19-05's test consumer proves a
  deployment's capacity. Link to the still-unassigned
  [projection performance qualification](../../design-docs/cdc/cdc-streaming.md#projection-performance-qualification)
  and the consumer's own capacity responsibility; add no arbitrary universal thresholds or
  new performance harness to this story.
- Include a sensitive-data containment procedure that identifies shipped connector-stop
  and retirement commands separately from operator-owned ACL revocation and platform purge
  verification. Record sanitized incident identity, affected generation, containment and
  deletion times, cleanup proof, and platform evidence or its absence. Follow the
  [disclosure correction owner](../../design-docs/cdc/cdc-streaming.md#sensitive-data-disclosure-correction)
  for completion criteria and deferred re-enablement; successful fixture cleanup cannot
  establish purge of broker remote copies or independent consumer stores.

### Documentation Verification and Evidence Reuse

- There must be no automated tests of documentation. Do not add, extend, or require tests
  that read or assert documentation contents, extract or execute Markdown snippets, compare
  documentation-bound catalogs, check documented settings/defaults or JSON fields, validate
  documentation links/anchors, or assert prose snapshots or keywords. Existing documentation
  assertions in reused suites must not be selected as this story's verification or acceptance
  evidence.
- Manually compare documented commands with generated help and the shipped command surface,
  settings/defaults with `CdcControlOptions` and its validator, and JSON examples with actual
  serialized fixture output. Manually follow local links and anchors, including evidence
  targets. Record the reviewed revision, procedure anchors, findings, corrections, and result
  in the evidence index; do not create an executable documentation catalog or drift checker.
- Reuse existing command-surface/exit-code and bootstrap behavior tests as supporting
  evidence. Select only tests of product behavior from mixed suites such as
  `eng/docker-compose/tests/BootstrapEnableKafkaCdc.Tests.ps1`, and record the exact selection.
  Automated tests of actual CLI parsing, configuration validation, serialization, wrappers,
  and provider behavior are allowed when their inputs and assertions are independent of
  documentation. Comparing their results with the runbook is a manual verification step.
- Extend `src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration`
  around `Given_DocumentCacheAdminRunbookWorkflows`, the CDC JSON-contract fixtures, and
  shipped-composition fixtures. Reuse provider and broker fixtures from the existing CDC
  control and connector integration projects and the 19-06 harness. Add only missing
  operator-path assertions; no second bootstrap, renderer, state store, or consumer.
- Maintain a compact evidence index mapping runbook anchors to design owners, applicable
  `CDC-INV-*` IDs, exact test identifiers, provider, and result/artifact location. This
  story owns runbook evidence for `CDC-INV-14` and `CDC-INV-15`; link to sibling evidence
  for setup, routing, durability, ACLs, and consumer behavior. Clearly distinguish mocked
  rejection coverage, real-provider exercises, and broker/API exercises.
- Exercise both providers' documented happy paths and interruptions in disposable targets,
  including planned stop/restart, missing-state adoption, replacement/retirement, partial
  retirement retry, default-tenant handling, and downstream-history command rejection.
  Reuse E18's provider prerequisite, rebuild/reset, and scrub evidence rather than duplicating
  it. Capture the revision, tool/image versions, command outcome, sanitized output, and
  relevant test artifacts. A missing provider, qualified image, or broker is an unmet
  prerequisite, not a passing exercise. Keep the manual documentation review checklist
  separate from commands for automated behavior tests and provider exercises.
- Review the final prose manually for decision clarity, secret/payload disclosure, and
  unsupported recovery promises. The acceptance handoff includes the documented commands
  for behavior tests and exercise suites, the manual review checklist, and actual results;
  it does not require running a destructive procedure against an operator's existing deployment.

## Acceptance Evidence

- Runbook commands are exercised against the supported PostgreSQL and SQL Server workflows.
- Provider exercises cover RCSI/nested-trigger target and activation validation, guidance
  that post-validation changes to either setting are outside the supported v1 contract,
  `Disabled`-only initialization correction-and-restart, unsupported-incident
  classification without a renewed-readiness guarantee for any other lifecycle,
  activation correction-and-retry, restart without source scan, reset/rebuild crash
  recovery, and work-table capture exclusion.
- Recorded manual review verifies documentation against the shipped configuration, status,
  and lifecycle surfaces, including commands, examples, and local links/anchors. No automated
  tests inspect or assert documentation as part of this story.
- Manual documentation review and exercised runbook scenarios cover the rejected active,
  historical, unknown, missing, and mismatched downstream-history evidence for the E18 command
  gate, and record that v1 ships no evidence shape that admits it.
- Every behavioral, security, recovery, or compatibility statement links to its owning
  design section instead of reproducing its normative algorithm or value table.
- Destructive procedures are verified against the implemented guarded operations.

## Not Assigned to This Story

- Cloud-provider-specific instructions and consumer product implementation guidance are
  separate work.
- Design changes must be made in the owning documents, not in the runbook.
