# Configuration

The sections below describe custom configuration options in the `appSettings.json`
file.

> [!NOTE]
> Environment Variables are supported and will take priority over
> `appsettings.json`. No special prefix is required on environment variable
> names. The standard convention for reading hierarchial keys from environment is
> to use a double underscore `__` separator. For example
> `AppSettings__Datastore=mssql`

## AppSettings

| Parameter                    | Description                                                                                                                                                                                               |
| ---------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Datastore                    | The primary datastore used by the DataManagementService. Valid values are `postgresql` and `mssql`                                                                                                        |
| QueryHandler                 | The query handling datastore used by the DataManagementService. Valid values: `postgresql`                                                                                            |
| DeployDatabaseOnStartup      | When `true` the database in `ConnectionStrings:DatabaseConnection` will be created and initialized on startup.                                                                                            |
| BypassStringTypeCoercion     | String type coercion attempts to coerce boolean and numeric strings to their proper type on `POST` and `PUT` requests. For example `"true"` becomes `true`. This setting bypasses that for performance.   |
| AllowIdentityUpdateOverrides | Comma separated list of resource names that allow identity updates, overriding the default behavior to reject identity updates.                                                                           |
| MaskRequestBodyInLogs        | Controls whether to mask HTTP request bodies in log statements to avoid potentially logging PII. This setting only applies to `DEBUG` logging where requests are logged.                                  |
| UseApiSchemaPath             | When set to `true`, the application will use the `UseApiSchemaPath` configuration to load core data standard and extension artifacts. The `ApiSchemaDownloader` CLI can be used to download and extract the published `ApiSchema` packages. |
| ApiSchemaPath                | Specifies the directory where core and extension ApiSchema.json files are located. The ApiSchemaDownloader CLI can be used to download and extract the published ApiSchema packages. |
| DomainsExcludedFromOpenApi   | Comma separated list of domain names to exclude from OpenAPI documentation generation. Domains listed here will not appear in the generated OpenAPI specifications. Case insensitive. |
| IdentityProvider             | Specifies the authentication provider. Valid values are `keycloak` (to use Keycloak's authentication) and `self-contained` (to use self-contained authentication). When using `self-contained`, you must also provide a value for `IdentitySettings:EncryptionKey`. |

## ConnectionStrings

| Parameter          | Description                                                                   |
| ------------------ | ----------------------------------------------------------------------------- |
| DatabaseConnection | The database connection string to the primary datastore                       |
| OpenSearchUrl      | The OpenSearch endpoint URL, if OpenSearch is being used as the query handler |

## DatabaseOptions

| Parameter      | Description                                                                                                                                                              |
| -------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| IsolationLevel | The `System.Data.IsolationLevel` to use for transaction locks. See [documentation](https://learn.microsoft.com/en-us/dotnet/api/system.data.isolationlevel?view=net-8.0) |

## RateLimit

Basic rate limiting can be applied by supplying a `RateLimit` object in the
`appsettings.json` file. If no `RateLimit` object is supplied, rate limiting is
not configured for the application. Rate limiting (when applied) will be set
globally and apply to all application endpoints.

The `RateLimit` object should have the following parameters.

| Parameter   | Description                                                                                                                                                                                                                                                                |
| ----------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| PermitLimit | The number of requests permitted within the window time span. This will be the number of requests, per hostname permitted per timeframe (`Window`). Must be > 0.                                                                                                           |
| Window      | The number of seconds before the `PermitLimit` is reset. Must be > 0.                                                                                                                                                                                                      |
| QueueLimit  | The maximum number of requests that can be Queued once `PermitLimit`s are exhausted. These requests will wait until the `Window` expires and will be processed FIFO. When the queue is exhausted, clients will receive a `429` `Too Many Requests` response. Must be >= 0. |

## Identity Provider Configuration

For most deployments, environment variables and the setup script are sufficient, but for custom scenarios you may edit these files directly.

By default, the configuration uses Keycloak as the identity provider. The `appsettings.json` files are pre-configured for Keycloak endpoints, and the setup scripts will use Keycloak unless you explicitly specify `self-contained` as the identity provider.

If you wish to use the self-contained (OpenIddict) option, you must update the relevant environment variables or appsettings .

### Relevant parameters in `appsettings.json` (Configuration Service)

| Parameter        | Description                                                      | Example (Keycloak)                                   | Example (Self-contained)                      |
|------------------|------------------------------------------------------------------|------------------------------------------------------|-----------------------------------------------|
| `IdentityProvider` | Selects the identity provider                                    | `keycloak`                                           | `self-contained`                              |
| `Authority`        | URL of the identity provider's authority (issuer)                | `http://dms-keycloak:8080/realms/edfi`              | `http://dms-config-service:8081`              |
| `EncryptionKey`    | Key used for token encryption (self-contained only)              | _(not used)_                                         | `QWJjZGVmZ2hpamtsbW5vcHFyc3R1dnd4eXo0NTY3ODkwMTIz` |

### JwtAuthentication parameters in `appsettings.json` (DMS API Service)

| Parameter         | Description                                         | Example (Keycloak)                                   | Example (Self-contained)                      |
|-------------------|-----------------------------------------------------|------------------------------------------------------|-----------------------------------------------|
| `Authority`       | URL of the identity provider's authority (issuer)   | `http://dms-keycloak:8080/realms/edfi`              | `http://dms-config-service:8081`              |
| `MetadataAddress` | OpenID Connect metadata endpoint                    | `http://dms-keycloak:8080/realms/edfi/.well-known/openid-configuration` | `http://dms-config-service:8081/.well-known/openid-configuration` |

Refer to the API service's `appsettings.json` for additional options and defaults.
