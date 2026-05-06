# DMS-916: Bootstrap DMS for Local Development

Developer workflow walkthrough and implementation story outline

---

## Audience and Goal

This walkthrough is for DMS developers who need a local Data Management Service environment for development,
debugging, test data loading, or IDE-based work.

DMS-916 defines a Docker-first bootstrap utility that prepares the local environment from a selected
`ApiSchema.json` set, provisions the matching database schema, configures CMS, and optionally loads seed data
through the DMS API.

Note: DMS-916 is an accepted design. Some commands shown here are target-state commands that still require the
implementation stories listed at the end of this deck.

---

## What Bootstrap Replaces

Bootstrap is not an ODS/API `initdev` clone.

DMS is different:

- Docker-first local infrastructure
- No code generation step
- No dacpac deployment
- `ApiSchema.json` drives the API surface
- Relational DDL is provisioned from the selected schema set
- Seed loading moves through the API instead of direct SQL

---

## The Developer Contract

The common developer entry point is the thin wrapper:

```powershell
pwsh eng/docker-compose/bootstrap-local-dms.ps1
```

The authoritative contract is the phase-command chain underneath the wrapper:

```text
prepare-dms-schema.ps1
  -> prepare-dms-claims.ps1
       -> start-local-dms.ps1 -InfraOnly
            -> configure-local-dms-instance.ps1
                 -> provision-dms-schema.ps1
                      -> start-local-dms.ps1
                           -> load-dms-seed-data.ps1
```

Use the wrapper for the happy path. Use phase commands directly when debugging or rerunning one step.

---

## Happy Path: Core Only

For a clean local DMS environment with core Ed-Fi resources only:

```powershell
pwsh eng/docker-compose/bootstrap-local-dms.ps1
```

What happens:

- Stages the core `ApiSchema.json`
- Stages the matching embedded claims behavior
- Starts Docker-managed infrastructure
- Configures a local DMS instance in CMS
- Provisions the target database from the selected schema
- Starts DMS and waits for health

Omitting `-Extensions` means core only in the DMS-916 design.

---

## Add a Supported Extension

Use `-Extensions` for the v1 mapped extension set:

```powershell
pwsh eng/docker-compose/bootstrap-local-dms.ps1 -Extensions sample
```

Multiple extensions use normal PowerShell array binding:

```powershell
pwsh eng/docker-compose/bootstrap-local-dms.ps1 -Extensions "sample","homograph"
```

Supported v1 values:

- `sample`
- `homograph`

Unsupported values fail before Docker starts. TPDM is intentionally out of scope for DMS-916 v1.

---

## Add Seed Data

Seed data is opt-in on the wrapper:

```powershell
pwsh eng/docker-compose/bootstrap-local-dms.ps1 -LoadSeedData -SeedTemplate Minimal
```

Use `Populated` when you need a richer local data set for manual testing:

```powershell
pwsh eng/docker-compose/bootstrap-local-dms.ps1 -LoadSeedData -SeedTemplate Populated
```

Seed templates:

| Template | Intended use |
|---|---|
| `Minimal` | Descriptors and school years for fast development and CI-style setup |
| `Populated` | Minimal plus sample education organizations, courses, students, and associations |

---

## What JSONL Seed Data Looks Like

JSONL means one complete JSON resource body per line. There is no outer array and no comma between records.

Example seed directory:

```text
seed-data/
  01-schoolYearTypes.jsonl
  02-absenceEventCategoryDescriptors.jsonl
  10-localEducationAgencies.jsonl
  11-schools.jsonl
  20-students.jsonl
  21-studentSchoolAssociations.jsonl
```

Example `01-schoolYearTypes.jsonl`:

```jsonl
{"schoolYear":2025,"currentSchoolYear":true,"schoolYearDescription":"2024-2025"}
```

Example `02-absenceEventCategoryDescriptors.jsonl`:

```jsonl
{"codeValue":"Sick Leave","namespace":"uri://ed-fi.org/AbsenceEventCategoryDescriptor","shortDescription":"Sick Leave"}
```

Example `20-students.jsonl`:

```jsonl
{"studentUniqueId":"604834","birthDate":"2008-01-01","firstName":"Thomas","lastSurname":"Johnson"}
```

Each line is the request body BulkLoadClient posts to the corresponding DMS resource endpoint. File ordering
and endpoint mapping are finalized by the BulkLoadClient JSONL work; bootstrap's job is to stage a flat JSONL
workspace and fail fast on filename collisions.

