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
`HttpRequestFailed` and `Level` set to `Error`. The `Exception` field is
present only when the logging layer itself observed an exception; when there is
none, the JSON formatter omits the field entirely rather than writing `null`,
so collector rules must treat `Exception` as optional. A request logged as
failed only because the downstream pipeline produced a 5xx status carries no
`Exception` field. In DMS, an exception caught by the core pipeline is
attached to the core-layer `HttpRequestFailed` event (`RequestLayer` = `Core`),
and the frontend then logs its own `HttpRequestFailed` event for the resulting
5xx response without one; the two events share the same `TraceId`, so use
`TraceId` plus `RequestLayer` to recover the exception behind a frontend
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

## Log Routing and Export

CMS and DMS support three supported paths for routing structured logs to an
observability platform: platform stdout collection (the default), optional
file-tailing agents, and OTLP export through the `OtlpLogging` configuration
section.

### Platform Stdout Collection

The structured JSON console output described above is the default and
supported collector contract for CMS and DMS. No additional configuration is
required to receive it: any platform log pipeline that collects a
container's or process's standard output (for example, a container runtime's
log driver, a node-level agent, or a hosting platform's built-in log
collection) receives the same newline-delimited JSON events described in
"CMS And DMS Request Log Console Contract" above.

### File-Tailing Agents

The bundled `appsettings.json` files also configure an optional structured
JSON file sink (see above). Organizations that run a file-tailing log agent
(for example, Filebeat, Fluent Bit, or a vendor-specific forwarder) can point
that agent at the rolling log files instead of, or in addition to, collecting
stdout. File logs carry the same structured properties as console logs, but
omit `RenderedMessage`, and remain a local convenience rather than part of
the collector contract.

### OTLP Export

CMS and DMS compile in `Serilog.Sinks.OpenTelemetry` as a single
vendor-neutral OTLP log exporter, configured through a top-level
`OtlpLogging` configuration section in each application's `appsettings.json`.
The section is disabled by default; operators opt in by setting `Enabled` to
`true`, typically through the environment-variable convention described in
[CONFIGURATION.md](./CONFIGURATION.md).

The `OtlpLogging` section supports these keys:

* `Enabled`: when `true`, log events are also exported over OTLP. Default:
  `false`.
* `Endpoint`: the OTLP collector endpoint, as an absolute `http://` or
  `https://` URL, for example `http://collector:4318`. Required when
  `Enabled` is `true`: if it is omitted or is not such a URL, OTLP export is
  not applied and a warning is written to stderr.
* `Protocol`: the OTLP wire protocol, either `Grpc` or `HttpProtobuf`. The
  value binds case-insensitively, but OTLP-convention spellings such as
  `http/protobuf` are not accepted and fail startup with a configuration
  binding error.
* `ServiceName`: the `service.name` resource attribute. Defaults to
  `EdFi.DataManagementService` for DMS and `EdFi.DmsConfigurationService` for
  CMS, matching each application's `Application` request log property.
* `ServiceVersion`: the `service.version` resource attribute. Defaults to the
  application's informational version.
* `DeploymentEnvironment`: optional deployment environment, emitted as both
  the legacy `deployment.environment` resource attribute and its stable
  semantic-convention replacement `deployment.environment.name`.
* `ServiceInstanceId`: optional `service.instance.id` resource attribute.
* `Headers`: optional headers sent with every export request, for example an
  `Authorization` value for an authenticated collector receiver. Header
  values are secrets: supply them through environment variables (for
  example, `OtlpLogging__Headers__Authorization`) or a secret store, never a
  committed configuration file.
  An invalid header name or value (for example, a value with a trailing
  newline from a mounted secret file) prevents the sink from being created:
  OTLP export is not applied, and the startup warning written to stderr
  deliberately omits the offending value because it may be a secret.

> [!NOTE]
> OTLP export is disabled by default. Enabling it does not replace console
> output: the console sink keeps emitting the same structured JSON described
> above, so it remains the diagnostic fallback if the OTLP endpoint is
> unreachable. Exporter delivery failures never block application startup or
> request serving; they are reported on stderr through Serilog's `SelfLog`
> facility rather than through the application's own structured logs.

