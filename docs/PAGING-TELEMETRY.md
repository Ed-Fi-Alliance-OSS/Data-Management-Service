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
| `edfi.dms.collection_paging.requests` | Counter | `{request}` | Every collection read paging validation refused, plus every one that reached its handler. Clearing paging validation is not on its own enough — see [Aggregation Intent](#aggregation-intent) |
| `edfi.dms.collection_paging.duration` | Histogram | `ms` | Every request where backend execution was attempted, so a request refused by parameter validation never contributes a microsecond-scale sample |
| `edfi.dms.collection_paging.page_size.requested` | Histogram | `{item}` | Collection `GET` requests that reached execution |
| `edfi.dms.collection_paging.page_size.returned` | Histogram | `{item}` | Collection `GET` requests that produced a page, an empty one included |
| `edfi.dms.collection_paging.partition_count.requested` | Histogram | `{partition}` | `partitions` requests that reached execution |
| `edfi.dms.collection_paging.partition_count.returned` | Histogram | `{partition}` | `partitions` requests that produced a boundary set, an empty one included |

The page-size instruments are never recorded for a `partitions` request, and the
partition-count instruments are never recorded for a collection `GET`, so
neither histogram mixes two units of measure.

A request refused by parameter validation records no size or count on any of the
four, because the size it asked for may be the value that was refused. It is
still counted on `requests`. A request that failed records the size it asked for
but nothing on the returned instruments, so a failure never contributes a zero
to a distribution of page sizes actually served.

## Dimensions

All four dimensions are present on every measurement, and each carries only the
values listed here.

| Dimension | Allowed values |
|---|---|
| `paging_mode` | `traditional`, `cursor`, `partition` |
| `command_category` | `page`, `page_with_count`, `boundary`, `none` |
| `provider` | `postgresql`, `sqlserver`, `unknown` |
| `outcome` | `success`, `terminal_page`, `early_empty`, `validation_rejected`, `not_authorized`, `not_implemented`, `security_configuration`, `retry_exhausted`, `unknown_failure`, `execution_exception` |

That is at most 240 reachable dimension combinations — 360 as spelled out above,
less the `unknown` provider third that a correctly assembled server never emits.
The set actually reached is smaller still, because most of the rest describe
states that cannot occur together.

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

The database engine that answered the request: `postgresql` or `sqlserver`. One
of those two is always present. The engine is resolved before paging validation
runs, so a request answered ahead of that resolution is not counted on any of
these instruments at all, rather than counted as `unknown`.

`unknown` therefore reports a server assembly fault rather than a client
outcome: a collection read that reached its handler with no engine resolved.
Investigate it as a deployment problem; it is not a routine bucket to chart.

### `outcome`

| Value | Meaning |
|---|---|
| `success` | A page was produced and a continuation token was offered with it. Also covers every executed `partitions` request, including one whose boundary command found no ranges: a boundary set is not a page and has no successor, so that operation never reports `terminal_page`. |
| `terminal_page` | A collection `GET` **after which nothing follows**: no continuation token could be produced for this page. On `paging_mode=cursor` that is the request ending a walk. On `paging_mode=traditional` it is the ordinary end of a `limit`/`offset` walk — most often a selection that chose no rows, which is how such a client learns it has reached the end — so it is expected traffic there rather than a cursor-specific signal. Never reported for `partitions`, which has no successor to offer. |
| `early_empty` | An empty result the API answered without issuing any selection command. Selection is the work this skips, and only that: a request that first had to resolve a descriptor filter value, or validate a custom view, still issued that command. |
| `validation_rejected` | Parameter validation answered the request: a paging, partition-count, change-version, or resource-filter parameter was refused. |
| `not_authorized` | Namespace authorization denied the request. A client whose claim set does not authorize reading the resource at all is refused before backend execution begins and is not counted at all; see the `requests` note under [Aggregation Intent](#aggregation-intent). |
| `not_implemented` | The operation is intentionally unavailable for that resource. |
| `security_configuration` | The security configuration metadata for the request is invalid. |
| `retry_exhausted` | A retryable condition survived the retry pipeline. |
| `unknown_failure` | A backend failure with no outcome value of its own. Includes a backend-reported query-term error, which is answered with a 400, so a rising rate here can mean client misuse rather than an unhealthy backend. |
| `execution_exception` | An exception escaped execution. The exception itself still propagates and is reported unchanged. Also covers a request the circuit breaker refused, which never reached the database and is answered `503` — while the breaker is open that is expected to be the whole of this outcome, so see the second note below before reading a rise here as a code fault. |

Two distinctions are worth knowing when reading these values.

**Client disconnects are not an outcome and are not recorded.** When a client
cancels a request in flight, nothing is emitted on any instrument. A disconnect
is the absence of a completed collection read rather than a kind of one, and
counting it would report client behavior as backend failure with a duration
measuring how long the client waited before giving up.

**An open circuit breaker moves the failure rate into `execution_exception`.**
The breaker opens on the same backend results that produce `unknown_failure`.
While it is open, every collection read is refused before it reaches the
database and is recorded as `execution_exception` with a near-zero duration. So
`unknown_failure` rising and then dropping to zero is not recovery — it is the
point at which the refusal became total, and the two outcomes have to be read
together. The breaker is shared with the write pipelines, so write traffic alone
can open it and place reads that never failed into this outcome. A `503` carrying
`Retry-After`, and the single breaker-opened entry in the API's logs, are what
confirm it.

## Aggregation Intent

- **`requests`** — rate, sliced by `outcome`. This is the primary health view:
  watch `validation_rejected` and the failure outcomes as a share of counted
  collection-read traffic. A rising `validation_rejected` share usually means a
  client is sending parameters the API refuses, not that the API is unhealthy.
  Read that denominator as counted traffic rather than as every request the
  route received. Exactly two classes are counted: a request paging validation
  refused, and a request that reached its handler. The second is stated as the
  handler rather than the database on purpose: a request the circuit breaker
  refused is counted, and it never reached the database. A request the API
  answers anywhere else is not counted on any of these instruments, and that
  class straddles paging validation rather than sitting in front of it. Part of
  it is settled first — an unknown resource, a rejected profile or media type.
  The rest is settled *after* paging validation has already passed the request:
  a failed authentication, a client whose claim set does not authorize reading
  the resource at all, or an error raised while authorizing. So neither
  reaching paging validation nor clearing it is on its own enough to be
  counted. A collection-read rate that falls with no matching rise in any
  failure outcome is the signature of that whole class of refusal, and the
  API's request logs rather than these metrics are where it is diagnosed.
- **`duration`** — p50, p95, and p99, sliced by `paging_mode` and
  `command_category`. Keep `page_with_count` in its own bucket: requesting a
  total count is expected to be the slow shape, and averaging it together with
  plain pages hides both. Exclude `command_category=none` from read-latency
  objectives, and watch it as a series of its own — sliced by `outcome`, because
  it holds unlike populations. A skipped selection issued no selection command;
  it may still have issued a reference lookup to establish that it had nothing
  to select, so it is cheaper than a served page rather than free. Every failure
  is here too, and its command may or may not have run. The two extremes both
  arrive in bulk and sit at opposite ends: `retry_exhausted` is a retryable
  condition that survived the whole retry pipeline and carries the slowest
  samples this instrument records, while `execution_exception` under an open
  circuit breaker is refused before reaching the database and carries the
  fastest, for every request over the whole break duration. Mixed into the
  `page`, `page_with_count`, and `boundary` shapes, these pull the percentiles
  in opposing directions, and no movement among them describes the cost of
  serving a page.
- **`page_size.requested` vs `page_size.returned`** — compare the two
  distributions, excluding both `command_category=none` and
  `outcome=terminal_page` from either side. A persistent gap in what remains
  means pages are being trimmed after selection, either by authorization or by
  documents deleted between selection and retrieval. Each exclusion removes a
  population that reports the page size it asked for against a returned zero
  for a reason that is not trimming. An `early_empty` answered without selecting
  anything; it is the only outcome in `command_category=none` that reaches a
  returned instrument, because failures record no returned size. A
  `terminal_page` selected and found nothing, which on
  `paging_mode=traditional` is the empty page a client walks into at the end of
  a `limit`/`offset` traversal — roughly one per walk, so ordinary traffic
  rather than an edge case. Between them the two exclusions remove every page
  that reports its requested size against a returned zero for a reason other
  than trimming, including the empty page a bounded change-version walk ends on,
  so a gap that survives both is trimming.
- **`partition_count.requested` vs `partition_count.returned`** — compare the
  two distributions, excluding `command_category=none` to remove the
  short-circuits, for the same reason as above. That is the only exclusion
  needed here: `partitions` never reports `terminal_page`, and an executed
  boundary command that found no starts is a genuine `success` with a returned
  zero, which the reading that follows already accounts for. A persistent gap
  in what remains means the collection is too small to be cut into as many
  partitions as clients ask for: the requested count is an upper bound, and a
  minimum partition size reduces it. This is normal for small collections and is
  only worth investigating if it appears on collections large enough to
  partition.

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
