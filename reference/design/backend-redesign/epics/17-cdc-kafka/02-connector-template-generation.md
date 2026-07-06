---
jira: TBD
source_spike: DMS-1245
epic: TBD
related:
  - DMS-1240
---

# Story: Generate PostgreSQL and SQL Server Connector Templates

## Description

Generate or parameterize Debezium source connector configurations for relational CDC.

The templates capture only `dms.DocumentCache`, key records by `DocumentUuid`, publish to the instance document
topic, preserve tombstones, and shape create/update values into the v1 Kafka contract.

## Acceptance Criteria

- PostgreSQL connector template captures only the selected instance database's `dms.DocumentCache`.
- SQL Server connector template captures only the selected instance database's `dms.DocumentCache`.
- Connector templates do not contain hard-coded database names, topic names, replication slot names, or data
  store IDs.
- Connector configuration sets the Kafka key to `DocumentUuid` for create/update/delete records.
- Transform pipeline produces:
  - lower-camel envelope fields,
  - `contractVersion = 1`,
  - no public `DocumentId`,
  - no public `ComputedAt`,
  - structured `document`, using the Ed-Fi expand-JSON SMT from DMS-1240 when needed,
  - tombstones with null values passed through unchanged,
  - topic routing to `<topic-prefix>.instance.<instance-key>.documents.v1`.
- If stock Kafka Connect SMTs cannot produce the exact contract without breaking tombstones, the story adds a
  small Ed-Fi value-shaping SMT and tests it directly.
- Template validation tests assert the published Kafka record shape, not only the connector JSON.
- Version-specific Debezium and SMT property names are verified against the pinned
  `edfialliance/ed-fi-kafka-connect` image.

## Tasks

1. Define connector template inputs: provider, host, port, database name, credentials, data store ID or instance
   key, topic prefix, replication slot/capture names, and snapshot mode.
2. Build PostgreSQL connector template generation.
3. Build SQL Server connector template generation.
4. Implement or configure transform pipeline for value shaping, JSON expansion, key simplification, tombstone
   preservation, and topic routing.
5. Add unit tests for rendered connector JSON from representative PostgreSQL and SQL Server inputs.
6. Add a connector smoke test that starts the pinned Connect image and verifies the transform classes load.
7. Add a fixture-based test that feeds representative Debezium records through the transform pipeline and asserts
   the v1 contract.

## Out of Scope

- Bootstrap command wiring.
- E2E API-driven Kafka scenarios.
- Production credential provisioning.
