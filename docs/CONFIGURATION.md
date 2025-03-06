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
| QueryHandler                 | The query handling datastore used by the DataManagementService. Valid values are `postgresql` and `opensearch`                                                                                            |
| DeployDatabaseOnStartup      | When `true` the database in `ConnectionStrings:DatabaseConnection` will be created and initialized on startup.                                                                                            |
| BypassStringTypeCoercion     | String type coercion attempts to coerce boolean and numeric strings to their proper type on `POST` and `PUT` requests. For example `"true"` becomes `true`. This setting bypasses that for performance.   |
| AllowIdentityUpdateOverrides | Comma separated list of resource names that allow identity updates, overriding the default behavior to reject identity updates.                                                                           |
| MaskRequestBodyInLogs        | Controls whether to mask HTTP request bodies in log statements to avoid potentially logging PII. This setting only applies to `DEBUG` logging where requests are logged.                                  |
| UseLocalApiSchemaJson        | When `true` the application will use `src\core\EdFi.DataManagementService.Core\ApiSchema\ApiSchema.json` instead of the published package. This file is gitignored and should be manually added if needed |
| UseApiSchemaPath             | Set to true when the user has downloaded the EdFi.DataStandard52.ApiSchema.Core and EdFi.DataStandard52.ApiSchema.TPDM NuGet packages into the EdFi.DataStandard52.ApiSchema folder using the EdFi.DataManagementService.ApiSchemaDownloader project |
| ApiSchemaPath                | Specifies the local path to the EdFi.DataStandard52.ApiSchema folder where both EdFi.DataStandard52.ApiSchema.Core and EdFi.DataStandard52.ApiSchema.TPDM NuGet packages are downloaded |

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
