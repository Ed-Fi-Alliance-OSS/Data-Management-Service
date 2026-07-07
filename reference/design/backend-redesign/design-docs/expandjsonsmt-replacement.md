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

The DMS Debezium/Kafka source-connector pipeline uses the
`RedHatInsights/expandjsonsmt` Single Message Transform (SMT) to turn
JSON-string fields in change records into structured JSON before publishing to
Kafka. That plugin is effectively unmaintained (last release `0.0.7`, March
2020) and **fails to load on Kafka Connect 4.x** because its outer transform
class is package-private.

**Recommendation:** Ed-Fi should own a **small, purpose-built expand-JSON SMT**
in its existing Kafka Connect plugin (`Ed-Fi-Alliance-OSS/Ed-Fi-Kafka-Connect`).
It should:

- expand **configured root-level fields** from JSON strings into structured
  JSON values, and know nothing about any DMS-specific table or column;
- be built on **Kafka Connect's JSON facilities / standard Jackson**, **not
  BSON**;
- be **Kafka 4 / Java 17 ready**;
- keep the `sourceFields` config key (a clear, neutral name; consistent with the
  prior config vocabulary).

It should be **functionally equivalent in purpose** to RedHat's transform — it
expands a configured JSON-string field into a structured value — but its
behavioral contract is defined by Ed-Fi's needs (below), **not** by matching
RedHat's edge-case behavior.

The legacy backend is being **retired**, so its connector configs and the
six-field `edfidoc` shape are going away — they are not a migration target. The
relational stream is the only forward consumer. DMS-1245 finalized the
relational CDC/Kafka design: Debezium captures `dms.DocumentCache`, the public
Kafka payload field is `document`, and the connector config uses
`sourceFields=document`. DMS-1232 remains the E2E follow-up that replaces the
legacy KafkaMessaging expectations.

## Background

### What the transform does

The Debezium source connector reads change records and serializes them to Kafka
with `JsonConverter`. Columns that hold JSON are emitted by Debezium as
**JSON-encoded strings** (PostgreSQL `jsonb`/`json` columns surface as
`io.debezium.data.Json`, i.e. a string) — even though the database column is
structured. Without expansion, downstream consumers receive an escaped JSON
string. The expand-JSON SMT parses each configured field and replaces it with a
structured JSON object so the published message carries real nested JSON.

So the requirement is independent of the database column type: as long as the
captured column is `jsonb`/`json`, Debezium stringifies it and a transform is
needed to expand it back.

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
RedHat `0.0.7` still loads there, so the pipeline works today. The failure is a
**forward-looking blocker** for a future Debezium 3 / Kafka Connect 4 / Java 17
upgrade. DMS-911 de-risks that upgrade ahead of time.

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
schema**, which no longer reflects the go-forward design. The transform must not
encode either schema.

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

So for the go-forward backend, the JSON field that needs expansion in the v1
relational CDC contract is the renamed payload field `document`, sourced from
`dms.DocumentCache.DocumentJson`. The multi-field / array-of-ids /
hierarchy-array shape is a legacy concern.

### Relational Kafka streaming contract (DMS-1245) and E2E follow-up (DMS-1232)

