# Logging Policy

This section describes the logging policy in the Ed-Fi API source
code. In general, this policy seeks to balance the goals of providing sufficient
information for an administrator to understand the health of the system and
understand user interaction with the system with the equally important goals of
protecting sensitive data and avoiding excessive log storage size.

## Logging Principles

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

## CMS And DMS Request Log Console Contract

The Configuration Management Service and Data Management Service emit request
completion and request failure logs as structured events. DMS includes both the
ASP.NET frontend request middleware and the core pipeline request middleware in
this contract. The console sink is the production collector contract for CMS and
DMS request logs and must emit newline-delimited JSON using
`Serilog.Formatting.Json.JsonFormatter, Serilog`.

Collector rules should target structured properties, not parse the rendered
message. These request log properties are emitted directly by each CMS and DMS
request logging layer:

* `Application`: `EdFi.DmsConfigurationService` or
  `EdFi.DataManagementService`.
* `EventName`: `HttpRequestCompleted` or `HttpRequestFailed`.
* `SourceContext`: logger category emitted by Serilog/Microsoft logging.
* `TraceId`: the application-visible trace or correlation ID. CMS uses
  `HttpContext.TraceIdentifier`; DMS uses the configured correlation header
  when present and falls back to `HttpContext.TraceIdentifier`.
* `Method`: sanitized HTTP method.
* `Path`: sanitized request path without the query string.
* `StatusCode`: HTTP response status code. An unhandled exception before a
  response is produced is logged as `500`.
* `DurationMs`: elapsed request duration in milliseconds as a numeric `long`.

The ASP.NET request logging layers can also add these optional request scope
properties when available:

* `ActivityTraceId`: W3C activity trace ID when `Activity.Current` exists.
* `SpanId`: W3C span ID when `Activity.Current` exists.
* `PathBase`: sanitized request path base when available from the ASP.NET
  request scope.

DMS core request events normally run inside the DMS frontend request scope, so
they may inherit `ActivityTraceId`, `SpanId`, and `PathBase` through Serilog log
context enrichment. Collectors must tolerate those optional ASP.NET-specific
properties being absent from DMS core events when the core pipeline is invoked
outside the ASP.NET frontend.

### Example Request Log Output

Each CMS and DMS request completion event produces newline-delimited JSON with
this structure:

```json
{
  "Timestamp": "2026-06-29T14:23:45.123Z",
  "Level": "Information",
  "MessageTemplate": "{EventName}: CMS request completed: {Method} {Path} responded {StatusCode} in {DurationMs} ms with TraceId {TraceId}",
  "RenderedMessage": "HttpRequestCompleted: CMS request completed: GET /v3/vendors responded 200 in 42 ms with TraceId 0HN...",
  "Exception": null,
  "Properties": {
    "Application": "EdFi.DmsConfigurationService",
    "EventName": "HttpRequestCompleted",
    "SourceContext": "EdFi.DmsConfigurationService.Frontend.AspNetCore.Middleware.RequestLoggingMiddleware",
    "TraceId": "0HN...",
    "ActivityTraceId": "4bf92f3577b34da6a3ce929d0e0e4736",
    "SpanId": "00f067aa0ba902b7",
    "Method": "GET",
    "Path": "/v3/vendors",
    "StatusCode": 200,
    "DurationMs": 42,
    "PathBase": ""
  }
}
```

Request failure logs follow the same structure with `EventName` set to
`HttpRequestFailed`, `Level` set to `Error`, and exception details in the
`Exception` field.

Completion logs use `Information`, except CMS `/.well-known/*` completion logs,
which use `Debug`. Failure logs use `Error`. Request start logs are diagnostic
breadcrumbs emitted at `Debug` and are outside the production completion/failure
collector contract. CMS rethrows the original exception for upstream middleware;
DMS frontend preserves its existing behavior of wrapping the original exception
after logging and writing its existing JSON error response when the response has
not started. DMS core preserves its existing behavior of wrapping core pipeline
failures after logging them.

Information-level request logs must not include request bodies, response
bodies, authorization headers, bearer tokens, API keys, client secrets,
connection strings, raw query strings, arbitrary headers, route values, or raw
tenant header values. Remote IP address and user agent are also excluded unless
a later story defines the privacy, retention, and cardinality requirements for
those fields.

## Log Levels

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

## Examples

These examples are general guidelines and not 100% exhaustive.

### Fatal

* Missing required configuration information
* Out of memory or disk space

### Error

* Unhandled null reference
* Database connection / transaction failure after exhausting retry attempts

### Warning

* A database connection / transaction failure occurred, but was recovered with
  an automatic retry

### Informational

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

### Debug

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
