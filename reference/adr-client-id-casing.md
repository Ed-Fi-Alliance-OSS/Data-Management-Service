# ADR: Canonical `client_id` casing in tokens, case-insensitive client lookup

**Status:** Implemented in CMS (`src/config`). Does not apply to DMS (`src/dms`), which does not
issue tokens. Applies to the self-contained (OpenIddict) identity provider only; Keycloak mints
its own tokens and resolves its own clients. \
**Date:** 2026-09-21 \
**Author:** Stephen Fuqua.

## Executive summary

A single registered client could end up with tokens carrying two different `client_id` values that
differed only by letter case, because the claim was minted from the string the caller typed rather
than the value stored at registration. Anything comparing that claim exactly then treated one
client as two. The ownership check on `POST /connect/revoke` is exactly such a comparison, so the
holder of one token silently failed to revoke the other and still received `200 OK`.

Two changes fix it, and one deliberately does not change:

1. Tokens are minted from the **stored canonical** client id, so every token issued to a client
   carries one identity regardless of how the caller cased its credentials.
2. Client lookup is **case-insensitive on both database engines**, with an exact match always
   preferred.
3. The ownership comparison in `RevokeTokenAsync` stays **case-sensitive** (`StringComparison.Ordinal`).

## Context

`POST /connect/token` resolves the client with `GetApplicationByClientIdAsync(clientId)` and then
mints a JWT. Two properties of the pre-existing code combined badly:

- **Lookup casing differed by engine.** SQL Server's default collation is case-insensitive, so
  `WHERE a.ClientId = @ClientId` matched `acme-client` against a stored `Acme-Client`. PostgreSQL's
  default collation is case-sensitive, so the same request failed there. The same deployment
  configuration therefore behaved differently depending on the database engine.
- **The claim was minted from request input.** `GenerateJwtTokenAsync` was passed the
  request-supplied `clientId`, not `applicationInfo.ClientId`. On SQL Server, where the
  mismatched-case request authenticated successfully, the resulting token carried the caller's
  casing.

So on SQL Server one client could hold a token claiming `acme-client` and another claiming
`Acme-Client`. `RevokeTokenAsync` compares the target token's `client_id` to the caller's with
`StringComparison.Ordinal`; the two failed to match, nothing was revoked, and — because RFC 7009
requires a uniform `200 OK` — the caller received a success response. The failure was silent, and
visible only in a Debug-level log line.

That is worse than an error would be: it lands on the credential-containment path, where an
operator revoking a leaked token concludes from the `200 OK` that the credential is dead when it
is still live until it expires.

## Decision

### Mint from the stored canonical client id

`OpenIddictTokenManager.GetAccessTokenAsync` resolves `applicationInfo` and then passes
`applicationInfo.ClientId` — not the request string — into `GenerateJwtTokenAsync`.

That one argument feeds four things, and all four become canonical together:

| Consumer | Claim / column |
|---|---|
| `JwtTokenGenerator` | `sub` |
| `JwtTokenGenerator` | `client_id` |
| `JwtTokenGenerator` | `azp` |
| `StoreTokenAsync` | the stored token's subject column |

Fixing only the `client_id` claim would have left `sub` and `azp` carrying request casing, so the
fix is applied at the call site rather than inside the generator.

If a stored row somehow carries an empty `ClientId`, the request value is used instead. Minting an
empty subject would be worse than minting a non-canonical one.

### Case-insensitive lookup on both engines, exact match preferred

Per the Robustness Principle, a client should be able to authenticate whatever casing it sends, on
either engine. SQL Server already behaved this way; PostgreSQL's `GetApplicationByClientIdAsync`
now runs two steps:

1. An **exact** match (`a."ClientId" = @ClientId`), which can use the
   `UX_OpenIddictApplication_ClientId` unique index.
2. Only if that misses, a **case-insensitive** match (`LOWER(a."ClientId") = LOWER(@ClientId)`).

Preferring the exact match is not an optimization — it is what makes the change safe. The unique
constraint is case-sensitive, so an existing database may already hold `acme-client` and
`Acme-Client` as two distinct clients. Trying the exact row first guarantees every
currently-working credential keeps resolving to exactly the row it resolved to before, and confines
the new behaviour to requests that previously failed outright.

### Ambiguous case-insensitive matches fail authentication, loudly

When no exact row exists and the case-insensitive query matches more than one row, there is no
correct answer: choosing one would make authentication depend on row order. The lookup returns
`null`, which surfaces as an ordinary `invalid_client` failure rather than an exception — the
previous `QuerySingleOrDefaultAsync` would have thrown on multiple rows and turned a login into a
`500`. The ambiguity is logged at `Warning` with the client id sanitized through
`LoggingUtility.SanitizeForLog`, because only an operator can resolve it by renaming or removing
the duplicate registrations.

### The ownership comparison stays case-sensitive

`RevokeTokenAsync` continues to compare `client_id` values with `StringComparison.Ordinal`, and a
test pins that.

Relaxing it to `OrdinalIgnoreCase` would have been the smaller edit, and it was rejected. The
ownership comparison is a security boundary, and making it case-insensitive would tie that boundary
to the collation of whichever engine happens to be deployed: with two genuinely distinct
case-variant clients in a database, `OrdinalIgnoreCase` would let one revoke the other's tokens.
Canonical minting removes the need for a loose comparison — after this change both tokens carry the
same value, so an exact comparison matches — which means the strict comparison costs nothing and
keeps the boundary independent of storage configuration.

## Consequences

- Tokens minted after this change carry the canonical casing. Tokens minted **before** it still
  carry whatever casing was requested, so a pre-existing token whose casing is non-canonical remains
  unrevocable by a canonical caller until it expires. No migration is attempted; token lifetimes are
  short and the population drains on its own.
- The case-insensitive fallback cannot use the unique index, so it is a sequential scan of
  `OpenIddictApplication`. This is accepted: the table holds one row per registered client, and the
  query only runs after an exact match has already missed, which for a correctly-cased credential
  never happens.
- Behaviour converges across engines: PostgreSQL now accepts mixed-case credentials as SQL Server
  already did.

## Open question: a case-insensitive unique constraint

The durable fix for the underlying data problem is a case-insensitive uniqueness rule on
`OpenIddictApplication."ClientId"`, so that case-variant pairs cannot be created in the first place
and the ambiguity branch above becomes unreachable.

That migration is **deliberately not included here.** It would fail to apply on any existing
database that already contains a case-variant pair, converting a code change into a failed
deployment. Adopting it needs a decision about those databases first — detect and report, or
require manual reconciliation before upgrade. A non-unique `LOWER("ClientId")` expression index
would be safe to add at any time and would remove the sequential scan, but it does not prevent the
duplicates.

Until that is settled, ambiguous pairs are handled at runtime by refusing authentication and
logging, as described above.
