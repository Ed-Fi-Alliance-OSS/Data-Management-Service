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
revocation all use the same snapshot. In steady state, validating a token does not read
the key table. The table is read only by a load, and a load starts in one of these ways:

- the startup load;
- a scheduled reload;
- a request that finds the snapshot missing, expired, or overdue;
- a token whose key id the snapshot does not hold.

The last two run on a request's behalf:

- **Missing or expired snapshot.** The request waits for the load, bounded by the load
  timeout. It is refused at once during a retry backoff, and while a load that outlived
  its deadline is still finishing.
- **Unknown key id.** The request also waits when its reload is allowed, or when it joins
  a load already running.
- **Overdue snapshot.** The request is served from the snapshot and does not wait.

The per-token status check (revoked or not) is a separate database read, never cached. Two
paths make it, and both make it only after the token's verification succeeds and its
`jti` claim is present and parses as a GUID:

- **Bearer authentication** (`Bearer`, `DmsJwtBearer`) checks the token from the
  `Authorization` header. On this path, a request without a bearer token reads no status.
  Neither does a request whose token fails verification or has no usable `jti`.
- **Introspection** checks the token supplied in the request form, whatever the request's
  own `Authorization` header.

Revocation verifies the token but does not read its status. See [Cached keys do not keep the service available through a
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

  The cooldown protects the key store's database capacity: without it, requests with
  made-up key ids could drive a store read each.

  **Bootstrap exception, once per instance.** When the instance's first successful load
  finds no key, one unknown-key reload may start inside the cooldown while the snapshot is
  still empty. This covers a fresh store whose setup inserts the first key after CMS
  starts: the first request carrying that key is accepted without waiting out the
  cooldown. The allowance is spent when that reload starts, even if it finds no key or
  fails (a failure backs off as usual). No later load restores it, and an instance whose
  first load found a key never gets it. The allowance helps this instance accept the
  first key's tokens. It does nothing for DMS or other consumers; see
  [Adding the first key](#adding-the-first-key).

  After the allowance is spent, the normal cooldown applies, including while the store
  stays empty. A key inserted after an earlier unknown-key reload has spent the allowance
  is accepted only after the cooldown, as on any store.
- **Empty key set.** A store that holds no active key is a *successful* load of zero keys.
  It is published with a Warning
  (`Signing-key snapshot {n} is empty: … so every token will be rejected`). JWKS then
  answers `200 {"keys":[]}`, and every token is rejected with 401, until a later load of
  that instance finds a key.
  - **A database with no key when the service starts** behaves this way until a key is
    added. After that, the first token signed with the new key triggers the unknown-key
    reload, subject to the cooldown, or the next scheduled reload picks the key up.
  - **JWKS lags the insert.** Issuance uses the new key at once, so JWKS can still answer
    `200 {"keys":[]}` after the insert until the instance's next successful load. A
    consumer that fetches JWKS in that window caches the empty set. See
    [Adding the first key](#adding-the-first-key).

### Dependency 503s, ordinary 401s, and an empty JWKS

Each response says whether the token was judged, or whether the service could not reach a
decision:

| Situation | Protected endpoints | `GET /.well-known/jwks.json` | Introspection | Revocation |
| --- | --- | --- | --- | --- |
| Usable snapshot, valid token, token status valid | Request proceeds | `200` with keys | `{"active": true}` | Revokes; `200` |
| Token rejected: bad signature, wrong issuer or audience, expired, missing or unknown `kid`, revoked, malformed | **401**, `WWW-Authenticate: Bearer`, no `Retry-After` | unaffected | `{"active": false}` | No-op `200` |
| No usable snapshot (never loaded, or past maximum staleness) | **503**, category `SigningKeyStore` | **503** | `{"active": false}` and an Error log | No-op `200` and an Error log |
| Token-status read fails (usable snapshot) | **503**, category `TokenStatusStore` | unaffected | `{"active": false}` and an Error log | not affected (revocation does not read the status) |
| The instance's last successful load found no active key (empty snapshot) | **401** for every token | `200 {"keys":[]}` | `{"active": false}` | No-op `200` |

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
- **An empty JWKS (`200 {"keys":[]}`)** reports what that instance's **last successful
  load** found: no active key. It is not a live read of the table, so a key inserted
  since that load is not yet listed. It is never the result of a failed load. A failed
  load never replaces the snapshot, so JWKS answers 503 when no usable snapshot exists,
  and otherwise serves the last keys it loaded.
  - **Before DMS-1556,** JWKS also answered `200 []` when the key read *failed*, and
    protected requests answered 401 when key or status reads failed. Both are now the 503
    above. This is a deliberate contract change.
  - **For a JWKS consumer,** a failed fetch is now visible as a failure, not as an empty
    key set it might adopt.
  - **Fresh keys still need verifying.** A successful `200` still shows only that
    instance's snapshot. Before relying on a key change, verify it on every instance; see
    [Verifying a key change on every instance](#verifying-a-key-change-on-every-instance).
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

This is how the system behaves, not an extra mechanism. A key change passes through **two
independent caching layers**, and each one must pick it up:

- **Each Configuration Service instance's snapshot.** It is used for validation and JWKS
  on that instance only.
- **Each consumer's cached key set.** DMS, and any other service that validates
  Configuration Service tokens from JWKS, keep their own.

The Configuration Service's own timing bounds say nothing about how long a consumer keeps
an old or empty key set.

#### What each layer does

- **Issuance.** Token issuance reads the newest active row directly. An inserted key
  therefore signs the next token issued, on every instance, at once.
- **Configuration Service validation and JWKS.** These use the instance's snapshot. Write
  `T_prop` for the time a healthy instance takes to use a key-table change, with no
  traffic:

  `T_prop` = load timeout + refresh interval + 10 % + load timeout = 10 + 300 + 30 + 10 =
  **350 s** at the defaults.

  A key-table change committed at time *t* is expected to be in use on a healthy instance
  by *t* + `T_prop`. "Healthy" means that every load succeeds within its deadline and the
  background reload runs as designed. This is an expectation under those conditions, not
  a guarantee to sleep on. The verification below is what tells you the change arrived.

  The first load-timeout term is the load that may already be running at *t*. That load
  can read the rows from before the change and publish them after it, as late as
  *t* + 10 s. The next refresh interval, and the cooldown, count from that publication. For
  example, a load that publishes the old rows at *t* + 9 s, followed by a scheduled load
  that starts 330 s later and takes 9 s, puts the change in use at *t* + 348 s. Any load
  that starts after *t* sees the change. An instance whose key store is failing picks up
  the change only with its first successful load after the store recovers.
- **DMS.**
  - **When it fetches.** DMS fetches the discovery document and JWKS when it starts. It
    logs `OIDC metadata cache warmed up successfully. Issuer: …, SigningKeys: {n}`.
    Afterwards it re-fetches on its automatic refresh schedule,
    `JwtAuthentication:AutomaticRefreshIntervalHours` (default 24). It requests an early
    re-fetch only when the discovered issuer does not match its configured authority.
  - **Unknown keys.** A token signed with a key DMS has not cached is rejected with 401.
    It does not trigger a re-fetch.
  - **Failed fetches.** A re-fetch that fails keeps the cached set: IdentityModel logs
    `IDX20806 … Returning the current configuration`. The refresh schedule is therefore
    not a bound on how long DMS trusts a retired key. A retired key leaves a DMS instance
    only after a **successful** re-fetch, or a restart that **successfully** loads the
    corrected set.
  - **At startup.** A DMS instance that cannot fetch JWKS at startup, for example because
    the Configuration Service answers 503, does not start.
- **Revocation.** `POST /connect/revoke` takes effect on the Configuration Service's own
  endpoints at the next request, because their status check is not cached. DMS validates
  tokens itself and never consults that status. A revoked token is accepted by DMS until
  it expires.

#### Verifying a key change on every instance

Before you restart or otherwise refresh any consumer, confirm the change on **every
Configuration Service instance that serves traffic**, addressing each one individually.
Use the container or pod address, or a port-forward per instance. Repeated requests
through a load balancer do not show that every instance has the change.

```powershell
foreach ($instance in 'http://cms-0:8081', 'http://cms-1:8081') {   # each instance, with its path base
    (Invoke-RestMethod "$instance/.well-known/jwks.json").keys.kid
}
```

- **The key id to look for** is the row's `KeyId`. `Generate-OpenIddictKey-Insert.ps1`
  sets it with `-KeyId` (default `key-<random>`) and prints it in the `INSERT`. The active
  ids are also returned by `SELECT "KeyId" FROM dmscs."OpenIddictKey" WHERE "IsActive"`.
  In certificate mode it is the certificate thumbprint.
- **After an insert,** every instance must list the new key id. **After a retirement,** no
  instance may list the retired one.
- **`503`:** that instance has no usable snapshot. Stop until it serves `200`.
- **A connection failure:** the instance is unavailable. Stop until it serves and passes
  the check, or until it is deliberately out of service and receives no traffic.
- **`200 {"keys":[]}`:** reports what that instance's last successful load found. It is
  not a live read of the table.
- **Shortening the wait.** Restarting an instance makes it load the table at startup. That
  can shorten the wait, but it does not replace the check, because the startup load can
  fail.

#### Refreshing DMS and other consumers

Waiting for the Configuration Service does not clear a consumer's cache.

1. Once the verification above passes, restart each DMS instance. A rolling restart is
   fine.
2. On **each** instance, confirm the outcome with a token whose header `kid` is the key in
   question. The `kid` is in the token's first segment (base64url-encoded JSON). The
   `SigningKeys: {n}` count in the startup log line is a diagnostic only. It does not
   identify the keys.
3. A restart also discards the token DMS caches for its own Configuration Service calls
   (`CacheSettings:TokenCacheExpirationSeconds`, default 1500 s, held in memory).

#### Adding the first key

There are two supported orders:

- **Key inserted before the Configuration Service starts** (`start-local-dms.ps1` runs
  `setup-openiddict.ps1 -InitDb` first). The startup load finds the key. Verify it on every
  instance, then start DMS.
- **Key inserted after the Configuration Service starts** (`start-local-config.ps1`, which
  the Configuration Service E2E setup uses).
  - Each running instance first publishes an empty snapshot, with the Warning
    `Signing-key snapshot {n} is empty`.
  - After the insert, tokens are issued with the new key at once. Each instance accepts
    them through the bootstrap exception described under
    [Refresh, backoff, cooldown and staleness](#refresh-backoff-cooldown-and-staleness),
    then subject to the cooldown, or at its next reload.
  - Its JWKS lists the key only after that instance's next successful load.
  - Verify the key on every instance before starting DMS.
  - Restart any DMS instance that started in the meantime, and confirm acceptance on it.
    Depending on its startup order, it may have cached an empty key set.

#### Adding a key (rotation)

1. **Insert the new key** as an active row, at a quiet time. On PostgreSQL,
   [`Generate-OpenIddictKey-Insert.ps1`](../../../eng/docker-compose/Generate-OpenIddictKey-Insert.ps1)
   prints a suitable `INSERT`. **Signing switches at the next token issued, on every
   instance.** Validation does not switch at that moment:
   - **Configuration Service instances** accept the new key's tokens as soon as their
     snapshot holds the key.
     - The first new-key token an instance sees triggers the unknown-key reload when the
       cooldown and backoff allow.
     - Otherwise that token is rejected with **401**, and the key arrives at the next
       allowed unknown-key reload or by `T_prop`, whichever comes first.
     - A request may also join a load that is already running, but a load that started
       before the insert returns without the key.
     - *Healthy key store, with traffic.* The last load that read the old rows completes
       within one load timeout of the insert. Its cooldown ends one cooldown later. The
       first new-key request after that starts a reload, which completes within one load
       timeout. New-key tokens are therefore accepted from at most load timeout + cooldown
       + load timeout after the insert: **50 s** at the defaults. This assumes a new-key
       request reaches the instance once the cooldown has ended. Without one, `T_prop`
       applies.
     - *Key store failing.* The key arrives with the first successful load after the store
       recovers, within the conditional recovery bounds above (*R* + 82 s at the defaults
       with no load in flight at recovery, *R* + 92 s with one).
     - Instances are independent: one may accept a new-key token while another still
       answers 401 until its cooldown ends or its next reload.
   - **Each DMS instance** rejects new-key tokens with 401 until a successful re-fetch, or
     a restart, loads the new key. A client can present the same token again once that
     has happened.
2. **Verify the new key on every instance** ([above](#verifying-a-key-change-on-every-instance)).
   Where you need no 401s from the Configuration Service itself, do this before new-key
   tokens reach it.
3. **Restart each DMS instance**, and confirm a new-key token is accepted on each
   ([above](#refreshing-dms-and-other-consumers)). Do this while the old key is still
   active. The restarted instance then holds both keys, and tokens minted before the
   switch keep validating.
4. **Keep the old key active** for at least `TokenExpirationMinutes` plus the 5-minute
   validation clock skew after the insert (35 min at the defaults). Tokens minted before
   the switch then stay valid until they expire.

#### Retiring a key

1. **Retire the old key**, after the retention window of step 4 above, by setting
   `IsActive = false`.
   - **Configuration Service instances** reject its tokens within `T_prop` on healthy
     instances.
   - **An instance that cannot read the key store** keeps trusting the retired key until
     its snapshot expires: at most `SigningKeyMaxStalenessSeconds` (3600 s) after its last
     successful load. Its JWKS keeps listing the key meanwhile. That is the configurable
     trade-off between availability and how long a retired key can be accepted.
2. **Verify on every instance** that the retired key id is no longer listed.
3. **Restart each DMS instance**, and confirm on each that a token signed with the retired
   key is rejected. Until a DMS instance successfully re-fetches or restarts, it keeps
   accepting such tokens. Retiring the key in the database does not, by itself, stop any
   consumer from trusting it.

#### Retiring a key early, for example after a compromise

1. Set `IsActive = false` on the key, and make sure another active key exists.
2. Restart every Configuration Service instance, so that each loads the table at startup.
   An instance whose startup load fails answers 503 rather than trusting the old snapshot.
3. Verify on every instance that the key id is no longer listed.
4. Restart every DMS instance. On each, confirm two things:
   - a token signed with the retired key, and not yet expired, is rejected;
   - a token signed with the remaining key is accepted.
5. Revoke known tokens through `POST /connect/revoke`. That stops them on the Configuration
   Service's own endpoints only.

#### Replacing the certificate (certificate mode)

The snapshot holds the one certificate key, so there is no overlap window.

- **Issuance** reads the certificate file at every token, so replacing the file switches
  signing at once.
- **Validation and JWKS** switch at the instance's next load. Until then, tokens signed with
  the new certificate face the acceptance delay of [Adding a key](#adding-a-key-rotation),
  step 1. After the load, tokens signed with the replaced certificate are rejected. Each
  DMS instance keeps the old certificate's key until it successfully re-fetches or
  restarts.

To replace the certificate:

1. Work at a quiet time.
2. Replace the file on every instance.
3. Restart each Configuration Service instance.
4. Verify the new thumbprint on every instance.
5. Restart each DMS instance, and confirm a new-certificate token is accepted on each.

Clients holding tokens signed with the old certificate must obtain new ones.

### Log signals

| Event | Level | Message starts with |
| --- | --- | --- |
| Snapshot published | Information | `Signing-key snapshot {n} published from {Database\|Certificate} ({Trigger}): {k} keys, …` |
| Empty snapshot published | Warning | `Signing-key snapshot {n} is empty: …` |
| Load failed | Error | `Signing-key load failed ({Trigger}): category SigningKeyStore, kind {Retrieval\|Processing}, consecutive failures {n}, next attempt in {s} s. …` |
| Load finished after its deadline | Warning | `A signing-key load that outlived its deadline has finished …; its result was discarded` |
| Unknown key id (bearer schemes) | Warning | `Bearer token key id {kid} was not in the signing-key snapshot; unknown-key refresh outcome: {Outcome}` |
| Unknown key id (introspection, revocation) | Warning | `Token key id {KeyId} was not in the signing-key snapshot; unknown-key refresh outcome: {Outcome}` |
| Dependency 503 | Error | `Authentication could not reach a decision: the {Category} is unavailable (trace …)` |
| JWKS 503 | Error | `The JWKS could not be served: the SigningKeyStore is unavailable (trace …)` |
| Introspection could not decide (answers `{"active": false}`) | Error | `Token validation could not reach a decision: the {Category} is unavailable` |
| Revocation could not decide (answers `200`) | Error | `Failed to revoke token: the {Category} is unavailable` |

A snapshot publication labelled `Request` is a normal background reload started by a
request that found the snapshot overdue. It is not a fault. The message
`Failed to fetch public keys for JWKS`, logged before DMS-1556, is no longer emitted.

**For alerting,** match on message text that covers every source of an event:

- **Unknown key ids:** `was not in the signing-key snapshot` matches both unknown-key
  lines. The bearer line alone misses introspection and revocation.
- **Undecided requests:** `is unavailable` matches the dependency 503, the JWKS 503, and
  the introspection and revocation lines. Introspection and revocation keep their
  protocol answers on the wire, so these logs are the only signal of their failures.

### Connection capacity

The snapshot takes key reads off the steady-state request path; the exceptions are listed
under [The key snapshot](#the-key-snapshot). Bearer authentication and introspection still
open a database connection for the uncached status check of every token that passes
verification with a valid `jti`, and most endpoints read their own data.
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
