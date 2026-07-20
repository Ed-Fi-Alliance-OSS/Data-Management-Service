---
jira: DMS-1240
spike: DMS-911
epic: DMS-1089
---

# Design: Replacement for `expandjsonsmt`

> Status: Proposed (DMS-911 spike). This document is the design deliverable for
> DMS-911 ("Look for an alternative for `expandjsonsmt`"). It records the
> investigation, the recommended path, and the follow-up implementation ticket.
> No implementation is in scope for DMS-911 itself; the implementation is
> tracked in DMS-1240.

## Summary

The legacy DMS Debezium/Kafka source-connector pipeline uses the
`RedHatInsights/expandjsonsmt` Single Message Transform (SMT) to turn
JSON-string fields in change records into structured JSON before publishing to
Kafka. That plugin is effectively unmaintained (last release `0.0.7`, March
2020) and **fails to load on Kafka Connect 4.x** because its outer transform
class is package-private.

**Recommendation:** replace the legacy expander in the forward relational pipeline with
one Ed-Fi-owned, DMS-specific **`DocumentState` SMT** in the existing Kafka Connect plugin
(`Ed-Fi-Alliance-OSS/Ed-Fi-Kafka-Connect`). It consumes raw Debezium records from the two
captured relational tables and emits the final v1 document-state upsert, final tombstone,
or no record. Parsing `DocumentJson` is one responsibility inside that contract adapter;
there is no independent generic expand-JSON SMT in the relational connector.

The transform should:

- classify source table and operation before discarding the Debezium envelope;
- shape the key and value together, including direct JSON parsing, provider timestamp
  normalization, nested `_etag` injection, envelope construction, and metadata checks;
- synthesize exactly one authoritative tombstone and route retained records to the
  configured instance topic;
- be built on Kafka Connect's JSON facilities / standard Jackson, not BSON; and
- be public and compatible with the pinned Connect runtime and the planned Kafka 4 / Java
  17 baseline.

Its behavioral contract is the DMS-1245 public topic/message contract, not RedHat
edge-case parity or a reusable field-expansion abstraction.

The legacy backend and its six-field `edfidoc` connector shape are not a migration
target. The relational connector follows
[Relational CDC and Document Projection](../../cdc-streaming.md) and the
[topic/message contract](cdc/0002-kafka-topic-and-message-contract.md). Its single local
SMT integration point is `org.edfi.kafka.connect.transforms.DocumentState`; DMS-1232 owns
replacement E2E coverage.

## Background

### What functionality needs replacing

The Debezium source connector reads change records and serializes them to Kafka
with `JsonConverter`. Columns that hold JSON are emitted by Debezium as
**JSON-encoded strings** (PostgreSQL `jsonb`/`json` columns surface as
`io.debezium.data.Json`, i.e. a string) — even though the database column is
structured. Without expansion, downstream consumers receive an escaped JSON
string. The relational connector must parse `DocumentJson` before publishing so the
public message carries real nested JSON.

That parsing is required regardless of the physical database JSON type. It does not,
however, require a standalone generic expander: the relational connector already needs a
custom contract transform for behaviors that a root-field expander cannot provide.

### Why it must change

