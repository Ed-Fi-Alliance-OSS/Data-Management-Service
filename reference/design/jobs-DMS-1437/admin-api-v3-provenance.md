# Provenance of `admin-api-v3-ed115fd8.yaml`

`admin-api-v3-ed115fd8.yaml` is the Ed-Fi Management API v3 OpenAPI document generated from the upstream ODS-Admin-API source. It is the pinned contract evidence for the CMS `GET /v3/jobs/{jobId}` endpoint in [DMS-1437](https://edfi.atlassian.net/browse/DMS-1437). See `spec.md` §1.4 and §1.4.1.

Upstream does not check in a v3 document. `docs/swagger.yaml` is `version: v2`, and `docs/api-specifications/openapi-yaml/` ends at `admin-api-2.3.0.yaml`. The v3 document exists only as an artifact of the `openapi-md.yml` workflow, so it was regenerated locally.

## File

| Property | Value |
| --- | --- |
| Size | 164 440 bytes |
| SHA-256 | `f0735ae1c7517b3534f31635fdc387b0665ff28679a98b2008103c7557b9466b` |
| `openapi` | `3.0.1` |
| `info.title` | `Ed-Fi Management API` |
| `info.version` | `3.0.0` |

The file is committed byte-for-byte as generated. It is not edited or reformatted.

## Source

- Repository: `Ed-Fi-Alliance-OSS/ODS-Admin-API`
- Commit: `ed115fd8c0cb3e17dcfefc86966582c74e39f277` (2026-08-28, "[ADMINAPI-1495] v3 - Correct inaccurate Swagger/OpenAPI documentation (#448)")
- Contract source: `Application/EdFi.Ods.AdminApi.V3/Features/Jobs/GetJobStatus.cs`, unchanged from the pinned commit through upstream `main` head `2967c2c9b407ce4f5f90511c8ed5aaad41d5ab8e` (checked 2026-09-23)
- Checkout: an isolated clone at the pinned commit, outside every DMS worktree. No upstream code was modified.

## Toolchain

- .NET SDK 10.0.401 (installed runtimes: .NET 8.0.31 and 10.0.12)
- Swashbuckle.AspNetCore.Cli 7.1.0, from the upstream `.config/dotnet-tools.json`, restored with `dotnet tool restore`

## Commands

This is the upstream workflow step, run from the repository root:

```powershell
$p = @{ Authority="http://api"; IssuerUrl="https://localhost"; DatabaseEngine="PostgreSql"; PathBase="adminapi"; SigningKey="test";
        AdminDB="host=db-admin;port=5432;username=username;password=password;database=EdFi_Admin;Application Name=EdFi.Ods.AdminApi;";
        SecurityDB="host=db-admin;port=5432;username=username;password=password;database=EdFi_Security;Application Name=EdFi.Ods.AdminApi;" }
./build.ps1 -APIVersion "ed115fd8" -Configuration Release -DockerEnvValues $p -Command GenerateOpenAPI
```

The connection strings and signing key are the placeholder values from the upstream workflow. No database or identity provider is contacted during generation.

The script rewrites `Application/EdFi.Ods.AdminApi/appsettings.json` with `AdminApiMode` and the values above, then runs `dotnet tool run swagger tofile … v3`. The Release build succeeded with 0 warnings and 0 errors. The generation step failed twice. Both workarounds below were applied without changing any upstream code.

After the script's own function set `AdminApiMode` to `v3` and the app was rebuilt, this was the final command, run from `Application/EdFi.Ods.AdminApi`:

```powershell
$env:DOTNET_ROLL_FORWARD = "Major"
dotnet tool run swagger tofile --output ../../docs/api-specifications/openapi-yaml/admin-api-v3-ed115fd8.yaml --yaml ./bin/Release/net10.0/EdFi.Ods.AdminApi.dll 3.0.0
```

## Workarounds

1. **Tool runtime.** `swashbuckle.aspnetcore.cli` 7.1.0 targets `net9.0` with `rollForward: false`, and no .NET 9 runtime was installed. Setting `DOTNET_ROLL_FORWARD=Major` let it run on .NET 10. The upstream design doc `docs/design/2026-07-30-openapi-artifact-workflow-design.md` records the same workaround.
2. **Document name.** The upstream script passes the Swagger document name `v3`. At `ed115fd8`, however, the app registers documents `1.4.4`, `2.4.0` and `3.0.0`, so `v3` fails with `UnknownSwaggerDocument`. The v3 document is named `3.0.0`. This is a defect in the upstream script at this revision. It is recorded here as an observation only.

## What the document establishes for CMS

The relevant parts are the `/v3/jobs/{jobId}` path and the `jobStatusResult` component schema:

- The component schema is named `jobStatusResult`. It has exactly five camelCase properties: `jobId`, `status`, `createdAt`, `finishedAt` and `errorMessage`.
- `createdAt` is a non-nullable `date-time`. `finishedAt` is a nullable `date-time`. `errorMessage` is a nullable `string`.
- The schema has no `required` list and sets `additionalProperties: false`.
- The `jobId` path parameter is a required `string` with no format.
- Responses 401, 403, 404 and 500 are `application/problem+json` using `problemDetails`. Response 200 is `application/json` using `jobStatusResult`.

## Differences CMS keeps on purpose

- **`jobId` and `status` nullability.** Upstream marks both properties `nullable: true`, but the source never returns null for them. This is a generator artifact: Swashbuckle 7.1 emits `nullable: true` for reference types that have no nullability annotation. CMS emits both as non-nullable. That is stricter, and it matches the upstream Bruno runtime assertion that both are required strings.
- **Schema name and `additionalProperties`.** CMS generates its document with `Microsoft.AspNetCore.OpenApi`, which may name the schema `JobStatusResponse` and may omit `additionalProperties: false`. The CMS contract test (spec step 4.1) asserts these: the property set and types, the nullability of `finishedAt` and `errorMessage`, the non-nullability of `createdAt`, the path parameter, and the `application/problem+json` 404. Any remaining generator differences are recorded in `docs/CMS-BACKGROUND-JOBS.md`.
- **404 problem type.** Upstream Bruno tests expect `urn:ed-fi:management-api:not-found`. CMS keeps its existing `urn:ed-fi:api:not-found` (spec Q1).
