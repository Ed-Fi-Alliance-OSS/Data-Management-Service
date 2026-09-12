# Source delta disposition

Source delta: `45046c1d3a26927d93e94394f901731e0aaa12fe..bb3aecb98cd887f9b05a7cebccda1d978ce40c8b`.
Replacement base: `c3ae3bfffbd0a049abf7b8768eb4f14744a6bbaf`.

This inventory records the implemented file disposition; qualification status is in
PORTING-RESULTS.md. Full live qualification remains in progress.

| Source path | Treatment | Reason |
| --- | --- | --- |
| `.github/workflows/on-dms-pullrequest.yml` | Superseded | Target Contract PR gate retained; live coverage integrated through shared runner, matrix, and nightly workflow. |
| `reference/design/backend-redesign/epics/19-cdc-kafka/05-message-contract-tests.md` | Retained | Source revised 05 governs acceptance; target revised 04 and design tree remain intact. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Control.Tests.Integration/packages.lock.json` | Superseded | Target dependency lock retained; obsolete Control dependency changes omitted. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Control/CdcConnectorObservationMapper.cs` | Superseded | Replacement Backend.Cdc transport/parser and dependencies; do not restore obsolete Control library. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcConnectorTemplatePinnedImageBrokerStartupTests.cs` | Adapted | Use target’s single broker-port field and preserve its external listener/volume cleanup conventions. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcConnectorTemplatePinnedImageFixture.MessageContract.cs` | Adapted | Use target’s single broker-port field and preserve its external listener/volume cleanup conventions. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcConnectorTemplatePinnedImageFixture.PostgresqlContract.cs` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcConnectorTemplatePinnedImageFixture.ProgressAcknowledgement.cs` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcConnectorTemplatePinnedImageFixture.RecordSize.cs` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcConnectorTemplatePinnedImageFixture.Retry.cs` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcConnectorTemplatePinnedImageFixture.SqlServerContract.cs` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcConnectorTemplatePinnedImageFixture.cs` | Adapted | Preserve controller/native/Compose modes, endpoints, cleanup and cancellation; add producer isolation, broker retry and message behavior. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcConnectorTemplatePinnedImageSmokeTests.cs` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcConnectorTemplatePinnedImageStartupCleanupTests.cs` | Adapted | Retain both branches’ assertion/cancellation/cleanup coverage with target volume cleanup. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcPinnedImageJavaRuntime.cs` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/EdFi.DataManagementService.Backend.Cdc.Tests.Integration.csproj` | Adapted | Preserve target dependencies and helper links; add fixture imports and runner assets. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/MessageContractConsumerBrokerTests.cs` | Adapted | Updated for current fixture/API or qualified-image evidence; source scenarios retained. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/MessageContractFailureTests.cs` | Adapted | Updated for current fixture/API or qualified-image evidence; source scenarios retained. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/MessageContractKafkaFixtureTests.cs` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/MessageContractPostgresqlTests.cs` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/MessageContractProgressAcknowledgementTests.cs` | Adapted | Current adapter helper, live offset/status evidence, explicit synthetic evaluator prerequisites; no operational rollout or writer admission claim. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/MessageContractProgressTests.cs` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/MessageContractProviderAssertions.cs` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/MessageContractProviderObservations.cs` | Adapted | Updated for current fixture/API or qualified-image evidence; source scenarios retained. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/MessageContractProviderRows.cs` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/MessageContractRecordSizeTests.cs` | Adapted | Current adapter helper, live offset/status evidence, explicit synthetic evaluator prerequisites; no operational rollout or writer admission claim. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/MessageContractRetryCleanupTests.cs` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/MessageContractRetryTests.cs` | Adapted | Updated for current fixture/API or qualified-image evidence; source scenarios retained. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/MessageContractRoutingTests.cs` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/MessageContractRunner.cs` | Adapted | Updated for current fixture/API or qualified-image evidence; source scenarios retained. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/MessageContractRunner/MessageContractProducerProxy.java` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/MessageContractRunner/MessageContractRunner.java` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/MessageContractRunner/MessageContractSourceObserver.java` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/MessageContractRunner/README.md` | Adapted | Current controller boundaries and shared qualification; explicit synthetic versus live evidence. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/MessageContractRunnerPrerequisiteTests.cs` | Adapted | Updated for current fixture/API or qualified-image evidence; source scenarios retained. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/MessageContractRunnerTests.cs` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/MessageContractSqlServerTests.cs` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/MessageContractUpsertTests.cs` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/packages.lock.json` | Regenerated | Target dependency graph retained; added direct Confluent.Kafka reference uses the existing resolved version. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/EdFi.DataManagementService.Backend.Cdc.Tests.Unit.csproj` | Adapted | Preserve target dependencies and helper links; add fixture imports and runner assets. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/MessageContractAdmissionFixture.cs` | Adapted | Current REST transport, production observation mapping and provider adapter; synthetic evaluator prerequisites; numeric string offset rejected; added lag identity cases. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/MessageContractAdmissionOffsetTests.cs` | Adapted | Current REST transport, production observation mapping and provider adapter; synthetic evaluator prerequisites; numeric string offset rejected; added lag identity cases. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/MessageContractAdmissionTests.cs` | Adapted | Current REST transport, production observation mapping and provider adapter; synthetic evaluator prerequisites; numeric string offset rejected; added lag identity cases. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/MessageContractConsumer.cs` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/MessageContractConsumerBootstrap.cs` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/MessageContractConsumerBootstrapTests.cs` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/MessageContractConsumerContinuityTests.cs` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/MessageContractConsumerOrderingTests.cs` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/MessageContractFixtureCatalog.cs` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/MessageContractFixtureTests.cs` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/MessageContractJson.cs` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/MessageContractTraceabilityTests.cs` | Adapted | Reconcile executable discovery and renamed claims; preserve IDs of unchanged tests; package and export evidence. |
| `src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Unit/packages.lock.json` | Superseded | Target dependency lock retained; obsolete Control dependency changes omitted. |
| `src/dms/backend/Fixtures/cdc/message-contract/MessageContractFixtures.props` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/Fixtures/cdc/message-contract/README.md` | Adapted | Current controller boundaries and shared qualification; explicit synthetic versus live evidence. |
| `src/dms/backend/Fixtures/cdc/message-contract/catalog.json` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/Fixtures/cdc/message-contract/failure-catalog.json` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/Fixtures/cdc/message-contract/partition-vectors.json` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/Fixtures/cdc/message-contract/providers/postgresql-delete.json` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/Fixtures/cdc/message-contract/providers/postgresql-upsert.json` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/Fixtures/cdc/message-contract/providers/sqlserver-delete.json` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/Fixtures/cdc/message-contract/providers/sqlserver-upsert.json` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/Fixtures/cdc/message-contract/traceability.json` | Adapted | Reconcile executable discovery and renamed claims; preserve IDs of unchanged tests; package and export evidence. |
| `src/dms/backend/Fixtures/cdc/message-contract/variants/exact-numbers.json` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/Fixtures/cdc/message-contract/variants/nested-transport-fields.json` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/Fixtures/cdc/message-contract/variants/postgresql-fractional-second.json` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/Fixtures/cdc/message-contract/variants/signed-int64-min.json` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |
| `src/dms/backend/Fixtures/cdc/message-contract/variants/sqlserver-seven-digit-fraction.json` | Retained | Source behavior/assets retained unchanged; validation recorded in PORTING-RESULTS.md. |

## Supplemental target changes

- `eng/ci/Invoke-CdcQualification.ps1`, `Get-CdcQualificationMatrix.ps1`,
  `cdc-qualification.psm1`, their Pester tests, and the nightly workflow share the provider
  suite selection and export sanitized message-contract attachments. Matrix: 15 jobs.
- `RepresentationRestampCdcStateTests.cs` uses exact generated identity-column metadata,
  restores SQL Server snapshot settings after database reset, and registers a descriptor-only
  slug resolver that fails if unexpected resource links are requested.
- `CdcPostgresqlContinuityRefreshTests.cs` now tests the current slot-before-offset sampling
  order and immediate containment of a proven gap; its fake reports a verified stopped state.
- `CdcKafkaPolicyFixture.cs` performs its independent live worker-start offset-policy check
  through shared adapter inspection without reacquiring the held controller session.
- `CdcCommandContainmentTests.cs` verifies that unreserved-source cleanup preserves possible
  exposure history, matching the existing controller retirement contract.
- `CdcLifecycleOrdering.Tests.ps1` retains actual Compose fixture paths in the offline bridge
  and safely reads an optional hashtable key under strict mode. `CdcE2EWorkflow.Tests.ps1`
  copies the schema-package module required by its isolated fixture.

- `CdcControllerFixtureComposeKafka.cs` retains both shipped Compose files together in its
  private directory so wrapper handoff inventory resolves the neighboring broker file and
  the effective services preserve the shipped CDC listener and worker configuration.
- `CdcManagedLifecycleTests.cs` asserts repeated polling until the persistent-backlog
  deadline without assuming four polls fit that deadline; the release-on-fourth-poll
  scenario still requires all four observations.
- `CdcRetirementOffsetStoreTests.cs` reports subprocess exit diagnostics before checking
  handoff observation, so wrapper failures retain their actionable cause.

- `CdcConnectorTelemetryQualificationTests.cs` reuses the live fixture observation request
  for the rendered connector, replacing generic unit data with an unrelated broker policy.

These supplemental changes repair test setup or outdated assertions discovered during
baseline/full qualification. No production controller implementation was changed.
