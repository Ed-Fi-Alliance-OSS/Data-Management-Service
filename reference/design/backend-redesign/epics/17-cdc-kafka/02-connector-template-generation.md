---
jira: TBD
source_spike: DMS-1245
epic: TBD
related:
  - DMS-1232
---

# Story: Generate PostgreSQL and SQL Server Connector Templates

## Design References

- [Connector transform pipeline](../../../cdc-streaming.md#connector-transform-pipeline)
- [Connector topology and provider setup](../../../cdc-streaming.md#connector-topology-and-provider-setup)
- [Topic and message contract](../../design-docs/cdc/0002-kafka-topic-and-message-contract.md)

## Outcome

Generate parameterized provider connector configurations that implement the authoritative
source routing and serialized public contract using the separately published
`DocumentState` transform.

## Dependencies

- Depends on 17-01 for provider CDC setup and 17-02a for a runnable published transform.
  Template authoring and rendering tests may proceed in parallel with 17-02a, but no
  connector is registered until the pinned image contains that class.

## Deliverables

1. Define inputs from the immutable deployment binding for provider/source fingerprint,
   target and generation identity, connector/topic names, `contractVersion`,
   credentials, replication/capture identity, and snapshot behavior.
2. Generate PostgreSQL and SQL Server connector configurations without hard-coded
   instance values.
3. Configure SQL Server with `time.precision.mode=adaptive` explicitly and make the
   Ed-Fi `DocumentState` SMT convert `datetime2(7)`
   `io.debezium.time.NanoTimestamp` values to the contract's lossless UTC ISO string.
4. Configure the `DocumentState` SMT delivered by 17-02a as the complete boundary from a raw
   Debezium record to a final public upsert, final public tombstone, or dropped record.
   Do not add an independent generic expand-JSON SMT or split the contract across stock
   predicate, unwrap, rename, and routing chains.
5. Configure only the transform's `provider` and `target.topic` values. Keep the DMS
   source table/column and v1 public-field mapping fixed in the versioned transform rather
   than generating a mapping DSL.
6. Keep `EffectiveSchemaHash`, link-option interpretation, and DMS ETag composition out
   of connector transforms.
7. Validate all version-specific properties and transform classes against the pinned
   `edfialliance/ed-fi-kafka-connect` image.

## Acceptance Evidence

- Rendering tests cover representative providers and reject invalid production topic
  prefixes, incomplete binding inputs, or generated identities that differ from the
  binding record.
- Template tests prove the configured class, source includes, key columns, converters,
  tombstone suppression, transform properties, and target topic match the binding and
  17-02a contract.
- SQL Server rendering tests require `time.precision.mode=adaptive`; pinned-image smoke
  coverage proves the published transform converts a realistic retained record.
- A pinned-image smoke test proves the configured transform class loads; detailed
  transform behavior remains owned by 17-02a and the shared contract suite in 17-04.

## Out of Scope

- Bootstrap command wiring.
- Full API-driven E2E scenarios.
- Production credential provisioning.
