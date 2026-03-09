# Startup Failure Status Surfacing

Story context: `reference/design/backend-redesign/epics/15-plan-compilation/06-projection-plan-compilers.md`

## Selected approach

Use a file-backed startup status signal that is written before HTTP route binding and updated at each fatal startup phase. In Docker-based runs, the stable default path is `/tmp/dms-startup-status.json`.

Contract:

- `State`: `Starting`, `Completed`, `Failed`, or `Ready`
- `Phase`: stable phase name such as `ConfigureServices`, `BuildApplication`, `LoadDmsInstances`, `InitializeDatabase`, `InitializeApiSchemas`, `WarmUpOidcMetadataCache`, or `Ready`
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

Bootstrap phases before the app host exists (`ConfigureServices`, `BuildApplication`) use the same status contract, but rethrow after writing the failure because the process has not yet built the DI graph used by the runtime exit hook.

## CI and local usage

- Docker compose injects `AppSettings__StartupStatusFilePath=/tmp/dms-startup-status.json` for local and published DMS containers.
- The DMS PR workflow reads that file on `build-and-start-dms` failures, prints it inline, and uploads it as an artifact.
- Local troubleshooting can inspect the same signal with:

```pwsh
docker exec dms-local-dms cat /tmp/dms-startup-status.json
```
