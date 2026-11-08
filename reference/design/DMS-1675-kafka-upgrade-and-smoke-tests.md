---
jira: DMS-1675
status: Implementation complete; live qualification pending
---

# DMS-1675: Upgrade Kafka and qualify CDC against Apache Kafka

Jira: https://edfi.atlassian.net/browse/DMS-1675

## Problem

The Kafka update originally proposed by Dependabot #1426 (3.9.0 to 4.3.1) was included in dependency consolidation PR #1436 and subsequently raised to 4.3.2 to address broker vulnerabilities reported against 4.3.1. Kafka qualification and test-fixture changes exceed the scope of that dependency bundle and need a separate PR.

At the initial split, Compose selected the new broker while CdcComposeBrokerSizeDeployment.BrokerImage still pinned 3.9.0 and checked the deployed image for exact equality. Native Kafka fixtures consumed that stale constant. The Backend CDC Connector Template Smoke Tests used Redpanda startup and rpk commands, so they did not qualify Apache Kafka 4.3.2. Normal PR checks skip this manually dispatched smoke lane and do not establish that the live RecordSize qualification suites pass.

## Intended outcome

Use the same digest-pinned Apache Kafka 4.3.2 broker in the DMS deployment and primary CDC smoke/qualification fixtures, with a durable check preventing image-pin drift. Keep the non-Kafka Dependabot updates in PR #1436 independently reviewable.

## Scope and acceptance criteria

1. Move the Kafka version bump out of PR #1436 into a separate PR. Target apache/kafka:4.3.2@sha256:852a0d49fc4e656b022e7f23785411802c9a088c78a7c5e39f33969de9c227a7. The new PR begins as a draft; the bump alone does not complete this story.
2. Align CdcComposeBrokerSizeDeployment.BrokerImage and all applicable native Kafka fixture pins with the shipped Compose broker. Add a meaningful consistency check that fails when the Compose image and application/fixture authority disagree.
3. Migrate the primary connector-template smoke lane from Redpanda to Apache Kafka, reusing existing native Kafka fixture support where appropriate. Replace Redpanda-specific startup and rpk operations; preserve connector validation/registration, classloading, partitioner, heartbeat, offset progress, and restart assertions.
4. Audit shared fixture callers, workflow variables, configuration, and documentation. Remove obsolete Redpanda requirements from migrated paths. Retain a separate Redpanda compatibility lane only if an explicit supported-deployment requirement is established; do not break other callers by removing shared behavior blindly.
5. Run connector-template smoke tests and live PostgreSQL and MSSQL RecordSize qualification suites against the new PR head. Verify Compose-backed broker-size changes and restart persistence use the intended Kafka image. Run relevant unit/contract tests and other affected live Kafka suites. Record actual results and failures; a skipped test or offline contract result is not live qualification.
6. Assess the 3.9.0 to 4.3.2 major-version transition, including startup and existing-data compatibility; document any required upgrade steps or restrictions.
7. Refresh the pinned-image vulnerability assessment and record remaining findings and their disposition. The prior 2026-11-03 Linux/amd64 Trivy scan found 9 package findings: 1 critical, 3 high, 2 medium, and 3 unknown. Critical: CVE-2026-111497 (brotli-libs); high: CVE-2026-117610, CVE-2026-118370, CVE-2026-77214 (libexpat). These package findings do not establish exploitability. Do not describe 4.3.2 as security-cleared without resolving or assessing them.

## Implementation status

Pin alignment and its Compose drift test, the shared smoke-fixture migration, and
the shared caller/configuration/documentation audit are implemented. Record-size
helpers read Kafka broker settings, telemetry creates topics with Kafka tools, and
restamp tests consume through Kafka's console consumer. Startup/cleanup tests use
the shared pin. Both workflows and the runner no longer require Redpanda; the
runner and smoke workflow resolve and validate the branch's Kafka pin. No explicit
Redpanda support requirement was found, so no compatibility lane is retained.

Live smoke, provider RecordSize, Compose persistence and affected Kafka suite
qualification remain pending, as do the 3.9.0 upgrade compatibility assessment and
refreshed security scan/disposition. Offline contract checks do not establish any
of those outcomes.

## References

- Dependency bundle: https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1436
- Original Kafka Dependabot proposal (closed): https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/pull/1426
- Broker Compose: eng/docker-compose/kafka-broker.compose.yml
- Deployment image authority: src/dms/backend/EdFi.DataManagementService.Backend.Cdc/CdcComposeBrokerSizeDeployment.cs
- Fixture: src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/CdcConnectorTemplatePinnedImageFixture.cs
- Native Kafka fixtures: CdcControllerFixtureKafka.cs and CdcControllerFixtureComposeKafka.cs in the same integration-test directory
- Qualification guide: docs/CDC-QUALIFICATION.md
