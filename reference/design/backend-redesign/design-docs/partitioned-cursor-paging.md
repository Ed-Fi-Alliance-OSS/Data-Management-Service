# Partitioned Cursor Paging: `pageToken`/`pageSize` and `/partitions`

## Status

This document is the normative design for the partitioned cursor-paging surface on regular
resource and descriptor GET-many endpoints:

- the `pageToken` / `pageSize` query parameters and the `Next-Page-Token` response header,
- the sibling `/partitions` operation that hands clients balanced, independently walkable ranges,
- the opaque cursor token contract, and
- the relational page-selection and partition-boundary SQL that make deep pages cost the same as
  shallow ones.

It does not own:

- the collection hydration batch shape (`PageKeysetSpec`, `PageDocumentIdSql`, multi-result-set
  reconstitution) — see [flattening-reconstitution.md](flattening-reconstitution.md);
- row-level authorization predicates and strategy shapes — see [auth.md](auth.md);
- the ascending root `DocumentId` ordering contract that cursor paging depends on — see
  [transactions-and-concurrency.md](transactions-and-concurrency.md);
- `ChangeVersion` semantics and stamping — see [update-tracking.md](update-tracking.md); or
- the `/deletes`, `/keyChanges`, and `/availableChangeVersions` endpoints — see
  [change-queries.md](change-queries.md). Those endpoints deliberately do **not** acquire cursor
  paging.

This design adds no persisted state, no schema migration, and no new index. It targets Ed-Fi API
v8.1 and does not replace `limit`/`offset` paging.

## Motivation

Traditional `limit`/`offset` paging has two structural problems for bulk consumers.

1. **Deep-page cost grows with depth.** `OFFSET n` requires the database to produce and discard
   `n` rows before the page begins. A client walking a million-row collection pays a cost that
   climbs on every request, and the last page is by far the most expensive.
2. **A collection cannot be split for parallel consumption.** Offsets are positions in one
   ordering of one result set. Two workers using offsets are reading the same scan, and any write
   that shifts the ordering shifts both of them.

Cursor paging replaces "skip `n` rows" with "seek to an anchor value and take `pageSize` rows".
Because every regular-resource root table and `dms.Descriptor` order collection results by an
indexed column — the root `DocumentId` primary key, or the indexed `ContentVersion` mirror when the
request carries a `maxChangeVersion` window — the seek is a range scan whose cost depends on the
page size, not on how far into the collection the page sits.

`/partitions` complements this by computing balanced, non-overlapping ranges over that same anchor,
across the filtered, authorized candidate set. Each returned token is a self-contained starting point, so
independent workers can consume disjoint slices of a collection concurrently without coordinating
offsets.

Cursor paging is a *cost* fix, not a *consistency* fix. It does not make a walk transactionally
consistent — see "Consistency Under Writes" below.

## Contract Source

The public surface implements the published Ed-Fi client guidance for partitioned cursor paging:

