---
jira: TBD
source_spike: DMS-1245
epic: TBD
related:
  - DMS-1240
---

# Story: Generate PostgreSQL and SQL Server Connector Templates

## Design References

- [Connector transform pipeline](../../../cdc-streaming.md#connector-transform-pipeline)
- [Connector topology and provider setup](../../../cdc-streaming.md#connector-topology-and-provider-setup)
- [Topic and message contract](../../design-docs/cdc/0002-kafka-topic-and-message-contract.md)

## Outcome

Generate parameterized provider connector configurations that implement the authoritative
source routing and serialized public contract.

## Deliverables

1. Define inputs for provider/source, credentials, instance identity, deployment topic
   prefix, replication/capture identity, and snapshot behavior.
2. Generate PostgreSQL and SQL Server connector configurations without hard-coded
   instance values.
3. Configure SQL Server with `time.precision.mode=adaptive` explicitly and make the
   Ed-Fi document-state shaping SMT convert `datetime2(7)`
   `io.debezium.time.NanoTimestamp` values to the contract's lossless UTC ISO string.
4. Configure source classification, value shaping, JSON expansion, opaque `StreamEtag`
   copying to envelope `etag` and `document._etag`, key simplification, tombstone
   handling, and topic routing.
5. Add the small Ed-Fi routing/shaping SMT required for SQL Server timestamp conversion
   and for any other contract behavior that verified stock transforms cannot safely
   implement.
6. Keep `EffectiveSchemaHash`, link-option interpretation, and DMS ETag composition out
   of connector transforms.
7. Validate all version-specific properties and transform classes against the pinned
   `edfialliance/ed-fi-kafka-connect` image.

## Acceptance Evidence

- Rendering tests cover representative providers and reject invalid production topic
  prefixes or incomplete inputs.
- Fixture tests cover every retained and dropped operation from each source table.
- Serialized-record tests enforce the topic/message ADR, including exact key/value byte
  forms, exact copying of `StreamEtag` to both public locations, expanded JSON,
  timestamp formatting, and tombstones.
- SQL Server rendering tests require `time.precision.mode=adaptive`; transform fixtures
  carry the `io.debezium.time.NanoTimestamp` logical type and prove lossless conversion
  through seven fractional digits, trailing `Z`, and exact equality with the embedded
  `_lastModifiedDate`.
- Tests fail when either public ETag differs from the projected source value and prove
  the transform does not attempt to derive a variant key.
- A pinned-image smoke test proves configured transform classes load.

## Out of Scope

- Bootstrap command wiring.
- Full API-driven E2E scenarios.
- Production credential provisioning.
