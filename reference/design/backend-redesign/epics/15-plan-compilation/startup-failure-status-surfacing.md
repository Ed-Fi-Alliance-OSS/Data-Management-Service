---
jira: DMS-1109
jira_url: https://edfi.atlassian.net/browse/DMS-1109
---

# Startup Failure Status Surfacing

Story context: `reference/design/backend-redesign/epics/15-plan-compilation/06-projection-plan-compilers.md`

## Selected approach

Use a file-backed startup status signal that is written before HTTP route binding and updated at each fatal startup phase. In Docker-based runs, the stable default path is `/tmp/dms-startup-status.json`.

Contract:

- `State`: `Starting`, `Completed`, `Failed`, or `Ready`
- `Phase`: one of `ConfigureServices`, `BuildApplication`, `LoadDataStores`, `InitializeApiSchemas`, `InitializeBackendMappings`, `InitializeAuthMetadata`, `ConfigureEndpoints`, or `Ready`
- `Summary`: short human-readable phase summary
- `ErrorType` / `ErrorMessage`: populated only for failures
- `UpdatedAtUtc`: last write timestamp

This keeps fatal startup semantics unchanged: fatal phases still terminate the process, but they now write the failure phase and summarized reason first. CI and local Docker troubleshooting can read one file instead of inferring from connection-refused symptoms.

## Rejected alternatives

- Lightweight startup-state endpoint: rejected because the relevant failures happen before Kestrel maps routes, so the endpoint would not exist when needed most.
- Structured log contract only: rejected as insufficient on its own because callers would still need to scrape or manually inspect container logs. The status file is easier to collect deterministically, while normal logs remain the detailed fallback.
- Replacing `Environment.Exit(...)` with non-fatal host behavior: rejected because the current design requires startup to fail fast on invalid configuration, schema compile failures, or identity provider bootstrap failures.

## Interaction with fatal startup

`Program.cs` now routes fatal phases through one executor:

1. Write `Starting` for the phase.
2. Run the phase body.
3. On success, overwrite the file with `Completed`.
4. On failure, overwrite the file with `Failed`, then invoke the existing process-exit behavior.

The current startup sequence writes these phase names in order: `ConfigureServices`, `BuildApplication`, `LoadDataStores`, `InitializeApiSchemas`, `InitializeBackendMappings`, `InitializeAuthMetadata`, `ConfigureEndpoints`, and `Ready`.

There is no database-provisioning phase in this list. Schema provisioning is owned by the bootstrap provisioning phase (`provision-dms-schema.ps1`) and never runs inside DMS startup — see `reference/design/backend-redesign/design-docs/bootstrap/command-boundaries.md`, which lists running inside DMS startup under that phase's "Must NOT do" and states that "Schema provisioning is entirely owned by this phase; DMS startup never performs it."

Bootstrap phases before the app host exists (`ConfigureServices`, `BuildApplication`) use the same status contract, but rethrow after writing the failure because the process has not yet built the DI graph used by the runtime exit hook. After the host exists, `ConfigureEndpoints` is written immediately before routing and endpoint registration, and `Ready` is written after middleware and endpoint mapping complete successfully.

`ConfigureEndpoints` bypasses `RunFatalAsync`. On success it transitions `Starting` -> `Ready` and writes no `Completed` state, because `Ready` is the success signal for endpoint configuration and would immediately overwrite it. On failure the phase name covers three distinct routes, told apart by `Summary`:

| `Summary` begins with | Cause | Preceding state | `Critical` log event | Process |
|---|---|---|---|---|
| `Middleware and endpoint configuration failed` | Routing, middleware, or endpoint mapping threw | `Starting` (`ConfigureEndpoints`) | Yes | Terminates by rethrow |
| `Configuration could not be read or bound` | A configured value could not be converted to its target type, or an options service could not be resolved | `Completed` (`BuildApplication`) | Yes | Terminates by rethrow |
| `Configuration validation failed` | An options validator rejected the configuration | `Completed` (`BuildApplication`) | No | Stays up; every request is short-circuited by invalid-configuration middleware |

The two terminating routes write the failure and rethrow instead of invoking the process-exit hook, so the unhandled exception terminates the process. Both are recorded through `StartupPhaseExecutor`, so both emit the same phase-labelled `Critical` log event as the phases that exit via the hook: every fatal failure from `LoadDataStores` onward is findable by a log search on the failing phase name. The two pre-host phases are the exception — they run before the DI graph the executor depends on exists, so they write the status file but emit no `Critical` event, and their log evidence is the runtime's unhandled-exception output.

The validation route is deliberately non-fatal, which is why it emits no `Critical` event: DMS stays up so callers receive a reportable error instead of a refused connection. Two consequences when reading a collected status file. First, a `Failed` document on this phase does not by itself mean the process is gone — read `Summary` to tell which route ran. Second, only the endpoint-mapping route is preceded by `Starting`; both configuration routes are reached before `Starting` is written, so the file transitions straight from `Completed` (`BuildApplication`) to `Failed`.

`Ready` means every startup phase completed. It is written before the host binds its listeners, so it does not by itself confirm that the process is accepting connections: a `Ready` file alongside a refused connection points at host startup, such as port binding, rather than at any startup phase.

## CI and local usage

- Docker compose injects `AppSettings__StartupStatusFilePath=/tmp/dms-startup-status.json` for local and published DMS containers.
- The DMS PR workflow reads that file on `build-and-start-dms` failures, prints it inline, and uploads it as an artifact.
- Local troubleshooting can inspect the same signal with:

```pwsh
docker exec ed-fi-api cat /tmp/dms-startup-status.json
```
