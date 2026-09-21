# ADR: Canonical `client_id` casing in minted tokens

**Status:** Implemented in CMS (`src/config`). Does not apply to DMS (`src/dms`), which does not
issue tokens. Applies to the self-contained (OpenIddict) identity provider only; Keycloak mints
its own tokens and resolves its own clients. \
**Date:** 2026-09-21 \
**Author:** Stephen Fuqua, with implementation assistance from Claude Code.

## Executive summary

A single registered client could end up holding tokens whose `client_id` claims differed only by
letter case, because the claim was minted from the string the caller typed rather than the value
stored at registration. Anything comparing that claim exactly then treated one client as two. The
ownership check on `POST /connect/revoke` is exactly such a comparison, so the holder of one token
silently failed to revoke the other and still received `200 OK`.

The decision is narrow:

1. Tokens are minted from the **stored canonical** client id, so every token issued to a client
   carries one identity regardless of how the caller cased its credentials.
2. Client lookup stays **case-sensitive**, per RFC 6749.
3. The ownership comparison in `RevokeTokenAsync` stays **case-sensitive**
   (`StringComparison.Ordinal`).

Point 1 alone closes the revocation defect. Points 2 and 3 record deliberate non-changes.

## Context

`POST /connect/token` resolves the client with `GetApplicationByClientIdAsync(clientId)` and then
mints a JWT. `GenerateJwtTokenAsync` was passed the request-supplied `clientId`, not
`applicationInfo.ClientId`, so wherever a mis-cased credential authenticated successfully the
resulting token carried the caller's casing rather than the registered casing.

Whether a mis-cased credential authenticates at all depends on the database engine. SQL Server's
default collation is case-insensitive, so `WHERE a.ClientId = @ClientId` matches `acme-client`
against a stored `Acme-Client`. PostgreSQL's default collation is case-sensitive and rejects it.

On SQL Server, therefore, one client could hold a token claiming `acme-client` and another
claiming `Acme-Client`. `RevokeTokenAsync` compares the target token's `client_id` to the
caller's with `StringComparison.Ordinal`; the two failed to match, nothing was revoked, and —
because RFC 7009 requires a uniform `200 OK` — the caller received a success response. The failure
was silent, visible only in a Debug-level log line.

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

**This change alone closes the revocation defect, on both database engines.** Even where an engine
lets a client authenticate with non-canonical casing, the token it receives now carries the
canonical `client_id`, so the ownership comparison matches the client's other tokens and revocation
succeeds. No change to lookup behaviour is required to fix it.

### Client lookup stays case-sensitive

An earlier revision of this work made the PostgreSQL lookup case-insensitive, on Robustness
Principle grounds, so that both engines would accept any casing. **That was reversed.** RFC 6749
states:

> Unless otherwise noted, all the protocol parameter names and values are case sensitive.

`client_id` is such a value, so rejecting a mis-cased id is the conformant behaviour, and
PostgreSQL's case-sensitive default collation already does the right thing. Accepting any casing
would have been a deliberate departure from the specification in order to be lenient, which is not
a trade this service needs to make: the revocation defect that motivated the leniency is already
closed by canonical minting.

(The quoted sentence is from RFC 6749; the section number was not verified when writing this ADR
and is deliberately omitted rather than guessed.)

Consequences of keeping it strict are small and appropriate: a client that sends the wrong casing
receives `invalid_client` and must correct its configuration.

### The ownership comparison stays case-sensitive

`RevokeTokenAsync` continues to compare `client_id` values with `StringComparison.Ordinal`, and a
test pins that.

Relaxing it to `OrdinalIgnoreCase` would have been the smaller edit, and it was rejected on two
grounds. First, it is a security boundary, and a case-insensitive comparison would tie that
boundary to the collation of whichever engine happens to be deployed: with two genuinely distinct
case-variant clients in a database, `OrdinalIgnoreCase` would let one revoke the other's tokens.
Second, it would contradict the same RFC 6749 case-sensitivity stance quoted above. Canonical
minting removes any need for a loose comparison — both tokens now carry the same value, so an
exact comparison matches.

## Known issue: MSSQL lookup diverges from PostgreSQL and from RFC 6749

SQL Server's default collation is case-insensitive, so `GetApplicationByClientIdAsync` there
resolves a client from a mis-cased `client_id` where PostgreSQL rejects it. The two engines
therefore behave differently for the same request, and the SQL Server behaviour departs from the
RFC 6749 case-sensitivity rule quoted above.

**This is deliberately out of scope for this change and will be addressed under a separate
ticket.** It is an RFC-conformance and cross-engine-consistency issue, **not** a revocation
hazard: canonical minting means a client that authenticates on SQL Server with non-canonical
casing still receives a token bearing the canonical `client_id`, so ownership-checked revocation
works correctly for it. Nothing about this divergence reopens the silent no-op.

## Consequences

- Tokens minted after this change carry the canonical casing. Tokens minted **before** it still
  carry whatever casing was requested, so a pre-existing token whose casing is non-canonical remains
  unrevocable by a canonical caller until it expires. No migration is attempted; token lifetimes are
  short and the population drains on its own.
- PostgreSQL lookup behaviour is unchanged by this work, and a test now pins its case-sensitivity
  so the property is deliberate rather than incidental.
