# AppSettings Configuration

The sections below describe custom configuration options in the `appSettings.json` file.

## AppSettings

| Parameter                    | Description                                                                                                                                                                                             |
| ---------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Datastore                    | The primary datastore used by the DataManagementService. Valid values are `postgresql` and `mssql`                                                                                                      |
| QueryHandler                 | The query handling datastore used by the DataManagementService. Valid values are `postgresql` and `opensearch`                                                                                          |
| DeployDatabaseOnStartup      | When `true` the database in `ConnectionStrings:DatabaseConnection` will be created and initialized on startup.                                                                                          |
| BypassStringTypeCoercion     | String type coercion attempts to coerce boolean and numeric strings to their proper type on `POST` and `PUT` requests. For example `"true"` becomes `true`. This setting bypasses that for performance. |
| AllowIdentityUpdateOverrides | Comma separated list of resource names that allow identity updates, overriding the default behavior to reject identity updates.                                                                         |

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
