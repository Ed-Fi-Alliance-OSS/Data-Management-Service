# AppSettings Configuration

The sections below describe custom configuration options in the `appSettings` file.

## AppSettings

| Parameter               | Description                                                                                                    |
| ----------------------- | -------------------------------------------------------------------------------------------------------------- |
| DatabaseEngine          | The database engine used by the DataManagementService. Valid values are `postgresql` and `mssql`               |
| DeployDatabaseOnStartup | When `true` the database in `ConnectionStrings:DatabaseConnection` will be created and initialized on startup. |

## RateLimit

Basic rate limiting can be applied by supplying a `RateLimit` object in the
`appsettings.json` file. If no `RateLimit` object is supplied, rate limiting is
not configured for the application. Rate limiting (when applied) will be set
globally and apply to all application endpoints.

The `RateLimit` object should have the following parameters.

| Parameter     | Description                                                                                                                                                                                                                                                                |
| ------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `PermitLimit` | The number of requests permitted within the window time span. This will be the number of requests, per hostname permitted per timeframe (`Window`). Must be > 0.                                                                                                           |
| `Window`      | The number of seconds before the `PermitLimit` is reset. Must be > 0.                                                                                                                                                                                                      |
| `QueueLimit`  | The maximum number of requests that can be Queued once `PermitLimit`s are exhausted. These requests will wait until the `Window` expires and will be processed FIFO. When the queue is exhausted, clients will receive a `429` `Too Many Requests` response. Must be >= 0. |
