---
status: accepted
date: 2026-08-04
jira: DMS-1354
related:
  - DMS-1353
---

# Expired Access Token Cleanup

## Decision

DMS handles expired client access token cleanup differently depending on the configured
identity-provider mode, and the two modes need different answers.

1. How DMS handles expired client access token cleanup.
   In self-contained mode (the bundled OpenIddict provider), DMS runs an in-process sweep
   that deletes expired rows from `dmscs.OpenIddictToken`.
   In Keycloak mode, cleanup is delegated entirely to Keycloak.
   The Keycloak token manager is a stateless HTTP proxy to Keycloak's own token endpoint and
   persists nothing on the DMS side, so there is nothing on the DMS side to clean up.
2. Whether an ODS-like cleanup mechanism is needed.
   Yes, but only for self-contained mode.
   Keycloak mode needs no DMS-side mechanism, because Keycloak already owns its own token and
   session housekeeping internally.
   Self-contained mode needed one, because its token table grows without bound and that growth
   was reproducible against a live stack.
   DMS-1354 implements that mechanism, described in Mechanism below.

## Current State

### Self-Contained Mode (OpenIddict)

Before this change, every token grant inserted a row and nothing ever removed it.
`OpenIddictTokenManager.GenerateJwtTokenAsync` calls `StoreTokenAsync` at
`src/config/backend/EdFi.DmsConfigurationService.Backend.OpenIddict/Services/OpenIddictTokenManager.cs:346`.
A repository-wide sweep found zero `DELETE` statements against `dmscs.OpenIddictToken` in
either backend (`EdFi.DmsConfigurationService.Backend.Postgresql` and
`EdFi.DmsConfigurationService.Backend.Mssql`).
The same sweep found zero `IHostedService` or `BackgroundService` registrations anywhere in
`src/config` or `src/dms`.

Live reproduction against the local Docker stack on 2026-08-04 confirmed the growth was real,
not theoretical.
Ten rows had all been expired since 2026-07-31, and every one of them still carried
`Status='valid'`.
A single `POST /connect/token` grant, issued the same way an end user would obtain one, grew
the table from ten rows to eleven.

Deleting swept rows is behavior-neutral.
`ValidateTokenAsync` checks the JWT's own lifetime first (`OpenIddictTokenManager.cs:365-378`)
and only looks up the row's status by `jti` afterward (`OpenIddictTokenManager.cs:381-385`).
That lifetime check accepts tokens up to five minutes past expiration
(`JwtTokenValidator.TokenValidationClockSkew`), so the sweep deletes only rows expired for
longer than that skew; every row the validator could still accept survives.
An already-expired row is therefore unreachable by the only production read path.
The DMS data API never reads the table at all: there are zero `OpenIddictToken` references
anywhere under `src/dms`, because the data API validates bearer tokens statelessly via JWKS.

Timestamps are UTC wall-clock values, stored as `timestamp without time zone` on PostgreSQL and
`DATETIME2` on SQL Server.
Any deletion predicate must therefore compare against UTC "now", never local time.
A deletion predicate is already index-supported on both engines: `IX_OpenIddictToken_ExpirationDate`
exists in both engines' DDL, in `0016_Create_openiddict_Token_Table.sql`.

Growth was unbounded by default.
The default token lifetime is 30 minutes (`IdentityOptions.cs:26`), so a client that requests
one token per lifetime accrues about 48 rows per day, and nothing limits how many grants a
client may request.
Nothing in the schema or code imposes a ceiling other than the cleanup mechanism described below.

### Keycloak Mode

`KeycloakTokenManager`
(`src/config/backend/EdFi.DmsConfigurationService.Backend.Keycloak/KeycloakTokenManager.cs`) is
a pure HTTP proxy to Keycloak's token endpoint.
It persists nothing locally.
Keycloak owns its own token and session lifecycle entirely outside DMS, so there is no
DMS-side state to clean up in this mode.

## ODS Precedent

