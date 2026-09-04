# Cursor paging

The Ed-Fi API offers two ways to read a large collection. Traditional paging
walks it with `limit` and `offset`. Cursor paging walks it with an opaque
`pageToken`, and can additionally split it into independent segments that a
client may read in parallel.

This document describes the behavior API clients can rely on. Cursor paging is
part of the API contract; it is not feature-toggled and does not need to be
enabled.

The rules below for *rejecting* a token that no longer matches the request
replaying it are the default behavior rather than part of that contract. A
deployment can be configured to walk every request in one order, and mismatches
are then served instead of refused — see
`UseLegacyDocumentIdOrderingForChangeQueries` in
[Configuration](./CONFIGURATION.md). Treat repeating your filters unchanged as
the requirement, not as something you can count on being told about.

## When to use which

`limit` and `offset` count rows from the start of a result set on every request,
so page 500 must be reached by skipping the 499 pages before it, and a
concurrent write that changes the ordering can shift an unread row across a page
boundary that has already been passed. A collection paged this way also cannot
be divided for parallel consumption, because an offset is a position in one
sequential scan.

Cursor paging instead asks for "the next page after the last item I saw." Each
request carries a `pageToken` that names where to resume, so the cost of a
request does not grow with how far into the collection it is, and a walk cannot
silently skip an unread row because a different row changed position.

| | Traditional | Cursor |
| --- | --- | --- |
| Parameters | `limit`, `offset` | `pageToken`, `pageSize` |
| Cost of a late page | Grows with depth | Flat |
| Parallel consumption | Not available | Via `/partitions` |
| Total count | `totalCount=true` | Not available with a `pageToken` |

Use traditional paging for browsing the first few pages and for any request that
needs `totalCount`. Use cursor paging to read a whole collection, especially a
large one, and use `/partitions` when you want several workers to read it at
once.

## Scope

Cursor paging applies to GET-many requests on **resource and descriptor
collections**, such as `/data/ed-fi/students` and
`/data/ed-fi/gradeLevelDescriptors`. Every collection that publishes a GET-many
operation also publishes a `/partitions` sibling; both appear in the API's
OpenAPI specification alongside their collection.

The behavior is identical on every supported database. Nothing in this document
depends on which datastore a deployment uses.

Example URLs below use the plain `/data/...` route. A deployment configured for
multi-tenancy or route qualifiers prefixes that route — for example
`/255901/2024/data/ed-fi/students`. Cursor paging behaves the same under every
route shape.

## Walking a collection sequentially

### 1. Start with an ordinary request

A cursor walk begins with a normal GET-many request. No cursor parameter is
required to start; `limit` and any resource filters are allowed here.

```http
GET /data/ed-fi/students?limit=100&lastSurname=Smith HTTP/1.1
Host: localhost:8080
Authorization: Bearer <access token>
```

A successful response carries the page, and — when there is somewhere to
continue from — a `Next-Page-Token` header:

```http
HTTP/1.1 200 OK
Content-Type: application/json
Next-Page-Token: <opaque token>

[ { "id": "...", "studentUniqueId": "604822", ... } ]
```

