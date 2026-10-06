# ADR: Derive `_etag` from `ContentVersion` instead of a content hash

**Status:** Accepted — implemented in the relational backend; the three design docs
(`update-tracking.md`, `transactions-and-concurrency.md`, `flattening-reconstitution.md`) have been
updated to match. \
**Date:** 2026-06-30 (accepted 2026-07-03; last amended 2026-09-30) \
**Deciders:** Development team (signed off 2026-07-08). \
**Author:** Stephen Fuqua, with analysis assistance from Claude Opus 4.8 and Claude Opus 5.5 (Claude Code).
The 2026-07-10 amendment was authored by Brad Banister, with implementation assistance from Codex.

This ADR describes architecture and client-visible behavior. Code-level details (classes, files, tests)
are deliberately omitted; the implementation is the source of truth for those.

## Amendment history

| Date | Change |
| --- | --- |
| 2026-07-04 | `profileCode` removed from the `If-Match` comparison, for legacy compatibility. |
| 2026-07-05 | Unquoted `If-Match` values accepted as equivalent to quoted ones, for legacy compatibility. |
| 2026-07-05 | Bare `If-Match: *` honored as an existence precondition (RFC 9110 §13.1.1). |
| 2026-07-06 | `If-None-Match` support added: conditional GET `304`, plus a write create-guard. |
| 2026-07-07 | Served descriptor `_etag` made profile-sensitive, matching other resources. |
| 2026-07-08 | Final `ContentVersion` read restored on the write path, after all table mutations. |
| 2026-07-08 | `profileCode` clarified as a hash of the profile *name*. |
| 2026-07-10 | Content coding added to the served etag. |
| 2026-09-30 | `If-None-Match` restricted to conditional GET; the write create-guard was withdrawn (DMS-1576). |

The sections below describe the current, consolidated design.

## Executive Summary

The `_etag` calculation originally required an extra database read on every write, which has a
noticeable impact on high volume operations. At the same time, the application already calculates a
`ContentVersion` (numeric) for every update. This number can be used as the state component of the
composed `_etag` instead.

The only additional benefit of the more complex hash-based approach is that it would guarantee etag
equality for the same payload when pushed to multiple servers, or in the case of a `DELETE` followed by
`POST` of the same body. That is not a requirement of an Ed-Fi API. Indeed, the legacy Ed-Fi ODS/API
likewise does not satisfy it. Consequently, this ADR bases the `_etag` on `ContentVersion`.

Additionally, this ADR brings the `_etag` in line with HTTP RFC 9110 by serving a different `_etag` for
each byte-different representation of a resource.

## Context

Before this ADR, the redesigned relational backend computed `_etag` as a SHA-256 hash of the canonical
resource-state JSON. A throughput review of the high-concurrency POST path (a client full-sync issuing
tens of thousands of POSTs) identified this per-write hash as the single most impactful bottleneck. In
the relational backend the hash cannot be computed from the request body, because the persisted document
can differ from the request (collection merges, identity cascades, normalization). The write path
therefore performed a full hydrate-materialize-hash **readback** of the just-written document, inside the
write transaction, solely to populate a response header whose body is otherwise empty.

Two facts make a change to the `_etag` contract feasible:

1. **The content-hash `_etag` has not shipped** (at the time of the initial ADR and code fix) in the
   redesigned backend, so changing it is not a breaking change for deployed clients.
2. **Content-addressability is not a requirement.** The deployment model does not need identical
   `_etag` values for identical content across rebuilds, migrations, or heterogeneous instances and
   engines.

> [!NOTE]
> Stephen has confirmed that this is *not* a requirement by inspecting actual legacy ODS/API behavior.
> Thus while the idea has merit, it is unnecessary.

## Decision drivers

- Reduce or eliminate the per-write readback cost on the high-concurrency write path, and the row-lock
  window it extends.
- Preserve correct optimistic-concurrency (`If-Match`) semantics.
- Adhere strictly to RFC 9110 strong-validator semantics for `ETag` / `If-Match`.
- Maintain cross-engine (PostgreSQL / SQL Server) determinism.
- Avoid high-risk refactors and contracts that are hard to maintain.
- Respect the backend redesign's stated priority of correctness over speed.

## Considered options

### Option 1 — Move the readback after `COMMIT`

