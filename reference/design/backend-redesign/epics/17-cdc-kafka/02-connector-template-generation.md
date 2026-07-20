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
   Ed-Fi `DocumentState` SMT convert `datetime2(7)`
   `io.debezium.time.NanoTimestamp` values to the contract's lossless UTC ISO string.
4. Add one Ed-Fi `DocumentState` SMT that consumes raw Debezium records and atomically
   owns source/operation classification, filtering, envelope extraction, direct
   `DocumentJson` parsing, opaque `StreamEtag` copying to `document._etag`, key and value
   shaping, provider timestamp normalization, tombstone synthesis, metadata consistency
   checks, and topic routing.
5. Configure the connector to use that transform as the complete boundary from a raw
   Debezium record to a final public upsert, final public tombstone, or dropped record.
   Do not add an independent generic expand-JSON SMT or split the contract across stock
   predicate, unwrap, rename, and routing chains.
6. Configure only the transform's `provider` and `target.topic` values. Keep the DMS
   source table/column and v1 public-field mapping fixed in the versioned transform rather
   than generating a mapping DSL.
7. Keep `EffectiveSchemaHash`, link-option interpretation, and DMS ETag composition out
   of connector transforms.
8. Validate all version-specific properties and transform classes against the pinned
   `edfialliance/ed-fi-kafka-connect` image.

## Acceptance Evidence

- Rendering tests cover representative providers and reject invalid production topic
  prefixes or incomplete inputs.
- Fixture tests cover every retained and dropped operation from each source table.
- Serialized-record tests enforce the topic/message ADR, including exact key/value byte
  forms, exact copying of `StreamEtag` to `document._etag`, expanded JSON, timestamp
  formatting, and tombstones.
- Transform tests begin with realistic raw PostgreSQL and SQL Server Debezium envelopes
  and assert the final topic, key, and value together; they do not rely on pre-unwrapped
  fixture values.
- SQL Server rendering tests require `time.precision.mode=adaptive`; transform fixtures
  carry the `io.debezium.time.NanoTimestamp` logical type and prove lossless conversion
  through seven fractional digits, trailing `Z`, and exact equality with the embedded
  `_lastModifiedDate`.
- Tests fail when `document._etag` differs from the projected source value or a top-level
  envelope `etag` is emitted, and prove the transform does not attempt to derive a
  variant key.
- A pinned-image smoke test proves configured transform classes load.

## Out of Scope

- Bootstrap command wiring.
- Full API-driven E2E scenarios.
- Production credential provisioning.