> [!NOTE]
> A `minChangeVersion`/`maxChangeVersion` window is allowed on this first
> request, and the `Next-Page-Token` it returns continues the walk inside that
> window like any other token. The window is not carried in the token, so it must
> be repeated on every later request of the walk — see [Repeat your filters on
> every request](#repeat-your-filters-on-every-request).

### 2. Copy the header into the next request

Send the value of `Next-Page-Token` back as the `pageToken` query parameter.
Repeat every filter from the first request, unchanged. Optionally set `pageSize`
to control how many items the next page selects.

```http
GET /data/ed-fi/students?pageToken=<opaque%20token>&pageSize=100&lastSurname=Smith HTTP/1.1
Host: localhost:8080
Authorization: Bearer <access token>
```

Or with curl, letting it URL-encode the token:

```bash
curl -G "http://localhost:8080/data/ed-fi/students" \
  -H "Authorization: Bearer $TOKEN" \
  --data-urlencode "pageToken=$NEXT_PAGE_TOKEN" \
  --data-urlencode "pageSize=100" \
  --data-urlencode "lastSurname=Smith"
```

> [!IMPORTANT]
> Do not combine `pageToken` or `pageSize` with `limit` or `offset`. A request
> that mixes the two paging styles is rejected. Once you are walking with
> `pageToken`, use `pageSize` where you would have used `limit`.

### 3. Repeat until the header is absent

Keep sending the newest `Next-Page-Token` back as `pageToken` until a response
arrives **without** the header. That absence is the normal end of the walk.

Two details matter here:

- **Follow the header even when the body is empty.** A page selects its items
  before it reads them, so a concurrent delete can empty a page whose token
  still points past those rows. A client that stopped on an empty body would
  stop early. Stop on the missing header, never on an empty array.
- **Expect one extra request.** The last page carrying items usually still
  carries a token, so the walk normally ends with one final request that returns
  an empty array and no header.

### Repeat your filters on every request

A token names a position in the collection. It does not carry your filters, your
change-version window, or your authorization. Every request is authorized and
filtered independently, so the resource route, resource filters, and
`minChangeVersion`/`maxChangeVersion` values must be identical on every request
of a walk. Changing a resource filter mid-walk does not resume the old walk; it
starts reading a different result set from that token's position.

On current data, adding or removing `maxChangeVersion` mid-walk is refused
rather than answered that way. A bounded change-version window walks the
collection in a different order than an unbounded request does, so a position
recorded under one is not a position under the other. Replaying a token from a
windowed request without `maxChangeVersion`, or a token from an unwindowed
request with it, is rejected with the same message as a malformed token. Tokens
are opaque, so there is nothing to correct in the token itself: restart the walk
under the window you mean to read. Changing the *value* of `maxChangeVersion` is
not refused — it leaves a bounded window bounded — but it reads a different
result set from that token's position, like any other filter change.

A walk served from a snapshot is a partial exception, because there a min-only
window and a bounded one are walked in the same change-version order. The
exception holds only while the window keeps at least one bound: moving between
`minChangeVersion` alone and `minChangeVersion` with `maxChangeVersion` is served
rather than rejected. Adding a ceiling to a walk that carried no window at all,
and dropping the ceiling from a walk that carried `maxChangeVersion` alone, are
still rejected there, because each of those leaves the token naming positions in
a column the new request no longer walks.

Where the request is served, the two directions do not do the same thing. Adding
`maxChangeVersion` partway through is honored: the walk continues over the
overlap of the token's range and the ceiling you sent. Dropping it is honored
too, and it widens the walk rather than narrowing it — the request no longer
names a ceiling, and the token does not carry the one the walk started under, so
the walk runs on to the end of the range the token names. For a walk started from
an ordinary request that is the newest version in the copy; for a walk started
from a `/partitions` token it is the end of that segment. You read more than a
bounded window describes, with nothing in the response to say so. Neither
direction returns a document twice or skips one. Repeat the window unchanged, as
above, and neither arises.

A rejection of the same kind applies to asking for a snapshot, but only
for a window of `minChangeVersion` alone. `Use-Snapshot: true` is the
request header that asks for a point-in-time copy of the data instead of
current data, where the deployment offers one. Where it does not — where the
data store has no `Snapshot` derivative configured — `Use-Snapshot: true` is
answered `404` with `Snapshot not found.` before any paging validation runs,
so none of the rules in this paragraph are reached. A snapshot walks a min-only
window in a different order than current data does, so such a walk belongs
to the source that started it: adding or dropping the header partway
through one is rejected with the invalid-token message, exactly as adding
or dropping `maxChangeVersion` is on current data.

A walk that carries `maxChangeVersion`, and a walk with no change-version
window at all, are walked in the same order on either source, so their
tokens are *not* rejected when the header changes. Keep the header
identical for the whole walk regardless: a token records a position, not
a database, and a walk that switches simply reads a different copy of the
collection from that position, like any other filter change.

## Reading a collection in parallel

To divide a collection among workers, ask for partitions first.

### 1. Request partition tokens

```http
GET /data/ed-fi/students/partitions?number=4 HTTP/1.1
Host: localhost:8080
Authorization: Bearer <access token>
```

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "pageTokens": [
    "<opaque token 1>",
    "<opaque token 2>",
    "<opaque token 3>",
    "<opaque token 4>"
  ]
}
```

`number` is optional; omitting it uses the deployment's configured default. The
response never contains more tokens than requested and **may contain fewer** —
including a single token, or none at all when no item is accessible — because the
API will not cut a collection into segments smaller than it is worth
coordinating. Treat the returned count as the answer, not the requested count.

Apply the same filters to the partitions request that you intend to use for the
walks. Filters and authorization are reapplied on every subsequent request
anyway, so a partition calculated over a different filter set describes segments
that do not match what the walks will read.

`maxChangeVersion` is stricter than a recommendation. Partition tokens carry the
same position marker a `Next-Page-Token` does, so the rule in [Repeat your
filters on every request](#repeat-your-filters-on-every-request) applies to them
from the moment they are issued: on current data, if the partitions request
included `maxChangeVersion`, every walk that replays one of its tokens must
include it too, and if the partitions request omitted it, no walk may add it.
Either mismatch is rejected with the invalid-token message, and the only recovery
is a new partitions request under the window you mean to read.

A set cut on a snapshot follows the same partial exception the sequential walks
do: what its tokens require is only that the window keep at least one bound. A
walk may add `maxChangeVersion` to a set cut with `minChangeVersion` alone, or
drop either bound from a set cut with both, and be served rather than rejected.
Only a change that leaves the request with no change-version window at all, or
that adds the first bound to a set cut with none, is refused there. Two of those
served cases change what the walk reads: an added ceiling narrows each range to
the overlap it shares with the token, and a dropped ceiling leaves the last of
the returned tokens — the only one with no upper bound of its own — reading past
the boundary the set was cut under.

Resource filters are not refused this way; they change what the walks read, as
any other filter change does. So does `minChangeVersion` on current data, where
it does not decide the order the collection is walked in. On a snapshot it does
decide that, so there it is refused on the same terms as the rule above: dropping
it from a set cut with `minChangeVersion` alone leaves the walk with no window,
and adding it to a set cut with no window at all leaves an unwindowed set's
tokens facing a windowed walk. Both are rejected.

```http
GET /data/ed-fi/students/partitions?number=4&lastSurname=Smith&maxChangeVersion=87421 HTTP/1.1
Host: localhost:8080
Authorization: Bearer <access token>
```

### 2. Walk each partition independently

Each returned token is used exactly like a `Next-Page-Token`: send it as
`pageToken`, with the same filters — including the change-version window the
partitions request used — and an optional `pageSize`.

```bash
# Each worker takes one token and walks it to completion.
curl -G "http://localhost:8080/data/ed-fi/students" \
  -H "Authorization: Bearer $TOKEN" \
  --data-urlencode "pageToken=$PARTITION_TOKEN" \
  --data-urlencode "pageSize=500" \
  --data-urlencode "lastSurname=Smith" \
  --data-urlencode "maxChangeVersion=87421"
