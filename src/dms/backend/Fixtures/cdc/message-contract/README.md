# Message-contract fixture inputs

`catalog.json` assigns stable scenario IDs and references E18 cases relative to the
backend `Fixtures` directory. Shared cache rows, opaque stream ETags, and public
bodies remain in `document-cache/materialized-documents`; do not copy them here.
The shared public fixture's `document` property contains the body, not an envelope.

Provider files describe test-only SourceRecord inputs, not the public wire contract.
`MessageContractFixtureCatalog` expands each template into a self-contained JSON
record for the Java runner. The expectation is independently assembled from
E18 metadata and its public body, never from transformed output.

Template vocabulary (format version 1):

- `{"$cacheRow":"field"}` substitutes a cache-row field with its JSON token kind intact.
  `"encoding":"json-string"` serializes the selected JSON as a Connect string, used
  for `DocumentJson`. No public document body is stored in the provider templates.
- `{"$schema":"row"}` expands a schema from the file's `schemas` dictionary. The
  expanded runner record omits that dictionary. References are depth bounded.
- Schema descriptors use uppercase Connect `type`, explicit boolean `optional`,
  optional logical `name` and integer `version`, `fields` for STRUCT, and `items`
  for ARRAY. Primitive values retain their JSON kind; BYTES uses a base64 string.
  Missing optional struct properties remain absent; explicit null remains null.
- `keySchema`/`key`, `valueSchema`/`value`, and each ordered header's `schema`/`value`
  are schema/value pairs. `sourcePartition`, `sourceOffset`, `sourceTopic`,
  `timestamp` (INT64 milliseconds or null), and `operation` describe source metadata.
- Provider source structs carry the required `io.debezium.connector.<provider>.Source`
  name. Debezium UUID/JSON/timestamp logical schemas explicitly carry version 1.
- PostgreSQL uses logical UUID/JSON/ZonedTimestamp strings and a server partition.
  SQL Server uses plain UUID/JSON strings, IsoTimestamp, and server/database partition
  identity. Both pin `__debezium_unavailable_value`. The baseline PostgreSQL delete
  supplies an available before UUID; SQL Server supplies its unavailable marker.
  These inputs retain that distinction; interpreting it belongs to the real transform.

`MessageContractFixtures.props` copies inputs and shared E18 expectations into both
unit and integration build/publish outputs. The loader first resolves `Fixtures`
from the supplied artifact directory, then supports checkout-relative resolution.
The integration project links the test helpers; no production project consumes them.
The integration project's `MessageContractRunner/README.md` describes the MC-02
image-only runner, its resource packaging, observations, and prerequisite diagnostics.

`upsertVariant` optionally names a file under this root. The loader validates the
shared E18 files first, then applies only the variant's signed `contentVersion`,
paired source/expected timestamps, and additive `documentProperties`. Additions
cannot overwrite shared document fields or reserved metadata. Expected timestamps
are explicit whole-second values checked against the cache timestamp, so rounding
across the year boundary is not accepted. Variant files are test inputs, not new
materializer goldens. The shared opaque ETag is deliberately unchanged even when a
synthetic variant changes the version or body: these scenarios test copying, not
ETag composition or materializer consistency for synthetic data.

MC-03's `Given_MessageContractUpsert` executes 42 scenarios (both providers, seven
cases, create/update/read) in two image-only runner batches. Stable runner IDs use
`MC-UPSERT-{PG|SQL}-{case}-{C|U|R}`. The four baseline case names match the catalog;
the additional cases are `EXACT-NUMBERS`, `SIGNED-INT64-MIN`, and
`FRACTIONAL-SECOND`. PostgreSQL supplies six fractional digits; SQL Server supplies
seven. Numeric data includes adjacent integers beyond IEEE-754 and INT64 precision,
exact decimal/exponent values, mixed nested arrays, Unicode strings, and absent
versus explicitly null sibling properties. Updates also supply uppercase source
UUIDs and a distinct before row to detect stale metadata or body selection.
Provider upsert templates include `ComputedAt`
distinct from `LastModifiedAt` to prove that computation metadata does not leak.

Each scenario independently asserts the complete semantic public envelope, derived
topic and unquoted UTF-8 key, exact required logical BYTES schema/version and
converter defensive copy, whole-second document time and cleared Connect metadata,
and opaque ETag placement/internal-field exclusion. Public JSON object order and
equivalent numeric spelling/scale are not golden byte contracts. Byte equality is
asserted separately only across the transform-to-converter boundary.

Run with the qualified immutable `CDC_CONNECTOR_TEMPLATE_CONNECT_IMAGE` configured:

```sh
dotnet test src/dms/backend/EdFi.DataManagementService.Backend.Cdc.Tests.Integration/EdFi.DataManagementService.Backend.Cdc.Tests.Integration.csproj --filter 'Category=CdcMessageContract&FullyQualifiedName~MessageContractUpsert'
```

This suite needs no broker/provider image settings or running Connect worker. It
uses `DatabaseIntegration`, `CdcMessageContract`, `CdcMessageContractSerialized`,
and the applicable `PostgresqlIntegration`/`MssqlIntegration` categories. Set
`CDC_CONNECTOR_TEMPLATE_FAIL_FAST=true` in qualification to fail on missing
prerequisites instead of skipping.

Schema/value validation checks representability as Connect data, not transform
acceptance: a plain string under an incorrect logical schema can be structurally
valid and still be a negative transform test. Diagnostics expose fixed reason codes
without record contents or parser exception details.

`MessageContractJson` compares complete objects and ordered arrays, preserving
property absence, nulls, and token kinds. Numbers compare using significant digits
and an arbitrary-precision base-10 exponent, without double/decimal conversion.
Object ordering and equivalent number spelling/scale are not contract differences.
