# Collection Paging Telemetry

The Ed-Fi API publishes metrics for the three shapes a collection read can take:

- **Traditional paging** — a collection `GET` using `limit` and `offset`.
- **Cursor paging** — a collection `GET` using `pageToken` and `pageSize`.
- **Partition planning** — a `GET` on a collection's `partitions` path, which
  returns one page token per partition instead of documents.

The metrics describe those reads only. They add no database work, and they
change no status code, header, or response body.

Regular resources and descriptors report through one shared contract. Their
execution semantics are equivalent, so no metric name and no dimension value
distinguishes the two.

## Collecting These Metrics

The meter name is:

```text
EdFi.DataManagementService.CollectionPaging
```

**Collecting these metrics requires an in-process metrics pipeline in the API
host.** The API publishes .NET `Meter` instruments. It exports *logs* over OTLP
through the `OtlpLogging` configuration section (see
[Logging Policy](./LOGGING.md#otlp-export)), but it registers no meter provider,
so out of the box nothing observes these instruments and nothing is collected.

To collect them, the host process must run a meter provider that subscribes to
the meter above by name — with the OpenTelemetry .NET SDK, that is an `AddMeter`
call naming `EdFi.DataManagementService.CollectionPaging` — and then export from
that pipeline to the metrics backend of choice.

**Deploying an OpenTelemetry Collector alone does not work.** .NET `Meter`
instruments are observable only inside the process that publishes them. A
Collector is an export *destination* for an in-process pipeline, not a way to
observe the instruments; with no meter provider in the host, a Collector
receives nothing from these instruments no matter how it is configured.

## Instruments

| Instrument | Type | Unit | Recorded for |
|---|---|---|---|
| `edfi.dms.collection_paging.requests` | Counter | `{request}` | Every classified request, including those refused by parameter validation |
| `edfi.dms.collection_paging.duration` | Histogram | `ms` | Every request where backend execution was attempted, so a request refused by parameter validation never contributes a microsecond-scale sample |
| `edfi.dms.collection_paging.page_size.requested` | Histogram | `{item}` | Collection `GET` requests |
| `edfi.dms.collection_paging.page_size.returned` | Histogram | `{item}` | Collection `GET` requests that produced a page |
| `edfi.dms.collection_paging.partition_count.requested` | Histogram | `{partition}` | `partitions` requests |
| `edfi.dms.collection_paging.partition_count.returned` | Histogram | `{partition}` | `partitions` requests that produced a boundary set |

The page-size instruments are never recorded for a `partitions` request, and the
partition-count instruments are never recorded for a collection `GET`, so
neither histogram mixes two units of measure.

## Dimensions

All four dimensions are present on every measurement, and each carries only the
values listed here.

| Dimension | Allowed values |
|---|---|
| `paging_mode` | `traditional`, `cursor`, `partition` |
| `command_category` | `page`, `page_with_count`, `boundary`, `none` |
| `provider` | `postgresql`, `sqlserver`, `unknown` |
| `outcome` | `success`, `terminal_page`, `early_empty`, `validation_rejected`, `not_authorized`, `not_implemented`, `security_configuration`, `retry_exhausted`, `unknown_failure`, `execution_exception` |

That is at most 360 dimension combinations. The set actually reachable is far
smaller, because most combinations describe states that cannot occur together.

### `command_category`

The shape of the database command a served result was built around:

- `page` — a collection `GET` that selected a page.
- `page_with_count` — a collection `GET` that also compiled a total count into
  the same command, which is the more expensive shape.
- `boundary` — a `partitions` request that selected partition boundaries.
- `none` — **every other outcome**: every failure, every request refused by
  parameter validation, and every request answered without issuing a selection
  command at all.

The uniform `none` rule is deliberate. For most failures the API cannot
establish whether a selection command ran — some are resolved before a command
is ever planned, and others can arise either before or after one — so
attributing a command shape, and therefore a duration, to a request that may
never have issued that command would be misleading. `paging_mode` still
separates traditional, cursor, and partition traffic for failures, so no slice
an operator needs is lost.

### `provider`

The database engine that answered the request: `postgresql` or `sqlserver`. It
is `unknown` only when the request was answered before the engine was resolved.

### `outcome`

| Value | Meaning |
|---|---|
| `success` | A page or a boundary set was produced. Also covers a page served with documents that carries no continuation token, and a `partitions` request whose boundary command ran and found no ranges. |
| `terminal_page` | A collection `GET` that **ends a cursor walk**: a continuation was possible for this page and none could be produced, so nothing follows it. Never reported for `partitions`, which has no successor to offer. |
| `early_empty` | An empty result the API answered without issuing any selection command. Selection is the work this skips; a request that also validates a custom view still issues that command first. |
| `validation_rejected` | Parameter validation answered the request: a paging, partition-count, change-version, or resource-filter parameter was refused. |
| `not_authorized` | Namespace authorization denied the request. |
| `not_implemented` | The operation is intentionally unavailable for that resource. |
| `security_configuration` | The security configuration metadata for the request is invalid. |
| `retry_exhausted` | A retryable condition survived the retry pipeline. |
| `unknown_failure` | The database returned a failure the API could not classify. |
| `execution_exception` | An exception escaped execution. The exception itself still propagates and is reported unchanged. |

Two distinctions are worth knowing when reading these values.

**`success` is not the same as "a continuation was offered."** A traditional
page inside a bounded change-version window is ordered so that it cannot anchor
a cursor continuation. It is served with documents, and a client keeps paging it
with `limit` and `offset`. Such a page is `success`, never `terminal_page`:
reporting it as terminal would say a healthy paging walk had ended.

**Client disconnects are not an outcome and are not recorded.** When a client
cancels a request in flight, nothing is emitted on any instrument. A disconnect
is the absence of a completed collection read rather than a kind of one, and
counting it would report client behavior as backend failure with a duration
measuring how long the client waited before giving up.

## Aggregation Intent

- **`requests`** — rate, sliced by `outcome`. This is the primary health view:
  watch `validation_rejected` and the failure outcomes as a share of total
  collection-read traffic. A rising `validation_rejected` share usually means a
  client is sending parameters the API refuses, not that the API is unhealthy.
- **`duration`** — p50, p95, and p99, sliced by `paging_mode` and
  `command_category`. Keep `page_with_count` in its own bucket: requesting a
  total count is expected to be the slow shape, and averaging it together with
  plain pages hides both. Exclude `command_category=none` from read-latency
  objectives: those samples time a backend attempt that issued no selection
  command, so they are fast by construction and pull the percentiles of the
  `page`, `page_with_count`, and `boundary` shapes down if mixed in.
- **`page_size.requested` vs `page_size.returned`** — compare the two
  distributions. A persistent gap means pages are being trimmed after
  selection, either by authorization or by documents deleted between selection
  and retrieval.
- **`partition_count.requested` vs `partition_count.returned`** — compare the
  two distributions. A persistent gap means the collection is too small to be
  cut into as many partitions as clients ask for: the requested count is an
  upper bound, and a minimum partition size reduces it. This is normal for
  small collections and is only worth investigating if it appears on
  collections large enough to partition.

## What Is Never Recorded

Every dimension value is drawn from the fixed lists above. Nothing derived from
a request becomes a metric dimension. In particular, these never appear:

- Resource or descriptor names, and no dimension distinguishing the two.
- Tenant keys, instance identifiers, or namespaces.
- Client identity, claims, or authorization details.
- Query filter names and filter values.
- Page token text, and the range bounds a page token decodes to.
- Document or candidate identifiers.
- Exception messages, types, or stack traces.

This keeps the metric bounded by construction: the number of distinct dimension
combinations cannot grow with traffic, with the size of the data, or with the
number of clients.