```

Partitions are independent, so the walks may run concurrently. Within a
partition the walk is the sequential walk described above: follow
`Next-Page-Token` until a response has no such header. A walk that started from
a partition token stays inside that partition and will not read another
worker's segment.

## Tokens are opaque

A `pageToken` — whether it came from a `Next-Page-Token` header or from a
`/partitions` response — is an opaque string.

- Do not decode, parse, edit, or construct one. Its encoding is an internal
  detail and may change without notice.
- Pass it through unchanged, and URL-encode it when placing it in a query
  string.
- A token carries no filters and no authority. It cannot grant access to
  anything your credential could not already read.
- Tokens are not promised to be meaningful across resources, clients, tenants,
  or databases. Reusing one somewhere else is not an error you can rely on being
  reported; it simply is not promised to produce a useful result.

## Cursor paging is not a snapshot

Cursor paging adds no long-running transaction, no server-side cursor state, and
no repeatable-read guarantee. A walk reads live data unless the deployment offers
a snapshot and the request asks for one, and it may take a while, so writes
committed during the walk can be observed. On a live source, specifically:

- A deleted document leaves a gap. This is harmless; the walk continues.
- A document that stops matching your filters, change-version window, or
  authorization before its page is reached will not be returned.
- A document that starts matching *behind* the position already passed will be
  missed by this walk.
- New documents, and deleted-then-recreated documents, receive larger identity
  values and may appear later in the walk — commonly in the final partition.
- Changing filters, claims, ownership, or relationship authorization during a
  walk changes what later requests return.
- Retrying a request with the same token may return different data than the
  first attempt, because it observes whatever is committed at that moment.
- A walk bounded by `maxChangeVersion` normally sees an updated document leave
  the window rather than move within it, so the document is not returned twice.
  That holds when the bound is a change version the API has already issued —
  which is what `GET /changeQueries/v1/availableChangeVersions` returns. A bound
  chosen above it leaves the window open at the top, so an update can move a
  document forward inside the window and the walk can return it a second time.
  Take the bound from that operation rather than choosing one.

A walk served from a snapshot observes none of the above, because the copy it
reads does not change while it is being walked. That stability is a property of
the snapshot, not of cursor paging, which is what this section's title means:
asking for `Use-Snapshot: true` is what supplies it, and a walk that drops the
header partway through gives it up. A walk carrying `minChangeVersion` without
`maxChangeVersion` is the one shape that does not merely give it up: the two
sources walk that shape in different orders, so dropping the header there is
rejected outright rather than quietly answered from current data.

That stability also rests on the deployment holding the same copy in place for
the life of the walk. Re-pointing a `Snapshot` derivative at a different database,
or re-creating the database behind an unchanged connection string, is not
something the API detects or reports: later pages simply come from the
replacement copy. See
[Data Store Derivatives](./API-CLIENT-AND-INSTANCE-CONFIGURATION.md#data-store-derivatives).

If you need a stable view of a collection and the deployment offers no snapshot,
do not rely on cursor paging to provide one.

> [!NOTE]
> Rely on the actual presence of the `Next-Page-Token` header rather than
> assuming a full page implies a successor, and equally do not treat a full page
> as proof that one is absent. The header is the only signal that a walk has
> more to read.

## Parameter reference

### GET-many collection requests

| Parameter | Rules |
| --- | --- |
| `pageToken` | Optional. An opaque token from a `Next-Page-Token` header or a `/partitions` response. Cannot be combined with `offset` or `limit`. An undecodable value is rejected, as is a token whose request resolves a different walk order than the one it was issued under — which the change-version window and the data source both feed — see `maxChangeVersion` below, which states where that rejection applies. |
| `pageSize` | Optional, and valid **only** alongside `pageToken`. An integer from `0` to the deployment's configured `MaximumPageSize`. Sending it without `pageToken` is rejected. |
| `limit`, `offset` | Traditional paging. Rejected alongside `pageToken`. |
| `totalCount` | `totalCount=true` is rejected alongside a valid `pageToken`. Cursor paging does not report a total. |
| `minChangeVersion`, `maxChangeVersion` | Allowed, including on the request that starts a walk. Both must be repeated unchanged on every request of the walk. On current data, adding or removing `maxChangeVersion` mid-walk is rejected with the invalid-token message, because a bounded window is walked in a different order than an unbounded request; restart the walk instead. A walk served from a snapshot is not rejected on that change while the window keeps at least one bound, because a min-only window and a bounded one are both walked in change-version order there: an added ceiling is honored, and a dropped one is honored too — the token does not carry the ceiling the walk started under, so the walk runs on past it to the end of the range the token names, which for a walk started from an ordinary request is the newest version in the copy. Adding a ceiling to a walk that carried no window at all, and dropping it from a walk that carried `maxChangeVersion` alone, are still rejected on a snapshot. On a snapshot `minChangeVersion` falls under that same rule, which it never does on current data: adding it to a walk that carried no window, and dropping it from a walk that carried `minChangeVersion` alone, are the two changes that cross the same boundary from the other side, and both are rejected with the invalid-token message. Where the data store has a `Snapshot` derivative configured, a walk carrying `minChangeVersion` without `maxChangeVersion` must also keep asking for the same data source: adding or dropping `Use-Snapshot: true` mid-walk is rejected the same way. Where it has none, `Use-Snapshot: true` is answered `404` with `Snapshot not found.` before any paging validation runs, so no invalid-token response arises. Every other walk is walked in the same order on either source and is not rejected on that change, but should still repeat the same `Use-Snapshot` choice so it keeps reading the database it started on. |

`pageSize=0` is accepted and returns an empty response with no
`Next-Page-Token`, because a page that selects nothing has nowhere to continue
from. It cannot be used to advance a walk.

### `/partitions` requests

| Parameter | Rules |
| --- | --- |
| `number` | Optional. The desired number of partitions, from `1` to `200`. Omitted means the deployment's configured `DefaultPartitionCount`. A non-numeric or out-of-range value is rejected. |
| Resource filters, `minChangeVersion` | Allowed, and should match the filters the walks will use. A partition calculated over a different filter set describes segments that do not match what the walks read. Where the data store has a `Snapshot` derivative configured and `minChangeVersion` is supplied without `maxChangeVersion`, the returned tokens also belong to the data source the request asked for: walks replaying them must repeat the same `Use-Snapshot` choice, and are rejected otherwise. On a snapshot the `minChangeVersion` bound itself is enforced as well, which it is not on current data: a walk that drops it from a set cut with `minChangeVersion` alone, and a walk that adds it to a set cut with no change-version window at all, are both rejected with the invalid-token message. A walk that adds `maxChangeVersion` beside it is served. Where the data store has no `Snapshot` derivative, `Use-Snapshot: true` is answered `404` with `Snapshot not found.` on this operation too. |
| `maxChangeVersion` | Allowed, and must match the walks that replay the returned tokens: on current data, a token from a request that included it is rejected on a walk that omits it, and a token from a request that omitted it is rejected on a walk that adds it. Same rule, same message, and the same snapshot exception as the `maxChangeVersion` row above, under the same limit: where the partitions request also carried `minChangeVersion`, a walk on a snapshot that drops the ceiling is served rather than rejected. Every token but the last then goes on bounding the partition it names, which is what keeps those walks inside their slices. The last range is unbounded above, so the walk replaying it reads past the boundary the set was cut under, to the newest version in the copy. The opposite direction is served on a snapshot as well: where the partitions request carried `minChangeVersion` without a ceiling, a walk that adds one keeps its tokens and reads each range narrowed to the overlap it shares with the ceiling. The `Use-Snapshot` choice is not enforced for these tokens, because a max-bearing window is anchored the same way on every data source; repeat it anyway, so the walks read the database the boundaries were cut from. |
| `pageToken`, `pageSize`, `limit`, `offset`, `totalCount` | Not supported on this operation and rejected. They belong to the collection GET-many. |

A `/partitions` response is always `application/json` and never carries
`Total-Count` or `Next-Page-Token`; a set of partition boundaries is not a page
and has no successor.

> [!NOTE]
> A resource that declares a query property literally named `number` can filter
> on it on its collection GET-many but not on its `/partitions` sibling, where
> that name is the partition-count parameter. This is an intentional difference
> from Ed-Fi ODS/API 7.3.2: ODS/API applies a single supplied `?number=` as both
> the partition count and the resource-property filter, while DMS uses it only as
> the partition count on this route.

## Related settings

Two deployment settings shape the numbers above, and both are published into the
API's OpenAPI specification, so a client reading the specification sees what the
deployment actually enforces rather than a shipped constant.

| Setting | Effect | Initial value |
| --- | --- | --- |
| `AppSettings:MaximumPageSize` | Upper bound for `pageSize` and `limit`, and the page size applied when neither is given. Published as both the `default` and the `maximum` of those two parameters | `500` |
| `AppSettings:DefaultPartitionCount` | Partition count used when `/partitions` omits `number`. Published as the `default` of `numberOfPartitions`; the accepted `1`..`200` range is fixed and not configurable | `10` |

See [Configuration](./CONFIGURATION.md) for the full settings reference.
