# Docker files details

## Dockerfile

The [Dockerfile](../src/Dockerfile) in `src` builds the Ed-Fi Data Management
Service (DMS) image directly from source code, and
[Nuget.Dockerfile](../src/dms/Nuget.Dockerfile) build the image from the
pre-built NuGet packages. Both versions include both the API application and the
database installer. The source code version is primarily used in local and
end-to-end testing; the NuGet package version is intended for production-like
environments and for publishing to [Docker
Hub](https://hub.docker.com/u/edfialliance).

The runtime base image is `mcr.microsoft.com/dotnet/aspnet` on Alpine, running
.NET 8. This image is maintained by Microsoft and considered trusted. Use of the
Alpine distribution means that the final image will be smaller than with other
distributions, and the Alpine images tend to have fewer vulnerabilities.

> [!TIP]
> There is no separate container for the database images. If you start the DMS
> container with `NEEDS_DATABASE_SETUP=true` _and_ with valid administrative
> credentials, it will install the database tables instead of running the API
> application. You can start any PostgreSQL container, or run PostgreSQL at
> any other location that is accessible from inside your Docker network.

## Environment Variables

The following environment variables are needed when starting the Data Management
Service container.

```none
NEED_DATABASE_SETUP=<Flag (true or false) to decide whether the DMS database setup needs to be executed as part of the container setup>
POSTGRES_ADMIN_USER=<Admin user to use with database setup>
POSTGRES_ADMIN_PASSWORD=<Admin password to use with database setup>
POSTGRES_PORT=<Port for postgres server Eg. 5432>
POSTGRES_HOST=<DNS or IP address of the PostgreSQL Server, i.e. sql.somedns.org Eg. 172.25.32.1>
LOG_LEVEL=<serilog log level i.e. Information>
MASK_REQUEST_BODY_IN_LOGS:<Mask incoming HTTP POST and PUT request body structures in DEBUG logging, default value is true>
OAUTH_TOKEN_ENDPOINT=<Authentication service url>
BYPASS_STRING_COERCION=<Boolean whether to bypass coercion of boolean and numeric values represented as strings to their natural type. Eg. "true" = true>
DATABASE_ISOLATION_LEVEL=<The System.Data.IsolationLevel to use for transaction locking. Eg. RepeatableRead>
ALLOW_IDENTITY_UPDATE_OVERRIDES=<Comma separated list of resource names that allow identity updates, overriding the default behavior to reject identity updates. Eg "accountabilityRatings,bellSchedules">
DATABASE_CONNECTION_STRING=<The non-admin database connection string>
FAILURE_RATIO=<decimal between 0 and 1 indicating the failure to success ratio at which the backend circuit breaker will break. Eg. 0.1 represents 10%>
SAMPLING_DURATION_SECONDS=<This is the duration in seconds of the sampling over which failure ratios are assessed. Eg. 10>
MINIMUM_THROUGHPUT=<Integer, this many actions or more must pass through the circuit in the time-slice, for statistics to be considered significant and the circuit-breaker to come into action. The minimum value is 2.>
BREAK_DURATION_SECONDS=<The number of seconds a broken circuit will stay open before resetting. Eg. 30>
```

For example, you might have a `.env` file like the following:

```none
NEED_DATABASE_SETUP=true
POSTGRES_ADMIN_USER=postgres
POSTGRES_ADMIN_PASSWORD=P@ssw0rd53
POSTGRES_PORT=5432
POSTGRES_HOST=localhost
LOG_LEVEL=Information
MASK_REQUEST_BODY_IN_LOGS=true
OAUTH_TOKEN_ENDPOINT=http://localhost:8080/oauth/token
BYPASS_STRING_COERCION=false
DATABASE_ISOLATION_LEVEL=RepeatableRead
ALLOW_IDENTITY_UPDATE_OVERRIDES=""
DATABASE_CONNECTION_STRING=host=localhost;port=5432;username=dms;password=P@ssw0rd;database=edfi_datamanagementservice;
FAILURE_RATIO=0.1
SAMPLING_DURATION_SECONDS=10
MINIMUM_THROUGHPUT=2
BREAK_DURATION_SECONDS=30
```

## Orchestration

We provide a Docker Compose based local deployment out of the box. See
[docker-compose/README.md](../eng/docker-compose/README.md) for detailed
instructions.

## Identity Provider Configuration

The Data Management Service supports two identity provider modes: **keycloak** and **self-contained (OpenIddict)**. The relevant environment variables are:

```none
# keycloak
KEYCLOAK_OAUTH_TOKEN_ENDPOINT=http://dms-keycloak:8080/realms/edfi/protocol/openid-connect/token
KEYCLOAK_DMS_JWT_AUTHORITY=http://dms-keycloak:8080/realms/edfi
KEYCLOAK_DMS_JWT_METADATA_ADDRESS=http://dms-keycloak:8080/realms/edfi/.well-known/openid-configuration

# Self-contained (OpenIddict)
SELF_CONTAINED_OAUTH_TOKEN_ENDPOINT=http://dms-config-service:8081/connect/token
SELF_CONTAINED_DMS_JWT_AUTHORITY=http://dms-config-service:8081
SELF_CONTAINED_DMS_JWT_METADATA_ADDRESS=http://dms-config-service:8081/.well-known/openid-configuration
```

When running the setup script (e.g., `start-local-dms.ps1`), you can specify the identity provider using the `-IdentityProvider` parameter:

- If you use `-IdentityProvider keycloak`, the script will configure the service to use the keycloak endpoints above.
- If you omit the parameter or use `self-contained`, the service will use the self-contained (OpenIddict) endpoints.

The selected identity provider will determine the values for the following parameters:

- `OAUTH_TOKEN_ENDPOINT`
- `DMS_JWT_AUTHORITY`
- `DMS_JWT_METADATA_ADDRESS`
- `DMS_CONFIG_IDENTITY_AUTHORITY`

These will be replaced with the corresponding keycloak or self-contained values based on your choice.

> **Note:**
> Advanced identity provider configuration can also be set directly in the `appsettings.json` files for each service (`src/dms/frontend/EdFi.DataManagementService.Frontend.AspNetCore/appsettings.json` and `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/appsettings.json`).
> For most deployments, environment variables and the setup script are sufficient, but for custom scenarios you may edit these files directly.

**Relevant parameters in `appsettings.json` (config):**

| Parameter                          | Description                                                      | Example (Keycloak)                                   | Example (Self-contained)                      |
|------------------------------------|------------------------------------------------------------------|------------------------------------------------------|-----------------------------------------------|
| `IdentityProvider`                 | Selects the identity provider                                    | `keycloak`                                           | `self-contained`                              |
| `Authority`                        | URL of the identity provider's authority (issuer)                | `http://dms-keycloak:8080/realms/edfi`              | `http://dms-config-service:8081`              |
| `EncryptionKey`                    | Key used for token encryption (self-contained only)              | _(not used)_                                         | `QWJjZGVmZ2hpamtsbW5vcHFyc3R1dnd4eXo0NTY3ODkwMTIz` |

**JwtAuthentication parameters in `appsettings.json` (dms):**

| Parameter                  | Description                                         | Example (Keycloak)                                   | Example (Self-contained)                      |
|---------------------------|-----------------------------------------------------|------------------------------------------------------|-----------------------------------------------|
| `Authority`               | URL of the identity provider's authority (issuer)   | `http://dms-keycloak:8080/realms/edfi`              | `http://dms-config-service:8081`              |
| `MetadataAddress`         | OpenID Connect metadata endpoint                    | `http://dms-keycloak:8080/realms/edfi/.well-known/openid-configuration` | `http://dms-config-service:8081/.well-known/openid-configuration` |

Refer to the API service's `appsettings.json` for additional options and defaults.