Keep the hash; run the readback after the transaction commits.

- **Pros:** Lowest risk; preserves the hash; shrinks the row-lock window.
- **Cons:** Total work per write is unchanged. It also introduces a race: after client A commits and
  releases its row lock, client B can commit a newer change before A's readback runs, so A's response
  would carry B's `_etag` rather than the state A wrote.

### Option 2 — Compute the hash in-memory from merged write state

Hash in-process at persist time, eliminating the readback.

- **Pros:** Removes the readback while preserving the hash contract.
- **Cons:** Substantial, high-risk refactor. At persist time the system holds a flattened relational view,
  not a canonical JSON document, so it would need a new serialization layer proven byte-for-byte
  identical to the read path on both engines. The lossy flatten/merge step is exactly where divergence
  would hide.

### Option 3 — Persist a `ContentEtag` column

Compute the hash once at write, store it, and serve reads from the column.

- **Pros:** Retires the per-read rehash.
- **Cons:** Does not help the write hot path, since the hash must still be computed at write time. It
  cannot be maintained by a database trigger because the canonical SHA-256 is not reproducible across
  engines. Useful only as a read-path complement to Option 1 or 2.

### Option 4 — Derive `_etag` from `ContentVersion` (CHOSEN)

Serve `_etag` from the monotonic per-document `ContentVersion`, plus a token describing the served
representation, and abandon the content hash.

- **Pros:** Cheapest possible: no readback and no hash. Eliminates the bottleneck for reads and writes.
  Satisfies every *written* requirement for `_etag`.
- **Cons:** Loses content-addressability and cross-instance content identity (both confirmed out of
  scope). Couples `If-Match` to the change-version counter, which is moot because `ChangeVersion` (equal
  to `ContentVersion`) is already client-visible.
- **Variant 4b:** Serve an opaque hash of document id and `ContentVersion` instead of the raw integer.
  Not adopted (marginal benefit), but recorded as a low-cost future option.

### Rejected alternative — Use `_lastModifiedDate`

Viable on performance, but `ContentVersion` strictly dominates it:

- **Uniqueness:** two writes within one clock tick produce identical timestamps, risking an `If-Match`
  false match and a lost update. A monotonic counter never collides.
- **Cross-engine determinism:** database clocks vary in skew and resolution; a `bigint` counter does not.

## Decision

Adopt **Option 4**: derive `_etag` from `ContentVersion`.

`ContentVersion` satisfies every requirement the redesign documents state for `_etag`:

- **Resource-state-sensitive** — advances on any representation change, including identity cascades.
- **No-op-suppressed** — left unchanged when an inbound write changes nothing.
- **Stored, not response-derived** — read directly from the document row.
- **Profile- and `link`-insensitive at the stamp level** — the stamp is taken before projection and
  response decoration. (The *served* etag deliberately reintroduces representation sensitivity; see
  below.)
- **Cross-engine** — a deterministic `bigint`.

It does not meet content-addressability or cross-instance content identity, both out of scope. The
change removes the bottleneck at its root rather than relocating it (Option 1) or accepting a high-risk
refactor (Option 2).

One redesign requirement is **deliberately reversed**: the redesign specified a profile/link-insensitive
`_etag`, whereas the *served* etag is now representation-sensitive for RFC 9110 compliance. The
underlying stamp remains profile/link-insensitive.

## ETag format and HTTP validator semantics (RFC 9110)

RFC 9110 §8.8.1 distinguishes strong and weak validators. A strong validator changes whenever the
representation changes in any way. This design requires **strong** validators: `If-Match` mandates
strong comparison (§13.1.1), and weak (`W/`) etags never satisfy it, so weak etags would make every
`If-Match` fail. Etags are therefore served as quoted strong entity-tags with no `W/` prefix. Weakening
is not an available fallback.

A bare `ContentVersion` is a strong validator only if each resource state has one served byte
representation. It does not: served bytes vary by **profile**, **link mode**, **content coding**
(identity, Brotli, gzip), and potentially **format / media type** in the future. A bare counter would
give the same etag to byte-different representations.

**The etag is therefore `"{ContentVersion}-{variantKey}"`.** `variantKey` is a short, deterministic
token encoding every byte-affecting representation selector. Each representation gets a distinct etag
while the cost stays negligible: the counter is concatenated with a small key, and no document is hashed.

