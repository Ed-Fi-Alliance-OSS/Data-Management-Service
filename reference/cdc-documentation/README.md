# CDC Operator References

This is the shared PostgreSQL and SQL Server operator reference set for relational
CDC. Delivery is in progress under [DMS-1326](../design/backend-redesign/epics/19-cdc-kafka/07-ops-docs-runbooks.md).
PostgreSQL setup, its DMS E2E opt-in variant, deployment-state preservation and
recovery classification are documented; remaining procedure bodies and live
qualification are **pending**. A reserved procedure or snippet ID is not an executable procedure
or evidence of a successful deployment.

- [Operations runbook](operations-runbook.md#procedure-navigation): procedure selection,
  stable anchors, required procedure records, and snippet conventions.
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
| Existing documentation entry-point audit | [Design disposition](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#documentation-audit-and-disposition); cross-link corrections are pending T12. |

## Scope Boundaries and Missing Surfaces

Preserve the [deployment-state continuity and adoption boundary](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-deployment-state-continuity-and-adoption-deferral),
[physical-source replacement deferral](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#v1-physical-source-replacement-deferral),
and [new-topic cutover deferral](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#deferred-new-topic-cutover).
Adoption, replacement, and new-generation cutover are not supported procedures in
this reference set. Cloud-specific deployment instructions and consumer products
are outside [this story](../design/backend-redesign/epics/19-cdc-kafka/07-ops-docs-runbooks.md#not-assigned-to-this-story).
[Production-scale performance qualification](../design/backend-redesign/design-docs/cdc/cdc-streaming.md#projection-performance-qualification)
also remains separate work.

No missing shipped surface has been established during T01's structural review.
The pending sections below are documentation work, not diagnosed runtime defects.
If a later procedure needs a missing capability, record the command/fixture,
expected surface, observed gap, and owning sibling here before describing a remedy:

| Surface | Owning implementation story |
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