> [!WARNING]
> The `OtlpLogging` section is the only surface for enabling OTLP export.
> Configuration-driven sink discovery is pinned to the Console and File sink
> assemblies, so a raw `Serilog:Using` / `Serilog:WriteTo` entry naming the
> OTLP sink does not activate it: the entry is ignored, and the application
> writes a warning to stderr at startup. The standard `OTEL_EXPORTER_OTLP_*`
> environment variables are likewise ignored by the exporter, so they cannot
> silently override the configured endpoint, protocol, headers, or resource
> identity.

Vendor-specific integrations belong outside the CMS and DMS processes: send
OTLP directly to a compatible service, or through an OpenTelemetry Collector,
to Splunk, Datadog, Elastic, Seq, CloudWatch, Azure, or another backend of
choice. CMS and DMS do not document or bundle vendor-specific sinks. The
standard `OTEL_EXPORTER_OTLP_HEADERS` variable is ignored along with the
other OTLP environment variables; authentication headers for the receiving
endpoint are configured through the `OtlpLogging:Headers` section instead.

### Security Considerations for OTLP Export

OTLP export sends the full structured log stream out of the process, so the
export path deserves the same care as a database connection string.

* **Prefer `https://` endpoints.** A cleartext `http://` endpoint provides
  neither confidentiality nor server authentication: anyone on the network
  path, or in control of DNS for the collector hostname, can read or divert
  the exported stream. TLS endpoints are validated with standard platform
  certificate validation, and no `OtlpLogging` setting can weaken that
  validation. Reserve cleartext for a same-host or otherwise trusted hop,
  such as a localhost agent or an in-cluster sidecar.
* **Treat the endpoint and headers as trust-sensitive configuration.** Any
  configuration layer that can set `OtlpLogging__Endpoint` silently redirects
  the log stream, and successful delivery produces no console evidence.
  Audit the same configuration sources you would for a connection string,
  and source header values from a secret store or environment variable.
* **Secure the collector's receiver.** A receiver reachable beyond a trusted
  network boundary should require authentication (for example, a bearer
  token checked by the collector), which CMS and DMS supply through
  `OtlpLogging:Headers`. Alternatively, keep the first hop inside a trusted
  boundary - a localhost agent, a sidecar, or a cluster service restricted
  by network policy - and let the collector make the authenticated,
  TLS-protected connection to the backend. Never expose an unauthenticated
  OTLP receiver to untrusted networks: anyone who can reach it can inject
  forged log records or flood the pipeline.
* **Verbosity governs what leaves the host.** The exporter ships the same
  events the console sink sees, so raising `Serilog:MinimumLevel` to `Debug`
  sends debug detail (including anonymized request payloads) to the
  collector.
* **Delivery is bounded and fail-safe.** Export batches up to 1,000 events
  every 2 seconds, queues at most 100,000 events while the collector is
  unreachable, abandons a failing batch after 10 minutes, and caps every
  export attempt at 30 seconds so a stalled collector cannot wedge the
  exporter or delay shutdown. Delivery failures never block startup or
  request serving; they are visible only on stderr through `SelfLog`.

### Deployment Recipes

These recipes are guidance for routing CMS and DMS logs in common deployment
environments. None of them require application code changes or a custom
image; each recipe uses the stdout contract, the file sink, or the
`OtlpLogging` section described above. The Docker recipe does require passing
the `OtlpLogging` environment variables through the compose service
definition, as described below.

#### Kubernetes

