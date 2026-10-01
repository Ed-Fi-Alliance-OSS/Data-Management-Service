# Authorization in the Configuration Service

## Identity Providers

The CMS supports two identity provider modes, configured via `AppSettings:IdentityProvider`:

- **`keycloak`** — tokens are issued by an external Keycloak instance. The CMS validates incoming tokens against it.
- **`self-contained`** — the CMS acts as its own OAuth 2.0 server using OpenIddict. No external provider is required.

## Bootstrap / Client Registration

A `POST /connect/register` endpoint handles initial client registration. It is
gated by `IdentitySettings:AllowRegistration`:

- When `true`, any caller can register a new client (intended for first-time setup).
- When `false`, the endpoint returns `403 Forbidden`.

Registered clients are assigned the scope `edfi_admin_api/full_access` and the
role defined in `IdentitySettings:ConfigServiceRole`.

## Token Endpoints

The CMS exposes standard OAuth 2.0 endpoints:

| Endpoint | Purpose | Caller authentication |
|---|---|---|
| `POST /connect/token` | Issue an access token (client credentials grant only) | Anonymous (client credentials in the request) |
| `POST /connect/introspect` | Introspect a token (RFC 7662) | Anonymous |
| `POST /connect/revoke` | Revoke a token (RFC 7009) | Client credentials (HTTP Basic or form body), same as `/connect/token` [^revoke] |