Current Ed-Fi ODS ships an in-process Quartz job, `DeleteExpiredTokensJob` (`EdFi.Ods.Api/Jobs`),
that invokes `ExpiredAccessTokenDeleter`.
`ExpiredAccessTokenDeleter` runs `DELETE FROM dbo.ClientAccessTokens WHERE Expiration <= @expirationTime`.
The shipped `Ed-Fi-ODS-Implementation` `appsettings.json` enables this job by default:

```json
{
    "Name": "DeleteExpiredTokens",
    "IsEnabled": true,
    "CronExpression": "0 */30 * ? * *"
}
```

Older guidance describing token cleanup as an external cron job or a separate database-agent
job is stale.
No external cron job and no separate database-agent job is involved in current ODS: the
cleanup runs in-process, on a 30-minute cadence, and is enabled out of the box.

## Mechanism

DMS-1354 adds an in-process, config-gated periodic cleanup job to CMS, implemented as a plain
.NET `BackgroundService` named `TokenCleanupService`
(`src/config/backend/EdFi.DmsConfigurationService.Backend.OpenIddict/Services/TokenCleanupService.cs`).
No scheduler library such as Quartz is used.

The deletion predicate is `ExpirationDate <=` UTC now minus the validator's five-minute clock
skew (`JwtTokenValidator.TokenValidationClockSkew`), and it applies regardless of `Status`.
Subtracting the skew keeps every row the JWT validator could still accept, preserving the
behavior-neutrality argued in Current State.
Rows already marked `revoked` are deleted once they are also expired, exactly like rows still
marked `valid`; `Status` plays no part in the predicate.
Both engines run the same predicate against `dmscs.OpenIddictToken`: PostgreSQL executes
`DELETE FROM "dmscs"."OpenIddictToken" WHERE "ExpirationDate" <= @ExpiredBefore` and SQL Server
executes `DELETE FROM dmscs.OpenIddictToken WHERE ExpirationDate <= @ExpiredBefore`, each behind
`OpenIddictDataRepository.DeleteExpiredTokensAsync`.

The repository surface is `IOpenIddictTokenRepository.DeleteExpiredTokensAsync(DateTimeOffset
expiredBefore)`, returning the count of deleted rows.
It is implemented on both engines
(`EdFi.DmsConfigurationService.Backend.Postgresql` and `EdFi.DmsConfigurationService.Backend.Mssql`),
each delegating to its own `OpenIddictDataRepository`.

The configuration surface is exactly two settings, added to `IdentityOptions`
(`src/config/backend/EdFi.DmsConfigurationService.Backend.OpenIddict/Models/IdentityOptions.cs`):
`TokenCleanupEnabled` (`bool`, default `true`) and `TokenCleanupIntervalMinutes` (`int`, default
`30`).
`OpenIddictServiceCollectionExtensions.AddOpenIddictIdentityOptions` binds these from the
configuration keys `IdentitySettings:TokenCleanupEnabled` and
`IdentitySettings:TokenCleanupIntervalMinutes`.
The Docker Compose stacks (`eng/docker-compose/local-config.yml` and
`eng/docker-compose/published-config.yml`) map those keys to the environment variables
`DMS_CONFIG_IDENTITY_TOKEN_CLEANUP_ENABLED` (default `true`) and
`DMS_CONFIG_IDENTITY_TOKEN_CLEANUP_INTERVAL_MINUTES` (default `30`).

`TokenCleanupService.ExecuteAsync` checks `TokenCleanupEnabled` first; when it is `false`, the
service logs that the sweep is disabled and returns without scheduling anything.
Otherwise it builds a `PeriodicTimer` from `TokenCleanupIntervalMinutes`, falling back to the
30-minute default when the configured value is below `1` or above the `PeriodicTimer` maximum
of 71,582 minutes (either extreme would otherwise fault the host at startup).
It sweeps once at startup, so a pre-existing backlog does not wait a full interval and
instances restarting more often than the interval still clean up, then sweeps on every tick,
each time calling `DeleteExpiredTokensAsync` with UTC now minus the validation clock skew.
A failed sweep logs an error and does not crash the host; the next interval retries.