**Both parts are opaque strings.** Neither server nor clients interpret `ContentVersion` numerically. It
is serialized as a string, compared character-by-character, and documented to clients as opaque.

### `variantKey` encoding

`variantKey` is a dot-delimited, fixed-order, lowercase token of five components, using only `[a-z0-9_]`
and `.`:

```
variantKey = schemaEpoch "." format "." profileCode "." linkFlag "." contentCoding
etag-value = ContentVersion "-" variantKey
ETag       = DQUOTE etag-value DQUOTE               ; quotes are HTTP framing only
```

1. **`schemaEpoch`** — the first 8 hex characters of the in-force `EffectiveSchemaHash`. It captures every
   rendering input that is not document state, including the resource's field set and profile
   *definitions*. A schema or profile-definition change rotates it, correctly invalidating etags whose
   bytes are no longer reproducible.
2. **`format`** — a stable code for the response media type, from a fixed registry. Today `j` =
   `application/json`; further codes (e.g. `x` for XML) are reserved for future formats.
3. **`profileCode`** — `_` when no profile applies; otherwise the first 8 hex characters of
   `SHA-256(profileName)`. This hashes only the tiny, static profile name, never the representation, so
   it upholds the decision to stop hashing document bodies. (A compile-time profile index was
   considered, but the mapping model exposes no stable profile catalog to assign one.)
4. **`linkFlag`** — `l` when links are emitted, `n` otherwise.
5. **`contentCoding`** — `i` identity, `b` Brotli, `g` gzip, taken from the coding the response
   compression middleware selected. A new coding requires registering a new stable code.

All components are always present (`_` / `n` / `i` for "none" / "off" / "identity"), so the token has a
fixed shape.

| Representation | `_etag` body value | `ETag` header |
| --- | --- | --- |
| JSON, no profile, links on, identity | `5-a1b2c3d4.j._.l.i` | `"5-a1b2c3d4.j._.l.i"` |
| JSON, no profile, links on, gzip | `5-a1b2c3d4.j._.l.g` | `"5-a1b2c3d4.j._.l.g"` |
| JSON, profiled, links off, identity | `5-a1b2c3d4.j.9f1d2c3a.n.i` | `"5-a1b2c3d4.j.9f1d2c3a.n.i"` |

The `_etag` body field carries the unquoted value; the `ETag` header is quoted.

**How etags are produced.** Reads use the row's `ContentVersion`. Write responses use the final
`ContentVersion` from the persistence layer (see "Write-path `ContentVersion`") and carry the identity
variant, since they enclose no encoded representation. No document hydration or hashing occurs. When
response compression is enabled, ETag-bearing GET responses, including `304`, send
`Vary: Accept-Encoding`.

