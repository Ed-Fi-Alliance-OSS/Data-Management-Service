---
status: proposed
date: 2026-08-03
jira: DMS-1374
related:
  - DMS-1058
  - DMS-1060
  - DMS-1372
  - DMS-1373
---

# Decision Record: Ownership-Token Operational Lifecycle and Administration

> **Review gate:** This record is proposed.
> It recommends creating no follow-on implementation stories; approval closes DMS-1374 with the
> operational answers below and authorizes only the one documentation erratum described in
> [Errata to the DMS-1058 Record](#errata-to-the-dms-1058-record).

## Decision

The approved [DMS-1058 contract](ownership-token-maintenance.md) already provides the complete
ownership-token operational lifecycle that current security, support, and administration evidence
justifies.
This spike adopts the following answers:

1. The ownership-access revocation SLA is the bounded-staleness contract already approved:
   ownership-configuration changes take effect within the configured application-context cache
   lifetime (default 600 seconds), and compromised-client cutoff is bounded by the configured
   access-token lifetime (default 30 minutes), which no ownership-scoped mechanism can improve.
2. The configured cache lifetime remains the only freshness mechanism.
   No explicit-reload endpoint and no push invalidation are added; process restart is the
   documented immediate-flush lever, matching the reference implementation's guidance.
3. The DMS-1372 CMS endpoints are the complete administrative surface.
   No Admin App or Admin Console workflows are proposed, and no CMS API changes beyond the
   approved maintenance contract are needed.
4. Ownership tokens get no retirement or deactivation lifecycle.
   Durable catalog rows, assignment removal, and editable descriptions already cover the need.
5. API-client replacement is served by copying the ownership configuration to the replacement
   client.
   No bulk transfer of existing document ownership is proposed.
6. `/oauth/token_info` does not expose ownership values.
   The secured CMS ownership endpoints are the diagnostics path.
7. The global positive `SMALLINT` identifier capacity is affirmed, with documented monitoring
   thresholds and a documented widening runbook instead of proactive implementation work.
8. No follow-on implementation stories are recommended.
   The only change this spike produces is a mechanical erratum to the DMS-1058 record.

## Document Ownership and Handoffs

| Artifact | Responsibility |
| --- | --- |
| This decision record | Operational lifecycle answers: revocation SLA, freshness mechanism, administration surface, retirement, hand-off, diagnostics, and capacity |
| [DMS-1058 decision record](ownership-token-maintenance.md) | The base maintenance, delivery, and cache contract; this record changes nothing in it except the erratum below |
| [DMS-1374 spike mirror](../epics/14-authorization/25-ownership-token-operational-lifecycle-spike.md) | Spike scope, acceptance criteria, and boundaries |
| [DMS-1060 story mirror](../epics/14-authorization/11-ownership-auth-strategy.md) | Ownership CRUD semantics; unaffected and not blocked by this record |

## Evidence Baseline

This record was evaluated against DMS
[`7f5004257`](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/commit/7f500425761eca231e72a0563cf9454bfe6f4b61)
(`main`, 2026-08-03) and the immutable reference revisions below.

- Ed-Fi-ODS at
  [`24fe66cfc`](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/tree/24fe66cfc04459ad6d6cac09d635d3c149b24669),
  the same commit the DMS-1058 record pinned.
- Ed-Fi-ODS-Implementation at
  [`37ff595c1`](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS-Implementation/tree/37ff595c171b73e524d96b13103ef9ae01712beb).
- Ed-Fi docs site at
  [`8eede7611`](https://github.com/Ed-Fi-Alliance-OSS/ed-fi-alliance-oss.github.io/tree/8eede7611072be0518526ccb16b6f62d9d729724);
  the ODS 7.3 ownership guide is byte-identical on current `main` (`c92d164ce`).
- ODS-Admin-API at
  [`15dbe5861`](https://github.com/Ed-Fi-Alliance-OSS/ODS-Admin-API/tree/15dbe5861414).
- [Ed-Fi-ODS-AdminApp-Legacy](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS-AdminApp-Legacy/tree/d067985e8489)
  at `d067985e8` and
  [Ed-Fi-AdminApp](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-AdminApp/tree/2b399127ee4e) at
  `2b399127e`.

### DMS evidence

- DMS validates bearer JWTs locally with signature and lifetime checks and performs no
  per-request introspection; see
  [`JwtValidationService`](../../../../src/dms/core/EdFi.DataManagementService.Core/Security/JwtValidationService.cs).
  A revoked identity-provider client therefore keeps its already-issued tokens usable until they
  expire, plus the default 30-second clock skew (`ClockSkewSeconds = 30`).
- Both bundled identity providers default to 30-minute access tokens:
  [`IdentityOptions.TokenExpirationMinutes = 30`](../../../../src/config/backend/EdFi.DmsConfigurationService.Backend.OpenIddict/Models/IdentityOptions.cs)
  for OpenIddict and `TokenLifespan = 1800` in
  [`setup-keycloak.ps1`](../../../../eng/docker-compose/setup-keycloak.ps1).
- [`CacheSettings`](../../../../src/dms/core/EdFi.DataManagementService.Core/Configuration/CacheSettings.cs)
  defaults: `ApplicationContextCacheExpirationSeconds = 600` and
  `TokenCacheExpirationSeconds = 1500`.
- [`CachedApplicationContextProvider`](../../../../src/dms/core/EdFi.DataManagementService.Core/Configuration/CachedApplicationContextProvider.cs)
  already implements per-client reload internally; nothing exposes it administratively.
- [`ApiClientModule`](../../../../src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Modules/ApiClientModule.cs)
  already provides `DELETE /v3/apiClients/{id}` (which deletes the identity-provider client
  before the database row, so token issuance stops immediately) and
  `PUT /v3/apiClients/{id}/reset-credential`.
- The [DDL emitter](../../../../src/dms/backend/EdFi.DataManagementService.Backend.Ddl/CoreDdlEmitter.cs)
  emits `dms.Document.CreatedByOwnershipTokenId` as nullable `SMALLINT` with an index.
- DMS-1337 (merged 2026-07-31, after the DMS-1058 evidence baseline) retyped CMS resource
  identifiers: `dmscs.ApiClient.Id` is now
  [`INT`](../../../../src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql/Deploy/Scripts/0003_Create_ApiClient_Table.sql),
  while `dmscs.Tenant.Id` remains
  [`BIGINT`](../../../../src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql/Deploy/Scripts/0024_Create_Tenant_Table.sql).

### ODS runtime evidence

- Every authenticated ODS request resolves ownership values through
  [`IApiClientDetailsProvider`](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/24fe66cfc04459ad6d6cac09d635d3c149b24669/Application/EdFi.Common/Security/IApiClientDetailsProvider.cs),
  which is hard-wired to a caching interceptor keyed by access token.
  The backing
  [`ExpiringConcurrentDictionaryCacheProvider`](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/24fe66cfc04459ad6d6cac09d635d3c149b24669/Application/EdFi.Ods.Api/Caching/ExpiringConcurrentDictionaryCacheProvider.cs)
  clears the whole dictionary on a recurring timer whose period is
  `Caching:ApiClientDetails:AbsoluteExpirationSeconds`, shipped as 900 seconds in the WebApi
  `appsettings.json` (the 7.3 configuration guide shows an even looser 14,400-second example
  value).
- With the optional external Redis cache (`UseExternalCache`, default false),
  [`CachingApiClientDetailsProviderDecorator`](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/24fe66cfc04459ad6d6cac09d635d3c149b24669/Application/EdFi.Ods.Features/ExternalCache/CachingApiClientDetailsProviderDecorator.cs)
  caches a token's details until token expiry plus 15 minutes, so ownership changes then reach
  only newly issued tokens.
- The shipped ODS access-token lifetime is 30 minutes (`BearerTokenTimeoutMinutes` in WebApi
  `appsettings.json`; the code default is 60).
- ODS has no HTTP cache-flush endpoint.
  Push invalidation exists only as the opt-in, default-off `Notifications` feature: a Redis
  pub/sub `expire-cache` message that clears a named cache, throttled to one handling per 300
  seconds, with no sender tooling provided
  ([`ExpireCacheHandler`](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/24fe66cfc04459ad6d6cac09d635d3c149b24669/Application/EdFi.Ods.Features/Notifications/ExpireCacheHandler.cs)).
- ODS security metadata is cached with a 10-minute default
  (`Caching:Security:AbsoluteExpirationMinutes`), and the claim-set guide documents that changes
  wait for that refresh or a manual restart.
- Create-time stamping and read/modify enforcement consume the same possibly-cached
  `ApiClientContext` snapshot
  ([`OwnershipInitializationCreateEntityDecorator`](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS-Implementation/blob/37ff595c171b73e524d96b13103ef9ae01712beb/Application/EdFi.Ods.Features.OwnershipBasedAuthorization/Security/OwnershipInitializationCreateEntityDecorator.cs)),
  so both share one staleness bound, exactly as the DMS-1058 contract specifies for DMS.
- [`TokenInfo.Create`](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/24fe66cfc04459ad6d6cac09d635d3c149b24669/Application/EdFi.Ods.Features/TokenInfo/TokenInfo.cs)
  returns `active`, `client_id`, `namespace_prefixes`, `education_organizations`,
  `student_identification_system`, `assigned_profiles`, `claim_set`, `resources`, and
  `services`; it never reads the creator or read/modify ownership token IDs even though its
  `ApiClientContext` input carries both.
- [`dbo.OwnershipTokens`](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/24fe66cfc04459ad6d6cac09d635d3c149b24669/Artifacts/PgSql/Structure/Admin/0060-Add-OwnershipTokens.sql)
  has exactly two columns (`OwnershipTokenId SMALLSERIAL`, `Description VARCHAR(50)`): no
  deactivation flag exists, no script or utility re-stamps existing rows'
  `CreatedByOwnershipTokenId`, and no artifact monitors or guards the `SMALLINT` range.

### ODS ecosystem administration evidence

- The ODS 7.3
  [ownership guide](https://github.com/Ed-Fi-Alliance-OSS/ed-fi-alliance-oss.github.io/blob/8eede7611072be0518526ccb16b6f62d9d729724/odsApi_versioned_docs/version-7.3/platform-dev-guide/features/ownership-based-authorization.md)
  administers tokens exclusively through raw SQL against `EdFi_Admin` ("Create an Ownership
  Token for each API Client / Assign the Creator Ownership Token / Assign the Ownership Token").
- Admin API has no ownership-token endpoints in any generation: the 1.4.3 and 2.3.0 OpenAPI
  specs contain no token resource, and the 2.x `apiClientModel` carries no ownership fields.
  The only ownership-adjacent capability is assigning the `OwnershipBased` strategy name to
  claim-set resource-claim actions.
- Neither the legacy Admin App nor Admin App v4 / Admin Console has any ownership-token UI;
  v4's "ownership" concept is the unrelated team-resource-ownership feature.
- The documented ODS compromised-client procedure is manual: `UPDATE ApiClients SET
  IsApproved = 0`, then `DELETE FROM ClientAccessTokens`, then expire the `api-client-details`
  cache by restart or by hand-publishing the Redis message
  ([key/secret guide](https://github.com/Ed-Fi-Alliance-OSS/ed-fi-alliance-oss.github.io/blob/8eede7611072be0518526ccb16b6f62d9d729724/odsApi_versioned_docs/version-7.3/how-to-guides/how-to-configure-key-secret.md)).
- The documented grain is one token per participating API client, for niche shared-instance use
  cases (private schools spanning LEAs and per-caller assessment metadata in the 7.3 guides;
  SEA-level multi-vendor protection in the draft version-8 how-to at the docs site's
  `c92d164ce`);
  no Ed-Fi document states a capacity expectation or exhaustion policy for the
  `SMALLINT` range.

## Revocation SLA

The SLA question is what bound each revocation event must meet, and whether the approved
bounded-staleness contract meets it.

### Adopted bounds

| Event | Adopted bound | Mechanism |
| --- | --- | --- |
| Read/modify assignment removal or addition | Configured application-context cache lifetime (default 600 s) | Cache expiry; next lookup reads CMS |
| Creator-token change | Same bound for new creates; documents stamped during the window keep the old token permanently | Cache expiry; PUT never re-stamps |
| Compromised client: new token issuance | Immediate | `reset-credential` or API-client `DELETE` (identity-provider-first) |
| Compromised client: issued tokens, ownership-relevant operations | Configured application-context cache lifetime after client deletion | Context lookup fails; DMS returns 401 |
| Compromised client: issued tokens, all other operations | Remaining access-token lifetime (default at most 30 minutes, plus the default 30-second clock skew) | JWT expiry; DMS validates locally |
| Replacement client becomes effective | First request (cold cache key reads CMS immediately) | New client is a new cache key |

Ownership-relevant operations are every POST and any GET/PUT/DELETE whose selected strategies
include `OwnershipBased`, because those require a resolved application context under the
DMS-1058 contract.

### Why these bounds are sufficient

- The access-token lifetime is the floor for full bearer cutoff.
  DMS validates JWTs locally without per-request introspection, so an issued token for a
  compromised client keeps its access to operations that need no application context until the
  token expires, no matter how fast ownership data propagates; ownership-relevant operations are
  the exception, cut off earlier by context expiry as the table above states.
  Tightening that floor means shortening the configured token lifetime or changing token
  architecture, which is identity-layer work outside ownership scope.
- The approved 600-second context lifetime already sits well inside that 30-minute floor and is
  stricter than the reference implementation's default staleness bounds: ODS ships a 900-second
  client-details cache (its configuration guide shows a 14,400-second example value), extends
  staleness to full token lifetime under its external cache, and documents its
  compromised-client response as manual SQL plus a restart.
  ODS can additionally hard-delete issued opaque tokens from `dbo.ClientAccessTokens`; DMS JWTs
  have no equivalent, which is precisely why the token-lifetime floor above is stated explicitly.
- Creator-token changes carry a permanence hazard rather than a latency hazard: documents
  stamped with the old token during the staleness window stay that way (matching ODS
  insertable-not-updatable semantics).
  The operational guidance is to treat creator-token changes as deliberate maintenance: make the
  CMS change, then wait out one cache lifetime (or restart DMS) before resuming the affected
  client's writes when exact stamping matters.

### Compromised-client runbook

1. Revoke credentials at the source: `PUT /v3/apiClients/{id}/reset-credential`, or
   `DELETE /v3/apiClients/{id}` when the client is being decommissioned.
   Both stop new token issuance immediately.
2. Remove or reduce the client's ownership assignments via
   `PUT /v3/apiClients/{id}/ownership` if the client must keep operating with a narrower scope
   (deletion makes this moot; assignment rows cascade).
3. Accept that already-issued tokens can keep operating for up to their remaining lifetime
   (default at most 30 minutes, plus the default 30-second clock skew), with ownership-relevant
   operations cut off earlier by context expiry after deletion.
   Restarting DMS instances drops cached contexts and advances only that ownership-relevant
   cutoff; no lever shortens an already-issued token's access to other operations.
   A deployment that needs a smaller worst case lowers the configured token lifetime as standing
   policy, which affects only tokens issued after the change.

No product or security requirement on record rejects these bounds, so the bounded-staleness
contract stands and DMS-1060 is not blocked.

## Cache Freshness and Invalidation

The acceptance criterion asks for a comparison of the approved configurable lifetime with
explicit reload and push invalidation.

| Aspect | Configured TTL (approved) | Explicit reload endpoint | Push invalidation (CMS to DMS) |
| --- | --- | --- | --- |
| Freshness bound | TTL, default 600 s | On demand, but per process only | Near-immediate when healthy |
| Tenant behavior | Tenant-qualified keys per DMS-1373; no extra work | Must address tenant-plus-client keys on every instance | Must fan out per tenant and per instance |
| Dependency-failure behavior | Fail closed per DMS-1373; no new dependencies | No new dependency, but a reload call reaches one process's in-memory cache, giving false fleet-wide confidence | New broker dependency; a missed message is silent staleness, so the TTL backstop remains mandatory |
| Operational complexity | None | New secured DMS admin surface plus a fleet fan-out mechanism to be truthful | Broker infrastructure, sender tooling, authorization, throttling |
| Reference precedent | ODS default posture (900 s client details, 10 min security metadata) | None; ODS documents restart instead | ODS `Notifications`: opt-in, default off, 300 s throttle, no sender tooling |

Recommendation: keep the configured application-context lifetime as the sole freshness
mechanism.

- The existing internal per-client reload stays internal; exposing it would add a secured
  endpoint whose per-process reach cannot honestly deliver a fleet-wide guarantee without the
  same fan-out machinery as push invalidation.
- Process restart is the documented immediate-flush lever, matching the ODS guidance for its
  security cache.
- Operators who need a tighter routine bound lower `ApplicationContextCacheExpirationSeconds`
  for their deployment; the contract already treats the configured value as the actual bound.
- If a validated customer requirement for sub-TTL revocation ever arrives, the ODS
  `Notifications` feature is the precedent shape: an opt-in, coarse-grained, throttled
  expire-by-cache-type channel rather than fine-grained per-key invalidation.

## Administration Surface

The reference ecosystem offers no administrative product surface for ownership tokens at all:
the ODS guide administers them with raw SQL, Admin API has no token endpoints in any generation,
and no Admin App generation has token UI.
The DMS-1372 contract (secured catalog CRUD-minus-delete, the atomic per-client ownership
subresource, and ownership fields on the limited API-client responses) therefore already exceeds
the entire upstream administrative surface.

The administrator workflows are complete API-level workflows over that contract:

| Workflow | Contract path |
| --- | --- |
| Token creation | `POST /v3/ownershipTokens/` |
| Assignment and reassignment | `GET` then `PUT /v3/apiClients/{id}/ownership` (atomic replacement) |
| Revocation | `PUT /v3/apiClients/{id}/ownership` with the token removed |
| Visibility | `GET /v3/ownershipTokens/`, `GET /v3/apiClients/{id}/ownership`, ownership fields on the limited API-client response |

No CMS API changes beyond the approved maintenance contract are needed, and no Admin App or
Admin Console workflow is proposed: there is no reference precedent and no validated customer
workflow requiring UI, and building one is a separate product decision outside this epic.

## Retirement and Deactivation

Ownership tokens get no retirement or deactivation lifecycle.

- Revocation of access is assignment removal, already part of the approved contract; the catalog
  row's durability preserves the interpretation of historical
  `dms.Document.CreatedByOwnershipTokenId` values, which is the constraint that matters.
- A token withdrawn from service is marked by editing its required description (for example
  prefixing "RETIRED"), which changes no authorization semantics; reactivation is simply
  re-assigning the token; listing needs no status filter because there is no status.
- The approved audit columns record who created and last modified the catalog rows and the
  current assignments.
  Removing an assignment deletes its row, so removals leave no CMS history; that matches
  existing CMS convention for every other aggregate and still exceeds the reference schema,
  which has no audit columns at all.
  A removal history would require an event or history mechanism no support or security
  requirement justifies.
- A deactivation flag would force new semantics (does it block assignment, block stamping, or
  strip existing assignments?) with no demonstrated need, and the reference implementation has
  no such flag (`dbo.OwnershipTokens` is two columns).

## API-Client Replacement and Hand-off

Assigning the existing tokens to a replacement API client covers the supported hand-off
scenarios, because ownership anchors on token identity, not client identity.

1. Create the replacement API client under the same application, or one configured
   equivalently, because claim-set, education-organization, namespace, and datastore
   authorization follow the application and client configuration, not ownership; the new client
   starts with a null creator and no tokens.
2. Copy the configuration: `GET /v3/apiClients/{old}/ownership`, then
   `PUT /v3/apiClients/{new}/ownership` with the same body.
3. The replacement client then reads, modifies, and stamps under ownership exactly as its
   predecessor did; its first request resolves fresh context, so ownership is effective
   immediately.
4. Decommission the old client with `DELETE /v3/apiClients/{id}`; assignment rows cascade while
   the catalog rows and all historical document stamps remain valid.

Bulk transfer of existing document ownership (mass re-stamping of
`dms.Document.CreatedByOwnershipTokenId`) is not proposed.
Replacement never requires it under the token-sharing model; re-stamping would only serve
ownership reorganization (splitting or merging token identities), no validated customer workflow
requires that, and the reference implementation has no such utility.

## Ownership Diagnostics and token_info

`/oauth/token_info` does not gain ownership fields.

- The reference implementation omits them: ODS `TokenInfo.Create` has both
  ownership values available in its `ApiClientContext` input and returns neither.
- The DMS-1058 record already rejected this delivery path, and the
  [token_info design](../../token_info-endpoint.md) carries no ownership fields.
- Token IDs are internal authorization identifiers that the bearer cannot act on; disclosing
  them to API clients adds no self-service value while widening what a leaked token reveals.
- The support diagnostics path is the secured, administrator-scoped CMS surface:
  `GET /v3/apiClients/{id}/ownership` answers "what can this client touch" and the catalog
  endpoints answer "what does this token mean", which is exactly the question support handles.

Because exposure is not recommended, no authorization or response-shape constraints are needed.

## Identifier Capacity

### Consumption quantification

Ownership-token IDs are consumed only by explicit administrative creation
(`POST /v3/ownershipTokens/`); nothing in the contract creates tokens automatically.
The documented grain is one token per participating API client in niche shared-instance
scenarios, so a large multi-tenant CMS database (for example 50 tenants with 40 participating
clients each, at three token creations per client over the database's lifetime) consumes on
the order of 6,000 of the 32,767 lifetime IDs.
The realistic exhaustion risk is not organic growth but automation: a provisioning script that
recreates tokens on every run consumes IDs permanently, because IDs are never reused even after
assignments are removed.

### Monitoring and thresholds

The approved contract already fails closed at exhaustion (CMS returns 500 without wrapping or
reusing an ID).
Operational monitoring is a documented query, not new product machinery: the identity
allocator's current value is the consumption watermark, read with
`SELECT last_value FROM dmscs."OwnershipToken_Id_seq"` on PostgreSQL or
`SELECT IDENT_CURRENT('dmscs.OwnershipToken')` on SQL Server.
`SELECT MAX("Id")` is only a lower bound, because rolled-back inserts and SQL Server identity
caching consume IDs without leaving rows.

| Watermark | Threshold | Action |
| --- | --- | --- |
| 16,384 (50%) | Warn | Audit token-creating automation for recreate-instead-of-reuse patterns |
| 26,214 (80%) | Act | Schedule the widening migration below |

### Widening runbook

Executed only if the 80% threshold is crossed; expected never for realistic deployments.
Widen from the consumer inward so every column can hold any ID the catalog might issue, using
`INT`, which DMS-1337 has already made the CMS identifier convention:

1. DMS storage: retype `dms.Document.CreatedByOwnershipTokenId` from `SMALLINT` to `INT` in the
   DDL emitter, which is a physical-schema change requiring the established relational-mapping
   version bump and golden regeneration.
   That covers newly provisioned datastores only: DMS provisioning is create-only
   (`ddl provision` targets a fresh database, reruns idempotently on a matching one, and rejects
   mismatched or partially provisioned states), so each existing datastore needs a coordinated
   manual migration - a provider-appropriate `ALTER` of the column and its index plus a rewrite
   of the stored provisioning metadata that fingerprint validation checks - or a
   reprovision-and-reload.
   The metadata rewrite must be a delete-and-reinsert: `dms.EffectiveSchema` is a singleton and
   `dms.SchemaComponent` references its hash with an immediate delete-cascade-only foreign key,
   so in-place hash updates fail in either order; delete the `dms.EffectiveSchema` row (the
   cascade removes the component rows), insert the replacement row, and re-insert the component
   rows under the new hash.
2. DMS core: retype the `ApplicationContext` ownership members from `short` to `int`.
3. CMS: retype `dmscs.OwnershipToken.Id` (identity), `dmscs.ApiClient.CreatorOwnershipTokenId`,
   and `dmscs.ApiClientOwnershipToken.OwnershipTokenId` with provider-appropriate migrations,
   and widen the model and validation ranges.

No ID values change, no reuse is introduced, and per-tenant ID namespaces remain rejected as in
the DMS-1058 record.

## Errata to the DMS-1058 Record

DMS-1337 (merged 2026-07-31, after the DMS-1058 record's evidence baseline) retyped
`dmscs.ApiClient.Id` to `INT`.
The proposed `dmscs.ApiClientOwnershipToken.ApiClientId` column must follow its parent key and
is corrected from `BIGINT` to `INT` in the DMS-1058 record alongside this record.
`dmscs.OwnershipToken.TenantId` stays `BIGINT` because `dmscs.Tenant.Id` was not retyped.

## Recommended Follow-on Work

No implementation stories are recommended.

| Investigated capability | Disposition | Justification |
| --- | --- | --- |
| Faster revocation / push invalidation | Not justified | Approved TTL is stricter than reference defaults; token lifetime is the real floor; ODS ships invalidation only as an opt-in default-off feature |
| Explicit reload endpoint | Not justified | Per-process reach cannot deliver a fleet guarantee; restart is the documented lever |
| Admin App / Admin Console workflows | Not justified | No reference precedent (upstream administers by raw SQL); DMS-1372 already exceeds the upstream surface; UI is a separate product decision |
| Token retirement / deactivation | Not justified | Assignment removal plus durable rows plus editable descriptions cover it; reference schema has no flag |
| Bulk ownership transfer | Not justified | Token-sharing makes replacement work without it; no validated workflow; no reference utility |
| Ownership in `/oauth/token_info` | Not justified | Reference omits it deliberately; CMS ownership GETs are the diagnostics path; exposure widens leak surface without client value |
| Capacity safeguards | Documented, not built | Admin-driven consumption, documented watermark query, thresholds, and widening runbook; contract already fails closed at exhaustion |
| DMS-1058 record erratum (`ApiClientId` type) | Applied with this record | Mechanical alignment with the DMS-1337 parent-key retype |

## Acceptance Criteria Traceability

| DMS-1374 acceptance criterion | Section |
| --- | --- |
| Establish the required revocation SLA | [Revocation SLA](#revocation-sla) |
| Compare cache lifetime with reload and push invalidation | [Cache Freshness and Invalidation](#cache-freshness-and-invalidation) |
| Define Admin App workflows and CMS API changes | [Administration Surface](#administration-surface) |
| Decide retirement or deactivation | [Retirement and Deactivation](#retirement-and-deactivation) |
| Determine replacement hand-off coverage and bulk transfer | [API-Client Replacement and Hand-off](#api-client-replacement-and-hand-off) |
| Evaluate token_info diagnostics | [Ownership Diagnostics and token_info](#ownership-diagnostics-and-token_info) |
| Quantify ID consumption, thresholds, and migration | [Identifier Capacity](#identifier-capacity) |
| Recommend stories or record why none | [Recommended Follow-on Work](#recommended-follow-on-work) |