Legacy Kafka E2E expectations were tied to DMS-1232 ("until relational backend
Kafka streaming supports document create/update/delete"). The current DMS
checkout does not carry active legacy Kafka feature coverage; DMS-1232 should add
or replace E2E coverage for the relational CDC contract. Existing checked-in
connector configs are legacy-oriented and do not implement the DMS-1245
relational CDC contract. Relational change tracking today is the polling Change
Queries API (`ContentVersion`), a separate mechanism from Debezium/Kafka.

**Consequence:** DMS-911 must not encode the relational stream shape inside the
SMT itself. The stream shape is now owned by the DMS-1245 CDC decision records:
capture `dms.DocumentCache`, rename `DocumentJson` to `document`, and configure
the SMT with `sourceFields=document`. Because the legacy backend is being
retired, its connector configs are **not a migration target** — they are removed
as part of legacy retirement. DMS-1232 should validate the DMS-1245 relational
contract in E2E rather than preserve the legacy topic/value shape.

## Decision

### Recommended: an Ed-Fi-owned, minimal, generic expand-JSON SMT

Add a new transform to `org.edfi.kafka.connect.transforms` in the Ed-Fi Kafka
Connect repo, purpose-built for root-level JSON-object-field expansion, public
and Kafka 4 / Java 17 ready, using Connect JSON / Jackson rather than BSON.

### Transform contract

- **Input:** a schema-backed Kafka Connect value record containing one or more
  configured string fields holding JSON.
- **Config:** a generic list key. Keep **`sourceFields`** (documented) — a clear,
  neutral name consistent with the prior config vocabulary.
- **Behavior:** parse each configured field's JSON-object string and replace
  that field with a structured Connect value.
- **Non-goals:** nested field dot-paths; whole-record schemaless conversion; key
  transforms; legacy multi-field DMS config compatibility as a contract; RedHat
  edge-case parity.

### Alternatives considered

| Option | Disposition |
| --- | --- |
| **Port RedHat verbatim + keep BSON** | Rejected. Its only advantage was preserving the legacy six-field message shape, which greenfield relational streaming does not require. Carrying BSON, anonymous-struct quirks, mixed-array behavior, and old invalid-JSON semantics is over-scoped for expanding one object field. |
| **Adopt `joshuagrisham/kafka-connect-expand-json-transform`** | Not primary. Single root-level field expansion is exactly its sweet spot (Apache-2.0, Kafka 4/Java 17, no BSON), but it is a single-maintainer external dependency — the supply-chain risk this ticket exists to remove. **Keep it as a reference implementation / comparison point** for the Ed-Fi transform. |
| **Fork RedHat into a new Ed-Fi repo** | Rejected. Creates a standalone repo + release pipeline when Ed-Fi already maintains a Kafka Connect plugin that can host the code. |
| **Remove the transform** | Rejected. Expanded JSON remains the intended streaming shape; the relational stream uses `sourceFields=document` for `dms.DocumentCache.DocumentJson` after connector field renaming. Removal is only viable if the streaming pattern itself is dropped. |

### Relationship to DMS-1245 and DMS-1232

DMS-1245 owns relational Kafka streaming architecture: the captured table,
message contract, topic strategy, and connector deployment model. DMS-911 /
DMS-1240 deliver a generic, Kafka-4-ready transform that the DMS-1245 connector
pipeline can use with `sourceFields=document`. DMS-1232 is the E2E follow-up for
the quarantined Kafka scenarios.

## Scope

- Add a public, Java 17 / Kafka 4-ready, generic root-level expand-JSON SMT to
  `Ed-Fi-Alliance-OSS/Ed-Fi-Kafka-Connect`, built on Connect JSON / Jackson, no
  BSON, using the `sourceFields` config key.
- Remove the RedHat `0.0.7` tarball download/extract from that repo's
  `kafka/Dockerfile`; the transform ships in the Ed-Fi SMT jar instead.
- The relational streaming connector — designed by DMS-1245 and implemented by
  the CDC/Kafka epic — is the forward consumer of the new transform. The legacy
  connector configs are removed with the legacy backend, not repointed.

## Non-goals

- Defining the relational captured tables, columns, or message field names
  inside the SMT. Those are DMS-1245 connector-contract concerns.
- Topic-per-instance / data-store routing. Routing is independent of expand-JSON
  and is owned by the DMS-1245 connector contract.
- Removing expand-JSON functionality.
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

1. Valid JSON object in a configured field → structured object on the wire.
2. Nested object.
3. Arrays that real Ed-Fi documents contain (homogeneous scalar and object
   arrays).
4. Empty arrays.
5. Null / missing configured field.
6. Invalid / non-JSON string → defined failure mode (decide and document:
   fail-fast vs pass-through).
7. Output verified with `value.converter.schemas.enable=false` (the DMS
   converter setting) — the published JSON structure is what matters; Connect
   schema names are stripped and are not part of the contract.

This validation is a prerequisite for wiring the relational connector — no DMS
connector config may point at the new class until the contract is validated.

## Affected files

| Repo | File | Change |
| --- | --- | --- |
| Ed-Fi-Kafka-Connect | `src/.../transforms/ExpandJson.java` (+ helpers) | New — minimal generic expander |
| Ed-Fi-Kafka-Connect | `kafka/Dockerfile` | Remove RedHat `ADD` + `tar` lines |
| Ed-Fi-Kafka-Connect | `build.gradle` / `NOTICES.md` | Java 17 target; no BSON; attribution only if third-party code is incorporated |
| Ed-Fi-Kafka-Connect | unit tests | New — one per contract case |
| DMS (legacy, retiring) | `eng/docker-compose/postgresql_connector.json` | Removed with legacy backend retirement |
| DMS (legacy, retiring) | `eng/docker-compose/data_store_connector_template.json` | Removed with legacy backend retirement |
| DMS (legacy, retiring) | `src/dms/tests/EdFi.InstanceManagement.Tests.E2E/Infrastructure/DebeziumConnectorClient.cs` | Removed/replaced with legacy backend retirement |
| DMS (relational CDC/Kafka) | new relational streaming connector config | Uses `org.edfi.kafka.connect.transforms.ExpandJson$Value` with `sourceFields=document` |

Proposed class path: `org.edfi.kafka.connect.transforms.ExpandJson$Value`
(matches the existing Ed-Fi transform package). Config key stays `sourceFields`.

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
- Generic: expands the fields named in `sourceFields`; contains no DMS-specific
  table/column knowledge. `sourceFields` config key, documented.
- **Contract validated** per the Contract-validation section, with unit tests
  per case.
- `kafka/Dockerfile` no longer downloads `expandjsonsmt`; the published image
  ships the Ed-Fi transform, starts cleanly, and has no build-time dependency on
  the RedHat GitHub release.
- Attribution: if any RedHat or joshuagrisham code is incorporated, retain its
  Apache-2.0 notices and record the derivation in `NOTICES.md` (blocking before
  publish); if clean-room, note the inspiration without a license obligation.
- Optionally, while in the same repo, retire the obsolete
  `RenameDmsTopicToOpenSearchIndex` transform (dead OpenSearch code).

### Dependencies / handoffs (not part of this ticket)

- **Relational connector wiring is owned by the CDC/Kafka implementation epic
  produced by DMS-1245.** It captures `dms.DocumentCache`, renames
  `DocumentJson` to `document`, configures `sourceFields=document`, and points
  the connector at `org.edfi.kafka.connect.transforms.ExpandJson$Value` once the
  image is available and the contract validation passes.
- **Legacy connector configs** (`postgresql_connector.json`,
  `data_store_connector_template.json`) and `DebeziumConnectorClient.cs` are
  removed as part of legacy backend retirement.

## Risks and open questions

- **CDC/Kafka implementation dependency.** The legacy backend is being retired,
  so the relational stream is the only forward DMS consumer. DMS-911 stays
  generic so it does not encode DMS table or field names; the consuming config
  applies the DMS-1245 contract.
- **JDK / bytecode baseline** of the pinned `debezium/connect:2.7.0.Final`
  digest must be confirmed before publishing (see Compatibility check).
- **Invalid-JSON failure mode** must be decided explicitly (fail-fast vs
  pass-through) and codified in tests.
- **Connect 4 upgrade timing.** Does not block this design, but sets urgency and
  whether the plugin's Gradle/Java baseline bumps now or later.

## Test expectations for the implementation ticket

- **Ed-Fi plugin repo (JUnit):** one test per Contract-validation case — valid
  object, nested object, scalar array, object array, empty array, null/missing
  field, invalid JSON failure mode, and output under
  `schemas.enable=false`. Tests assert the **Ed-Fi contract**, not RedHat
  behavior.
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
  reference architecture `reference/design/cdc-streaming.md` and
  `reference/design/backend-redesign/design-docs/cdc/`
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