- [Improve Paging Performance with Partitioned Cursor Paging](https://docs.ed-fi.org/reference/ed-fi-api/client-developers-guide/improve-paging-performance-cursor-paging/)

Client tooling and generated SDKs written against that guidance must work against DMS unmodified.

That guidance leaves several things unspecified: validation precedence when parameters conflict,
exact error text, decoder strictness, terminal-page behavior, and partition sizing edge cases. For
those, **this document is the normative specification**, and each choice is recorded alongside the
rule it governs.

## Requirements and Non-Goals

### Requirements

1. **Depth insensitivity.** Cursor page latency MUST NOT grow with position in the collection.
   Cursor SQL MUST contain no `OFFSET`, no row-number skip, and no count query.
2. **No regression to traditional paging.** Existing `limit`/`offset` page-selection SQL MUST
   remain behaviorally and textually unchanged, and its latency MUST NOT regress.
3. **No extra roundtrip.** A cursor page MUST use the existing single-command page-keyset
   hydration architecture and add no database command, transaction, or roundtrip.
4. **One command for `/partitions`.** The partition endpoint MUST perform exactly one database
   command for its boundary selection, return identifiers only, and hydrate nothing. Where a
   view-based authorization strategy is configured, the pre-existing custom-view validation probe
   runs first, exactly as it does for GET-many. That probe is authorization validation rather than
   boundary retrieval, and keeping it separate is what preserves the configured check ordering: a
   view that may be validated only after an earlier check has passed cannot be co-batched behind
   that check without letting a relation masquerading as the view answer the membership SQL.
5. **Authorization parity.** Cursor pages and partition boundaries MUST be computed over the same
   filtered, authorized candidate set. A forged or hand-edited range MUST NOT expose an
   inaccessible identifier.
6. **Cross-engine parity.** PostgreSQL and SQL Server MUST produce identical candidate sets,
   identical partition boundaries, and identical tokens for the same seeded data and authorization
   context. Provider SQL may differ.
7. **Published contract conformance.** Parameter names, token syntax, response shapes, and error
   messages MUST match the published Ed-Fi cursor-paging contract, so existing client tooling works
   unmodified.
8. **Cursor paging is contract, not configuration.** It MUST NOT be feature-toggled.

### Non-goals

- Removing, deprecating, or transparently rewriting `limit`/`offset` paging.
- Cursor paging for composites, `/deletes`, `/keyChanges`, discovery, or management endpoints.
- Snapshot-consistent export, or any guarantee against data or authorization changes during a walk.
- Server-side cursor storage, token signing, token encryption, token expiration, or cross-database
  token portability.
- New DDL or indexes without measured provider evidence.

## Public API Contract

### GET-many cursor paging

| Input/output | Contract |
| --- | --- |
| `Next-Page-Token` response header | Included whenever regular-resource or descriptor GET-many page selection produces a non-null `HighestSelectedAnchor`, including on a `limit`/`offset` response that can begin a cursor walk and when concurrent deletes leave the hydrated response body empty. The token is anchored on whichever column page selection ordered by — `DocumentId` for unfiltered and min-only requests, `ContentVersion` for a max-bearing change-version window — and names that anchor in its marker, so ordering is never a reason to withhold a continuation. See "Consistency Under Writes" below. Absent when page selection is skipped or selects no keys, and at `Int64.MaxValue` where advancing would overflow. |
| `pageToken` | Selects the next inclusive anchor range, in the units its marker names. It is opaque to clients and is normally copied from `Next-Page-Token` or from a `/partitions` response. |
| `pageSize` | Optional, and permitted only when `pageToken` is present; integer `0..MaximumPageSize`. When omitted, the configured `MaximumPageSize` applies — initially `500`, matching the existing default GET-many size. |
| `limit`, `offset` | Remain supported for traditional paging. When `limit` is omitted, the configured `MaximumPageSize` applies. Neither parameter may be combined with `pageToken` or `pageSize`, including when its value is zero. |
| `totalCount` | Remains supported for traditional paging. When `pageToken` is present and valid, `totalCount=true` is invalid; an explicitly supplied `totalCount=false` is allowed. Clients wanting a count may issue `?totalCount=true&limit=0` separately before starting a cursor walk. |
| filters | Resource-property filters and `minChangeVersion`/`maxChangeVersion` compose with the cursor range. Clients MUST repeat the same filters on every request; the token stores none of them. The one exception is the anchor: because a token's bounds are meaningless in the wrong units, the token carries an ordering marker and a request whose change-version window resolves a different anchor than the marker names is rejected — see "Cursor Token Contract" below. |

**Starting a walk.** The first cursor page is an ordinary GET-many request, optionally using
`limit`. Its `Next-Page-Token` starts the seek-based walk *after* the page just returned. A
traditional page that used a non-zero offset therefore continues after that offset page, not from
the beginning of the collection. A token obtained from `/partitions` starts at that partition's
first accessible candidate.

Emitting `Next-Page-Token` on an ordinary `limit`/`offset` response is a deliberate extension: it
lets a client enter a cursor walk without a separate call, and it is inert for clients that ignore
it. The extension reaches every traditional response that selected keys, whichever column selection
ordered by: a max-bearing change-version window orders by `ContentVersion` and hands out a
`ContentVersion`-anchored token. The published guidance does not describe the header for traditional
responses, and the authoritative collection fixtures do not define it as a response header.

**Ending a walk.** The implementation does not fetch one extra row to predict the terminal page. It
emits a token whenever cursor page selection returns a non-empty keyset, and completion is normally
discovered by the next request returning an empty keyset and no token. Consequently the last useful
page is followed by one empty request. Predicting termination would require either an extra fetched
row on every page or a count query, and both cost more across a full walk than one trailing empty
request costs once. A bounded partition reaches its terminal empty page because advancing past the
item at the upper bound produces an inverted (match-nothing) range.

**Zero-size pages.** `pageSize=0` returns HTTP 200 with an empty array and no `Next-Page-Token`,
because its selected keyset is empty and `HighestSelectedAnchor` is null. It intentionally
cannot advance a cursor walk.

**Empty body with an advancing header.** If every selected row is concurrently deleted before
hydration, the response body can be empty while `Next-Page-Token` still advances past those
selected keys. Clients MUST treat the presence of the header, not a non-empty body, as the signal
to continue.

### Query-parameter name canonicalization

`pageToken`, `pageSize`, and the partition `number` parameter are case-insensitive at the HTTP
boundary and are canonicalized before Core validation. The canonicalization this design adds is
scoped to exactly those three names, and `number` only on the partitions operation.

`limit`, `offset`, and `totalCount` were already case-insensitive at the HTTP boundary before this
design, and remain so. That is deliberate public contract: the frontend has folded their names since
DMS-397, and three URL-validation scenarios lock it — `?liMIt=2`, `?OfFSeT=1`, and `?tOtAlCoUnT=trUE`
all succeed. This design makes that fold culture-invariant, which restores recognition on a server
whose culture is not the invariant one: the previous culture-sensitive fold left, for example,
`LIMIT` unrecognized under a Turkish locale, which lowercases `I` to a dotless `ı`. It does not
otherwise change which names are recognized.

A consequence worth stating, because it decides a mixed-mode outcome: since `LIMIT` already reaches
Core as `limit`, `?pageToken=<valid>&LIMIT=10` is a genuine traditional/cursor mixed-mode conflict
and is reported as one. It is not an invalid query field.

The frontend's existing last-value-wins behavior for repeated query parameters is preserved. Case
variants such as `pageToken` and `PAGETOKEN` MUST collapse to one canonical key and retain only the
last value in request order. Collapsing case variants MUST NOT throw.

### Cursor validation and ProblemDetails

The presence of either `pageToken` or `pageSize` — including a blank or malformed value — selects
the cursor validation path. Both that path and traditional `limit`/`offset`/`totalCount` parsing
answer a parameter fault with the parameter-validation ProblemDetails shell; the traditional
failures retain their existing messages, which predate this design.

A cursor request returns **exactly one** error. Evaluate the following four phases in order, use the
exact message shown for each rule, and stop at the first match.

The phases are ordered by dependency, not by parameter position. An undecodable token makes every
rule that reasons about a valid token meaningless; a mixed-mode conflict makes the individual
parameters' ranges irrelevant, because one of them should not have been sent at all; and an
unsatisfied required relationship makes range checking premature. Reporting the first failure in
this order returns the one error the client must fix first, and makes the response deterministic for
a request with several problems.

Query-key *presence* — including a blank, malformed, or zero value — is what controls phase
selection and the relationship/conflict rules through phase 2, before any general syntax or range
parsing. A client that sent `pageSize=` meant to send a page size; it should be told that
`pageToken` is missing, not that the parameter it typed was ignored.

**Phase 0 — token decode**

- `pageToken` present and not decodable: `The page token provided was invalid.`
- `pageToken` decodable, but its ordering marker disagrees with the anchor the request's
  change-version window resolves: the same message. A token whose bounds are read against the wrong
  column is no more replayable than a malformed one, and the answer is identical in both directions
  — a `ContentVersion`-marked token replayed without `maxChangeVersion`, and a `DocumentId`-marked
  token replayed with it. The token is opaque, so neither direction could tell the client anything
  it could act on beyond starting the walk over.

**Phase 1 — mixed-mode conflicts**

All rules in this phase require `pageToken` to be present and valid:

1. `offset` present: `Both offset and pageToken parameters were provided, but they support alternative paging approaches and cannot be used together.`
2. `limit` present: `Use pageSize instead of limit when using cursor paging with pageToken.`
3. `totalCount=true`: `The totalCount parameter cannot be set to true when using cursor paging with pageToken.`

**Phase 2 — required relationships**

These rules apply when `pageToken` is absent:

4. `pageSize` and `offset` present, with no `limit`: `Use limit instead of pageSize when using limit/offset paging.`
5. `pageSize` present: `PageToken is required when pageSize is specified.`

**Phase 3 — syntax and range**

Evaluate this phase in the canonical order `pageSize`, `limit`, `offset`, `totalCount`:

6. invalid `pageSize`: `PageSize must be a value between 0 and {MaximumPageSize}.`
7. invalid `limit`: `Limit must be omitted or set to a numeric value between 0 and {MaximumPageSize}.`
8. invalid `offset`: `Offset must be a numeric value greater than or equal to 0.`
9. invalid `totalCount`: `TotalCount must be a boolean value.`

Rules 7 and 8 keep DMS's existing `limit` and `offset` wording, which predates this design and
which existing clients may already match on. They read differently from the cursor messages for
that reason.

The numbered rules are the within-phase tie-breakers. A phase-0 failure suppresses every other rule;
a mixed-mode conflict suppresses relationship and syntax/range rules; a required-relationship
failure suppresses syntax/range rules.

Cursor, partition, and traditional-paging failures use HTTP 400 with this JSON shape and the
current DMS `application/json` response media type:

```json
{
  "detail": "Parameters supplied to the request were invalid.",
  "type": "urn:ed-fi:api:bad-request:parameter-validation-failed",
  "title": "Parameter Validation Failed",
  "status": 400,
  "correlationId": "<request correlation id>",
  "validationErrors": {},
  "errors": ["<message>"]
}
```

For a cursor failure, `errors` contains exactly the one message selected above. A partition failure
uses the same shell but may contain several ordered messages. A traditional-paging failure also uses
this shell, and reports every faulty parameter rather than the first, ordered `offset`, `limit`,
`totalCount`. That order is not the phase-3 order above: phase 3 breaks ties to select a single
error, while traditional parsing emits a list. Sharing one shell therefore does not mean sharing one
cardinality — a client cannot infer from the response shape how many messages it may hold.

#### Worked precedence examples

`X` denotes any successfully decoded page token. Each request below returns HTTP 400 with exactly
the listed message.

| Request | Expected message |
| --- | --- |
| `?pageToken=X&offset=-1` | `Both offset and pageToken parameters were provided, but they support alternative paging approaches and cannot be used together.` |
| `?pageToken=X&limit=99999` | `Use pageSize instead of limit when using cursor paging with pageToken.` |
| `?pageSize=99999` | `PageToken is required when pageSize is specified.` |
| `?pageSize=` | `PageToken is required when pageSize is specified.` |
| `?pageToken=!!!&offset=5` | `The page token provided was invalid.` |
| `?pageToken=!!!&limit=10` | `The page token provided was invalid.` |
| `?pageSize=5&limit=10` | `PageToken is required when pageSize is specified.` |
| `?pageSize=5&totalCount=true` | `PageToken is required when pageSize is specified.` |
| `?pageSize=5&offset=3&totalCount=true` | `Use limit instead of pageSize when using limit/offset paging.` |
| `?pageToken=X&pageSize=-1` | `PageSize must be a value between 0 and {MaximumPageSize}.` |
| `?pageToken=X&pageSize=abc` | `PageSize must be a value between 0 and {MaximumPageSize}.` |
| `?pageToken=X&limit=10&pageSize=5` | `Use pageSize instead of limit when using cursor paging with pageToken.` |
| `?pageToken=X&totalCount=true` | `The totalCount parameter cannot be set to true when using cursor paging with pageToken.` |

Note the `?pageToken=X&limit=10&pageSize=5` row: a phase-1 conflict rejects the request even though
its cursor parameters are individually well formed and would otherwise have returned a page.
Sending `limit` alongside `pageSize` is a paging-mode mistake, and it is reported ahead of any
phase-3 range check on `pageSize` so that a request carrying both mistakes is answered with the mode
error rather than a range message pointing at the wrong parameter.

#### Operation scoping

Cursor parameter recognition is operation-scoped. Supplying `pageToken` or `pageSize` to `/deletes`
or `/keyChanges` returns the existing HTTP 400 bad-request shell with
`The query field '{parameter}' is not valid for this Change Query endpoint.`

These names MUST NOT become globally reserved parameters that unsupported endpoint families
silently ignore. Silently accepting and discarding a `pageToken` on a change-query endpoint would
let a client believe it was walking a cursor when it was re-reading page one.

### `/partitions`

Each regular resource and descriptor collection exposes a sibling operation:

```http
GET /data/{projectEndpoint}/{resourceEndpoint}/partitions?number=10
```

Route qualifiers, tenant segments, authentication, resource authorization, and profile routing
continue to compose through their existing DMS boundaries.

| Input/output | Contract |
| --- | --- |
| `number` | Optional desired partition count. Valid range is `1..200`; the default is configurable and initially `10`. |
| response | HTTP 200 with `{ "pageTokens": ["...", "..."] }`. No accessible candidates produces an empty array. |
| fewer partitions | The response may contain fewer tokens than requested, because every partition is at least five maximum-sized pages. It never contains more. |
| filters | Supports the same resource-property and live-resource change-version filters as GET-many. Boundaries are calculated after filters and authorization. |
| excluded parameters | `limit`, `offset`, `pageToken`, `pageSize`, and `totalCount` are not part of the partition operation. |

**Profile behavior.** Profile content-type handling follows the existing GET-many outcome: a
`/partitions` request that explicitly requests a profile whose resource exposes no readable content
type returns the existing HTTP 405 profile method-usage response, and a request for which no
readable profile applies implicitly proceeds unfiltered. The partition response is never
profile-projected, so runtime enforcement and the OpenAPI omission of `/partitions` from write-only
profile documents agree.

#### Partition validation

`number` is the only partition-control parameter the operation accepts. Alongside it, `/partitions`
accepts the same resource-property filters and `minChangeVersion`/`maxChangeVersion` live
change-version filters that GET-many accepts, because boundaries are calculated over the filtered,
authorized candidate set. The five reserved paging parameters — `pageToken`, `pageSize`, `limit`,
`offset`, and `totalCount` — are instead reported with the specific unsupported message below, so a
client that confused the two endpoints gets a useful answer. Every other query field is rejected by
the existing unknown-query-field rule.

Partition validation uses its own ordered phases, and unlike cursor validation the last of them may
report several errors. The four phases run in this order, and the first one to find a fault answers:

1. **Change-version window.** The same `minChangeVersion`/`maxChangeVersion` parsing GET-many
   applies, in the same position relative to filters that GET-many puts it in.
2. **Resource filters.** The same unknown-query-field and filter-value-type rules GET-many applies,
   over the same candidate set. The five reserved paging names and `number` are excluded from filter
   matching before this phase runs, so a supplied `limit` is not reported as an unknown query field.
   Excluding `number` is also what makes a resource property of that name unfilterable here while it
   stays filterable on the collection GET, which is the approved intentional ODS difference the epic
   records.
3. **`number` syntax and range.** A malformed or out-of-range `number` produces the exact error
   `Number of partitions must be between 1 and 200.` A present-but-blank `?number=` is a malformed
   value and produces that same error rather than being treated as absent and defaulted: a client
   that typed `number=` asked for a partition count, and the parameter it typed should not be
   silently ignored. This phase takes precedence over the unsupported-parameter phase.
4. **Reserved parameters.** Reserved paging parameters are reported as unsupported *without* first
   parsing their values, using the exact error
   `The '{parameter}' parameter is not supported by the partitions endpoint.` If several are
   present, report them in the canonical order `pageToken`, `pageSize`, `limit`, `offset`,
   `totalCount`.

The change-version window and filters are placed ahead of the two partition phases, unlike GET-many,
which validates paging first because a paging fault is the first thing wrong with a page request.
This operation has no page. Within the two shared phases, the window is validated ahead of filters,
which is the order GET-many uses: a query string that faults in both ways must be answered with the
same problem type by both operations, because a client that discriminates on `type` should not have
to know which of the two sibling endpoints it called. Filters must in turn run ahead of phase 4,
because excluding the reserved names from filter matching is what lets phase 4 report `?limit=5` as a
parameter that does not apply here rather than as an unknown query field, and that exclusion is only
meaningful if filter matching happens before the reserved-parameter phase reports.

Four consequences of the ordering, each a fixed part of the contract:

- `?number=abc&notAField=1` answers with the unknown-query-field error alone. Both are client
  mistakes, and answering the field first keeps this operation's unknown-field behavior identical to
  GET-many's.
- `?number=abc&minChangeVersion=bogus` answers with the change-version error alone.
- `?notAField=1&limit=5` answers with the unknown-query-field error alone.
- `?minChangeVersion=bogus&notAField=1` answers with the change-version error alone, in the
  parameter-validation shell — the same problem type GET-many answers that query string with.

The asymmetry with cursor validation is deliberate. Cursor parameters are interdependent — the
meaning of `limit`, `pageSize`, and `totalCount` all depend on whether a valid `pageToken` is
present — so reporting more than one error would report consequences rather than the cause.
Unsupported partition parameters are independent mistakes, and a client that sent three of them
should learn about all three in one response instead of over three round trips. `number` is
validated first because it is the only parameter that controls the partition calculation itself,
while the reserved paging parameters have no effect on it at all.

#### Partition sizing

```text
requested count = number ?? DefaultPartitionCount
computed size   = ceiling(accessible candidate count / requested count)
minimum size    = MaximumPageSize * 5
partition size  = max(computed size, minimum size)
```

Select the actual anchor value at candidate row numbers `1`, `1 + partition size`,
`1 + 2 * partition size`, and so on. Each token covers its starting anchor through one less than the
next starting anchor; the last token is unbounded above, though a max-bearing request still clips it
to `maxChangeVersion` because that filter is reapplied on every page. Selecting actual values at
those row numbers — rather than dividing the value range arithmetically — is what keeps partitions
balanced when the anchor is sparse, which `DocumentId` always is after deletes and `ContentVersion`
always is over a window that other resources also stamped.

Assembling starts into ranges assumes the selected starts are strictly ascending, which is also
where this design's `ContentVersion` uniqueness assumption is enforced: anchors and tokens carry one
`ContentVersion`, not a `(ContentVersion, DocumentId)` pair, and a duplicate anchor could otherwise
put a row on the wrong side of a boundary. The shared change-version sequence assigns distinct
values, including across the rows of a multi-row write. A schema constraint enforcing that is not
part of this design.

The five-page minimum exists so that a small collection is not sliced into partitions that cost
more to coordinate than to read. It is why a client asking for 200 partitions of a 1,000-row
collection receives one token.

**The size is a true mathematical ceiling.** A floored size produces partitions smaller than the
requested count requires, so covering the candidate set takes one *more* partition than requested.
Because the contract promises at most `number` tokens and never more, the division MUST NOT be
computed as an integer quotient with a ceiling applied afterward — that ceiling is a no-op on an
already-truncated value. Use non-integer arithmetic, or any exact equivalent. The algebraic
spelling is not contractual; the returned token count is.

`/partitions` applies all filters and authorization *before* row numbering and counting. The
candidate relation MUST contain one row per `DocumentId`, even when an authorization strategy uses
joins internally.

## Cursor Token Contract

Clients MUST treat tokens as opaque. The token is a transport encoding of an ordering-mode marker
and an inclusive anchor range, and carries nothing else.

### Encoding

1. Format the ordering-mode marker — `d` for a `DocumentId`-anchored range, `c` for a
   `ContentVersion`-anchored one — followed by the inclusive minimum and maximum anchor values as
   invariant-culture signed decimal `Int64` values, all three separated by commas.
2. UTF-8 encode that text.
3. Base64url encode it: replace `+` with `-`, `/` with `_`, and remove `=` padding.

The encoder always includes all three fields and always emits canonical unpadded base64url.

**The marker is mandatory in every token, and there is no unmarked legacy form.** A token stores no
request filters, so without the marker the server could not tell a `ContentVersion` anchor from a
`DocumentId` one when a client changes `maxChangeVersion` mid-walk, and would replay the token's
bounds against the wrong column. Cursor paging and the marker ship in the same release and tokens
are opaque, so nothing holds a two-field token.

### Decoding

The decoder accepts correctly padded or unpadded base64url input. It rejects:

- the characters `+` and `/`;
- internal padding, and more padding than required;
- an impossible base64url length;
- invalid UTF-8; and
- anything other than exactly three comma-separated fields.

The marker field MUST be exactly `d` or `c`. Casing variants, padding, and surrounding whitespace
are rejected by that exact match, without a rule of their own. An unknown or empty marker is
rejected exactly as a malformed bound is.

A decimal field MUST match `-?[0-9]+`. Whitespace and a leading `+` are invalid, and the parsed
value MUST fit `Int64`. The minimum is required. An empty maximum decodes as `Int64.MaxValue`,
meaning unbounded above; the encoder never emits that form, but accepting it lets a client or tool
express an open-ended range. The latitude is confined to the maximum bound; the marker has none.

The decoder is deliberately stricter than a permissive base64 reader. Accepting forms the encoder
never emits would create an undocumented input surface that a later change could not safely narrow
without breaking clients that had come to depend on it.

Decoding grants no authority and makes no authorization decision, so it reports success or failure
rather than a message. Whether a successfully decoded marker agrees with the anchor the request
resolved is request validation's decision, reported as phase 0 above.

### Range semantics

The decoded value is a typed `CursorRange(InclusiveMinimum, InclusiveMaximum)` plus the marker's
ordering mode. `CursorRange` is not renamed for the anchor: its units are whatever the marker says,
and the bounds are compared against that anchor column.

Negative bounds, and a minimum greater than the maximum, are safe match-nothing ranges rather than
authorization bypasses or errors. An inverted range is also how a bounded partition reaches its
terminal empty page after returning the item at its upper bound.

After a non-empty selected keyset, the next token uses `HighestSelectedAnchor + 1` and retains
the request's maximum bound — which is how a partition walk stays inside its slice. A request that
carried no `pageToken`, and therefore no maximum bound, uses `Int64.MaxValue`, so a walk entered from
a traditional response is unbounded above. If the highest selected anchor is `Int64.MaxValue`, omit
the header rather than overflowing.

The bounds stay inclusive under either anchor. `ContentVersion > @anchor` and
`ContentVersion >= @anchor + 1` are the same predicate over an integer sequence, so reusing the
inclusive form keeps one bound shape, one token shape, and one partition-range assembler rather than
a second set that would have to be kept in step with the first.

The decoded ordering mode is not carried past validation. Once the marker check has passed it equals
the anchor the request resolved, and that is what the SQL compiler and the token emitter read; a
second copy on the paging record would be one more defaultable value.

### Security properties

Tokens are not signed, encrypted, or bound to a resource, filter set, client, tenant, or database.
This is safe because a token carries no authority: routing, resource authorization, supplied
filters, and row-level authorization are independently recompiled and reapplied on every request.
Editing a range in a token can only narrow or widen the id window *inside* an already-authorized,
already-filtered candidate set. Tokens are not promised to be portable between data stores.

### Layering

The codec belongs to Core's HTTP-contract boundary. Frontend code only canonicalizes parameter
names. Backend contracts, planners, and SQL compilers receive the typed `CursorRange` and never
parse or emit token text. A SQL compiler that could see a token string would be one refactor away
from making an authorization decision from client-supplied text.

## Relational Design

### Paging-mode choice

Both PostgreSQL and SQL Server already page regular resources and descriptors in ascending
`DocumentId` order (see [transactions-and-concurrency.md](transactions-and-concurrency.md)), and
every page root has a `DocumentId` primary key. That existing ordering contract is the entire
foundation of this feature; cursor paging adds a range predicate to it rather than a new sort.

Extend page selection with an explicit paging-mode choice:

- **traditional**: existing `ORDER BY <anchor>` plus `OFFSET`/`LIMIT` or `FETCH`;
- **cursor**: add `<anchor> >= @cursorMin AND <anchor> <= @cursorMax`, order by the anchor,
  and take `@pageSize` with no offset operation;
- **partition planning**: reuse the same unpaged, filtered, authorized candidate relation.

Every candidate mode carries the anchor Core resolved for the request, and one resolver maps that
choice to a column name. Ordering, bounds, and projection therefore cannot name different columns
for one plan, and the partition compiler resolves through the same mapping rather than repeating it
— a second mapping that drifted would rank a column the candidate relation does not project. The
parameter names `cursorMin` and `cursorMax` are unchanged and now name anchor bounds.

Represent live collection paging as a discriminated choice rather than nullable combinations, so
"cursor request with a null range" and "traditional request with a page size" are unrepresentable:

```text
CollectionPaging
|- Traditional(PaginationParameters)
`- Cursor(CursorRange, PageSize)
```

Retain the existing `PaginationParameters` model for traditional and tracked-change paging, so
`/deletes` and `/keyChanges` neither acquire cursor behavior nor reserve cursor parameter names.
Add explicit query-parameter roles for cursor bounds and page size, and for partition count and
minimum size; do not overload the existing offset/limit roles.

### Shared candidate relation

Extend the existing `PageDocumentIdQuerySpec` and shared `PageDocumentIdSqlCompiler` rather than
introducing a parallel candidate abstraction. The spec already carries the root relation, value and
live change-version predicates, unified-alias rewrites, the row-level authorization specification,
paging parameter names, and deterministic compiler inputs — and both the regular-resource and
descriptor planners already construct it.

The additions are:

- explicit cursor-bound and page-size parameter roles;
- explicit partition-count and minimum-size parameter roles; and
- exposure of the same unpaged candidate relation to the partition compiler.

The regular-resource builder continues to root on the resource table; the descriptor builder
continues to root on `dms.Descriptor` with its mandatory `ResourceKeyId` predicate.

The residual Core-side work is to share resource-filter and live change-version parsing between
GET-many and `/partitions`, so candidate behavior cannot drift between the two before it reaches
SQL. A client whose partition boundaries were computed over a different candidate set than its
cursor pages would silently skip or duplicate documents, and nothing in the response would say so.

**One row per `DocumentId` (normative).** The candidate relation MUST produce exactly one row per
`DocumentId`. Duplicate candidate rows would corrupt `ROW_NUMBER()`, the candidate count, and
therefore every partition boundary. Authorization strategies MUST preserve uniqueness by
construction, normally through `EXISTS`. Do not add an unconditional `DISTINCT` to conceal a
duplicate-producing authorization plan — that hides the defect and adds a sort.

Prove the invariant with test coverage, not with a runtime guard: assert one row per `DocumentId`
in an explicit test for each consumer and each supported authorization strategy. A per-request
uniqueness check would add cost to every query for the same reason an unconditional `DISTINCT`
would, and it would report the defect in production rather than in the build.

### Provider cursor SQL

`<anchor>` below is `DocumentId` for an unfiltered or min-only request and `ContentVersion` for a
max-bearing change-version window. One column name is substituted throughout: ordering, both bounds,
and the projection all resolve from the same anchor.

PostgreSQL:

```sql
SELECT r."DocumentId"[, r."ContentVersion"]
FROM <shared candidate FROM/JOIN clauses>
WHERE <resource, change-version, and authorization predicates>
  AND r."<anchor>" >= @cursorMin
  AND r."<anchor>" <= @cursorMax
ORDER BY r."<anchor>"
LIMIT @pageSize;
```

SQL Server uses `TOP`, not `OFFSET 0`, so the no-offset invariant is literal rather than merely
semantic:

```sql
SELECT TOP (@pageSize) r.[DocumentId][, r.[ContentVersion]]
FROM <shared candidate FROM/JOIN clauses>
WHERE <resource, change-version, and authorization predicates>
  AND r.[<anchor>] >= @cursorMin
  AND r.[<anchor>] <= @cursorMax
ORDER BY r.[<anchor>];
```

**The candidate projection is asymmetric, and deliberately so.** A `DocumentId`-anchored page
projects `DocumentId` alone, byte-for-byte as before. A `ContentVersion`-anchored *page* projects
both columns: `DocumentId` feeds the keyset insert and every downstream hydration join, while
`ContentVersion` is the continuation anchor, and hydration can only read columns this embedded SQL
projects. A `ContentVersion`-anchored *unpaged candidate relation* — the one partition planning
consumes — projects the anchor alone, because its consumer ranks and cuts boundaries on that column
and hands the value straight back.

Projecting `DocumentId` alone on an anchored page would compile, and would then force a second
lookup after selection to recover the anchor. That is the concurrent-delete stall this design was
built against: a page whose rows are all deleted between selection and hydration must still report
where it ended.

Cursor mode never compiles or runs total-count SQL. The filter and authorization fragments are
byte-identical to the `DocumentId` case, which is what makes cursor pages and partition boundaries
provably the same candidate set.

Existing traditional page-selection SQL MUST remain behaviorally and textually unchanged. That
textual gate does not cover the collection hydration-batch change described next, which is required
to expose selected keys; traditional response behavior is nonetheless unchanged.

### Carrying the selected-keyset boundary

A regular-resource collection page materializes its keyset once, as the current hydration batch
already does (see [flattening-reconstitution.md](flattening-reconstitution.md) §7.10). For
`PageKeysetSpec.Query`, surface the inserted keys as the **first** batch result set:

- PostgreSQL: `RETURNING "DocumentId"`
- SQL Server: `OUTPUT INSERTED.[DocumentId]`

When the keyset is `ContentVersion`-anchored, and only then, the keyset temp table gains a nullable
`ContentVersion` column, the `page_ids` selection and the insert column list carry it, and the
returning clause names it as a second column. The conditional is what keeps every existing
emitted-SQL golden byte-identical and leaves the "traditional page-selection SQL is textually
unchanged" gate intact; a zero-size page and the candidate-metadata batch take the same treatment,
so an empty page keeps the same result-set shape as any other.

`HydrationExecutor` calculates the anchor maximum from that result set without depending on row
order, reading the anchor ordinal when the keyset carries one and the `DocumentId` ordinal when it
does not. `PageKeysetSpec.Single` GET-by-id hydration retains its existing batch shape and does not
gain this result set.

Carry the nullable `HighestSelectedAnchor` through `HydratedPage` and `QuerySuccess` so Core
can create `Next-Page-Token`. This returns at most `MaximumPageSize` bigint values per column and
adds no second candidate selection, database command, transaction, or roundtrip.

Deriving the anchor from the selected keyset — rather than from hydrated document metadata — is
what makes concurrent deletion safe. Any or all selected rows may be deleted before hydration
completes; a body-derived anchor would then stall the walk on the last surviving document, or
stop it entirely when the body came back empty. That reasoning is why widening the selected keyset
is the only place the anchor can come from, and it holds under either anchor.

Descriptor page selection retrieves its rows in the same statement that selects them, so it needs no
keyset temp table — but it does need the same discipline about *which* column the anchor is. Under a
`ContentVersion` anchor the descriptor page-rows statement projects the anchor out of the embedded
page-selection relation, aliased away from the `ContentVersion` it also projects from `dms.Document`,
and the handler takes its maximum from that alias. The two columns hold the same value for a
committed row — the stamping triggers keep the root mirror in lock-step with the canonical
`dms.Document` value — but they are read through different joins in one statement, so under a
provider that admits intra-statement read skew a concurrent update can make the document copy the
larger of the pair. A continuation anchored on that copy would start the next page past rows the
current one never returned. The anchor is therefore always the value page selection ordered,
bounded, and indexed on, on this path exactly as on the regular-resource path.

`HighestSelectedAnchor` is null when page selection is skipped or the selected keyset is empty
— including authorization, preprocessing, and planner early-empty paths, and zero-size pages. An
empty response array alone cannot distinguish those cases from concurrent deletion after selection,
which is precisely why the anchor is a separate nullable value rather than an inference from the
body. Core emits the token whenever `HighestSelectedAnchor` is present, regardless of the final
response-body count, except in the `Int64.MaxValue` overflow case. There is no separate flag saying
whether a page may be continued: the anchor's presence is the whole rule, and the token names which
anchor it is.

### Partition planning

Compile the already filtered and authorized candidate relation into one provider-specific statement
that:

1. orders unique candidates by the request's anchor;
2. derives row number and candidate count;
3. computes the partition size; and
4. returns only the starting anchor values.

`<anchor>` below is `DocumentId` for an unfiltered or min-only request and `ContentVersion` for a
max-bearing change-version window — the same resolution a page of the same request uses, so
boundaries and pages cannot be computed over different columns. The unpaged candidate relation
projects that one column and nothing else, which is why the CTEs name it directly.

PostgreSQL has this illustrative logical shape:

```sql
WITH candidates AS (
    SELECT r."<anchor>"
    FROM <shared candidate relation>
    WHERE <all predicates>
),
ranked AS (
    SELECT
        "<anchor>",
        ROW_NUMBER() OVER (ORDER BY "<anchor>") AS row_number,
        COUNT(*) OVER () AS candidate_count
    FROM candidates
),
sized AS (
    SELECT *,
        GREATEST(
            CEIL(candidate_count::numeric / @number),
            @minimumPartitionSize
        ) AS partition_size
    FROM ranked
)
SELECT "<anchor>"
FROM sized
WHERE (row_number - 1) % partition_size = 0
ORDER BY "<anchor>";
```

SQL Server uses the equivalent CTE with `COUNT_BIG`, `ROW_NUMBER`, and a provider-appropriate
ceiling expression.

The database returns starting anchor values only. Backend code converts each non-final start to the
inclusive range `start..nextStart-1` and the final start to `start..Int64.MaxValue`; Core
token-encodes those typed ranges and stamps the request's anchor marker on every one of them. Range
assembly is anchor-agnostic — ascending starts become inclusive ranges identically over either
column — so there is one assembler, not two.

The endpoint performs one database command for its boundary selection and does not hydrate
documents, project profiles, resolve descriptors, inject links, or return a total count.

**The linear cost is deliberate.** The partition query is `O(n)` over accessible candidates. That
cost is paid once, per client, to enable an arbitrary number of subsequent depth-insensitive range
scans — the opposite of `OFFSET`, which pays a growing cost on every page forever. Adding secondary
indexes is out of scope unless measured provider plans demonstrate a specific regression that
existing root, filter, and authorization indexes cannot serve.

## Application Boundaries

- **Frontend / path routing.** Replace the implicit "optional third segment means UUID" model with
  `ResourcePathOperation.Collection`, `.ById(DocumentUuid)`, and `.Partitions`. This recognizes
  `/{project}/{resource}/partitions` before UUID parsing. Unknown third segments retain the
  existing invalid-UUID response, and additional segments remain unmatched. The frontend
  canonicalizes `pageToken`, `pageSize`, and partition `number` alongside the traditional names it
  already folded, while preserving last-value-wins semantics.
- **Core model.** Use the explicit traditional/cursor choice and the typed `Int64` range described
  above. Keep token text encoding and decoding at the HTTP contract boundary.
- **Core pipelines.** Keep the existing GET-many pipeline for cursor pages. Add a dedicated
  partition pipeline with request logging and error handling, tenant and datastore resolution,
  typed path parsing, database fingerprint / resource seed / mapping resolution, endpoint and
  profile resolution, resource-info construction, shared filter and change-version validation,
  partition validation, resource-action authorization, row-level authorization filter construction,
  and then a partition handler. Partitions do not hydrate or profile-project documents.
- **Backend contracts.** Add a dedicated `IPartitionQueryHandler`; do not route partition work
  through `QueryDocuments`, whose contract is built around hydrated documents and total count.
  Query success carries the nullable selected anchor maximum, and partition success carries typed
  ranges. SQL planners and executors never parse token strings.
- **Anchor ownership.** Core resolves the anchor once, from the parsed change-version window plus
  the legacy ordering setting, and carries it on the request contracts the backend consumes —
  including the descriptor query and partition request records, which do not travel on those
  contracts and would otherwise have no source for it. The backend never re-resolves it. Two
  resolutions of one rule would be two rules: Core has to know the anchor to validate an incoming
  token's marker before the backend runs and to stamp the marker on an outgoing one, and the backend
  has to know it to compile the SQL, so the choice is made in one place and passed down.
- **Candidate planning.** Extend `PageDocumentIdQuerySpec` and `PageDocumentIdSqlCompiler` with
  cursor and partition roles and the request's anchor, share Core filter and change-version parsing,
  and assert that traditional pages, cursor pages, and partition boundaries all consume one unique
  candidate row per document.

## OpenAPI Assembly

The current ApiSchema base documents already contain `pageToken`, `pageSize`, and
`numberOfPartitions` parameter components — the last of which describes the `number` query
parameter — while resource fragments omit the cursor parameter references, the `Next-Page-Token`
header, and the partition paths.

Add these platform-wide operations during DMS OpenAPI assembly, after all core, abstract, and
extension fragments are merged but before domain and profile filtering.

For every eligible core-resource, extension-resource, and descriptor collection:

- append `pageToken` and `pageSize` parameter references to the collection GET;
- document `Next-Page-Token` as a string header on its HTTP 200 response;
- add a sibling `/partitions` GET operation;
- copy resource filters, live change-version filters, security, tags, and domain metadata from the
  collection operation, but do **not** copy traditional/cursor paging or `totalCount` parameters to
  the partition operation;
- use a reusable HTTP 200 `application/json` schema containing `pageTokens: string[]`;
- generate the partition `operationId` by appending `Partitions` to the collection GET
  `operationId` — for example `getStudentsPartitions` and `get_TPDMCandidatesPartitions`; and
- provide a partition-specific summary and description rather than copying the collection GET text.

**Publish only what runs (normative).** Each piece of published metadata is gated on its
corresponding runtime execution being complete: cursor parameters and the response header are
published only once regular-resource and descriptor cursor execution are active, and the
`/partitions` path is published only once the runtime partition pipeline is active. There is no
advance-publication interval and no interim feature toggle. A generated client that calls a
published path DMS does not serve fails at runtime, which is worse than the path being absent.

Do not augment item-by-id, change-query, discovery, or management paths, and do not introduce
composite paths.

Publish the runtime `MaximumPageSize`, initially `500`, as both the default and the maximum for the
existing `limit` parameter and the new `pageSize` parameter. This replaces the authoritative
fixture's published `limit` default of `25` with fixed maximum `500`, and its `pageSize` default of
`25` with no maximum, using the runtime value consistently across assembled DMS documents. Publish
`DefaultPartitionCount` as the `numberOfPartitions` default. Published metadata that disagrees with
runtime enforcement is a defect regardless of which value is more conservative.

**Profile filtering.** Profile OpenAPI filtering MUST explicitly associate `/partitions` with its
base collection, because the partition response carries no resource schema from which the current
filter could infer the relationship. Retain the operation only when the profile exposes readable
content for that resource. The partition success response stays `application/json` in profile
documents and is not rewritten to a resource-profile media type. Descriptor partition operations
belong to descriptor OpenAPI; this feature does not introduce descriptors into the existing
resource-derived profile document.

## Configuration

| Setting | Default | Validation |
| --- | --- | --- |
| `AppSettings:MaximumPageSize` | `500` (existing) | `> 0` |
| `AppSettings:DefaultPartitionCount` | `10` | `1..200` |

`DefaultPartitionCount` has both a property default and a configured default of `10`. Its
environment override is `AppSettings__DefaultPartitionCount`. Both values are validated at startup
and passed into OpenAPI assembly.

Calculate the minimum partition size with checked `long` arithmetic as `(long)MaximumPageSize * 5`,
so a large configured page size cannot silently overflow into a negative or wrapped minimum — which
would defeat the `max(computed, minimum)` guard and produce absurd partition counts.

Cursor paging is part of the API contract and is not feature-toggled.

## Consistency Under Writes

Cursor paging is not a snapshot protocol:

- deletes create harmless identity gaps;
- under a `DocumentId` anchor, updates retain `DocumentId` and do not move between ranges, but can
  change filter, change-version, ownership, namespace, or relationship membership;
- under a `ContentVersion` anchor, an update advances the row past the window maximum, so the row
  leaves the window rather than moving to a later range within it — nothing else shifts;
- an item that becomes eligible behind the current lower bound can be missed, while an item that
  becomes ineligible before its page is reached disappears;
- new documents receive larger identity values and larger change versions, and may appear in the
  final unbounded partition;
- a deleted and recreated document receives a new `DocumentId` and may appear later in the walk;
- changing filters, claims, ownership, or relationship authorization during a walk may change later
  results; and
- retries with the same token may observe committed changes.

Non-final partition upper bounds prevent a later insert from moving into a completed partition —
which is why every partition except the last is bounded above.

Routing, resource authorization, supplied filters, and row-level authorization are independently
reapplied on every request, so moving a token between resources, clients, tenants, or databases
does not confer access and is not promised to produce meaningful results.

This feature adds no long-running transactions, no server-side cursor state, no snapshot handles,
and no repeatable-read guarantees.

**Relationship to change-query extraction.** [change-queries.md](change-queries.md) catalogues the
hazards of extracting a `ChangeVersion` window without snapshots. Cursor paging changes one of them
and none of the others.

Under "Using limit/offset without using snapshots", a document whose `ChangeVersion` moves out of
the requested window mid-extraction shifts every later document one position earlier, so an
*unrelated, unchanged* document can slide into an already-paged offset and be silently skipped.
Cursor ranges are anchored to a value — `DocumentId` or `ContentVersion` — rather than to a position
in a shifting result set, so a mid-walk eligibility change cannot move another document across a
page boundary. The document that
itself left the window is still not returned, but its `ChangeVersion` is now above the client's
watermark, so the next synchronization picks it up. That is the specific mechanism reverse paging
was introduced to work around.

Cursor paging does **not** address the other hazards in that document — unresolved references when
not using snapshots, key changes observed without a `maxChangeVersion` filter, or data that becomes
newly accessible behind the client's change window. The mitigations recorded in
[change-queries.md](change-queries.md) continue to apply to those.

**Conditional change-window ordering.** Page selection orders a max-bearing change-version window by
`ContentVersion`; see "Page-selection ordering" in [change-queries.md](change-queries.md).

The first page of a cursor walk uses the same page-selection query as an offset request, so a token
based on a `DocumentId` boundary would be unsafe whenever that query orders by `ContentVersion`:
replaying it could skip qualifying rows with a smaller `DocumentId` and a later `ContentVersion`.

Windowed (max-bearing) requests therefore anchor cursor pages, partition boundaries, and
continuation tokens on `ContentVersion`. **Invariant: the anchor follows the ordering.** A page's
ordering key, its cursor bounds, and the anchor stamped on the token it hands out are always the
same column, and the token names which column that was.

Min-only and unfiltered walks keep `DocumentId` anchors, and the asymmetry is not an omission. A
min-only window stays open as data changes: an update moves a row later in `ContentVersion` order
while the row remains eligible, so a `ContentVersion`-anchored walk could return it twice. A
max-bearing window is a monotonic-escape window — an update pushes the row past the maximum and out
of the window entirely — so the same movement removes the row rather than replaying it.

`ContentVersion` anchoring also means a windowed walk seeks the change-version index instead of
walking the primary key and discarding rows outside the window, so a windowed page costs what its
page size costs rather than what its window position costs. The applicable indexes already exist:
`IX_<Table>_ContentVersion` for regular resources, and
`IX_Descriptor_ResourceKeyId_ContentVersion_DocumentId` for descriptors, whose page selection filters
on the authoritative `ResourceKeyId`. This anchoring adds no DDL.

`AppSettings:UseLegacyDocumentIdOrderingForChangeQueries` governs cursor and partition anchoring
along with traditional page selection, from one shared resolver. A deployment running with legacy
ordering keeps issuing and accepting `DocumentId`-marked windowed tokens rather than breaking a walk
in progress.

## Performance Invariants

These are properties of the running system. A change that preserves the contract above but
violates one of these has not implemented this design.

### Structural invariants

- Cursor SQL contains no `OFFSET`, no row-number skip, and no count query, and uses an indexed
  anchor column as a range predicate: the root `DocumentId` key, or `ContentVersion` under a
  max-bearing window.
- Cursor implementation does not otherwise change traditional `limit`/`offset` page-selection
  SQL. The conditional ordering rule and the selected-key result set added to collection
  hydration batches are the explicit exceptions, and the latter widens only when the keyset is
  `ContentVersion`-anchored — `DocumentId`-anchored batch text is byte-identical to its pre-cursor
  form.
- Cursor hydration performs one database command and adds no roundtrip over the existing
  single-command page-keyset architecture.
- `/partitions` performs one database command for its boundary selection and returns identifiers
  only. The separate custom-view validation probe described in the requirement above is the explicit
  exception, and it is present only where a view-based authorization strategy is configured.

### Latency invariants

Stated as ratios rather than absolute times, because they must hold on any provider and any
hardware.

- **Depth insensitivity.** The middle and last ranges of a cursor walk cost at most `1.20x` (p50)
  and `1.30x` (p95) the first range. This is the invariant that distinguishes cursor paging from
  the surface it supplements; the others are guards around it.
- **Cheap entry.** A first cursor page costs at most `1.20x` (p50) / `1.30x` (p95) an offset-0
  traditional page, so beginning a walk is not itself an expensive operation.
- **No traditional regression.** Shallow-offset traditional paging costs at most `1.20x` (p50) and
  `1.30x` (p95) its pre-change cost.
- **Partition count is free.** Requesting 200 partitions costs at most `1.25x` requesting 1 over
  the same candidate set, so the requested count cannot cause repeated scans of the candidate
  relation.

Deep-offset latency is expected to improve substantially, but that is a consequence rather than an
invariant: this design does not promise anything about the surface it is not replacing.

### Provider independence

PostgreSQL behavior does not establish SQL Server behavior. Parameterized `TOP` and large window
queries have provider-specific plan and memory-grant characteristics, so both providers satisfy
these invariants independently or the design is not satisfied.

### Access-path expectations

- Unfiltered cursor plans use primary-key range access.
- A `ContentVersion`-anchored first page over an upper-tail window seeks the change-version index —
  `IX_<Table>_ContentVersion` for a regular resource, `IX_Descriptor_ResourceKeyId_ContentVersion_DocumentId`
  for a descriptor — with no dead-run primary-key scan ahead of the first qualifying row.
- Filtered and authorized plans retain applicable existing indexes without repeated full candidate
  scans.
- A partition plan may scan and sort the candidate set once — that single linear pass is the
  intended cost of the endpoint.

New DDL or indexes are justified only by a demonstrated, repeatable deficiency against these
expectations, never by anticipation of one.

## Bounded Telemetry

Production telemetry records paging mode, requested and returned page size, requested and returned
partition count, duration, provider, command category, and success/failure, with bounded
dimensions.

Never record raw token text, decoded bounds, filter names or values, client identity, or candidate
identifiers. Decoded bounds are candidate identifiers by another name.

## Risks and Guardrails

1. **Duplicate-producing authorization joins.** An authorization strategy that duplicates a root id
   corrupts partition counts and boundaries — every downstream partition drifts. The
   one-row-per-`DocumentId` candidate invariant must hold for every supported strategy, and a
   violation is fixed at the join rather than masked with `DISTINCT`.
2. **Hydration batch ordering.** Adding the selected-id result set changes hydration batch result
   ordering. Both provider executors and all normalized plan contracts move together; a partial
   change misreads every subsequent result set.
3. **SQL Server plan behavior.** Parameterized `TOP` and large window queries can produce
   provider-specific plan or memory-grant behavior, so the two providers can satisfy the same
   contract with materially different execution characteristics.
4. **Partition query spill.** The intentionally linear partition query may sort or spill on large
   filtered or authorized sets. The one-command, one-scan shape is preserved rather than traded
   away for indexes that no observed deficiency justifies.
5. **OpenAPI defaults conflict.** The authoritative OpenAPI paging defaults and fixed `limit`
   maximum conflict with runtime behavior. Assembled documents must expose `MaximumPageSize`
   consistently as both default and maximum across resource and descriptor specifications.
6. **Profile association.** `/partitions` has no resource-schema response from which the current
   profile filter can infer ownership; explicit base-path association is mandatory, or partition
   paths will leak into or vanish from profile documents unpredictably.
7. **Edge conditions are not errors.** `pageSize=0`, inverted ranges, sparse identifiers, an empty
   candidate set, and `Int64.MaxValue` are valid conditions with defined behavior, not server
   errors.
8. **Custom-view validation is a second command.** Where a view-based authorization strategy is
   configured, a read issues the custom-view validation probe before its boundary or page command,
   on `/partitions` and GET-many alike. Collapsing the two into one provider command is deferred,
   not rejected: it would change shared authorization behavior for both endpoints, needs
   provider-specific multi-result-set composition on PostgreSQL and SQL Server, and must preserve
   the configured check ordering — a view validatable only after an earlier check has passed cannot
   be co-batched behind that check without letting a relation masquerading as the view answer the
   membership SQL. Until that work is scheduled, the boundary-selection qualification above is the
   contract, and neither endpoint may be changed alone, because cursor pages and partition
   boundaries must keep resolving authorization identically.
