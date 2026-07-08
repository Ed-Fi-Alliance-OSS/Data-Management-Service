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

The bundled `appsettings.json` files configure the file sink with the same JSON
formatter, so file logs carry the same structured properties as console logs.
File logs are a local convenience rather than part of the collector contract,
and they omit `RenderedMessage` because the file sink does not set
`renderMessage`.

> [!WARNING]
> Do not set `Serilog:WriteTo:*:Args:outputTemplate` in an environment override
> such as a local `appsettings.development.json`. Serilog configuration arrays
> merge by index, and `Serilog.Settings.Configuration` prefers the
> `outputTemplate` string overload over the `formatter` object, so an override
> that adds `outputTemplate` to the console sink silently replaces the JSON
> formatter with plain text and breaks this structured request-log contract.
> Override `MinimumLevel` only, or restate the full `formatter` object when the
> console sink itself must change.

Collector rules should target structured properties, not parse the rendered
message. These request log properties are emitted directly by each CMS and DMS
request logging layer:

* `Application`: `EdFi.DmsConfigurationService` or
  `EdFi.DataManagementService`.
* `EventName`: `HttpRequestCompleted` or `HttpRequestFailed`.
* `EventId`: structured event id with `Id` `1228001` (`HttpRequestCompleted`)
  or `1228002` (`HttpRequestFailed`). This document is the source of truth for
  these values; CMS and DMS build as separate solutions, so each application
  defines them in its own `RequestLoggingEventIds` class and pins them with its
  own unit test.
* `SourceContext`: logger category emitted by Serilog/Microsoft logging.
* `RequestLayer`: DMS-only value of `Frontend` or `Core`. Use this field to
  separate externally visible HTTP request events from core pipeline request
  events when aggregating DMS request volume or failure rates.
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
outside the ASP.NET frontend. For DMS request-count dashboards, collectors
should filter to `RequestLayer = "Frontend"` so the frontend and core request
events are not counted as separate external HTTP requests.

### Example Request Log Output

Each CMS and DMS request completion event produces newline-delimited JSON with
this structure. CMS example:

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
    "EventId": { "Id": 1228001, "Name": "HttpRequestCompleted" },
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

DMS frontend example, which additionally carries the DMS-only `RequestLayer`
property:

```json
{
  "Timestamp": "2026-06-29T14:23:45.123Z",
  "Level": "Information",
  "MessageTemplate": "{EventName}: DMS request completed: {Method} {Path} responded {StatusCode} in {DurationMs} ms with TraceId {TraceId}",
  "RenderedMessage": "HttpRequestCompleted: DMS request completed: GET /ed-fi/students responded 200 in 42 ms with TraceId 0HN...",
  "Exception": null,
  "Properties": {
    "Application": "EdFi.DataManagementService",
    "EventName": "HttpRequestCompleted",
    "EventId": { "Id": 1228001, "Name": "HttpRequestCompleted" },
    "SourceContext": "EdFi.DataManagementService.Frontend.AspNetCore.Infrastructure.LoggingMiddleware",
    "RequestLayer": "Frontend",
    "TraceId": "0HN...",
    "ActivityTraceId": "4bf92f3577b34da6a3ce929d0e0e4736",
    "SpanId": "00f067aa0ba902b7",
    "Method": "GET",
    "Path": "/ed-fi/students",
    "StatusCode": 200,
    "DurationMs": 42,
    "PathBase": ""
  }
}
```

Request failure logs follow the same structure with `EventName` set to
`HttpRequestFailed` and `Level` set to `Error`. The `Exception` field carries
the exception the logging layer itself observed, when there is one: a request
logged as failed only because the downstream pipeline produced a 5xx status has
`Exception` `null`. In DMS, an exception caught by the core pipeline is
attached to the core-layer `HttpRequestFailed` event (`RequestLayer` = `Core`),
and the frontend then logs its own `HttpRequestFailed` event for the resulting
5xx response with `Exception` `null`; the two events share the same `TraceId`,
so use `TraceId` plus `RequestLayer` to recover the exception behind a frontend
failure event.

Completion logs use `Information`, except CMS `/.well-known/*` completion logs,
which use `Debug`. Failure logs use `Error`. Request start logs are diagnostic
breadcrumbs emitted at `Debug` and are outside the production completion/failure
collector contract. The DMS frontend emits oversized-request-body rejections
(HTTP 413) as `HttpRequestCompleted` events carrying the status code the client
actually received — 413 unless the response had already started with another
status — so they remain visible to the request-log contract as client-error
responses rather than failures. The `HttpRequestFailed` event is the canonical
application error log for a request: when the CMS global exception handler
converts an unhandled exception into a 500 response, the request logging
middleware logs that handled exception on the `HttpRequestFailed` event, and the
exception handler itself does not log. When the CMS global exception handler
instead converts an exception into a 400 response (malformed request bodies and
validation failures), the request is a handled client error: it is logged as a
normal `HttpRequestCompleted` event with no exception payload, and the error
details are returned to the client in the problem-details response body. This
intentionally replaces the previous behavior of logging handled exceptions at
`Error` from the exception handler. An exception the CMS exception handler does
not observe (thrown outside its scope, or after the response has started)
propagates through the request logging middleware, which logs it on
`HttpRequestFailed` and rethrows it for the host.
DMS frontend preserves its existing behavior of wrapping the original exception
after logging and writing its existing JSON error response when the response has
not started. The `traceId` in that error response body is the raw correlation
value for the request — the same raw value every other DMS error response body
returns — while log events always carry the sanitized `TraceId`. The two differ
only when a client-supplied correlation id contains characters outside the
logging whitelist; applying that whitelist to a client-reported trace id yields
the `TraceId` to search for in the logs. DMS core preserves its existing
behavior of wrapping core pipeline failures after logging them.

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
