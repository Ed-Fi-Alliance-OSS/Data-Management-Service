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
   target and generation identity, connector/topic names, fixed topic partition count,
   `contractVersion`, credentials, replication/capture identity, and snapshot behavior.
2. Generate one PostgreSQL or SQL Server connector configuration per DMS instance and
   immutable binding, without hard-coded instance values. Scope each connector to exactly
   one instance database and reject SQL Server configurations that select multiple
   databases.
   Configure `message.key.columns` to use `DocumentUuid` for both captured tables without
   requiring it to be the `DocumentCache` primary key or a cache index.
3. Configure SQL Server with `time.precision.mode=adaptive` explicitly and make the
   Ed-Fi `DocumentState` SMT convert `datetime2(7)`
   `io.debezium.time.NanoTimestamp` values to the existing DMS whole-second UTC string,
   deliberately truncating fractional seconds without rounding.
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
8. Pin and validate one key-based partitioner for the binding lifetime; do not rely on an
   image upgrade silently retaining equivalent key-to-partition behavior.
9. Emit the fixed source-producer overrides `enable.idempotence=true`, `acks=all`,
   `retries=2147483647`, and `max.in.flight.requests.per.connection=5` using the Kafka
   Connect `producer.override.*` connector properties. Do not expose them as template
   inputs. Reject duplicate properties or any attempted override that conflicts with
   these values.

## Acceptance Evidence

- Rendering tests cover representative providers and reject invalid production topic
  prefixes, incomplete binding inputs, nonpositive or changed partition counts, or
  generated identities that differ from the binding record.
- Pinned-image tests prove identical serialized `DocumentUuid` keys route consistently
  under the selected partitioner; changing the partitioner or partition count requires a
  new binding generation/topic.
- Template tests prove the configured class, source includes, key columns, converters,
  tombstone suppression, transform properties, and target topic match the binding and
  17-02a contract.
- Provider template/smoke tests prove the non-indexed cache UUID column produces the same
  public key bytes as the canonical document-delete source.
- SQL Server rendering tests require `time.precision.mode=adaptive`; pinned-image smoke
  coverage proves the published transform converts a realistic retained record.
- SQL Server rendering tests prove that each connector selects exactly one instance
  database and one binding topic, and reject attempted multi-database consolidation.
- Rendering tests require the exact ordering-safe source-producer overrides and reject
  idempotence disabled, acknowledgements other than `all`, fewer retries, more than five
  in-flight requests, and duplicate/conflicting producer properties.
- A pinned-image smoke test proves the configured transform class loads; detailed
  transform behavior remains owned by 17-02a and the shared contract suite in 17-04.

## Out of Scope

- Bootstrap command wiring.
- Full API-driven E2E scenarios.
- Production credential provisioning.
- Multi-database SQL Server connectors and source-aware topic routing.