---

## Custom Seed Data

Use JSONL files from a directory when the built-in templates are not enough:

```powershell
pwsh eng/docker-compose/bootstrap-local-dms.ps1 `
    -LoadSeedData `
    -SeedDataPath "./my-seeds/" `
    -AdditionalNamespacePrefix "uri://state.example.org"
```

The seed phase:

- Creates `SeedLoader` credentials
- Adds baseline and selected extension namespace prefixes
- Adds any `-AdditionalNamespacePrefix` values
- Invokes BulkLoadClient against the healthy DMS endpoint

Bootstrap does not infer schemas, extensions, claims, or namespace permissions from arbitrary seed files.

---

## Keycloak-Backed Local Run

Use `-IdentityProvider keycloak` when the local environment should use Keycloak:

```powershell
pwsh eng/docker-compose/bootstrap-local-dms.ps1 `
    -IdentityProvider keycloak `
    -LoadSeedData `
    -SeedTemplate Minimal
```

The identity provider selection is forwarded to the infrastructure phase and, when seed loading is selected,
to the seed phase so BulkLoadClient uses the matching token endpoint.

---

## Multi-Year Local Run

Use `-SchoolYearRange` to create and prepare year-specific DMS instances:

```powershell
pwsh eng/docker-compose/bootstrap-local-dms.ps1 `
    -SchoolYearRange "2025-2026" `
    -LoadSeedData `
    -SeedTemplate Minimal
```

The wrapper captures the selected instance IDs from `configure-local-dms-instance.ps1` and forwards them to
provisioning and seed loading within the same invocation.

Manual phase commands use explicit selectors when needed:

```powershell
pwsh eng/docker-compose/provision-dms-schema.ps1 -SchoolYear 2025,2026
pwsh eng/docker-compose/load-dms-seed-data.ps1 -SchoolYear 2025,2026 -SeedTemplate Minimal
```

---

## IDE Workflow: Infrastructure First

For local debugging, run infrastructure in Docker and DMS in the IDE:

```powershell
pwsh eng/docker-compose/bootstrap-local-dms.ps1 -InfraOnly
```

This prepares the pre-DMS environment and prints guidance for the IDE-hosted DMS process.

The IDE-hosted DMS uses host-local addresses:

| Setting | Local value |
|---|---|
| PostgreSQL | `localhost:5435` |
| Config Service | `http://localhost:8081` |
| Kafka | `localhost:9092` |
| ApiSchema path | `<repo-root>/eng/docker-compose/.bootstrap/ApiSchema` |

---

## IDE Workflow: Continue to a Local DMS Process

If you want the wrapper to wait for your IDE-hosted DMS process:

```powershell
pwsh eng/docker-compose/bootstrap-local-dms.ps1 `
    -InfraOnly `
    -DmsBaseUrl "http://localhost:5198"
```

The wrapper does not pass `-DmsBaseUrl` to the initial infrastructure start. It first configures the instance
and provisions the database, then waits for the IDE-hosted DMS endpoint to become healthy.

If seed loading is selected, the same `-DmsBaseUrl` is forwarded to the seed phase as the BulkLoadClient
target.

---

## Expert Mode: Custom ApiSchema

Use `-ApiSchemaPath` for a local directory of pre-built schema files:

```powershell
pwsh eng/docker-compose/bootstrap-local-dms.ps1 `
    -ApiSchemaPath "./my-custom-schema"
```

When the schema includes unmapped non-core resources, provide claims fragments:

```powershell
pwsh eng/docker-compose/bootstrap-local-dms.ps1 `
    -ApiSchemaPath "./my-custom-schema" `
    -ClaimsDirectoryPath "./my-claims"