Collect stdout through the platform's log pipeline, for example a node-level
agent (Fluent Bit, Fluentd, or a managed platform's built-in logging), or set
the `OtlpLogging` environment variables (`OtlpLogging__Enabled=true`,
`OtlpLogging__Endpoint=...`, `OtlpLogging__Protocol=...`) to point at an
in-cluster OpenTelemetry Collector.

#### Docker

Enable OTLP export through environment variables passed to the container, for
example `OtlpLogging__Enabled=true` and
`OtlpLogging__Endpoint=http://collector:4318`. The compose files under
`eng/docker-compose` (and the azure-vm stack) forward the core keys from the
`.env` file: `OTLP_LOGGING_ENABLED`, `OTLP_LOGGING_ENDPOINT`,
`OTLP_LOGGING_PROTOCOL`, and `OTLP_LOGGING_DEPLOYMENT_ENVIRONMENT` for DMS,
with `DMS_CONFIG_`-prefixed equivalents for CMS. The remaining keys are not
forwarded from `.env` (the service identity keys have per-service defaults,
and header names are arbitrary keys): to override them, add
`OtlpLogging__<Key>` entries, such as `OtlpLogging__Headers__<Name>`, to the
compose service's `environment` map directly.

#### Windows Services

Tail the file sink's rolling log files with the organization's log agent, or
run a local OpenTelemetry Collector on the same host and point `OtlpLogging`
at its endpoint.

#### AWS

Route stdout to CloudWatch through the container or instance log driver or
agent (for example, the ECS `awslogs` driver or the CloudWatch agent on
EC2), or configure `OtlpLogging` to export through a Collector, such as the
AWS Distro for OpenTelemetry (ADOT) Collector, to the backend of choice.

#### Azure

Route stdout through the hosting platform's log integration (for example,
Azure Container Apps or App Service log streaming), or configure
`OtlpLogging` to export through a Collector to Azure Monitor or another
backend.

## Docker Compose Defaults and Container Log Retention

The Docker Compose stacks under `eng/docker-compose` and
`eng/azure-vm/compose` ship a set of defaults for log verbosity and container
log retention. This section documents those defaults, how to override them,
and the trade-offs involved in doing so.

### Shipped Log Levels

