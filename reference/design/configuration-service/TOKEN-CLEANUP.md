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
A row expired beyond that skew is therefore unreachable by the only production read path.
The DMS data API never reads the table at all: there are zero `OpenIddictToken` references
anywhere under `src/dms`, because the data API validates bearer tokens statelessly via JWKS.

`ExpirationDate` is an instant, stored as `timestamp with time zone` on PostgreSQL and `DATETIME2`
holding UTC on SQL Server.
Any deletion predicate must therefore compare against UTC "now", never local time.
As shipped by DMS-1354 the PostgreSQL column was `timestamp without time zone`, which made both the
insert and the sweep convert through the session time zone; DMS-1430 replaced that with the
`timestamptz` declaration described under Time-Zone Independence below, so neither path converts
any more.
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
Because the cleanup service is registered only in the self-contained branch, the
`TokenCleanupEnabled` and `TokenCleanupIntervalMinutes` settings are inert in Keycloak mode:
nothing reads them, whatever they are set to.
A deployment that ran self-contained and later switches to Keycloak strands whatever rows were
in `dmscs.OpenIddictToken` at switch time; they are harmless dead weight (nothing in Keycloak
mode reads or writes the table) and can be removed with a one-time manual `DELETE` if desired.

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
Subtracting the skew keeps every row the JWT validator could still accept until the moment that
acceptance itself lapses, preserving the behavior-neutrality argued in Current State; a request
racing the sweep in the final instant of the skew window is rejected at most a moment before the
validator itself would have rejected it.
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
  token revoked through CMS remains usable at DMS until its JWT expires (the configured token
  lifetime, 30 minutes by default, plus the data API's configured validation clock skew); this is
  consistent with the bounded-staleness stance the
  [ownership-token operational-lifecycle record](../backend-redesign/design-docs/ownership-token-operational-lifecycle.md)
  adopted.
- The PostgreSQL time-zone dependence this record originally deferred is resolved by DMS-1430; see
  Time-Zone Independence below.
- Integration test coverage for `DeleteExpiredTokensAsync` exists for both engines, in each
  project's `OpenIddictDataRepositoryTests.cs`
  (`EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration` and
  `EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration`), covering a mix of expired
  and unexpired rows and a row at the exact expiration boundary; the SQL Server cases skip
  locally when no SQL Server connection is configured and run in CI.

## Time-Zone Independence (DMS-1430)

DMS-1354 shipped `ExpirationDate` as `timestamp without time zone` while both repository paths bound
`DateTimeOffset` values, which Npgsql sends as `timestamptz`.
The insert cast down to a wall clock through the PostgreSQL session time zone, and the sweep's
predicate cast the stored column back up through it, using the operator `timestamp_le_timestamptz`.
Those two conversions do not round-trip on a DST-observing server.
DMS-1430 declares the column `timestamp with time zone`, adds the guarded migration
`0032_Alter_OpenIddictToken_ExpirationDate_TimeZone.sql`, and pins both the insert parameter and the
sweep bound to `DbType.DateTimeOffset` normalized with `ToUniversalTime()`.
Neither path converts any more, so no stored value and no comparison depends on the session zone.

### Correction to the original failure description

This record previously stated that a DST-observing server "could delete a row while the validator
still accepts its token".
That framing is wrong for a *stable* session zone and is corrected here.
Under a single unchanging DST-observing zone the round trip errs strictly late: a fall-back wall
clock is ambiguous and PostgreSQL resolves it to the later of the two candidate instants, and a
spring-forward gap resolves forward, so the sweep could only *under*-delete and rows lingered past
their true expiry.
Premature deletion of a live token was real but needed the session zone to **differ** between the
write and the sweep - an operator changing the server `timezone`, setting `PGTZ`, or adding
`Timezone=` to one connection string, with rows written under the old zone still present.
Both defects are removed by the same change; the distinction matters only for describing the
pre-fix severity honestly.

### What the migration can and cannot recover

`0032` reinterprets each stored wall clock through the session time zone *the migration runs under*.
That is the best available inverse of how the rows were written, not a full repair, because the old
column type discarded information before the script ever runs.
Three cases, and they differ in whether the result can land early:

1. **Same session zone as the write, unambiguous wall clock.** Exact recovery.
   On the shipped UTC containers this is the identity, no value moves, and this case applies
   throughout.
2. **Same session zone, wall clock in a DST transition window.** Late or equal.
   Two instants an hour apart were already stored identically, so both recover as the later one.
   This matches how the sweep already read such a row before the fix, and the error direction is
   late: the token lingers, it is never swept early.
3. **Session zone changed since the write.** Irrecoverable, and the result may land **earlier** as
   well as later, by the difference between the two offsets.
   A row written under `America/New_York` for `18:00Z` stores `14:00`; migrated under `UTC` it
   reconstructs as `14:00Z`, four hours early.
   The zone a row was written under was never recorded, so nothing in the migration can detect or
   correct this.
   The mitigation is operational: run the upgrade under the same session time zone the rows were
   written under.

Case 2's "later or equal" property does **not** extend to case 3.
The script carries no `USING` clause for the same reason: the implicit assignment cast is the
inverse of how the rows were written, whereas `USING "ExpirationDate" AT TIME ZONE 'UTC'` forces
case 3 on every row of a non-UTC server.

### Operational cost

`ALTER COLUMN ... TYPE` takes an `ACCESS EXCLUSIVE` lock and rewrites the table and the
`ExpirationDate` index.
This applies only to an existing database still carrying the old column type: the script's guard
checks the declared type first, so the rewrite happens at most once per database and is skipped
entirely on a fresh install and on any replay.
The pause is proportional to the row count, which the sweep above bounds; an install upgrading from
a pre-DMS-1354 build may still carry an unbounded backlog and should expect a correspondingly longer
one-time pause.
No operator time-zone requirement follows from this change - the point of the fix is that the
session zone no longer matters at run time.

### Deliberate scope limit

`CreationDate` and `RedemptionDate` on the same table remain `timestamp without time zone` and carry
the same latent dependence.
They are intentionally out of scope for DMS-1430: no predicate compares them, and no production code
path reads them, so nothing about cleanup or validation safety depends on them.
Converting them is available as follow-up cleanup rather than part of this fix.
The CMS-wide `CreatedAt` / `LastModifiedAt` audit columns follow one convention across every `dmscs`
table and are a separate decision again.
SQL Server needed no counterpart change and has none.

### Coverage

`Given_A_DST_Observing_PostgreSQL_Session_Time_Zone` in the PostgreSQL
`OpenIddictDataRepositoryTests.cs` runs the repository against an `America/New_York` session
connection with two expirations that collide on one wall clock, asserting distinct stored instants,
a sweep that deletes only the truly expired row, and an unchanged read-back.
`Given_a_pre_DMS_1430_OpenIddictToken_PostgreSQL_upgrade` exercises `0032` against a journaled
pre-upgrade database, pinning exact recovery, `NULL` preservation, replay safety, and both
unrecoverable cases above.

## Evidence Baseline

This record was evaluated against DMS
[`02d63b558`](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/commit/02d63b5580bc053731f9186e29b6a6f84b6bcefc)
(`main`, 2026-08-04) and the immutable reference revisions below.

- Ed-Fi-ODS at
  [`24fe66cfc`](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/tree/24fe66cfc04459ad6d6cac09d635d3c149b24669).
- Ed-Fi-ODS-Implementation at
  [`37ff595c1`](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS-Implementation/tree/37ff595c171b73e524d96b13103ef9ae01712beb).

The Time-Zone Independence section was added for DMS-1430 and evaluated separately, against
PostgreSQL 16.8 (`postgres:16.8-alpine`, the image the shipped stacks use) and Npgsql 8.0.4.
Its conversion, ambiguity-resolution, and migration claims were measured on that version rather than
derived from documentation; the ambiguity-resolution rule was checked across `America/New_York`,
`Europe/London`, `Australia/Sydney`, and `America/Santiago`.