[^revoke]: RFC 7009 §2.1 requires the revocation caller to authenticate the same
way it authenticates to the token endpoint (RFC 6749 §2.3) — client credentials,
not a bearer access token. This is checked, and enforced, only in `self-contained`
mode; revocation itself only happens there too. In `keycloak` mode `/connect/revoke`
is an unauthenticated no-op that returns `200 OK` and revokes nothing. See
[Which provider modes actually perform the check](#which-provider-modes-actually-perform-the-check).

In `self-contained` mode, `/connect/token` also accepts credentials via HTTP
Basic authentication in addition to the form body.

### Revocation Is Authenticated and Ownership-Checked

`POST /connect/revoke` requires the caller to authenticate with client
credentials — `client_id`/`client_secret`, via HTTP Basic auth or form fields,
exactly as `/connect/token` accepts them. This is not the ownership check below;
it just establishes who is asking. It is deliberately **not** a bearer access
token: the token named in the `token` field is the one being revoked, so it
cannot double as proof of the caller's identity without letting a client revoke
itself using the very credential that gets invalidated by the call, and RFC 7009
does not permit it regardless. `AuthenticateClientAsync` on
`ITokenRevocationManager` authenticates the pair by reusing the same
application lookup and secret-hash comparison `/connect/token` uses, so the two
call sites cannot drift on what counts as a valid secret. It returns the
client's **stored** `client_id` rather than a bare success flag; see
[Client id casing](#client-id-casing) for why that distinction is load-bearing.
Missing or invalid client credentials return `401 Unauthorized` in the RFC 6749
§5.2 **OAuth error format** — `application/json` with `error` and
`error_description` as top-level members — plus a `WWW-Authenticate: Basic`
challenge when the caller used the `Authorization` header:

```json
{ "error": "invalid_client", "error_description": "Invalid client or Invalid client credentials" }
```

This is the one place CMS does not answer in its usual
`application/problem+json` contract, and the departure is deliberate:
`/connect/revoke` is an OAuth endpoint, and an OAuth client reads `error` off the
root of the body. Flattening the code into a sentence inside a problem-details
`errors` array would put it somewhere no conforming client looks.

The failure is reported rather than masked as `200 OK` because RFC 7009's
"always 200" guarantee covers whether a *token* is valid or owned, not whether
the *caller* authenticated.

Once authenticated, a caller may only revoke a token that belongs to it. The
token named in the `token` form field is first verified for signature, issuer,
audience and lifetime, and its `client_id` claim is then compared to the
canonical `client_id` that authentication resolved for the caller. Verification
comes first on purpose: trusting an unverified `client_id` would let a caller
forge a token naming itself while embedding another client's `jti`.

Two distinct credentials are in play on every revocation request, which is easy
to conflate. The **caller's** `client_id`/`client_secret` (Basic auth or form
fields) establishes *who is asking*; the **target** token is the one in the
`token` form field and is *what gets revoked*. A client typically revokes its
own currently-held access token, but the target can be any token that client
was issued:

```http
POST /connect/revoke HTTP/1.1
# Authorization identifies the caller by client credentials, not the token being revoked.
Authorization: Basic base64(client_id:client_secret)
Content-Type: application/x-www-form-urlencoded

# The token form field is what gets revoked; its client_id is the one compared.
token=eyJ...TARGET
```

A client revoking one of its *other* outstanding tokens (say, rotating a leaked
credential while keeping its current session alive) authenticates with its
client credentials and names a different target token minted for the same
`client_id`, and the revocation succeeds. A client supplying a target token
minted for a different `client_id` gets `200 OK` and no revocation.

When the caller has authenticated but the target token cannot be verified,
carries no `client_id`, or belongs to a different client, nothing is revoked and
the response is still `200 OK`. Per RFC 7009 this is indistinguishable from
revoking an unknown token, so the response reveals nothing about whether the
token exists or who owns it. Only a missing `token` form field returns
`400 Bad Request`. This request-shape check runs before client authentication
and before the provider-mode branch, so it applies even to an unauthenticated
caller or in `keycloak` mode: a caller with no credentials and no `token` field
gets `400`, not `401`.

Because verification includes the lifetime check, a target token already past its
`exp` (plus the validator's clock-skew allowance) is also a no-op. This is a
deliberate narrowing: revocation previously parsed the target token without
verifying it and would revoke an expired token by `jti`. An expired token is
already rejected everywhere else, so there is nothing left to revoke. A practical
consequence for anyone reading the `dmscs.OpenIddictToken` table or an admin status
view directly: an expired token keeps its stored status (typically `valid`) rather
than being flipped to `revoked` by a revocation attempt, so "not `revoked`" in the
table does not imply "still usable".

### Confirming that a revocation actually took effect

Every outcome of `POST /connect/revoke` is an identical bodyless `200 OK` — success,
wrong owner, unverifiable token, expired token, and (in `keycloak` mode) not
attempted at all. That is required by RFC 7009 and is deliberate, but it means the
`200` alone is **not** evidence that anything was revoked. Anyone who must be certain
— containing a leaked credential, for example — has to confirm out of band:

```http
POST /connect/introspect
Content-Type: application/x-www-form-urlencoded

token=eyJ...TARGET
```

A revoked token reports `{"active": false}`. If it still reports `{"active": true}`,
the revocation did not take effect and the credential is still live. Treat
containment as incomplete until introspection confirms it.

### Client id casing

Both sides of the ownership comparison are derived from the **stored canonical**
`client_id`, never from the casing a caller happened to type:

- Tokens are minted with the stored value, not the casing supplied at
  `/connect/token`, so every token issued to one registered client carries one
  identity whatever casing that client used on a given call.
- The caller's id is the stored value that `AuthenticateClientAsync` resolved, not the
  one it sent in its credentials.

Either half alone leaves the defect open. Where an engine authenticates a mis-cased
`client_id` — SQL Server's default collation does — passing the caller's own spelling
into the case-sensitive comparison would fail it against the client's own canonically
minted token: a silent `200 OK` no-op on a legitimate revocation.

This was previously a live defect: a token minted under one casing could not be
revoked by a caller authenticated under another, and the mismatch surfaced as the
silent `200 OK` no-op described above. It is closed on **both** database engines. See
[ADR: Canonical `client_id` casing in minted tokens](../../adr-client-id-casing.md)
for the decision record, including why the ownership comparison itself deliberately
remains case-sensitive.

Client **lookup** is a separate matter and is unchanged: it is case-sensitive on
PostgreSQL, which matches RFC 6749's rule that protocol parameter values are case
sensitive. SQL Server's default collation makes its lookup case-insensitive, so the
two engines currently differ and SQL Server departs from the RFC; that is a known
issue tracked separately and out of scope here. It is a conformance and consistency
concern only — because both the minted claim and the authenticated caller's id are
canonical, a client that authenticates on SQL Server with non-canonical casing still
receives a canonical token and can revoke normally.

One residue remains: tokens minted **before** this change still carry the requested
casing, so such a token may resist revocation by a canonically-cased caller until it
expires. Token lifetimes are short and the population drains on its own.

### Which provider modes actually perform the check

The `client_id` claim name is identical for self-contained and Keycloak-issued
tokens, so the comparison itself needs no per-provider branching. It does not
follow that both modes perform ownership-checked revocation:

- **`self-contained`** — the OpenIddict store registers an `ITokenRevocationManager`,
  so the endpoint authenticates the caller's client credentials, verifies the
  target token, compares `client_id`, and revokes by `jti` on a match.
- **`keycloak`** — no `ITokenRevocationManager` is registered (`KeycloakTokenManager`
  implements `ITokenManager` only), so there is no local way to authenticate the
  caller's client credentials, and the handler's revocation branch is never
  entered. The request returns `200 OK` and **nothing is revoked or checked** —
  not client authentication, not ownership comparison. Token revocation remains
  the IdP's responsibility in this mode. This is safe precisely because the mode
  is a no-op end to end: there is no data to protect behind the missing
  authentication check, since nothing is read, mutated, or revealed either way.

## Signing keys in self-contained mode

This section applies to `self-contained` mode only. In `keycloak` mode none of these
components is registered, and token validation is unchanged. The settings named here
are described, with their defaults and accepted ranges, in
[Signing-key settings](../../../docs/CONFIGURATION.md#signing-key-settings-configuration-service-self-contained-only).
The design and its test evidence are in
[DMS-1556](./DMS-1556-cms-signing-key-resolution-under-load.md).

### The key snapshot

Each Configuration Service instance holds the public keys it validates tokens with in one
in-memory **snapshot**. The snapshot comes from one of two sources:

- **Database keys** (the default): every row of `dmscs.OpenIddictKey` with
  `IsActive = true`. Token issuance signs with the newest active row.
- **Certificates** (`IdentitySettings:UseCertificates`): the one key of the configured or
  development certificate, with the certificate thumbprint as the key id.

The default `Bearer` scheme, `DmsJwtBearer`, the JWKS endpoint, introspection and
revocation all use the same snapshot. Validating a token does not read the key table.
The table is read only by a load, and a load starts in one of these ways:

- the startup load;
- a scheduled reload;
- a request that finds the snapshot missing, expired, or overdue;
- a token whose key id the snapshot does not hold.

The per-token status check (revoked or not) is a separate database read, made on every
request and never cached. See [Cached keys do not keep the service available through a
database outage](#cached-keys-do-not-keep-the-service-available-through-a-database-outage).

### Refresh, backoff, cooldown and staleness

- **One load at a time.** An instance runs at most one load. Requests that need keys while
  a load is running wait for that load rather than starting their own. A load that runs
  longer than `SigningKeyLoadTimeoutSeconds` (default 10 s) is canceled and counted as
  failed. If the database call itself does not end at that deadline, no new load starts
  until it does, and its late result is discarded.
- **Scheduled reload.** After a successful load the next reload is due after
  `SigningKeyRefreshIntervalSeconds` ±10 % (default 300 s, so 270–330 s). The ±10 % is drawn
  once per snapshot.
- **Fresh and overdue.** A snapshot is *fresh* until the refresh interval has passed and
  *overdue* after that. An overdue snapshot is still used, and the first request that finds
  it overdue starts a reload in the background. Whichever comes first, the scheduled reload
  or such a request, does the load.
- **A failed load never replaces the snapshot.** The previous keys stay in use, and no
  failure is ever published as an empty key set. The next load is allowed only after a
  backoff of 5, 10, 20, 40 and then 60 s, each ±20 %, after consecutive failures (at most
  72 s). During a backoff no request starts a load. The background service wakes at the
  end of the backoff, so an instance retries without any traffic.
- **Recovery.** These bounds are conditional (design §4.4, *Bounds*). When the key store
  recovers at *R* and no load is in flight, the next load starts within the remaining
  backoff, by *R* + 72 s, and completes within `SigningKeyLoadTimeoutSeconds` after that:
  *R* + 82 s at the default settings. A load already in flight at *R* started against the
  failing store and may still fail; its backoff then counts from its own failure, so add
  one load timeout (*R* + 92 s). A database call that outlives its deadline delays the
  next load until it actually ends, so neither bound holds while such a call is
  outstanding.
- **Maximum staleness.** A snapshot can be used for `SigningKeyMaxStalenessSeconds` after
  its last successful load (default 3600 s). After that it has *expired*, and it is not
  used again until a load succeeds.
- **Unknown key id.** A token whose `kid` is not in the snapshot triggers one reload. The
  reload happens only when no backoff is running and the last completed load, of any kind,
  ended at least `SigningKeyUnknownKeyRefreshCooldownSeconds` ago (default 30 s). The
  cooldown applies to the whole instance, so tokens carrying made-up key ids cause at most
  one load per cooldown. If the key is still absent after the reload, or the reload was
  not allowed, the token is rejected with 401.
- **Empty key set.** A store that holds no active key is a *successful* load of zero keys.
  It is published with a Warning
  (`Signing-key snapshot {n} is empty: … so every token will be rejected`). JWKS then
  answers `200 {"keys":[]}`, and every token is rejected with 401. A database that has no
  key when the service starts behaves this way until a key is added. After that, the first
  token signed with the new key triggers the unknown-key reload, subject to the cooldown,
  or the next scheduled reload picks the key up.

### Dependency 503s, ordinary 401s, and an empty JWKS

Each response says whether the token was judged, or whether the service could not reach a
decision:

| Situation | Protected endpoints | `GET /.well-known/jwks.json` | Introspection | Revocation |
| --- | --- | --- | --- | --- |
| Usable snapshot, valid token, token status valid | Request proceeds | `200` with keys | `{"active": true}` | Revokes; `200` |
| Token rejected: bad signature, wrong issuer or audience, expired, missing or unknown `kid`, revoked, malformed | **401**, `WWW-Authenticate: Bearer`, no `Retry-After` | unaffected | `{"active": false}` | No-op `200` |
| No usable snapshot (never loaded, or past maximum staleness) | **503**, category `SigningKeyStore` | **503** | `{"active": false}` and an Error log | No-op `200` and an Error log |
| Token-status read fails (usable snapshot) | **503**, category `TokenStatusStore` | unaffected | `{"active": false}` and an Error log | not affected (revocation does not read the status) |
| Store holds no active key (successful empty load) | **401** for every token | `200 {"keys":[]}` | `{"active": false}` | No-op `200` |

- **A dependency 503** carries `Retry-After: 30`, has no `WWW-Authenticate`, and has a
  generic `application/problem+json` body (`title` `Service Unavailable`, the request's
  `correlationId`). The body never names the failing store. The category appears only in
  the Error log line:
  `Authentication could not reach a decision: the {SigningKeyStore|TokenStatusStore} is unavailable (trace …)`,
  and for JWKS `The JWKS could not be served: the SigningKeyStore is unavailable (trace …)`.
  Clients should treat it as transient and retry after the indicated delay, not discard
  their token.
- **An ordinary 401** means the token was judged against the keys the instance holds, and
  rejected. While the snapshot is usable, a key-store failure does not turn a request into
  a 503. A token whose `kid` is unknown, and whose reload fails or is not allowed, is
  therefore judged against the usable keys and gets 401. The same token gets 503 when no
  usable snapshot exists.
- **An empty JWKS (`200 {"keys":[]}`)** is an authoritative answer: the store has no active
  key. Before DMS-1556, JWKS also answered `200 []` when the key read *failed*, and
  protected requests answered 401 when key or status reads failed. Both are now the 503
  above. This is a deliberate contract change. A JWKS consumer now sees a failed fetch
  instead of an empty key set it might adopt.
- **Introspection and revocation are unchanged on the wire.** They keep their protocol
  answers (`{"active": false}`; RFC 7009's always-`200`) when a dependency fails, and log
  an Error naming the category.
- Failures after authentication, in the endpoint's own data access, are not part of this
  classification and keep their existing responses (for example 500).

### Cached keys do not keep the service available through a database outage

The snapshot makes authentication independent of the **key table**, not of the
**database**. The token-status check is deliberately uncached, so a revocation takes
effect on the next request. That check reads the database for every token that passes
validation. When the whole database is unavailable, no token is accepted, and where a
request stops depends on the snapshot:

- **No usable snapshot:** 503 `SigningKeyStore`, before the token is judged.
- **Usable snapshot, token rejected on its own merits** (bad signature, expired, unknown
  `kid`, and so on): 401, before any status read.
- **Usable snapshot, token passes validation:** the status read fails, giving 503
  `TokenStatusStore`.

Maximum staleness keeps valid tokens accepted only when key reads fail while token-status
reads still work, for example while the key table is locked.

### Rotating and retiring a database signing key

This is how the system behaves, not an extra mechanism. Write `T_prop` for the time a
healthy instance takes to use a key-table change, with no traffic:

`T_prop` = load timeout + refresh interval + 10 % + load timeout = 10 + 300 + 30 + 10 =
**350 s** at the defaults.

A key-table change committed at time *t* is in use on every healthy instance by
*t* + `T_prop`. "Healthy" means every load succeeds within its deadline.

The first load-timeout term is the load that may already be running at *t*. That load can
read the rows from before the change and publish them after it, as late as *t* + 10 s. The
next refresh interval, and the cooldown, count from that publication. For example, a load
that publishes the old rows at *t* + 9 s, followed by a scheduled load that starts 330 s
later and takes 9 s, puts the change in use at *t* + 348 s. Any load that starts after *t*
sees the change.

1. **Insert the new key** as an active row. On PostgreSQL,
   [`Generate-OpenIddictKey-Insert.ps1`](../../../eng/docker-compose/Generate-OpenIddictKey-Insert.ps1)
   prints a suitable `INSERT`. **Signing switches at the next token issued, on every
   instance,** because issuance reads the newest active row directly. Validation does not
   switch at that moment.
2. **Expect a short new-key acceptance delay.** An instance accepts the new key's tokens as
   soon as its snapshot holds the key. The first new-key token it sees triggers the
   unknown-key reload when the cooldown and backoff allow. Otherwise that token is
   rejected with **401**, and the key arrives at the next allowed unknown-key reload or by
   `T_prop`, whichever comes first. A request may also join a load that is already
   running, but a load that started before the insert returns without the key.
   - **Healthy key store, with traffic.** The last load that read the old rows completes
     within one load timeout of the insert. Its cooldown ends one cooldown later. The
     first new-key request after that starts a reload that completes within one load
     timeout. New-key tokens are therefore accepted from at most load timeout + cooldown +
     load timeout after the insert: **50 s** at the defaults. This assumes a new-key
     request reaches the instance once the cooldown has ended. Without one, `T_prop`
     applies.
   - **Key store failing.** The key arrives with the first successful load after the store
     recovers, within the conditional recovery bounds above (*R* + 82 s at the defaults
     with no load in flight at recovery, *R* + 92 s with one).

   Instances are independent: one may accept a new-key token while another still answers
   401 until its cooldown ends or its next reload. **If you need zero such 401s, insert the
   key at a quiet time and wait `T_prop` before new-key tokens reach validators**, or
   restart each instance. A restarted instance loads the table at startup.
3. **Keep the old key active** for at least `TokenExpirationMinutes` plus the 5-minute
   validation clock skew after the insert (35 min at the defaults). Tokens minted before
   the switch then stay valid until they expire.
4. **Retire the old key** by setting `IsActive = false`. Its tokens are rejected within
   `T_prop` on healthy instances. If an instance cannot read the key store, it keeps
   trusting the retired key until its snapshot expires: at most
   `SigningKeyMaxStalenessSeconds` (3600 s) after its last successful load. That is the
   configurable trade-off between availability and how long a retired key can be accepted.
   To force an immediate reload, for example after a key compromise, restart each instance
   once the row is updated. A token you know about can also be revoked at once through
   `POST /connect/revoke`, because the status check is not cached.

In **certificate mode** the snapshot holds the one certificate key, so there is no overlap
window. Issuance reads the certificate file at every token, so replacing the file switches
signing at once. Validation switches at the instance's next load. Until then, new-certificate
tokens face the acceptance delay of step 2. After the load, tokens signed with the replaced
certificate are rejected. Replace the certificate at a quiet time, or restart each instance
after replacing it.

### Log signals

| Event | Level | Message starts with |
| --- | --- | --- |
| Snapshot published | Information | `Signing-key snapshot {n} published from {Database\|Certificate} ({Trigger}): {k} keys, …` |
| Empty snapshot published | Warning | `Signing-key snapshot {n} is empty: …` |
| Load failed | Error | `Signing-key load failed ({Trigger}): category SigningKeyStore, kind {Retrieval\|Processing}, consecutive failures {n}, next attempt in {s} s. …` |
| Load finished after its deadline | Warning | `A signing-key load that outlived its deadline has finished …; its result was discarded` |
| Unknown key id | Warning | `Bearer token key id {kid} was not in the signing-key snapshot; unknown-key refresh outcome: {Outcome}` |
| Dependency 503 | Error | `Authentication could not reach a decision: the {Category} is unavailable (trace …)` |
| JWKS 503 | Error | `The JWKS could not be served: the SigningKeyStore is unavailable (trace …)` |

A snapshot publication labelled `Request` is a normal background reload started by a
request that found the snapshot overdue. It is not a fault. The message
`Failed to fetch public keys for JWKS`, logged before DMS-1556, is no longer emitted.

### Connection capacity

The snapshot takes key reads off the request path, but each authenticated request still
opens a database connection for its status check, and most endpoints read their own data.
Under heavy concurrency the database's connection limit can still be reached. See the
connection-capacity note in
[Signing-key settings](../../../docs/CONFIGURATION.md#signing-key-settings-configuration-service-self-contained-only).

## Scopes

Authorization is scope-based. Three scopes are defined:

| Scope | Description |
|---|---|
| `edfi_admin_api/full_access` | Full access to all CMS API endpoints |
| `edfi_admin_api/readonly_access` | Read-only access to all CMS API endpoints |
| `edfi_admin_api/authMetadata_readonly_access` | Read access to the `MapLimitedAccess` endpoints below: every Configuration Service read a single-tenant DMS makes. A multi-tenant DMS also reads `GET /v3/tenants/`, which is `MapSecuredGet`, so it needs `readonly_access` |

## Endpoint Authorization Model

Scope requirements are applied per HTTP method via extension helpers:

| Helper | Allowed scopes | Used for |
|---|---|---|
| `MapSecuredGet` | `full_access` or `readonly_access` | All other GET endpoints |
| `MapSecuredPost` | `full_access` only | All POST endpoints |
| `MapSecuredPut` | `full_access` only | All PUT endpoints |
| `MapSecuredDelete` | `full_access` only | All DELETE endpoints |
| `MapLimitedAccess` | Any of the three scopes | Exactly these GET routes: `/v3/authorizationMetadata`, `/v3/claimSets/`, `/v3/apiClients/`, `/v3/apiClients/{id}`, `/v3/dataStores/`, `/v3/dataStores/{id}`, `/v3/dataStoreContexts/`, `/v3/dataStoreContexts/{id}`, `/v3/dataStoreDerivatives/`, `/v3/dataStoreDerivatives/{id}`, `/v3/applications/{id}`, `/v3/profiles/`, `/v3/profiles/{id}` |
| `MapPublic` | Anonymous | Health, JWKS, discovery endpoints |

## Roles

Two roles are configurable via `IdentitySettings`:

- `ConfigServiceRole` — assigned to clients that manage the CMS (admin tooling).
- `ClientRole` — assigned to DMS clients that consume the CMS (e.g., to retrieve authorization metadata).