**Byte-changing corrections.** If a code correction or deployment changes representation bytes without
changing any `variantKey` selector, the corrected representation must not be served under the prior
strong etag. The deployment must first run the supported out-of-band representation restamp to advance
`ContentVersion` for every affected document. See
[Offline byte-changing representation correction](design/backend-redesign/design-docs/cdc/cdc-streaming.md#offline-byte-changing-representation-correction).

**Alternative (fixed-length opaque token).** `variantKey` could instead be a 12-hex-character SHA-256
prefix of the five components. This hashes only a tiny descriptor, so it keeps the performance goal, but
it trades operator readability for fixed width. The structured form is recommended.

## Conditional request behavior

The *served* etag is representation-complete, but the headers use it differently on reads and writes.
The origin server mints these tags and may compare them with knowledge of their structure; opacity
binds clients, not the server.

### `If-Match` (writes: PUT, POST-as-update, DELETE)

- **State-significant comparison.** `If-Match` compares only `ContentVersion` and `schemaEpoch`.
  `format`, `profileCode`, `linkFlag`, and `contentCoding` are representation selectors, not resource
  state, and are ignored. This keeps optimistic concurrency safe, because any persisted change advances
  `ContentVersion`, while avoiding false `412`s across representation variants of the same state.
- **Why `profileCode` is ignored (2026-07-04).** The legacy ODS/API accepts an etag from a
  profile-filtered read as `If-Match` for a full write. An asymmetric read-only profile forces exactly
  that pattern, so profile sensitivity would produce a `412` the client cannot avoid. It also added no
  lost-update protection. `schemaEpoch` stays significant because a schema or profile-definition change
  genuinely changes reproducible bytes.
- **Strong only.** Weak (`W/`) tags are rejected.
- **Quoted and unquoted accepted (2026-07-05).** The legacy ODS/API accepts both forms, so the DMS
  does too on input. This is safe because tags contain no whitespace, commas, or quotes. The DMS still
  emits quoted tags.
- **Wildcard (2026-07-05).** A bare, unquoted `If-Match: *` is an existence precondition per RFC 9110
  §13.1.1. It succeeds when the target exists and returns `412` when it does not, including for PUT and
  DELETE on a missing target, which otherwise return `404`. A POST that would insert returns `412`. A
  quoted `"*"` is an ordinary opaque tag that simply mismatches. A wildcard never guards against
  concurrent modification.
- **Divergence from the ODS/API (intentional).** The ODS/API compares `*` as a literal tag and always
  returns `412`. No working client can depend on that, so the DMS is only more permissive.

### `If-None-Match` (reads only)

`If-None-Match` is a **conditional-read validator for GET-by-id**. POST, PUT, and DELETE ignore it
entirely and behave as if it were absent, even when `If-Match` is also present. When a write carries the
header, the DMS logs at `Debug` that it was ignored, recording the method and trace id but not the value.

For GET-by-id:

- **Full served-tag comparison.** A client that cached a gzip, profiled, or links-off body must not
  receive `304` when it re-requests a different representation, because the bytes differ. All
  `variantKey` components are significant.
- **Weak comparison** (RFC 9110 §8.8.3.2). A `W/` prefix is accepted on input and stripped before
  comparison.
- **Lists.** A comma-separated list of tags is accepted; the precondition is false if any tag matches.
- **Wildcard.** A bare `*` means "no current representation exists," so it yields `304` when the
  resource exists. This is the inverse of `If-Match: *`.
- **Unquoted accepted.** The legacy ODS/API returns `304` only for an *unquoted* `If-None-Match`
  (defect ODS-6853), so clients that work against it send unquoted values. Accepting both forms
  preserves their `304` behavior after migration.
- **Result.** A match returns `304 Not Modified` with the current `ETag` header and no body. No match
  returns `200` with the full representation. An absent resource returns `404` as usual.
- **Independence from write preconditions.** The GET path reads and evaluates the header on its own. It
  does not share the write-precondition machinery.

| Operation | `If-None-Match: *` | `If-None-Match: "<tag>"` or a list |
| --- | --- | --- |
| GET-by-id, resource exists | `304` | `304` if any tag matches the full served tag; else `200` |
| GET-by-id, resource absent | `404` | `404` |
| POST, PUT, DELETE | ignored | ignored |

### Why the write create-guard was withdrawn (2026-09-30, DMS-1576)

The 2026-07-06 amendment also made `If-None-Match` a create-guard on POST and PUT (`412` if the resource
exists). Skyward Qmlativ sends `If-None-Match: *` on every POST, so it could no longer re-send existing
records. Testing against the legacy ODS/API showed that it never had a create-guard: POST upserts, PUT
returns `204`, and only GET honors the header. The earlier claim that the guard was a non-breaking
addition held for the DMS alone, not for clients migrating from the ODS/API.

The RFC does not require the guard. It defines `If-None-Match` against the request URI's target
resource, and for a POST upsert that is the collection, which exists. Ignoring the header on POST is a
reasonable reading, and it matches installed behavior. PUT ignores it too, for ODS/API compatibility.

No optimistic-concurrency protection is lost: the guard was an existence test, and `If-Match` still
provides lost-update protection. A POST that loses a create race now gets the ordinary identity-conflict
result, with or without the header. The change is listed under "Breaking changes" in the 8.1.0
changelog, since the guard shipped in 8.0.0 and a client relying on it now upserts silently.

### Out of scope: `If-Modified-Since`, `If-Unmodified-Since`, `If-Range`

- **`If-Modified-Since` / `If-Unmodified-Since`** are timestamp validators. The reasons for rejecting
  `_lastModifiedDate` as an etag basis (clock skew, sub-tick collisions, cross-engine nondeterminism)
  apply equally here. Clients wanting a conditional read should use `If-None-Match`.
- **`If-Range`** only matters with range requests, which the DMS does not support.

## Write-path `ContentVersion` (2026-07-08)

Option 4 originally assumed the write-response etag could use the `ContentVersion` returned by the root
insert at no marginal cost. That value is stale: child-table writes can fire stamp triggers that advance
the owning document's `ContentVersion` after the root insert. An etag composed from the insert-time value
would not match what a subsequent GET returns, breaking conditional-GET correctness.

The design therefore performs a **single lightweight `ContentVersion` read after every table mutation**,
owned by the persistence layer, which is the only layer that knows when all mutations are done. This is
persistence metadata, not response-materialization metadata. The guarded no-op path, where nothing is
written, reuses the version already established by the freshness check.

This does not return the throughput cost the ADR removed. The eliminated cost was the
hydrate-materialize-hash readback; the retained read is one scalar lookup. Placing it in the persistence
layer leaves a path to remove even that round trip later by capturing the final stamp directly from the
database. No content hashing is reintroduced.

## Descriptors (2026-07-07)

Descriptors are subject to readable-profile projection on GET, so their served etag follows the same
rule as every other resource: a profiled and an unprofiled read carry distinct strong etags. This
applies to descriptor write responses as well, so a profiled descriptor POST or PUT returns the same
`_etag` a profiled GET serves. Descriptor `If-Match` remains profile-insensitive, like all `If-Match`.

## Consequences

### Cross-backend scope

- **JSON-document backend:** `_etag` was computed from the request body before persistence. Because
  `ContentVersion` does not exist until the write, etag production must happen after the write, or the
  backend must return the counter. This must not regress the zero-readback property.
- **Relational backend:** the etag's state component comes directly from the persisted `ContentVersion`,
  composed with the active `variantKey`. The `If-Match` check becomes a `ContentVersion` comparison and
  can converge with the existing freshness check's row read.

### Design documentation

The redesign docs that specified the content hash were updated: `update-tracking.md`,
`transactions-and-concurrency.md`, and `flattening-reconstitution.md`. The updates reverse the
requirement that `_etag` be insensitive to profile filtering and link decoration, and specify the
`"{ContentVersion}-{variantKey}"` format and the opaque-string requirement.

### Client-visible behavior

- `_etag` values are compact, opaque, quoted strong entity-tags, not hashes. Acceptable because the
  contract has not shipped.
- The *served* etag varies by profile, format, link mode, and content coding.
- `If-Match` ignores those selectors, so representation-only differences never cause a spurious `412`.
- `If-None-Match` is honored on GET only, using the full served tag.

### What is given up

Content-addressability across rebuilds and migrations, and identical etags across instances and engines.
If either becomes a requirement, the fallback is to keep the hash and pursue Option 1 for writes plus
Option 3 for reads, or invest in Option 2.

## Open question

Confirm that an **identity-update cascade into referrers** (e.g. a `StudentUniqueId` change rippling into
every resource embedding that reference) advances the referrers' `ContentVersion`. The cascade-stamping
trigger observed in the DDL is descriptor-specific; the mechanism for general identity cascades must be
verified. This requirement applies equally to the content-hash approach. Any cascade path that fails to
advance `ContentVersion` is a defect to fix regardless of this decision, and it is a correctness
precondition for this ADR.

## Supporting findings (code review, 2026-06-30)

- **No-op detection does not depend on the etag hash.** Unchanged bodies are detected by row-by-row
  value comparison of the flattened write, not by hashing. A hash is not needed for no-op detection. The
  no-op success path used to pay the full readback to build the response etag, so even unchanged
  re-POSTs in a re-sync incurred the bottleneck; Option 4 eliminates that.
- **`ContentVersion` is always on and not configurable.** The database assigns it on every insert, and no
  application setting gates it. The etag therefore rests on a value guaranteed present on every write.

## References

- Design documents in this repository
- [RFC 9110: HTTP Semantics](https://www.rfc-editor.org/info/rfc9110/)
- [Ed-Fi-ODS Repository](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS)
- [Ed-Fi-ODS-Implementation Repository](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS-Implementation)
- Manual testing of etag behavior in ODS/API 7.3
- ODS-6853 — legacy ODS/API returns `304` only for an unquoted `If-None-Match` (quoted values do not match); basis for the DMS unquoted-acceptance requirement
