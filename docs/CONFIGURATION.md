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

| Parameter                        | Description                                                                                                                                                                                                                   |
| --------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Datastore                        | The primary datastore used by the DataManagementService. Valid values are `postgresql` and `mssql`                                                                                                        |
| DeployDatabaseOnStartup          | When `true` the database will be created and initialized on startup.                                                                                            |
| BypassStringTypeCoercion         | String type coercion attempts to coerce boolean and numeric strings to their proper type on `POST` and `PUT` requests. For example `"true"` becomes `true`. This setting bypasses that for performance.   |
| AllowIdentityUpdateOverrides     | Comma separated list of resource names that allow identity updates, overriding the default behavior to reject identity updates.                                                                           |
| MaskRequestBodyInLogs            | Controls whether to mask HTTP request bodies in log statements to avoid potentially logging PII. This setting only applies to `DEBUG` logging where requests are logged.                                  |
| UseApiSchemaPath                 | When set to `true`, the application will use the `UseApiSchemaPath` configuration to load core data standard and extension artifacts. The `ApiSchemaDownloader` CLI can be used to download and extract the published `ApiSchema` packages. |
| ApiSchemaPath                    | Specifies the directory where core and extension ApiSchema.json files are located. The ApiSchemaDownloader CLI can be used to download and extract the published ApiSchema packages. |
| DomainsExcludedFromOpenApi       | Comma separated list of domain names to exclude from OpenAPI documentation generation. Domains listed here will not appear in the generated OpenAPI specifications. Case insensitive. |
| IdentityProvider                 | Specifies the authentication provider. Valid values are `keycloak` (to use Keycloak's authentication) and `self-contained` (to use self-contained authentication). When using `self-contained`, you must also provide a value for `IdentitySettings:EncryptionKey`. Default: self-contained |
| RouteQualifierSegments           | Comma separated list of route qualifier context segments as defined by `dataStoreContexts` in Configuration Service. Example: "districtId,schoolYear" |
| MultiTenancy                     | When `true`, enables multi-tenancy mode where the tenant identifier is extracted from the URL route. Default: `false` |
| EnableApplicationResetEndpoint   | When `true`, enables the `/v3/applications/{id}/reset-credential` endpoint in the Configuration Service, allowing application credentials to be reset via API. When `false`, the endpoint is not registered and will return a 404 (Not Found) response. <br>**Recommended:** Set to `false` if you need to support multiple API clients per application, as enabling this endpoint may interfere with multi-client scenarios. Default: `false` |

## Configuration Service AppSettings

The following parameters apply to the DMS Configuration Service (`appsettings.json`).

| Parameter                    | Description                                                                                                                                                                                               |
| ---------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Datastore                    | The primary datastore used by the Configuration Service. Valid values are `postgresql` and `mssql`                                                                                                        |
| DeployDatabaseOnStartup      | When `true` the database will be created and initialized on startup.                                                                                                                                      |
| IdentityProvider             | Specifies the authentication provider. Valid values are `keycloak` and `self-contained`. Default: `self-contained`                                                                                        |
| MultiTenancy                 | When `true`, enables multi-tenancy support in the Configuration Service. Default: `false`                                                                                                                 |
| PathBase                     | Segment of the URL to use as base for all requests.                                                                                                                                                       |
| TokenRequestTimeoutSeconds   | Timeout in seconds for token requests. Default: `30`                                                                                                                                                      |
| UseReverseProxyHeaders       | When `true`, the application will respect reverse proxy headers for URL generation. Default: `false`                                                                                                      |

## DatabaseOptions

| Parameter      | Description                                                                                                                                                              |
| -------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| IsolationLevel | The `System.Data.IsolationLevel` to use for transaction locks. See [documentation](https://learn.microsoft.com/en-us/dotnet/api/system.data.isolationlevel?view=net-8.0) |

## ConfigurationServiceSettings

These settings configure how the DMS API connects to the Configuration Service to retrieve claim sets, data stores, and other metadata. `EncryptionKey` must match CMS `DatabaseSettings:EncryptionKey`.

| Parameter              | Description                                                                                                                                                              |
| ---------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| BaseUrl                | The base URL of the Configuration Service. Example: `http://dms-config-service:8081`                                                                                     |
| ClientId               | The client identifier (client ID) used to access the Configuration Service endpoints.                                                                                    |
| ClientSecret           | The client secret associated with the client ID for accessing the Configuration Service endpoints. Set via the `CONFIG_SERVICE_CLIENT_SECRET` environment variable. Must satisfy the CMS client-secret rules described in [IdentitySettings.ClientSecretValidation](#identitysettingsclientsecretvalidation). |
| EncryptionKey         | Key used to encrypt and decrypt Configuration Service connection strings. Set via the `DMS_CONFIG_DATABASE_ENCRYPTION_KEY` environment variable and must match CMS `DatabaseSettings:EncryptionKey`. Used by `provision-dms-schema.ps1` to decrypt protected CMS datastore connection strings. Must be non-empty; see the note below for valid-value semantics. |
| Scope                  | The authorization scope required for accessing the Configuration Service endpoints. Example: `edfi_admin_api/authMetadata_readonly_access`                               |
| CacheExpirationMinutes | The duration in minutes before cached claim sets and other metadata expire and are refreshed from the Configuration Service.                                             |

> [!NOTE]
> **Shared key.** In the provided Docker Compose files, a single
> `DMS_CONFIG_DATABASE_ENCRYPTION_KEY` value feeds both the CMS
> `DatabaseSettings__EncryptionKey` and the DMS
> `ConfigurationServiceSettings__EncryptionKey`. CMS encrypts datastore connection
> strings with this key and DMS decrypts them with the same key (and
> `provision-dms-schema.ps1` decrypts with it too), so all three must be configured
> with an identical value.
>
> **Valid values.** The value must be non-empty. The AES-256 key is derived from the
> UTF-8 bytes of the configured text, right-padded with `0` to 32 characters and then
> truncated to the first 32 characters (see CMS `ConnectionStringEncryptionService`,
> DMS `ConnectionStringDecryptionService`, and `provision-dms-schema.ps1`).
> Consequences operators must account for:
> - Only the first 32 characters are significant; any characters beyond 32 are ignored.
> - Values shorter than 32 characters are zero-padded — accepted, but weakens the key.
> - Use ASCII characters. A 32-character ASCII string yields exactly 32 key bytes;
>   multi-byte (non-ASCII) characters push the UTF-8 length past 32 bytes and break
>   AES key initialization.
>
> Recommended: a 32-character ASCII string.

## IdentitySettings.ClientSecretValidation

These settings configure the allowed client-secret length range used by CMS registration validation and by CMS startup validation for configured client secrets.

| Parameter       | Description                                                                                                           |
| --------------- | --------------------------------------------------------------------------------------------------------------------- |
| MinimumLength   | Minimum allowed client-secret length. Default: `32`                                                                   |
| MaximumLength   | Maximum allowed client-secret length. Default: `128`                                                                  |

`IdentitySettings.ClientSecretValidation` controls the accepted size range used by CMS registration, generated secrets, and startup validation. CMS startup also requires configured client secrets to satisfy the same lowercase/uppercase/number/special-character complexity rules enforced by registration, where supported special characters are `!@#$%^&*()-_=+[]{}:;,.?`. The bounds are set from the `DMS_CONFIG_IDENTITY_CLIENT_SECRET_MINIMUM_LENGTH` / `DMS_CONFIG_IDENTITY_CLIENT_SECRET_MAXIMUM_LENGTH` environment variables.

> [!IMPORTANT]
> Two different secrets are validated on two different paths — do not conflate them:
>
> - **`DMS_CONFIG_IDENTITY_CLIENT_SECRET`** is the CMS's own client secret
>   (`IdentitySettings:ClientSecret`, client `DmsConfigurationService`). CMS validates it at
>   **startup** via `IdentitySettingsValidator`; when it is invalid (for example, shorter than
>   the configured minimum), `ReportInvalidConfiguration` in `Program.cs` returns true,
>   `InitializeDatabase` is skipped, and the DbUp migrations that create the OpenIddict tables
>   never run — causing `start-local-dms.ps1` to fail.
> - **`CONFIG_SERVICE_CLIENT_SECRET`** is the DMS-to-CMS client secret
>   (`ConfigurationServiceSettings:ClientSecret`, client `CMSReadOnlyAccess`) that DMS uses at
>   runtime to obtain CMS tokens. It is validated when that client is **registered** by the
>   setup scripts, not by the CMS startup validator.
>
> Both secrets must satisfy the length and complexity rules above. During initial identity
> provisioning the local startup scripts register each client from its env-file secret and pass
> the env-file length bounds (`DMS_CONFIG_IDENTITY_CLIENT_SECRET_MINIMUM_LENGTH` /
> `_MAXIMUM_LENGTH`) to `setup-keycloak.ps1` / `setup-openiddict.ps1`, so a secret that is valid
> for CMS is not rejected by the setup scripts' own default 32/128 bounds.
>
> Registration applies only to clients that do not yet exist: `setup-keycloak.ps1` warns and
> skips a client that is already present, and `setup-openiddict.ps1` inserts with
> `ON CONFLICT (ClientId) DO NOTHING`. Changing one of these secrets therefore does **not**
> update an already-registered client. To apply a new value, recreate the identity state first —
> run `teardown-local-dms.ps1` and set up again, or drop the Keycloak realm / `dmscs` OpenIddict
> tables — then start with the new secret.

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

For most deployments, environment variables and the setup script are sufficient,
but for custom scenarios you may edit these files directly.

By default, the configuration uses the self-contained (OpenIddict) identity
provider. The `appsettings.json` files are pre-configured for self-contained
endpoints, and the setup scripts will use self-contained unless you explicitly
specify `keycloak` as the identity provider.

If you wish to use Keycloak as the identity provider, you must update the
relevant environment variables or appsettings to set `IdentityProvider` to
`keycloak` and configure the appropriate Keycloak endpoints.

### Relevant parameters in `appsettings.json` (Configuration Service)

| Parameter        | Description                                                      | Example (Keycloak)                                   | Example (Self-contained)                      |
|------------------|------------------------------------------------------------------|------------------------------------------------------|-----------------------------------------------|
| `AppSettings.IdentityProvider` | Selects the identity provider                                    | `keycloak`                                           | `self-contained`                              |
| `IdentitySettings.Authority`        | URL of the identity provider's authority (issuer)                | `http://dms-keycloak:8080/realms/edfi`              | `http://dms-config-service:8081`              |
| `IdentitySettings.EncryptionKey`    | Key used for token encryption (self-contained only)              | _(not used)_                                         | `QWJjZGVmZ2hpamtsbW5vcHFyc3R1dnd4eXo0NTY3ODkwMTIz` |

### JwtAuthentication parameters in `appsettings.json` (DMS API Service)

| Parameter         | Description                                         | Example (Keycloak)                                   | Example (Self-contained)                      |
|-------------------|-----------------------------------------------------|------------------------------------------------------|-----------------------------------------------|
| `AppSettings.AuthenticationService`       | URL of the identity provider's authority (issuer)   | `http://dms-keycloak:8080/realms/edfi/protocol/openid-connect/token`              | `http://dms-config-service:8081/connect/token`              |
| `JwtAuthentication.Authority`       | URL of the identity provider's authority (issuer)   | `http://dms-keycloak:8080/realms/edfi`              | `http://dms-config-service:8081`              |
| `JwtAuthentication.MetadataAddress` | OpenID Connect metadata endpoint                    | `http://dms-keycloak:8080/realms/edfi/.well-known/openid-configuration` | `http://dms-config-service:8081/.well-known/openid-configuration` |

Refer to the API service's `appsettings.json` for additional options and defaults.
