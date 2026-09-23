# CDC Operator References

This is the shared PostgreSQL and SQL Server operator reference set for relational
CDC. Delivery is in progress under [DMS-1326](../design/backend-redesign/epics/19-cdc-kafka/07-ops-docs-runbooks.md).
PostgreSQL and SQL Server setup, their DMS E2E opt-in variants, deployment-state preservation, managed
lifecycle, connector restart/resume, recovery classification and the projection
administration/history handoff, monitoring, provider-retention troubleshooting,
security and consumer-evidence checklists, coordinated record-size increases, guarded
generation retirement, destructive stack teardown, compatible restamp handoff and
sensitive-data disclosure response are documented. Marked CLI commands, settings,
acknowledgement inputs, serialized result excerpts and touched document links have
[focused automated checks](cdc-inv-evidence.md#documentation-verification). Packaged host
checks cover output streams and exits. Wrapper snippet checks and required-case guards run
in Contract/PR. [PostgreSQL local/direct-E2E setup](cdc-inv-evidence.md#postgresql-setup-qualification-t16)
and [SQL Server local/published/direct-E2E setup](cdc-inv-evidence.md#sql-server-setup-qualification-t17)
and observation snippets passed live qualification. [PostgreSQL lifecycle](cdc-inv-evidence.md#postgresql-lifecycle-qualification-t18)
passed T18 and [SQL Server lifecycle](cdc-inv-evidence.md#sql-server-lifecycle-qualification-t19)
passed T19; [PostgreSQL native recovery](cdc-inv-evidence.md#postgresql-native-recovery-qualification-t21)
passed T21; [SQL Server native recovery](cdc-inv-evidence.md#sql-server-native-recovery-qualification-t22)
passed T22; [PostgreSQL record-size increase](cdc-inv-evidence.md#postgresql-record-size-qualification-t23)
passed T23; [SQL Server record-size increase](cdc-inv-evidence.md#sqlserver-record-size-qualification-t24)
passed T24; [PostgreSQL history and retirement](cdc-inv-evidence.md#postgresql-history-and-retirement-qualification-t25)
passed T25 and [SQL Server history and retirement](cdc-inv-evidence.md#sql-server-history-and-retirement-qualification-t26)
passed T26, including the status and disclosure-stop handoffs. [PostgreSQL telemetry and retention inspections](cdc-inv-evidence.md#postgresql-telemetry-and-retention-qualification-t27)
passed T27; [SQL Server telemetry and retention inspections](cdc-inv-evidence.md#sql-server-telemetry-and-retention-qualification-t28)
passed T28. Remaining procedures are **pending**. A reserved procedure or snippet ID is not an executable procedure or evidence of a successful deployment.

- [Operations runbook](operations-runbook.md#procedure-navigation): procedure selection,
  stable anchors, required procedure records, and snippet conventions.
- [Checked JSON excerpts](operations-runbook.md#serialized-result-examples): readiness, optional observations, operation scope and failures.
- [Evidence index](cdc-inv-evidence.md): procedure-to-test mapping and actual results,
  with unexercised work explicitly pending.
- [SchemaTools CDC reference](../../src/dms/clis/EdFi.DataManagementService.SchemaTools/README.md#cdc-deployment-commands):
  shipped commands, options, configuration, output, and wrapper examples.

## Supported Deployment

The shipped `api-schema-tools cdc` composition targets the qualified local
single-worker, single-broker Compose profile: `LocalSingleBroker` with
`AuthorizationDisabledLocal`. Its result reports `deploymentProfile.aclIsolationProven: false`.
This is not evidence of consumer ACL isolation. Other profile tokens are rejected
by the current CLI configuration loader; changing a token is not a secured
production installation. See the [topology owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#connector-topology-and-provider-setup),
[telemetry topology boundary](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#local-and-ci-connector-telemetry),
and [security owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#security-telemetry-and-operations).

Other deployments require deployment-supplied live authority adapters and evidence
for their durability and effective access policies, protected persistent state,
worker lifecycle, and fresh telemetry. The local CLI does not install those
capabilities. Authorization-enabled Kafka qualification is separate from local
setup; use the [security evidence row](cdc-inv-evidence.md#procedure-evidence) and
the existing [Kafka qualification lane](../../eng/ci/Invoke-CdcQualification.ps1).
Local authorization-disabled results cannot satisfy that row's access checks.
The governing requirements remain in the [deployment topology](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#connector-topology-and-provider-setup)
and [security](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#security-telemetry-and-operations) sections.

Supported implementation entry points are the SchemaTools CDC group,
[local bootstrap](../../eng/docker-compose/bootstrap-local-dms.ps1),
[published bootstrap](../../eng/docker-compose/bootstrap-published-dms.ps1), and
[DMS E2E setup](../../src/dms/tests/EdFi.DataManagementService.Tests.E2E/setup-local-dms.ps1).
Use their controller-managed workflows as specified by the
[local bootstrap owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#local-bootstrap-and-ci).
DMS E2E setup support does not confer CDC support on Instance Management E2E;
API-driven message scenarios belong to [DMS-1325](../design/backend-redesign/epics/19-cdc-kafka/06-e2e-kafka-scenarios.md).

## Documentation Ownership

| Subject | Owner and handoff |
| --- | --- |
| Ordered operator procedures and their qualification results | This reference set; implementation scope is [DMS-1326](../design/backend-redesign/epics/19-cdc-kafka/07-ops-docs-runbooks.md). |
| Command/configuration catalog and generated connector configuration | [SchemaTools CDC reference](../../src/dms/clis/EdFi.DataManagementService.SchemaTools/README.md#cdc-deployment-commands) and [connector integration owner](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#connector-transform-integration). Do not maintain a second property catalog here. |
| Projection administration and provider prerequisite correction | [DocumentCache runbook](../document-cache-documentation/operations-runbook.md), reached through the [CDC projection handoff](operations-runbook.md#projection-handoff). |
| Offline representation restamp | [DocumentCacheAdmin restamp procedure](../../src/dms/clis/EdFi.DataManagementService.DocumentCacheAdmin/README.md#representation-restamp); this runbook owns only the [CDC handoff](operations-runbook.md#representation-restamp). |
| Deployment, readiness, recovery, and security contracts | [CDC integration design](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#authority-and-document-ownership). |
| Projector/source behavior | [ADR 0001](../design/backend-redesign/design-docs/cdc/0001-relational-cdc-projector-and-sources.md). |
| Topic/message, consumer, and compatibility contract | [ADR 0002](../design/backend-redesign/design-docs/cdc/0002-kafka-topic-and-message-contract.md). |
| Existing documentation entry-point audit | [Design disposition](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#documentation-audit-and-disposition); entry points link to these procedures. Scoped link/anchor, result and wrapper checks run in Contract/PR; setup for both providers and PostgreSQL lifecycle have live evidence; remaining live procedures are pending. |

## Scope Boundaries and Missing Surfaces

Preserve the [deployment-state continuity and adoption boundary](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-deployment-state-continuity-and-adoption-deferral),
[physical-source replacement deferral](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-physical-source-replacement-deferral),
and [new-topic cutover deferral](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deferred-new-topic-cutover).
Adoption, replacement, and new-generation cutover are not supported procedures in
this reference set. Cloud-specific deployment instructions and consumer products
are outside [this story](../design/backend-redesign/epics/19-cdc-kafka/07-ops-docs-runbooks.md#not-assigned-to-this-story).
[Production-scale performance qualification](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-performance-qualification)
also remains separate work.

The SQL Server provider now maps a missing same-name database user to the deployment's
restricted SQL login during managed initial setup. Durable provider completion switches
retries to validation-only; missing users and conflicting SIDs then fail without repair.
[DMS-1326's T29 evidence](cdc-inv-evidence.md#sql-server-initial-user-mapping-t29) records
provider/controller qualification and wrapper ordering checks for this former setup gap.
The [SQL Server operator procedure](operations-runbook.md#sql-server-setup) and
[E2E variant](operations-runbook.md#sql-server-e2e-variant) are documented in T03.
[T17 live evidence](cdc-inv-evidence.md#sql-server-setup-qualification-t17) now covers local,
published and direct E2E setup from the restricted login, including the production
user mapping and writer-publication boundary. Build-based E2E remains unexercised.

If a later procedure needs a missing capability, record the command/fixture, expected
surface, and observed gap here. Required runtime integration work belongs to DMS-1326,
with contract amendments in the owning design documents and existing deferrals preserved.
The sibling stories below remain implementation and evidence references; required runtime
integration fixes are not routed back to completed stories.

| Surface | Implementation/evidence reference |
| --- | --- |
| Binding/readiness and projection prerequisites | [DMS-1319](../design/backend-redesign/epics/19-cdc-kafka/00-documentcache-cdc-prerequisites.md) |
| Provider CDC DDL/setup | [DMS-1320](../design/backend-redesign/epics/19-cdc-kafka/01-cdc-ddl-support.md) |
| Connector templates | [DMS-1321](../design/backend-redesign/epics/19-cdc-kafka/02-connector-template-generation.md) |
| Record transform | [DMS-1322](../design/backend-redesign/epics/19-cdc-kafka/03-document-state-transform.md) |
| Bootstrap, provenance, lifecycle, containment, sizing, retirement | [DMS-1323](../design/backend-redesign/epics/19-cdc-kafka/04-bootstrap-enable-kafka-cdc.md) |
| Message/consumer conformance evidence | [DMS-1324](../design/backend-redesign/epics/19-cdc-kafka/05-message-contract-tests.md) |
| API-driven message scenarios | [DMS-1325](../design/backend-redesign/epics/19-cdc-kafka/06-e2e-kafka-scenarios.md) |

Follow the [story's delivery boundary](../design/backend-redesign/epics/19-cdc-kafka/07-ops-docs-runbooks.md#documentation-home-and-integration-with-existing-guidance):
do not replace a missing capability with manual SQL repair, raw Connect lifecycle
mutation, or new runbook-only automation. Read-only provider inspections must be
bounded and linked to their owning procedure.
