# Logging in the Ed-Fi API

## Logging Principles

These principles seek to balance the goals of providing sufficient information
for an administrator to understand the health of the system and understand user
interaction with the system with the equally important goals of protecting
sensitive data and avoiding excessive log storage size.

* Use structured logging for integration into log-monitoring applications
  (LogStash, Splunk, CloudWatch, etc.).
* Do not log sensitive data.
* Use an appropriate log level.
* Include a correlation / trace ID wherever possible, with the ID being unique
  to each HTTP request.
* Provide enough information to help someone understand what is going on in the
  system, and where, but
* Be careful not to make the log entries too large, thus becoming a storage
  problem.
* Logs will be written to the console, at minimum.
* If any transformation or business logic is necessary for writing an info or
  debug message, use the utility `IsDebugEnabled` and `IsInfoEnabled` functions
  first before executing that logic.

### Log Levels

The DMS applications will utilize the following levels when logging messages.
These levels help the reader to understand if any remedial action is needed, and
they allow the administrator to tune the amount of data being logged.

* `FATAL`
  * The application should shut down after logging a message, if possible.
  * Applies when:
    * System is unable to startup.
  * Response:
    * Investigate in detail. Is there a service down? Is there an application bug?
    * Submit a bug report with the Ed-Fi team if appropriate, through the [Ed-Fi
      Community Hub](https://success.ed-fi.org).
* `ERROR`
  * Applies when:
    * Something unexpected occurred in code, which interrupts service in some
      way, or
    * An error occurred in an external service, for example, a database server
      was down.
  * Response:
    * Submit a bug report with the Ed-Fi team if appropriate, through the [Ed-Fi
      Community Hub](https://success.ed-fi.org).
    * Investigate the external service; report error to service provider if
      applicable.
* `WARN`
  * Applies when:
    * Something unexpected occurred in code, but the system is able to recover
      and continue.
  * Response:
    * If you see this happening frequently, consider submitting a detailed
      report in the [Ed-Fi Community Hub](https://success.ed-fi.org). There may
      be an opportunity for improving the code and/or providing better error
      handling for the situation.
* `INFO`
  * Applies when:
    * Displays information about the state of an HTTP request, for example,
      which function is currently processing the request.
  * Response:
    * Generally, none required.
* `DEBUG`
  * Displays additional information about the state of an HTTP request and/or
    state of responses from external services.
  * Includes anonymized HTTP request payloads for debugging integration
    problems.

> [!TIP]
> **Anonymized Payloads** — When vendor API clients encounter data integration
> failures, the support teams often want to know about the failed payload, and
> this information is not always readily available from the maintainers of the
> client application. Providing anonymized payloads meets the support need "half
> way" in that the system administrator and/or a support team member can see the
> _structure_ of the messages sent, without being able to see the detailed
> _content_. In many cases, this will be sufficient to understand why a request
> failed.

### Examples

These examples are general guidelines and not 100% exhaustive.

#### Fatal

* Missing required configuration information
* Out of memory or disk space

#### Error

* Unhandled null reference
* Database connection / transaction failure after exhausting retry attempts

#### Warning

* A database connection / transaction failure occurred, but was recovered with
  an automatic retry

#### Informational

* Received an HTTP request
  * URL
  * clientId
  * traceId
  * verb
  * contentType
  * _do not include the payload_
* Responded to an HTTP request
  * URL
  * response code
  * clientId
  * duration from time of receipt of HTTP request to response (milliseconds)
  * _do not include the payload_
* Process startup and shutdown
* Database created

#### Debug

* Received an HTTP request → add anonymized payload
  * Replace potentially sensitive string and numeric data with `null` before
    logging.
  * Could hard code restrictions to "known-to-be-sensitive" attributes, for
    example attributes on Student, Parent, and Staff.
  * However, that could fall short with a change to the data model.
  * Therefore, it will be safest to replace all string and numeric data.
  * One potential exception: descriptors.
    * Descriptor values will never contain sensitive data.
    * Since the other string and numeric values are anonymized, the descriptor
      value itself does not provide a side channel to sensitive information.
    * There is value to having this when debugging failed HTTP requests.
* Responded to an HTTP request → add payload
  * Will require anonymization of the natural key fields when reporting a
    referential integrity problem.
* Entered a function
* About to connect to a service or run through an interesting algorithm
* Received information back from a service
  * Metadata only

## Logging Configuration

### Framework

DMS uses **Serilog 4.x** as its sole logging framework, replacing all
Microsoft.Extensions.Logging providers at startup. Configuration is driven by
`Serilog.Settings.Configuration`, which reads from `appsettings.json`.

### Default Sinks

Two sinks are active by default:

| Sink        | Details                                    |
| ----------- | ------------------------------------------ |
| **Console** | `{Timestamp} {Level} {Message}{Exception}` |
| **File**    | `./logs/.log`, rolls daily                 |

> [!WARNING]
> Only the two built-in sinks (Console, File) are available. Adding
> Elasticsearch, Seq, Splunk, Datadog, etc. requires recompiling with the
> appropriate NuGet packages.
>
> Design document [Runtime Serilog Sink Plugin
> Loading](../reference/design/serilog-plugin-loading.md) describes a proposal
> for avoiding recompile.

### Key Configuration Settings

| Key                                 | Default                                       | Purpose                                   |
| ----------------------------------- | --------------------------------------------- | ----------------------------------------- |
| `Serilog:MinimumLevel:Default`      | `Information`                                 | Global log level                          |
| `Serilog:WriteTo`                   | Console + File                                | Sink list                                 |
| `Serilog:Using`                     | `[Serilog.Sinks.File, Serilog.Sinks.Console]` | Assembly references for sinks             |
| `Serilog:Enrich`                    | `[FromLogContext]`                            | Log enrichers                             |
| `AppSettings:MaskRequestBodyInLogs` | `true`                                        | Mask request body values at `DEBUG` level |
| `AppSettings:CorrelationIdHeader`   | `"correlationid"`                             | HTTP header used to correlate requests    |

### Overriding Configuration at Runtime

All Serilog settings can be overridden without recompiling, in ascending order
of precedence:

1. `appsettings.json` (base configuration)
2. `appsettings.{Environment}.json` (e.g., `appsettings.Production.json`)
3. Environment variables (double-underscore as path separator)
4. Command-line arguments

**Example — `appsettings.Production.json`:**

```json
{
  "Serilog": {
    "MinimumLevel": { "Default": "Debug" },
    "WriteTo": [
      { "Name": "Console" }
    ]
  },
  "AppSettings": {
    "MaskRequestBodyInLogs": false
  }
}
```

**Example — environment variables:**

```bash
Serilog__MinimumLevel__Default=Debug
Serilog__WriteTo__0__Args__path=/var/log/dms/app.log
AppSettings__MaskRequestBodyInLogs=false
```

> [!NOTE]
> Configuration reload on file change is disabled to work around a .NET
> file-watcher issue on Linux. A process restart is required to pick up
> configuration changes.
