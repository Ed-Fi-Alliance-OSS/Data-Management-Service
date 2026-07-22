---
jira: DMS-1240
spike: DMS-911
epic: DMS-1089
status: implemented
---

# Design: Replacement for `expandjsonsmt`

> DMS-1240 implemented the generic expand-JSON transform described here. The relational
> CDC design in DMS-1245 does not amend that completed transform contract. Its proposed
> DMS-specific `DocumentState` transform is separate work tracked in the relational CDC
> epic.

## Decision

Ed-Fi owns a small, generic root-level JSON-object-field expander in
`Ed-Fi-Alliance-OSS/Ed-Fi-Kafka-Connect`. It replaces the unmaintained
`RedHatInsights/expandjsonsmt` dependency without embedding a DMS table, operation,
message, key, timestamp, tombstone, or topic contract.

The implemented transform contract is:

- **Input:** a schema-backed Kafka Connect value record with one or more configured
  string fields containing JSON objects.
- **Configuration:** `sourceFields`, a list of root-level fields to expand.
- **Behavior:** parse each configured JSON-object string and replace that field with a
  structured Connect value.
- **Implementation:** Kafka Connect JSON facilities / Jackson, with no BSON dependency;
  public and compatible with the current Connect 3 runtime and the planned Connect 4 / Java
  17 baseline.
- **Non-goals:** nested dot paths, whole-record schemaless conversion, key transforms,
  source-table or Debezium-operation classification, envelope construction, timestamp
  normalization, tombstone synthesis, topic routing, or RedHat edge-case parity.

The transform class is `org.edfi.kafka.connect.transforms.ExpandJson$Value`; its only
mapping input is `sourceFields`.

## Why It Exists

The legacy DMS Debezium/Kafka connector used `RedHatInsights/expandjsonsmt` to convert
JSON strings into structured values. That plugin's last release was `0.0.7` in 2020, it
carried a BSON dependency unnecessary for the required behavior, and its package-private
outer transform class does not load under Kafka Connect 4's plugin scanner.

DMS-1240 therefore added the bounded Ed-Fi-owned replacement, removed the RedHat release
download from the Ed-Fi Kafka Connect image, contract-tested the replacement, and
published it through that repository's image workflow. Those completed deliverables must
not be redefined by a later DMS connector design.

## Backend Context

The retired legacy backend stored several JSON fields, including `EdfiDoc`, authorization
arrays, and hierarchy data. The relational backend does not preserve that shape:

- `dms.Document` is metadata-only;
- `dms.DocumentCache.DocumentJson` is the projected caller-agnostic document JSON; and
- authorization and EdOrg hierarchy state are relational rather than JSON fields in the
  streamed document.

The generic expander is capable of expanding `DocumentCache.DocumentJson`, but DMS-1240
does not decide whether a relational connector uses it. Captured tables, operation
mapping, keys, public values, tombstones, and topics belong to DMS-1245.

## Relationship to the Relational `DocumentState` Transform

DMS-1245 selects one DMS-specific `DocumentState` transform as the v1 boundary from raw
Debezium records to the final public record. That transform parses `DocumentJson`
directly while it also classifies source operations, preserves the delete key, normalizes
provider timestamps, copies `StreamEtag`, builds the public envelope, synthesizes the
authoritative tombstone, and routes the record.

Because the selected transform performs JSON parsing as part of that larger atomic
adapter, the relational v1 connector does not also configure the generic `ExpandJson`
transform. This is a consuming-architecture choice, not a replacement or amendment of
DMS-1240. The generic transform remains available to connectors whose requirement is only
root-level JSON expansion.

The new cross-repository implementation is explicitly planned in
[Add the relational DocumentState transform](../epics/19-cdc-kafka/03-document-state-transform.md).
Connector-template generation consumes that artifact; it does not silently implement it.

## DMS-1240 Contract Evidence

The completed generic-transform contract covers:

1. a valid JSON object;
2. nested objects;
3. homogeneous scalar and object arrays;
4. empty arrays;
5. null and missing configured fields;
6. a defined invalid-JSON failure mode; and
7. serialized output with `value.converter.schemas.enable=false`.

The implementation remains intentionally unaware of the DMS-1245 public record contract.

## Ownership and Handoffs

| Work | Owner |
| --- | --- |
| Generic `sourceFields` expansion and removal of the RedHat plugin | DMS-1240 — complete |
| Relational CDC sources, topic, key, value, delete, and compatibility contract | DMS-1245 |
| DMS-specific `DocumentState` implementation in Ed-Fi-Kafka-Connect | Proposed `19-03` story |
| Provider connector templates using the published `DocumentState` class | Proposed `19-02` story |
| API-driven relational Kafka scenarios | DMS-1232 / proposed `19-06` story |

DMS-1232's implementation criteria must be refined against the DMS-1245 topic/message
contract before that work begins. In particular, the relational v1 delete is a Kafka-null
tombstone rather than the legacy `deleted=true` document body.

## References

- [Relational CDC and Document Projection](../../cdc-streaming.md)
- [Kafka topic and message contract](cdc/0002-kafka-topic-and-message-contract.md)
- [RedHatInsights/expandjsonsmt](https://github.com/RedHatInsights/expandjsonsmt)
- [RedHatInsights/expandjsonsmt issue #19](https://github.com/RedHatInsights/expandjsonsmt/issues/19)
- [Ed-Fi-Alliance-OSS/Ed-Fi-Kafka-Connect](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-Kafka-Connect)