The Compose `.env` files (`.env.example`, `.env.multitenancy`, the active
`.env.template*` files, and the Azure VM `.env.example`) ship
`LOG_LEVEL=Information` for DMS and `DMS_CONFIG_LOG_LEVEL=Information` for the
Configuration Service. Neither service ships at `Debug` by default: `Debug`
includes anonymized HTTP request payloads and additional service-call detail
(see [Log Levels](#log-levels)), which is more
verbose and more expensive to store than most deployments need day to day.

### Overriding Log Levels

Both services read their log level from the Compose environment:

* `LOG_LEVEL` controls the DMS application's minimum Serilog level.
* `DMS_CONFIG_LOG_LEVEL` controls the Configuration Service's minimum
  Serilog level.

Set either variable to `Information`, `Warning`, or `Debug` (see
[Log Levels](#log-levels)) in the `.env` file used to start the stack, or
export it in the shell before running `docker compose up`. Raise a service to
`Debug` temporarily when investigating an integration problem, then return it
to `Information` - `Debug` logging is verbose and, per the container
retention behavior below, shortens how long evidence survives on disk.

### Docker Container Log Retention

The Compose stacks configure the Docker `json-file` log driver with two
variables:

* `DOCKER_LOG_MAX_SIZE` (default `50m`) - the maximum size of a single log
  file before Docker rotates it.
* `DOCKER_LOG_MAX_FILE` (default `5`) - the maximum number of log files Docker
  keeps per container, including the active file.

These defaults cap retained container logs at 250 MB (`50m` × `5`) **per
container**. The cap is per-container, not per-stack: each service (DMS,
Configuration Service, Postgres, Kafka, Keycloak, and so on) gets its own
independent 50 MB × 5-file allowance, so a multi-service stack retains up to
250 MB per service on disk, not 250 MB in total.

Retention is also a time window, not just a size, and that window can be short
under load. The 25.6 MB/s measurement came from sustained high-throughput
request traffic while DMS was at `Debug`. At that rate, a single 50 MB log file
fills in roughly 2 seconds, and the full 250 MB allowance (all 5 files) is
overwritten in roughly 10 seconds. In other words, at that rate the oldest
evidence in the retained log files is no more than about 10 seconds old - once
a new entry causes Docker to rotate out the oldest file, whatever was only in
that file is gone. Quieter workloads, including routine `Information` logging,
fill the allowance much more slowly and retain evidence far longer; the
10-second figure is a worst-case `Debug`-load bound, not a general expectation.

### Very Large Container Log Caps

Docker's `json-file` driver does not accept an "unlimited" sentinel value for
`max-size` in these Compose files. Values such as `-1`, `0`, `0m`, and
`unlimited` are rejected by Docker before any container starts. If an
investigation needs an env-only escape hatch from the default 250 MB retention
window, use a very large accepted size value instead:

```dotenv
DOCKER_LOG_MAX_SIZE=1000g
```

> [!WARNING]
> Setting `DOCKER_LOG_MAX_SIZE=1000g` effectively removes the local rotation
> safety net for most investigations. A very large log file can grow until it
> exhausts the host's disk, which can destabilize every container on that host,
> not only the one doing the logging. Use a large cap only on hosts with
> monitored, ample free disk space, and prefer the mitigations below for
> long-running investigations instead of leaving the cap raised indefinitely.

### Long-Running Investigations

For an investigation that needs more history than the default 250 MB /
~10-second worst-case window allows, prefer one of these over leaving
the cap raised indefinitely:

* **Increase the cap temporarily.** Raise `DOCKER_LOG_MAX_SIZE` and/or
  `DOCKER_LOG_MAX_FILE` for the duration of the investigation, then restore
  the defaults afterward. This keeps a bound in place while extending the
  retention window.
* **Export logs via OTLP.** Point `OtlpLogging` at a collector (see
  [OTLP Export](#otlp-export) above) so structured log events are shipped
  out of the container as they are emitted. An external collector or
  backend is not subject to the container's local rotation limits at all,
  so it is the more durable option for logs that must survive longer than
  any local retention window, or that must survive the container itself
  being removed.

### Evidence Loss Trade-Off

The three options above trade off disk usage against how much log history
survives:

* **Bounded (the default, `50m` / `5` files).** Safe for unattended hosts —
  disk usage per container is capped — but under sustained heavy load the
  retained window can be as short as ~10 seconds when DMS is at `Debug`, so
  evidence of a transient problem may already be gone by the time someone goes
  looking for it.
* **Very large cap (`DOCKER_LOG_MAX_SIZE=1000g`).** Less evidence loss from
  rotation, but little practical disk-usage safety net; left in place
  indefinitely, it risks disk exhaustion.
* **OTLP export.** Avoids both problems for logs that reach the collector
  before rotation would have discarded them, but adds an external
  dependency (the collector must be reachable and correctly configured;
  see [Security Considerations for OTLP Export](#security-considerations-for-otlp-export)
  above) and does not itself change the local container's rotation
  behavior. Docker's `DOCKER_LOG_MAX_SIZE` and `DOCKER_LOG_MAX_FILE` settings
  govern the container stdout/stderr `json-file` logs only; the Serilog file
  sinks inside the DMS and CMS containers are separate daily rolling files and
  are not capped by these Docker variables.

Choose based on the deployment: bounded defaults for routine operation,
a temporarily raised cap or OTLP export when a specific investigation
needs a longer window.

### Manual Container Log Truncation

In an emergency — for example, a container's log file has already grown
large enough to threaten disk space and rotation has not caught up — the
underlying `json-file` log file on the host can be truncated manually (for
example, with `truncate -s 0` on the file Docker reports via
`docker inspect --format='{{.LogPath}}' <container>`).

> [!WARNING]
> Docker's own documentation warns that manually manipulating a container's
> log file outside of Docker's log driver may interfere with the log
> driver's internal state tracking (for example, its record of how much has
> been read or rotated). Treat manual truncation strictly as an **emergency
> recovery** action to relieve acute disk pressure, not as a routine
> operating procedure. Prefer the configured rotation
> (`DOCKER_LOG_MAX_SIZE` / `DOCKER_LOG_MAX_FILE`) or OTLP export for
> ordinary retention management, and reserve manual truncation for
> situations where those mechanisms have not kept disk usage under control
> in time.

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
