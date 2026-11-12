# Docker files details

## Dockerfile

The [Dockerfile](../src/dms/Dockerfile) in `src/dms` builds the Ed-Fi API (DMS) image
directly from source code, and
[Nuget.Dockerfile](../src/dms/Nuget.Dockerfile) build the image from the
pre-built NuGet packages. The source code version is primarily used in local
and end-to-end testing; the NuGet package version is the image published to
[Docker Hub](https://hub.docker.com/u/edfialliance). Running either image through the
repository's Docker Compose stack is local development and testing, not a hardened
deployment; see [Local development only](#local-development-only).

The runtime base image is `mcr.microsoft.com/dotnet/aspnet` on Alpine, running
.NET 10. This image is maintained by Microsoft and considered trusted. Use of the
Alpine distribution means that the final image will be smaller than with other
distributions, and the Alpine images tend to have fewer vulnerabilities.

## Environment Variables

The following environment variables are commonly used when starting the Ed-Fi API
container. `AppSettings__Datastore` defaults to `postgresql`. When the datastore
is `postgresql`, `src/dms/run.sh` waits for PostgreSQL by parsing host, port, and
username from `DATABASE_CONNECTION_STRING_ADMIN`.
The wait is skipped when `DATABASE_CONNECTION_STRING_ADMIN` is absent or yields no
host, and DMS starts without waiting.
`local-dms.compose.yml` sets a default for the variable. `published-dms.compose.yml` has no
default and requires a value; to keep it out of the container there, see
[Secrets on the shipped Compose files](./OPERATIONS.md#secrets-on-the-shipped-compose-files).

```none
AppSettings__Datastore=<Database engine: postgresql or mssql. Defaults to postgresql>
DATABASE_CONNECTION_STRING_ADMIN=<Admin PostgreSQL connection string used for the readiness check when AppSettings__Datastore is postgresql>
LOG_LEVEL=<serilog log level i.e. Information>
MASK_REQUEST_BODY_IN_LOGS=<Mask incoming HTTP POST and PUT request body structures in DEBUG logging, default value is true>
OAUTH_TOKEN_ENDPOINT=<Authentication service url>
BYPASS_TYPE_COERCION=<Boolean whether to bypass all schema-guided request value type coercion. This includes boolean strings, numeric strings, and boolean numeric aliases. Eg. "true" = true and 1 = true for schema boolean fields>
DATABASE_ISOLATION_LEVEL=<The System.Data.IsolationLevel to use for transaction locking. Eg. RepeatableRead>
ALLOW_IDENTITY_UPDATE_OVERRIDES=<Comma separated list of resource names that allow identity updates, overriding the default behavior to reject identity updates. Eg "accountabilityRatings,bellSchedules">
USE_LEGACY_DOCUMENT_ID_ORDERING_FOR_CHANGE_QUERIES=<Boolean, restores legacy DocumentId ordering for change-version-filtered queries (maps to AppSettings__UseLegacyDocumentIdOrderingForChangeQueries). Default: false>
FAILURE_RATIO=<decimal greater than 0 and at most 1 indicating the failure to success ratio at which the backend circuit breaker will break. Eg. 0.1 represents 10%. A value of 0 is rejected at startup.>
SAMPLING_DURATION_SECONDS=<This is the duration in seconds of the sampling over which failure ratios are assessed. From 0.5 to 86400 inclusive, and long enough to accumulate MINIMUM_THROUGHPUT calls at the deployment's quietest sustained request rate, or the circuit can never open. Eg. 120>
MINIMUM_THROUGHPUT=<Integer, this many actions or more must pass through the circuit in the time-slice, for statistics to be considered significant and the circuit-breaker to come into action. The minimum accepted value is 2, but keep FAILURE_RATIO multiplied by MINIMUM_THROUGHPUT above 1 so a single failed request cannot open the circuit.>
BREAK_DURATION_SECONDS=<The number of seconds a broken circuit will stay open before resetting. From 0.5 to 86400 inclusive. Eg. 30>
```

OTLP log export is also available, disabled by default, through the
`OtlpLogging` configuration section. The compose files forward the core keys
from `.env`: `OTLP_LOGGING_ENABLED`, `OTLP_LOGGING_ENDPOINT`,
`OTLP_LOGGING_PROTOCOL`, and `OTLP_LOGGING_DEPLOYMENT_ENVIRONMENT` for DMS,
with `CONFIGURATION_SERVICE_`-prefixed equivalents for the Configuration Service. The remaining `OtlpLogging`
keys are not forwarded from `.env`: `ServiceName`, `ServiceVersion`, and
`ServiceInstanceId` have sensible per-service defaults, and
`OtlpLogging__Headers__*` names are arbitrary keys. To override any of them,
add the corresponding `OtlpLogging__<Key>` variable to the compose service's
`environment` map directly. See
[LOGGING.md](./LOGGING.md#otlp-export) and
[CONFIGURATION.md](./CONFIGURATION.md#otlplogging) for details, including the
security guidance for the export path.

For example, you might have a `.env` file like the following:

```none
AppSettings__Datastore=postgresql
DATABASE_CONNECTION_STRING_ADMIN=host=localhost;port=5432;username=postgres;password=P@ssw0rd53;database=postgres;
LOG_LEVEL=Information
MASK_REQUEST_BODY_IN_LOGS=true
OAUTH_TOKEN_ENDPOINT=http://localhost:8080/oauth/token
BYPASS_TYPE_COERCION=false
DATABASE_ISOLATION_LEVEL=RepeatableRead
ALLOW_IDENTITY_UPDATE_OVERRIDES=""
FAILURE_RATIO=0.1
SAMPLING_DURATION_SECONDS=120
MINIMUM_THROUGHPUT=20
BREAK_DURATION_SECONDS=30
```

## Orchestration

We provide a Docker Compose based local deployment out of the box. See
[docker-compose/README.md](../eng/docker-compose/README.md) for detailed
instructions.

## Local development only

The `eng/docker-compose` stack exists for local development and testing. The values in
`eng/docker-compose/.env.example`, and the fallbacks in its Compose files, are public. The
published Docker Hub images are the release deliverable, but running them through this stack
does not make it a hardened deployment. The stock Compose files publish the host ports of the
services that hold credentials on `127.0.0.1` only; Swagger UI publishes its host port on all
interfaces. That limits access through those host ports to the local machine. It does not
isolate the services from other containers on the stack's Docker networks.

### Before a shared or exposed deployment

Replace every credential and key in this table, and review the settings listed after it.
Generate passwords, client secrets and encryption-key material with a cryptographically secure
random generator, then use those credentials in the connection strings.

| Setting | What it protects or does | When it applies |
| --- | --- | --- |
| `POSTGRES_PASSWORD` | PostgreSQL superuser password. `postgresql.compose.yml` falls back to a public value when it is unset. | PostgreSQL |
| `MSSQL_SA_PASSWORD` | SQL Server `sa` password. `mssql.compose.yml` falls back to a public value when it is unset. | SQL Server |
| `KEYCLOAK_ADMIN`, `KEYCLOAK_ADMIN_PASSWORD` | Keycloak administrator account. `keycloak.compose.yml` falls back to `admin` for both when they are unset. | Keycloak |
| `CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY` | Encrypts the data store connection strings the Configuration Service stores and DMS decrypts. The Configuration Service and DMS must use the same value. **Changing it after data exists makes the stored connection strings unreadable**, so choose it before the first start. | Always |
| `CONFIGURATION_SERVICE_IDENTITY_ENCRYPTION_KEY` | Protects the OpenIddict signing keys held in the Configuration Service database. Changing it after initialization breaks the active signing key; see [Signing key on rerun](../eng/docker-compose/README.md#signing-key-on-rerun--initdb). | Used by the self-contained identity provider. Required whenever `published-config.compose.yml` is included, even with Keycloak, which leaves it unused; a Keycloak invocation that omits that file does not require it. For teardown, see [What the published Compose files enforce](#what-the-published-compose-files-enforce). |
| `DATABASE_CONNECTION_STRING_ADMIN` | Carries database credentials. Inside the DMS container it is used only for the PostgreSQL readiness wait. `published-dms.compose.yml` requires a value; to keep it out of the container, see [Secrets on the shipped Compose files](./OPERATIONS.md#secrets-on-the-shipped-compose-files). | Required by the published Compose file; read at runtime only with PostgreSQL |
| `CONFIGURATION_SERVICE_DATABASE_CONNECTION_STRING` | The Configuration Service's own database connection string, password included. The example connects as the PostgreSQL superuser. When it is missing or empty, `published-config.compose.yml` and `local-config.compose.yml` fall back to a connection string for the bundled `dms-postgresql` service that uses the `postgres` account and `POSTGRES_PASSWORD`. Set it explicitly, with a least-privilege login. | Always |
| `DMS_CONFIGURATION_SERVICE_CLIENT_SECRET` | The client secret DMS presents to the Configuration Service. | Always |
| `CONFIGURATION_SERVICE_IDENTITY_CLIENT_SECRET` | The Configuration Service's own client secret. | Always |
| `DMS_BOOTSTRAP_ADMIN_CLIENT_SECRET` | The admin client secret the setup scripts register and authenticate with. The scripts fall back to a public value when it is unset. | Setup scripts |
| `CDC_DATABASE_PASSWORD` | The CDC worker's database password. | Change data capture |
| `CDC_WORKER_KEY` | **An identifier, not a credential.** It labels the CDC worker container and names its Kafka Connect group. It does not need to be secret. | Change data capture |

Other settings to review:

- Set `CONFIGURATION_SERVICE_ALLOW_LEGACY_CONNECTION_STRINGS=false` once every stored connection
  string has been re-encrypted; see [CONFIGURATION.md](./CONFIGURATION.md).
- Do not enable development certificates (`IdentitySettings__UseDevelopmentCertificates`).
- `.env.example` sets `DMS_JWT_REQUIRE_HTTPS_METADATA` and
  `CONFIGURATION_SERVICE_IDENTITY_REQUIRE_HTTPS` to `false` for the stack's private-network
  identity provider. Review both.
- Swagger UI publishes its host port (`DMS_SWAGGER_UI_PORT`) on all interfaces.
- Port exposure, TLS termination and reverse proxies are the operator's responsibility.

### What the published Compose files enforce

`published-config.compose.yml` and `published-dms.compose.yml` read
`CONFIGURATION_SERVICE_DATABASE_ENCRYPTION_KEY`, `CONFIGURATION_SERVICE_IDENTITY_ENCRYPTION_KEY`
and `DATABASE_CONNECTION_STRING_ADMIN` with `${VAR:?message}` and supply no fallback. When one
of them is missing or empty, `docker compose config`, `up` and `down` stop with an error that
names the service, the environment key and the variable. The guards apply to every service
definition in the invocation, not only to the services it starts: `start-published-dms.ps1 -DmsOnly`
starts only DMS, but it still composes `published-config.compose.yml` when the Configuration
Service is part of its compose set (with `-EnableConfig`, `-SeparateConfigDatabase`, bootstrap
mode, or the default self-contained identity provider) or when it finds an existing paired DMS and
Configuration Service proxy network, so the Configuration Service guards apply then too.
Teardown through
`start-published-dms.ps1 -d` is the one exception: it always includes
`published-config.compose.yml`, so for each of the three values that is missing it gives `stop`
and `down` an inert placeholder, which removal does not use, and then restores the environment.
A direct `docker compose down` with these files still needs all three, and startup through the
script still requires them. The check is for presence only:
example values, weak values and quoted whitespace-only values pass. Generate the encryption keys,
and the credentials inside the admin connection string, with a cryptographically secure random
generator. The local Compose files (`local-*.compose.yml`) keep their fallbacks.

## Identity Provider Configuration

The Ed-Fi API supports two identity provider modes: **keycloak** and **self-contained (OpenIddict)**. The relevant environment variables are:

```none
# keycloak
KEYCLOAK_OAUTH_TOKEN_ENDPOINT=http://dms-keycloak:8080/realms/edfi/protocol/openid-connect/token
KEYCLOAK_DMS_JWT_AUTHORITY=http://localhost:8045/realms/edfi
KEYCLOAK_CONFIGURATION_SERVICE_IDENTITY_AUTHORITY=http://dms-keycloak:8080/realms/edfi
KEYCLOAK_DMS_JWT_METADATA_ADDRESS=http://dms-keycloak:8080/realms/edfi/.well-known/openid-configuration

# Self-contained (OpenIddict)
SELF_CONTAINED_OAUTH_TOKEN_ENDPOINT=http://ed-fi-api-config:8081/connect/token
SELF_CONTAINED_DMS_JWT_AUTHORITY=http://ed-fi-api-config:8081
SELF_CONTAINED_DMS_JWT_METADATA_ADDRESS=http://ed-fi-api-config:8081/.well-known/openid-configuration
```

When running the setup script (e.g., `start-local-dms.ps1`), you can specify the identity provider using the `-IdentityProvider` parameter:

- If you use `-IdentityProvider keycloak`, the script will configure the service to use the keycloak endpoints above.
- If you omit the parameter or use `self-contained`, the service will use the self-contained (OpenIddict) endpoints.

The selected identity provider will determine the values for the following parameters:

- `OAUTH_TOKEN_ENDPOINT`
- `DMS_JWT_AUTHORITY`
- `DMS_JWT_METADATA_ADDRESS`
- `CONFIGURATION_SERVICE_IDENTITY_AUTHORITY`

These will be replaced with the corresponding keycloak or self-contained values based on your choice.

In keycloak mode the two authorities differ. `DMS_JWT_AUTHORITY` is Keycloak's public issuer, which
DMS requires to match the `issuer` in the metadata document exactly. `CONFIGURATION_SERVICE_IDENTITY_AUTHORITY` is
the in-network URL the Configuration Service calls. Self-contained mode uses one value for both.

> **Note:**
> Advanced identity provider configuration can also be set directly in the `appsettings.json` files for each service (`src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/appsettings.json` and `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/appsettings.json`).
> For most deployments, environment variables and the setup script are sufficient, but for custom scenarios you may edit these files directly.

**Relevant parameters in `appsettings.json` (config):**

| Parameter                          | Description                                                      | Example (Keycloak)                                   | Example (Self-contained)                      |
|------------------------------------|------------------------------------------------------------------|------------------------------------------------------|-----------------------------------------------|
| `IdentityProvider`                 | Selects the identity provider                                    | `keycloak`                                           | `self-contained`                              |
| `Authority`                        | Identity provider issuer. In self-contained mode, also the base URL for every advertised discovery endpoint: omit `AppSettings:PathBase` from this value, and keep DMS `JwtAuthentication:MetadataAddress` on the same origin (scheme, host, and port). DMS `JwtAuthentication:Authority` must exactly match the issuer. | `http://dms-keycloak:8080/realms/edfi` | `http://ed-fi-api-config:8081` |
| `EncryptionKey`                    | Key used for token encryption (self-contained only)              | _(not used)_                                         | `QWJjZGVmZ2hpamtsbW5vcHFyc3R1dnd4eXo0NTY3ODkwMTIz` (public example; generate your own) |
| `TokenCleanup:Enabled`            | Enables the expired-token cleanup sweep (self-contained only)    | _(not used)_                                         | `true`                                        |
| `TokenCleanup:IntervalMinutes`    | Minutes between expired-token cleanup sweeps (self-contained only) | _(not used)_                                       | `30`                                          |
| `BearerTokenPerClientLimit`        | Maximum active access tokens one limited client may hold (self-contained only; default `15`); further grants are rejected with HTTP 429. Exemption requires no live `dmscs.ApiClient` row and nonempty registered scopes consisting only of recognized Configuration Service admin scopes. Ed-Fi API clients remain limited. Below 1 disables enforcement globally. Compose variable: `CONFIGURATION_SERVICE_IDENTITY_BEARER_TOKEN_PER_CLIENT_LIMIT` | _(not used)_ | `15` |

See [CONFIGURATION.md](./CONFIGURATION.md#identity-provider-configuration) for the
enforcement boundary, the Configuration Service's 429, and DMS's canonical `Too Many Tokens` 429 response.
Explicit operator values override the default; DMS's non-API Configuration Service credential
and Configuration Service admin/bootstrap clients are exempt when they have no live `ApiClient` row and
their nonempty registered scopes consist only of recognized Configuration Service admin scopes.

**JwtAuthentication parameters in `appsettings.json` (dms):**

| Parameter                  | Description                                         | Example (Keycloak)                                   | Example (Self-contained)                      |
|---------------------------|-----------------------------------------------------|------------------------------------------------------|-----------------------------------------------|
| `Authority`               | URL of the identity provider's authority (issuer). It must equal the metadata document's `issuer` exactly | `http://localhost:8045/realms/edfi`              | `http://ed-fi-api-config:8081`              |
| `MetadataAddress`         | OpenID Connect metadata endpoint. The document's `jwks_uri` must share this address's origin | `http://dms-keycloak:8080/realms/edfi/.well-known/openid-configuration` | `http://ed-fi-api-config:8081/.well-known/openid-configuration` |

Refer to the API service's `appsettings.json` for additional options and defaults.