- Last release `0.0.7` (2020-03-05); the project is unmaintained.
- It **no longer loads on Kafka Connect 4.x**. Confirmed in source: the outer
  class is `abstract class ExpandJSON<R...> implements Transformation<R>`
  (package-private), with `public static class Value extends ExpandJSON`. Kafka
  Connect 4's plugin scanner requires the `Transformation` implementation to be
  public. Upstream issue
  [RedHatInsights/expandjsonsmt#19](https://github.com/RedHatInsights/expandjsonsmt/issues/19)
  ("The `ExpandJSON` class must be declared `public` starting from Kafka
  v4.x.x") is **open** (opened 2026-01-06).
- It carries an `org.mongodb/bson` runtime dependency used for its JSON parsing
  and BSON-type-driven schema inference — heavyweight machinery this design does
  not need.

### Current runtime state (this is preventive, not a live outage)

The Ed-Fi Kafka Connect image (`edfialliance/ed-fi-kafka-connect:pre`, built in
`Ed-Fi-Alliance-OSS/Ed-Fi-Kafka-Connect`) is built
`FROM debezium/connect:2.7.0.Final`, which is on the **Kafka Connect 3.x** line.
RedHat `0.0.7` still loads there, so the legacy connector can load today. The failure is
a **forward-looking blocker** for a future Debezium 3 / Kafka Connect 4 / Java 17
upgrade. DMS-911 also provides the opportunity to replace the legacy field-expansion
boundary with the complete relational contract transform.

The image installs `expandjsonsmt` by downloading the RedHat release tarball at
image-build time:

```dockerfile
ARG expandjsonsmt=kafka-connect-smt-expandjsonsmt-0.0.7.tar.gz
ADD --chown=kafka --chmod=600 https://github.com/RedHatInsights/expandjsonsmt/releases/download/0.0.7/${expandjsonsmt} /kafka/connect/
RUN tar -xvf ${expandjsonsmt} && rm ${expandjsonsmt}
```

The same repo already builds and ships an Ed-Fi SMT jar via Gradle
(`org.edfi.kafka.connect.transforms.*`), so adding another transform there is
low-friction.

## Backend context: legacy vs current schema

This matters because the **existing connector configs target the legacy backend
schema**, which no longer reflects the go-forward design. The new transform encodes only
the DMS-1245 relational source and public contract; it does not preserve or generalize
the legacy schema.

### Legacy backend (`EdFi.DataManagementService.Old.Postgresql`) — being retired

The legacy backend is being retired. Its connector configs and the original
DMS-911 field list correspond to the legacy `dms.Document` table, which stored
multiple JSON columns:

```sql
EdfiDoc JSONB NOT NULL,
SecurityElements JSONB NOT NULL,
StudentSchoolAuthorizationEdOrgIds JSONB NULL,
StudentEdOrgResponsibilityAuthorizationIds JSONB NULL,
ContactStudentSchoolAuthorizationEdOrgIds JSONB NULL,
StaffEducationOrganizationAuthorizationEdOrgIds JSONB NULL,
```

plus `dms.EducationOrganizationHierarchyTermsLookup.Hierarchy JSONB`. The
current connectors expand five of those document fields (`edfidoc`,
`securityelements`, and the three `...edorgids` arrays) plus `hierarchy`.

### Current relational backend (the go-forward design)

Verified against the authoritative generated DDL
(`src/dms/backend/Fixtures/authoritative/sample/expected/pgsql.sql`):

- **Canonical JSON lives in one column:**
  `dms."DocumentCache"."DocumentJson" jsonb NOT NULL`, constrained with
  `CHECK (jsonb_typeof("DocumentJson") = 'object')`.
- `dms."Document"` is **metadata-only** (versioning, ownership token); it holds
  no JSON.
- The legacy `EdfiDoc` / `SecurityElements` / `*AuthorizationEdOrgIds` /
  `Hierarchy` JSON columns **do not exist** in the relational schema.
  Authorization and the ed-org hierarchy are implemented **relationally** —
  `edfi.*` tables with `TF_TR_*_AuthHierarchy_*` triggers and auth views — not
  as JSON arrays on the document.

For the go-forward backend, the one JSON value that must become a structured public
document is `dms.DocumentCache.DocumentJson`. The `DocumentState` SMT parses that source
column while building the complete v1 envelope. The multi-field / array-of-ids /
hierarchy-array shape is a legacy concern.

### Relational Kafka streaming contract (DMS-1245) and E2E follow-up (DMS-1232)

Legacy Kafka E2E expectations were tied to DMS-1232 ("until relational backend
Kafka streaming supports document create/update/delete"). The current DMS
checkout does not carry active legacy Kafka feature coverage; DMS-1232 should add
or replace E2E coverage for the relational CDC contract. Existing checked-in
connector configs are legacy-oriented and do not implement the DMS-1245
relational CDC contract. Relational change tracking today is the polling Change
Queries API (`ContentVersion`), a separate mechanism from Debezium/Kafka.

**Consequence:** the replacement is intentionally contract-specific. The `DocumentState`
SMT implements the DMS-1245 relational source mapping and public record shape instead of
exposing a generic `sourceFields` expansion stage. Legacy connector configs are removed
rather than repointed; DMS-1232 validates the relational contract instead of the legacy
shape.

## Decision

### Recommended: one Ed-Fi-owned `DocumentState` SMT

Add a new transform to `org.edfi.kafka.connect.transforms` in the Ed-Fi Kafka
Connect repo. It is a bounded adapter for the DMS relational document-state contract,
is public and Kafka 4 / Java 17 ready, and uses Connect JSON / Jackson rather than BSON.

### Transform contract

- **Input:** a schema-backed raw Debezium record from the configured
  `dms.DocumentCache` or `dms.Document` source, including its key and envelope.
- **Config:** only `provider=postgresql|sqlserver` and `target.topic=<instance document
  topic>`. Source table/column and public-field semantics are fixed by the versioned
  transform contract rather than exposed as a generic mapping DSL.
- **Upsert behavior:** retain cache create/update/snapshot operations, extract and
  normalize the key, parse `DocumentJson`, normalize the provider timestamp, build the
  lower-camel envelope, copy `StreamEtag` to `document._etag`, verify embedded metadata,
  and route the final record.
- **Delete behavior:** turn a canonical document delete into one routed record-level null
  tombstone for the normalized key.
- **Drop behavior:** return no record for every source operation excluded by the
  projector/source ADR, including cache deletes and non-delete canonical events.
- **Failure behavior:** fail malformed retained records, invalid JSON, unexpected logical
  types, precision loss, and inconsistent key or embedded metadata. Do not publish a
  partial record or silently pass through an unknown source shape.
- **Non-goals:** a generic field-expansion API, a configurable record-mapping language,
  legacy multi-field compatibility, DMS ETag calculation, or RedHat edge-case parity.

### Alternatives considered

| Option | Disposition |
| --- | --- |
| **Port RedHat verbatim + keep BSON** | Rejected. Its only advantage was preserving the legacy six-field message shape, which greenfield relational streaming does not require. Carrying BSON, anonymous-struct quirks, mixed-array behavior, and old invalid-JSON semantics is over-scoped for expanding one object field. |
| **Add an independent generic expand-JSON SMT** | Rejected. It cannot own table/operation classification, key conversion, nested ETag injection, tombstone synthesis, or topic routing. The connector would still require a custom transform and ordering-sensitive intermediate records. |
| **Adopt `joshuagrisham/kafka-connect-expand-json-transform`** | Rejected for the relational connector. Root-level expansion covers only one step of the required contract and adds a single-maintainer external dependency. It may remain a JSON-parsing reference, subject to license attribution if code is incorporated. |
| **Fork RedHat into a new Ed-Fi repo** | Rejected. Creates a standalone repo + release pipeline when Ed-Fi already maintains a Kafka Connect plugin that can host the code. |
| **Predicate-heavy stock-SMT chain plus a custom fallback** | Rejected. A custom transform is already required for the pinned SQL Server timestamp representation, and the source mapping couples pre-unwrap classification to key-preserving tombstone behavior. Splitting the contract adds configuration and ordering risk without creating a reusable boundary. |
| **Publish `DocumentJson` as a string** | Rejected. The v1 contract requires a structured `document` object with a nested `_etag`. |

### Relationship to DMS-1245 and DMS-1232

DMS-1245 owns the linked relational CDC architecture and public contract. DMS-911 /
DMS-1240 deliver the Kafka-4-ready `DocumentState` implementation of that contract.
DMS-1232 is the E2E follow-up.

## Scope

- Add a public, Kafka 4 / Java 17-ready `DocumentState` SMT to
  `Ed-Fi-Alliance-OSS/Ed-Fi-Kafka-Connect`, built on Connect JSON / Jackson with no BSON.
- Implement the complete DMS-1245 raw-Debezium-to-public-record contract in that one
  transform, including direct `DocumentJson` parsing.
- Remove the RedHat `0.0.7` tarball download/extract from that repo's
  `kafka/Dockerfile`; the transform ships in the Ed-Fi SMT jar instead.
- The relational streaming connector — designed by DMS-1245 and implemented by
  the CDC/Kafka epic — configures only the new contract transform. The legacy connector
  configs are removed with the legacy backend, not repointed.

## Non-goals

- Supporting arbitrary captured tables, source fields, envelope mappings, or routing
  rules. The transform implements the versioned DMS contract rather than a mapping DSL.
- Reproducing legacy multi-field expand-JSON configuration.
- Calculating `StreamEtag`, interpreting DMS schema or link configuration, or shaping
  readable-profile-specific documents.
- The broader Debezium 3 / Kafka Connect 4 base-image upgrade itself (the
  transform is built to load on both Connect 3.x and 4.x).
- Un-quarantining DMS-1232.

## Compatibility / runtime baseline check

The transform depends only on the stable `connect-api` / `connect-transforms`
(and `connect-json`) interfaces, so the same artifact is intended to load on
both the current Connect 3.x runtime and a future Connect 4 runtime once the
class is public.

**The compile target must match the runtime JDK** and is a blocking
implementation check:

- Verified: the current image is on the **Kafka Connect 3.x** line (Debezium
  2.7.0.Final).
- Must verify against the pinned image digest at implementation time: the exact
  JDK. If the current image runs **Java 11**, a Java 17 bytecode plugin will
  throw `UnsupportedClassVersionError` on the current image — in that case
  either compile to the image's bytecode level now (and bump later) or bump the
  base image first. If it runs **Java 17**, the Connect 4-ready plugin loads on
  the current image as well.

## Contract validation (gate before any connector uses the new class)

Test the **desired Ed-Fi contract**, not RedHat parity. Cover:

1. Cache create, update, and snapshot/read envelopes produce final public upserts.
2. Canonical document delete produces exactly one final public tombstone; cache delete
   and every other canonical operation produce no record.
3. The Debezium key struct becomes lowercase `DocumentUuid` text for both upserts and
   tombstones and exactly matches `document.id` for upserts.
4. A valid `DocumentJson` object becomes the structured `document` value, including
   nested objects, real Ed-Fi scalar/object arrays, and empty arrays.
5. `StreamEtag` appears only as `document._etag`; internal and operational source columns
   do not appear in the public record.
6. PostgreSQL and SQL Server timestamps produce the exact public UTC representation. SQL
   Server `NanoTimestamp` fixtures prove no loss through seven fractional digits.
7. Missing fields, invalid/non-object JSON, unexpected source schemas or logical types,
   precision loss, and key/embedded-metadata mismatches fail closed.
8. The configured instance topic receives the final upsert or tombstone, and output with
   `value.converter.schemas.enable=false` has the exact public byte-level shape.

This validation is a prerequisite for wiring the relational connector — no DMS
connector config may point at the new class until the contract is validated.

## Affected files

| Repo | File | Change |
| --- | --- | --- |
| Ed-Fi-Kafka-Connect | `src/.../transforms/DocumentState.java` (+ helpers) | New — DMS relational contract transform |
| Ed-Fi-Kafka-Connect | `kafka/Dockerfile` | Remove RedHat `ADD` + `tar` lines |
| Ed-Fi-Kafka-Connect | `build.gradle` / `NOTICES.md` | Runtime-compatible bytecode target; no BSON; attribution only if third-party code is incorporated |
| Ed-Fi-Kafka-Connect | unit tests | New — raw Debezium-to-final-record contract matrix |
| DMS (legacy, retiring) | `eng/docker-compose/postgresql_connector.json` | Removed with legacy backend retirement |
| DMS (legacy, retiring) | `eng/docker-compose/data_store_connector_template.json` | Removed with legacy backend retirement |
| DMS (legacy, retiring) | `src/dms/tests/EdFi.InstanceManagement.Tests.E2E/Infrastructure/DebeziumConnectorClient.cs` | Removed/replaced with legacy backend retirement |
| DMS (relational CDC/Kafka) | new relational streaming connector config | Uses `org.edfi.kafka.connect.transforms.DocumentState` as its single contract SMT |

Proposed class path: `org.edfi.kafka.connect.transforms.DocumentState` (matching the
existing Ed-Fi transform package). Its configuration names the provider and destination
topic; it has no `sourceFields` or other mapping configuration.

## Follow-up implementation ticket

A single ticket covers the work, all in `Ed-Fi-Alliance-OSS/Ed-Fi-Kafka-Connect`:
build the SMT, contract-validate it, remove the RedHat download, and publish the
image. These land together in one repo and PR — building the SMT and removing
the download are inseparable, and the image publish is the repo's CI release on
merge.

Acceptance criteria:

- Public transform class in `org.edfi.kafka.connect.transforms` loads under
  Kafka Connect 4.x (and the current 3.x runtime); compile target verified
  against the image JDK.
- Built on Connect JSON / Jackson; **no BSON dependency**.
- Contract-specific: accepts raw records from the two DMS relational sources and emits
  only a final public upsert, final public tombstone, or no record as defined by DMS-1245.
- Parses `DocumentJson` directly and owns key conversion, envelope construction,
  timestamp normalization, nested ETag injection, metadata validation, tombstone
  synthesis, and topic routing. No independent expand-JSON SMT is configured.
- **Contract validated** per the Contract-validation section, with unit tests
  per case.
- `kafka/Dockerfile` no longer downloads `expandjsonsmt`; the published image
  ships the Ed-Fi transform, starts cleanly, and has no build-time dependency on
  the RedHat GitHub release.
- Attribution: if any RedHat or joshuagrisham code is incorporated, retain its
  Apache-2.0 notices and record the derivation in `NOTICES.md` (blocking before
  publish); otherwise implement the bounded contract directly.
- Optionally, while in the same repo, retire the obsolete
  `RenameDmsTopicToOpenSearchIndex` transform (dead OpenSearch code).

### Dependencies / handoffs (not part of this ticket)

- **Relational connector wiring is owned by the DMS-1245 CDC/Kafka epic.** It
  follows the authoritative transform pipeline and points to
  `org.edfi.kafka.connect.transforms.DocumentState` after image and contract validation.
- **Legacy connector configs** (`postgresql_connector.json`,
  `data_store_connector_template.json`) and `DebeziumConnectorClient.cs` are
  removed as part of legacy backend retirement.

## Risks and open questions

- **Contract/version coupling.** The transform intentionally encodes the versioned DMS
  source and public record contract. A source-shape or output-contract change requires
  coordinated connector fixtures and, when output changes for unchanged inputs, the
  versioned topic cutover defined by DMS-1245.
- **JDK / bytecode baseline** of the pinned `debezium/connect:2.7.0.Final`
  digest must be confirmed before publishing (see Compatibility check).
- **Malformed retained records fail closed.** Invalid JSON, unexpected provider logical
  types, precision loss, and inconsistent metadata stop transformation; only operations
  explicitly excluded by the source mapping are silently dropped.
- **Connect 4 upgrade timing.** Does not block this design, but sets urgency and
  whether the plugin's Gradle/Java baseline bumps now or later.

## Test expectations for the implementation ticket

- **Ed-Fi plugin repo (JUnit):** a raw PostgreSQL and SQL Server Debezium fixture matrix
  covers every source/operation result, key/value/topic shaping, nested objects, scalar
  and object arrays, empty arrays, provider timestamps, malformed input, metadata
  mismatch, and output under `schemas.enable=false`. Tests assert the complete **Ed-Fi
  contract**, not RedHat behavior or intermediate SMT records.
- **DMS E2E:** DMS-1232 should add or replace relational CDC scenarios once
  relational-backend streaming lands, asserting the DMS-1245 v1 contract rather
  than the legacy `deleted=true` / `EdFiDoc` shape.

## References

- Current schema (verified): `src/dms/backend/Fixtures/authoritative/sample/expected/pgsql.sql`
  (`dms."DocumentCache"."DocumentJson"`); DDL emitter
  `src/dms/backend/EdFi.DataManagementService.Backend.Ddl/CoreDdlEmitter.cs`
- Legacy schema (for contrast):
  `src/dms/backend/EdFi.DataManagementService.Old.Postgresql/Deploy/Scripts/0001_Create_Document_Table.sql`,
  `.../0005_Create_EducationOrganizationHierarchyTermsLookup_Table.sql`
- Legacy connector usage (being retired):
  `eng/docker-compose/postgresql_connector.json`,
  `eng/docker-compose/data_store_connector_template.json`,
  `src/dms/tests/EdFi.InstanceManagement.Tests.E2E/Infrastructure/DebeziumConnectorClient.cs`
- Streaming status: DMS-1232 owns replacement relational CDC E2E coverage;
  reference architecture `reference/design/cdc-streaming.md`
- Local context: `eng/docker-compose/kafka.yml` (Kafka 3.9.0 broker; Connect
  image `edfialliance/ed-fi-kafka-connect:pre`)
- Upstream: [RedHatInsights/expandjsonsmt](https://github.com/RedHatInsights/expandjsonsmt)
  (Apache-2.0, `0.0.7`), issue
  [#19](https://github.com/RedHatInsights/expandjsonsmt/issues/19) (Kafka 4 load
  failure, open)
- Reference implementation / comparison:
  [joshuagrisham/kafka-connect-expand-json-transform](https://github.com/joshuagrisham/kafka-connect-expand-json-transform)
  (Apache-2.0, `0.1.0`, Kafka 4/Java 17, no BSON)
- Ed-Fi plugin: [Ed-Fi-Alliance-OSS/Ed-Fi-Kafka-Connect](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-Kafka-Connect)