```

Custom schema mode is an expert path:

- `-ApiSchemaPath` and `-Extensions` are mutually exclusive
- Built-in `-SeedTemplate` selection is disabled
- Seed loading requires `-SeedDataPath`
- Unmapped non-core schemas require `-ClaimsDirectoryPath`

---

## Manual Phase Flow

Use phase commands when you want to rerun or debug one part of bootstrap:

```powershell
pwsh eng/docker-compose/prepare-dms-schema.ps1
pwsh eng/docker-compose/prepare-dms-claims.ps1
pwsh eng/docker-compose/start-local-dms.ps1 -InfraOnly
pwsh eng/docker-compose/configure-local-dms-instance.ps1
pwsh eng/docker-compose/provision-dms-schema.ps1
pwsh eng/docker-compose/start-local-dms.ps1
pwsh eng/docker-compose/load-dms-seed-data.ps1 -SeedTemplate Minimal
```

Selector rule:

- One matching DMS instance: auto-selected
- Zero or multiple matching instances: fail fast unless `-InstanceId` or `-SchoolYear` is supplied

---

## What Bootstrap Writes

Bootstrap stages generated local artifacts under:

```text
eng/docker-compose/.bootstrap/
```

Important staged content:

```text
.bootstrap/
  ApiSchema/
    bootstrap-api-schema-manifest.json
    schemas/
    content/
  claims/
  bootstrap-manifest.json
```

This directory is scratch bootstrap state. It is not a persisted workflow control plane, not a resume file,
and not source-controlled application state.

---

## Safety Rules Developers Should Know

The selected schema set controls the exact physical database shape.

That means:

- Core-only provisions only core tables
- Core plus `sample` provisions the core and Sample table set
- Switching extension combinations on a live database is unsafe unless the stored schema state matches
- If the staged schema workspace differs from an existing one, bootstrap should fail and require teardown

Teardown remains:

```powershell
pwsh eng/docker-compose/start-local-dms.ps1 -d -v
```

---

## Implementation Story Map

The implementation is split into small stories under `reference/design/bootstrap/tickets/`.

| Story | Area | Main deliverable |
|---|---|---|
| `00` | Schema and security selection | Direct `-ApiSchemaPath`, normalized workspace, claims staging, root bootstrap manifest |
| `01` | Schema deployment safety | `provision-dms-schema.ps1` invoking SchemaTools/runtime provisioning |
| `02` | API seed delivery | `load-dms-seed-data.ps1`, `SeedLoader`, JSONL seed workspace, Minimal/Populated assets |
| `03` | Entry point and IDE workflow | `start-local-dms.ps1`, optional wrapper, `-InfraOnly`, `-DmsBaseUrl`, IDE guidance |
| `04` | Runtime content loading | DMS runtime reads ApiSchema content and XSD from the normalized workspace |
| `05` | MetaEd packaging | Asset-only ApiSchema NuGet packages from MetaEd |
| `06` | Package-backed standard mode | Omitted `-Extensions` core-only mode and named `sample`/`homograph` package-backed mode |

---

## Story Dependencies

The implementation sequence is not strictly linear, but several dependencies matter:

- Story 00 creates the staged schema, claims, and manifest contract consumed by later DMS-side stories.
- Story 01 depends on Story 00's staged schema workspace.
- Story 02 depends on Story 00's bootstrap manifest and is externally blocked end to end by ODS-6738
  BulkLoadClient JSONL support.
- Story 03 coordinates the developer-facing wrapper and IDE flow over the other phase commands.
- Story 04 depends on the normalized ApiSchema workspace from Story 00, not on package-backed mode.
- Story 05 is MetaEd-side package production work.
- Story 06 depends on Story 05 for asset-only packages and on Story 00 for the shared staging contract.

---

## Delivery Notes

Internal implementation prerequisites:

- Add the `SeedLoader` claim set and permissions to CMS embedded claims.
- Add repo-local deterministic seed assets under `eng/docker-compose/seed-data/minimal/` and
  `eng/docker-compose/seed-data/populated/`.
- Implement direct filesystem schema and claims staging.
- Implement `-InfraOnly` and `-DmsBaseUrl` behavior.
- Update DMS runtime content loading to use the staged ApiSchema workspace.

External blockers:

- BulkLoadClient must support `--input-format jsonl` and `--data <directory>` for end-to-end seed delivery.
- SchemaTools CLI shape must remain stable for `dms-schema hash` and provisioning.
- MetaEd must publish asset-only ApiSchema packages before package-backed standard mode can land.

---

## Takeaway

DMS-916 gives developers one practical local bootstrap workflow while keeping implementation concerns
separate:

- The wrapper is for daily use.
- Phase commands are for correctness, testing, and targeted reruns.
- `ApiSchema.json` is the source of truth for API surface, claims selection, and physical DDL shape.
- Seed data goes through the API path, not direct SQL.
- IDE debugging stays Docker-first by running only DMS locally.
