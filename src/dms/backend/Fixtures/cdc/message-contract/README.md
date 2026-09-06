# Message-contract fixture inputs

`catalog.json` assigns stable scenario IDs and references E18 cases relative to the
backend `Fixtures` directory. Shared cache rows, opaque stream ETags, and public
bodies remain in `document-cache/materialized-documents`; do not copy them here.
The shared public fixture's `document` property contains the body, not an envelope.

Provider files describe test-only SourceRecord inputs, not the public wire contract.
`MessageContractFixtureCatalog` expands each template into a self-contained JSON
record for the future Java runner. The expectation is independently assembled from
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
- PostgreSQL uses logical UUID/JSON/ZonedTimestamp strings and a server partition.
  SQL Server uses plain UUID/JSON strings, IsoTimestamp, and server/database partition
  identity. Both pin `__debezium_unavailable_value`. The baseline PostgreSQL delete
  supplies an available before UUID; SQL Server supplies its unavailable marker.
  These inputs retain that distinction; interpreting it belongs to the real transform.

`MessageContractFixtures.props` copies inputs and shared E18 expectations into both
unit and integration build/publish outputs. The loader first resolves `Fixtures`
from the supplied artifact directory, then supports checkout-relative resolution.
The integration project links the test helpers; no production project consumes them.
Future runner source/resources belong to the integration project and must likewise
be copied to its output. MC-02 owns that runner and pinned-image execution.

Schema/value validation checks representability as Connect data, not transform
acceptance: a plain string under an incorrect logical schema can be structurally
valid and still be a negative transform test. Diagnostics expose fixed reason codes
without record contents or parser exception details.

`MessageContractJson` compares complete objects and ordered arrays, preserving
property absence, nulls, and token kinds. Numbers compare using significant digits
and an arbitrary-precision base-10 exponent, without double/decimal conversion.
Object ordering and equivalent number spelling/scale are not contract differences.