`WebApplicationBuilderExtensions.ConfigureIdentityProvider`
(`src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Infrastructure/WebApplicationBuilderExtensions.cs`)
registers the service with `webApplicationBuilder.Services.AddHostedService<TokenCleanupService>();`
in the `self-contained` identity-provider branch only, immediately after registering the
PostgreSQL or SQL Server OpenIddict stores.
The Keycloak branch never registers it, because the OpenIddict token store is not exposed
through Keycloak, matching the Current State finding that Keycloak owns its own cleanup.

The deletion predicate is already index-supported on both engines: `IX_OpenIddictToken_ExpirationDate`
exists in both engines' DDL.

Running the sweep from multiple CMS replicas is safe.
The `DELETE` is idempotent, so a replica that finds no rows left to delete because another
replica already deleted them causes no harm; concurrent sweeps across replicas are therefore
harmless.

### Library Fork from ODS

The implementation deliberately departs from ODS's library choice, and that departure rests on
evidence rather than preference.
ODS's Quartz plumbing - `SchedulerModule`, `ApiJobScheduler`, and `TenantSpecificJobBase` - exists
to serve a fleet of scheduled jobs and to iterate per-tenant Admin databases, and neither driver
applies to CMS.
CMS token storage binds a single connection string at construction, even in multi-tenant mode:
`OpenIddictDataRepository(IOptions<DatabaseOptions> databaseOptions)` reads
`databaseOptions.Value.DatabaseConnection` once, in the constructor, with no per-request or
per-tenant connection selection
(`src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql/OpenIddict/Repositories/OpenIddictDataRepository.cs:19-22`).
`TenantResolutionMiddleware` exempts `/connect` from tenant resolution entirely
(`src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Middleware/TenantResolutionMiddleware.cs`),
so the token store the cleanup job targets is never tenant-partitioned in the first place.
The cleanup job is therefore a single-database sweep, and a plain `BackgroundService` is
sufficient to run it.

### Rejected Alternatives

- **Quartz job, matching ODS exactly.**
  This would add a new dependency to serve a single fixed-interval job, and DMS carries zero
  scheduled jobs today.
- **A documented external cron job or database-agent job.**
  Every deployment would have to remember to set it up, and this is the stale pattern that older
  ODS guidance described.
- **Deleting a token's row at issuance time instead of on a sweep.**
  This would add work and lock exposure to the authentication hot path, and it would never clean
  up deployments that go idle before their tokens expire.
- **An audit-retention window before deletion.**
  ODS retains nothing past expiration, and no DMS requirement for token-grant audit history
  exists.

### Adjacent Observations

- `IOpenIddictTokenRepository.GetTokenByIdAsync` has no production caller.
- `GetTokenStatusAsync` reads only `Status` and ignores `ExpirationDate`; it stays unexposed
  because the JWT lifetime check runs first, and the gap is now moot: an expired row is deleted
  by the sweep before it could ever be read as stale.
- The DMS data API validates bearer tokens statelessly and never consults revocation status, so a
  token revoked through CMS remains usable at DMS until its JWT expires (at most the 30-minute
  default lifetime); this is consistent with the bounded-staleness stance the
  [ownership-token operational-lifecycle record](../backend-redesign/design-docs/ownership-token-operational-lifecycle.md)
  adopted.
- Integration test coverage for `DeleteExpiredTokensAsync` exists for both engines, in each
  project's `OpenIddictDataRepositoryTests.cs`
  (`EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration` and
  `EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration`), covering a mix of expired
  and unexpired rows and a row at the exact expiration boundary; the SQL Server cases skip
  locally when no SQL Server connection is configured and run in CI.

## Evidence Baseline

This record was evaluated against DMS
[`02d63b558`](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/commit/02d63b5580bc053731f9186e29b6a6f84b6bcefc)
(`main`, 2026-08-04) and the immutable reference revisions below.

- Ed-Fi-ODS at
  [`24fe66cfc`](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/tree/24fe66cfc04459ad6d6cac09d635d3c149b24669).
- Ed-Fi-ODS-Implementation at
  [`37ff595c1`](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS-Implementation/tree/37ff595c171b73e524d96b13103ef9ae01712beb).
