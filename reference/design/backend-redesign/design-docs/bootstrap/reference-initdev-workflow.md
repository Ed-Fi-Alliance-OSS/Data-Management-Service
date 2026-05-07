# InitDev Workflow — ODS/API Bootstrap Reference

> **Source reference:** This document describes the `initdev` workflow as implemented in the Ed-Fi ODS/API Implementation repository (`Ed-Fi-ODS-Implementation`). Reference version: ODS Suite 3, targeting Data Standard 6.0.0. The module structure, file paths, and step details are accurate as of this design artifact (DMS-916, 2026-03-31). Consult the ODS-Implementation repository and the `configuration.packages.json` version pins for the exact artifacts in any specific release. **This document is not updated as ODS evolves** — it is a point-in-time reference captured for DMS bootstrap design context only.

This document describes the `initdev` workflow for the Ed-Fi ODS/API platform and serves as historical
reference for the DMS bootstrap design. The ODS pipeline, configuration cascade model, task-wrapper
contract, and plugin/extension mechanisms documented here directly informed the DMS bootstrap design -
particularly the audit tables in [Section 2 of bootstrap-design.md](bootstrap-design.md#2-ods-initdev-audit-informational-reference).

> **Scope note for DMS contributors:** This is a read-only reference document for DMS context. Changes to the ODS initdev pipeline are out of scope for DMS-916.

---

## What InitDev Does

`initdev` is a PowerShell command that bootstraps a complete Ed-Fi ODS/API development environment from scratch. It generates application settings, builds the .NET solution, provisions databases (Admin, Security, ODS templates), and optionally runs tests. The entire process is driven by a single function with ~20 parameters that control what gets built and how.

---

## Historic ODS Pattern Summary

The ODS initdev implements a general-purpose **developer environment bootstrap** pattern. This section
captures what ODS answered for itself. DMS may answer these concerns differently, mark them not applicable,
or deliberately ignore them; this section is reference material, not a required checklist.

### Core Phases (in order)

```
Phase 1: ENVIRONMENT SETUP
  -> Load modules/scripts, resolve paths across repos, detect platform

Phase 2: CONFIGURATION
  -> Assemble settings from: parameters -> config files -> defaults -> plugins
  -> Generate environment-specific config files (e.g., appsettings.Development.json)
  -> Generate secrets (API keys, encryption keys, signing keys)

Phase 3: CODE GENERATION (if applicable)
  -> Run any code generators that produce source from data models/schemas

Phase 4: BUILD
  -> Compile the solution / build artifacts

Phase 5: TOOL INSTALLATION
  -> Install CLI tools needed for database migrations, bulk loading, etc.

Phase 6: DATABASE PROVISIONING
  -> For each database type (Admin, Security, ODS, etc.):
       Backup (if exists + pending changes) -> Drop -> Create -> Migrate
  -> Varies by install type (single DB vs per-tenant vs per-token)
  -> Varies by engine (SQL Server vs PostgreSQL vs other)

Phase 7: DATA SEEDING (if applicable)
  -> Load bootstrap/sample data into template databases
  -> May require starting an API host to load data through the API

Phase 8: VERIFICATION (optional)
  -> Run unit tests, integration tests, smoke tests, SDK generation
```

### Design Principles Embedded in ODS InitDev

| Principle | How ODS Does It | Why It Matters |
|-----------|----------------|----------------|
| **Single entry point** | `initdev` alias, one function with flags | Developers memorize one command |
| **Idempotent by default** | Drop-and-recreate databases, overwrite config files | Running twice produces the same result |
| **Opt-in complexity** | Tests, plugins, multi-tenancy are all off by default | Fast inner loop for most developers |
| **Engine abstraction** | Strategy pattern for SQL Server vs PostgreSQL | Same command works regardless of backend |
| **Settings cascade** | Parameters -> config files -> defaults, deep-merged | Override anything without editing files |
| **Task wrapper** | Every step wrapped in `Invoke-Task` with timing/errors | Consistent logging, easy to find failures |
| **Skip flags** | `-NoRebuild`, `-NoDeploy`, `-ExcludeCodeGen` | Re-run partial pipeline after a failure |
| **Extensibility via plugins** | Plugin scripts return package metadata, auto-discovered | Third parties extend without forking |

### Questions ODS InitDev Explicitly Answered

The ODS pipeline had explicit answers for questions like the following. DMS may answer them differently or
mark them out of scope:

- **Module loading**: How are scripts/modules discovered and loaded? Cross-repo paths?
- **Configuration assembly**: How do parameters, config files, and defaults merge? What is the precedence order?
- **Config file generation**: Which projects need generated config? What secrets/keys are needed?
- **Code generation**: Is there a code-gen step? What triggers it? Can it be skipped?
- **Build step**: What builds? Can it be skipped for config-only changes?
- **Tool installation**: What external CLI tools are needed? How are versions pinned?
- **Database lifecycle**: What databases exist? What is the create/migrate strategy per engine?
- **Install type variance**: Does the system support single-tenant, multi-tenant, sandbox modes? How does DB provisioning differ?
- **Data seeding**: Are template/seed databases needed? How is sample data loaded?
- **Test infrastructure**: Is there a test harness? How does it start/stop? What test types are supported?
- **Plugin/extension system**: Can third parties extend the data model? How are extensions discovered and installed?
- **Error handling**: Does each step capture errors independently? Can the pipeline continue or does it halt?
- **Timing/observability**: Are step durations reported? CI integration (TeamCity, GitHub Actions)?
- **Skip/resume**: Can individual steps be skipped? Can you resume after a failure without re-running everything?
- **Cross-platform**: Does it work on Windows, macOS, Linux? Are there platform-specific branches?
- **Docker alternative**: Is there a Docker-based path that bypasses local setup entirely?
- **Migration from old system**: Is there a data migration path? Schema compatibility layer? Side-by-side operation?

---

## Entry Points

### 1. Developer Shell Setup

```powershell
. ./Initialize-PowershellForDevelopment.ps1   # loads all modules
initdev                                        # runs the pipeline
```

**File:** `Initialize-PowershellForDevelopment.ps1` (repo root)

This script:

1. Imports `logistics/scripts/modules/load-path-resolver.ps1` (cross-repo path resolution)
2. Imports `logistics/scripts/modules/utility/cross-platform.psm1`
3. On Windows: scans for Zone.Identifier-blocked files (`Find-BlockedFiles`)
4. Dot-sources all `.ps1` and imports all `.psm1` from `Application/SolutionScripts/`
5. This makes `initdev` (alias for `Initialize-DevelopmentEnvironment`) available

### 2. Main Orchestrator

**File:** `Application/SolutionScripts/InitializeDevelopmentEnvironment.psm1`

This module imports 18 sub-modules and exposes the `Initialize-DevelopmentEnvironment` function.

---

## Key Parameters

| Parameter | Default | Effect |
|-----------|---------|--------|
| `-InstallType` | `Sandbox` | Deployment mode: `Sandbox`, `SingleTenant`, `MultiTenant` |
| `-Engine` | `SQLServer` | Database engine: `SQLServer` or `PostgreSQL` |
| `-OdsTokens` | `@()` | ODS database tokens (year-specific databases) |
| `-Tenants` | `@()` | Tenant identifiers (MultiTenant mode only) |
| `-StandardVersion` | `6.0.0` | Ed-Fi Data Standard version |
| `-ExtensionVersion` | `1.1.0` | Extension version |
| `-NoRebuild` | `$false` | Skip `dotnet build` |
| `-NoDeploy` | `$false` | Skip all database provisioning |
| `-ExcludeCodeGen` | `$false` | Skip code generation |
| `-UsePlugins` | `$false` | Enable plugin extensions |
| `-RunPester` | `$false` | Run Pester test suite |
| `-RunDotnetTest` | `$false` | Run .NET integration tests |
| `-RunPostman` | `$false` | Run Postman integration tests |
| `-RunSmokeTest` | `$false` | Run smoke tests |
| `-RunSdkGen` | `$false` | Generate SDK |
| `-MssqlSaPassword` | — | SQL Server SA password (non-integrated auth) |

---

## Pipeline Execution (17 Steps)

Every step is wrapped in `Invoke-Task` (from `logistics/scripts/modules/tasks/TaskHelper.psm1`), which provides timing, error capture, and TeamCity block reporting.

```
Step  Function                              Condition           Module Source
----  ------------------------------------  ------------------  -------------------------
 1    Clear-Error                           always              TaskHelper.psm1
 2    Set-DeploymentSettings                always              Deployment.psm1
 3    Merge plugin settings                 UsePlugins flag     settings-management.psm1
 4    Invoke-NewDevelopmentAppSettings       always              local (-> settings-management)
 5    Install-Plugins                       UsePlugins flag     plugin-source.psm1
 6    Invoke-CodeGen                        !ExcludeCodeGen     local (-> ToolsHelper)
 7    Invoke-RebuildSolution                !NoRebuild          local (-> dotnet build)
 8    Install-DbDeploy                      always              local (-> ToolsHelper)
 9    Reset-TestAdminDatabase               always              local (-> database-lifecycle)
10    Reset-TestSecurityDatabase            always              local (-> database-lifecycle)
11    Reset-TestPopulatedTemplateDatabase   !NoDeploy           local (-> database-lifecycle)
12    Initialize-DeploymentEnvironment      !NoDeploy           Deployment.psm1
13    Invoke-PesterTests                    RunPester           local (-> Pester)
14    Invoke-DotnetTest                     RunDotnetTest       local (-> run-tests.ps1)
15    Invoke-PostmanIntegrationTests        RunPostman          Invoke-PostmanIntegrationTests.ps1
16    Invoke-SmokeTests                     RunSmokeTest        local (-> run-smoke-tests.ps1)
17    Invoke-SdkGen                         RunSdkGen           Invoke-SdkGen.ps1
```

Output: a `Format-Table` of task names and durations.

---

## Step Details

### Step 2: Settings Assembly (`Set-DeploymentSettings`)

**File:** `InstallerPackages/EdFi.RestApi.Databases/Deployment.psm1`

Merges parameters into a settings hashtable through multiple layers:

1. Caller parameters (InstallType, Engine, tokens, etc.)
2. `configuration.packages.json` (NuGet package versions)
3. Default connection strings per engine
4. Default features (14 feature flags: OpenApiMetadata, ChangeQueries, Extensions, etc.)
5. Plugin settings (if enabled)

The result is a single `$Settings` hashtable that flows through every subsequent step.

### Step 4: App Settings Generation (`Invoke-NewDevelopmentAppSettings`)

**File:** `logistics/scripts/modules/settings/settings-management.psm1` -> `New-DevelopmentAppSettings`

Generates `appsettings.Development.json` for each project:

- `Application/EdFi.Ods.WebApi`
- `Application/EdFi.Ods.SandboxAdmin`
- `Application/EdFi.Ods.SwaggerUI`
- `Application/EdFi.Ods.Api.IntegrationTestHarness`
- Test projects under `tests/`

Each file gets project-specific connection strings, feature flags, plugin folder paths, JWT signing keys (via `New-PublicPrivateKeyPair`), and AES encryption keys (via `New-AESKey`).

Validation: `Assert-ValidAppSettings` checks that generated JSON is parseable.

### Step 6: Code Generation (`Invoke-CodeGen`)

Installs `EdFi.Suite3.Ods.CodeGen` as a .NET tool (version from `configuration.packages.json`), then runs it. This generates NHibernate mappings and API metadata from the Ed-Fi data model.

### Step 7: Solution Build (`Invoke-RebuildSolution`)

Runs `dotnet build Application/Ed-Fi-Ods.sln` with the configured build configuration.

### Step 8: DbDeploy Installation

Installs `EdFi.Suite3.Db.Deploy` as a .NET tool. This tool applies database migration scripts.

### Steps 9-11: Test Database Reset

Each creates a test-specific database (`Admin_Test`, `Security_Test`, `PopulatedTemplate_Test`) by calling `Initialize-EdFiDatabase` from `database-lifecycle.psm1`.

### Step 12: Full Database Deployment (`Initialize-DeploymentEnvironment`)

**File:** `InstallerPackages/EdFi.RestApi.Databases/Deployment.psm1`

This is the core database provisioning step. Behavior varies by install type:

#### Sandbox Mode (default)

```
1. Install-Plugins              (if configured)
2. Reset-AdminDatabase          -> EdFi_Admin
3. Reset-SecurityDatabase       -> EdFi_Security
4. Remove-SandboxDatabases      -> drop all Ods_Sandbox_*
5. Reset-MinimalTemplateDatabase -> EdFiMinimalTemplate
6. Reset-PopulatedTemplateDatabase -> EdFi_Ods (populated with sample data)
```

#### SingleTenant Mode

```
1. Install-Plugins
2. Reset-AdminDatabase          -> EdFi_Admin
3. Reset-SecurityDatabase       -> EdFi_Security
4. Reset-OdsDatabase            -> EdFi_Ods_{token} (per OdsToken)
```

#### MultiTenant Mode

```
For each tenant:
  1. Reset-AdminDatabase        -> EdFi_Admin_{tenant}
  2. Reset-SecurityDatabase     -> EdFi_Security_{tenant}
  3. Reset-OdsDatabase          -> EdFi_Ods_{tenant}_{token} (per token)
```

Each `Reset-*Database` call goes through the database lifecycle:

**File:** `logistics/scripts/modules/database/database-lifecycle.psm1`

```
Initialize-EdFiDatabase:
  1. Backup   -> (SQL Server only, if persistent + pending scripts)
  2. Remove   -> (if DropDatabases = true)
  3. Create   -> (restore from backup OR create new)
  4. Script   -> (run migrations via EdFi.Db.Deploy)
```

Strategy selection is engine-specific:

- **SQL Server:** Full backup/remove/create/script chain
- **PostgreSQL:** No-op backup, custom remove/create, psql-based script
- **Azure SQL:** Special create strategy, no backup

### Steps 13-17: Test Runners (All Optional)

Each test step starts/stops infrastructure as needed:

- **Pester:** Runs PowerShell unit tests
- **DotnetTest:** Runs .NET integration tests via `logistics/scripts/run-tests.ps1`
- **Postman:** Starts test harness -> runs Newman -> stops harness
- **SmokeTest:** Starts test harness -> runs smoke test client -> stops harness
- **SdkGen:** Starts test harness -> generates SDK from metadata -> stops harness

The test harness (`logistics/scripts/modules/TestHarness.psm1`) is an in-memory API host. It starts the `EdFi.Ods.Api.IntegrationTestHarness` project and polls until ready.

---

## Module Dependency Map

```
InitializeDevelopmentEnvironment.psm1
 |
 +-- settings-management.psm1          -- settings cascade, defaults, feature flags
 |    +-- key-management.psm1          -- AES key generation
 |    +-- public-private-key-pair.psm1 -- RSA key pairs for JWT
 |
 +-- Deployment.psm1                   -- deployment orchestration
 |    +-- database-lifecycle.psm1      -- backup/remove/create/script strategies
 |    |    +-- database-management.psm1     -- SQL Server operations (SMO)
 |    |    +-- postgres-database-management.psm1 -- PostgreSQL operations (psql/pg_dump)
 |    |    +-- ToolsHelper.psm1        -- .NET tool management, Invoke-DbDeploy
 |    +-- plugin-source.psm1           -- plugin discovery and installation
 |    +-- config-management.psm1       -- database IDs, features, connection strings
 |
 +-- TaskHelper.psm1                   -- task timing, error handling, TeamCity integration
 +-- hashtable.psm1                    -- deep merge, flatten, clone utilities
 +-- path-resolver.psm1               -- cross-repo path resolution
 |
 +-- create-minimal-template.psm1      -- minimal template creation
 +-- create-populated-template.psm1    -- populated template creation
 |    +-- create-database-template.psm1 -- shared template infrastructure
 |         +-- TestHarness.psm1        -- test harness lifecycle
 |         +-- LoadTools.psm1          -- bulk load client, smoke tests
 |
 +-- database-template-source.psm1     -- template source resolution
      +-- get-populated-from-nuget.ps1 -- download template from NuGet
      +-- get-populated-from-web.ps1   -- download template from web
      +-- get-template-from-web.ps1    -- generic template downloader
```

---

## Key Configuration Files

| File | Purpose | Generated? |
|------|---------|-----------|
| `configuration.packages.json` | NuGet package versions for tools and database scripts | No (version-controlled) |
| `appsettings.json` | Base application settings per project | No (version-controlled) |
| `appsettings.Development.json` | Dev overrides with connection strings, features, keys | **Yes** (by initdev step 4) |

### `configuration.packages.json` Structure

Contains version pins for:

- `EdFi.Suite3.Db.Deploy` (database migration tool)
- `EdFi.Suite3.Ods.CodeGen` (code generator)
- Database packages per engine and standard version (Admin, Security, ODS templates)
- `EdFi.Suite3.BulkLoadClient.Console`

---

## Database Engine Abstraction

The entire pipeline abstracts over two engines. The `-Engine` parameter propagates to:

| Concern | SQL Server | PostgreSQL |
|---------|-----------|------------|
| Connection string template | `Data Source=.; Initial Catalog={0}; Integrated Security=true; Encrypt=false` | `Host=localhost; Port=5432; Username=postgres; Database={0}` |
| Migration tool | `EdFi.Db.Deploy` with `-e SqlServer` | `EdFi.Db.Deploy` with `-e PostgreSql` |
| Database operations | SMO (SQL Management Objects) | `psql`, `pg_dump`, `pg_restore` CLI |
| Backup format | `.bak` | `.sql` (pg_dump plain text) |
| Template databases | Standard databases | PostgreSQL template databases (marked via `ALTER DATABASE ... IS_TEMPLATE`) |
| Sandbox removal | `DROP DATABASE` per sandbox | `SELECT pg_terminate_backend(...)` then `DROP DATABASE` |

---

## Install Types Deep Dive

### Sandbox (Default)

- Creates template databases that the SandboxAdmin app clones on-demand
- `EdFiMinimalTemplate` -> empty ODS schema
- `EdFi_Ods` (populated) -> sample data from GrandBend dataset
- Sandbox instances are named `Ods_Sandbox_{key}`

### SingleTenant

- One Admin + Security database
- N ODS databases, one per token (e.g., `EdFi_Ods_2024`, `EdFi_Ods_2025`)
- No sandbox cloning

### MultiTenant

- Per-tenant Admin + Security databases (`EdFi_Admin_{tenant}`, `EdFi_Security_{tenant}`)
- Per-tenant x per-token ODS databases (`EdFi_Ods_{tenant}_{token}`)
- Tenant configuration in `appsettings.Development.json` includes per-tenant connection strings

---

## Plugin System

Plugins extend the ODS with additional data models. This section is ODS reference context only.

**File:** `logistics/scripts/modules/plugin/plugin-source.psm1`

Flow:

1. Plugin scripts live in `Plugin/` directory
2. Each script returns a hashtable with NuGet package names and versions
3. `Get-Plugins` downloads plugin NuGet packages
4. `Install-Plugins` extracts them to `Plugin/` folder
5. The WebApi discovers plugins at runtime via `Plugin.Folder` in `appsettings.json`

Default plugins from `Get-EdFiDeveloperPluginSettings` are historical ODS reference context.

---

## Feature Flags

Stored in `Settings.FeatureManagement`, these control which API features and database subtypes are active:

| Feature | Default | Database Subtype |
|---------|---------|-----------------|
| ChangeQueries | true | `Changes` |
| OwnershipBasedAuthorization | true | `RecordOwnership` |
| Extensions | true | — |
| Composites | true | — |
| Profiles | true | — |
| MultiTenancy | false | — |
| IdentityManagement | false | — |

Features with database subtypes cause additional migration script folders to be included during `Initialize-EdFiDatabase`.

---

## File System Layout

```
Ed-Fi-ODS-Implementation/
+-- Initialize-PowershellForDevelopment.ps1    (developer entry point)
+-- configuration.packages.json                (NuGet version pins)
+-- Application/
|   +-- Ed-Fi-Ods.sln                         (.NET solution)
|   +-- SolutionScripts/
|   |   +-- InitializeDevelopmentEnvironment.psm1  (main orchestrator)
|   +-- EdFi.Ods.WebApi/                      (Web API project)
|   +-- EdFi.Ods.SandboxAdmin/               (Sandbox Admin project)
|   +-- EdFi.Ods.SwaggerUI/                  (Swagger UI project)
|   +-- EdFi.Ods.Api.IntegrationTestHarness/ (test harness project)
+-- InstallerPackages/
|   +-- EdFi.RestApi.Databases/
|       +-- Deployment.psm1                   (deployment orchestrator)
+-- DatabaseTemplate/
|   +-- Modules/                              (template creation modules)
|   +-- Scripts/                              (template scripts: GrandBend, EdFiMinimalTemplate, etc.)
+-- Plugin/                                   (plugin scripts and downloaded packages)
+-- logistics/scripts/modules/
|   +-- tasks/TaskHelper.psm1                 (task execution framework)
|   +-- settings/settings-management.psm1     (settings cascade)
|   +-- database/
|   |   +-- database-lifecycle.psm1           (strategy-based DB lifecycle)
|   |   +-- database-management.psm1          (SQL Server operations)
|   |   +-- postgres-database-management.psm1 (PostgreSQL operations)
|   +-- config/config-management.psm1         (DB IDs, features, paths)
|   +-- plugin/plugin-source.psm1             (plugin management)
|   +-- tools/ToolsHelper.psm1               (.NET tool management)
|   +-- packaging/                            (NuGet packaging)
|   +-- utility/                              (hashtable, cross-platform, keys)
|   +-- TestHarness.psm1                      (test harness lifecycle)
|   +-- LoadTools.psm1                        (bulk load, smoke tests)
+-- Docker/                                   (Docker images and compose files)
+-- SecurityMetadata/                         (auth metadata XML->SQL pipeline)
+-- tests/                                    (.NET integration test projects)
```

---

## Common Modification Scenarios

### Adding a new feature flag

1. Add to `Get-DefaultFeatures` in `settings-management.psm1`
2. If it has a database subtype, add to `Get-SubtypesByFeature`
3. The flag will automatically propagate to `appsettings.Development.json` and database script folder selection

### Adding a new database type

1. Add to `Get-DatabaseTypes` in `settings-management.psm1`
2. Add connection string key mapping in `Get-ConnectionStringKeyByDatabaseTypes`
3. Add lifecycle handling in `Deployment.psm1`

### Adding a new plugin

1. Create `Plugin/{name}.ps1` returning a hashtable with package info
2. Add the name to `Get-EdFiDeveloperPluginSettings` in `settings-management.psm1`

### Changing database migration behavior

1. Strategy functions in `database-lifecycle.psm1` control backup/remove/create/script
2. `Initialize-EdFiDatabase` calls strategies in order
3. `ToolsHelper.psm1` -> `Invoke-DbDeploy` runs the actual migration tool

### Adding a new test runner

1. Add a `-Run*` switch to `Initialize-DevelopmentEnvironment`
2. Create an `Invoke-*` function wrapping the test execution
3. Add an `Invoke-Task` call in the pipeline section of `Initialize-DevelopmentEnvironment`

---

## Known Issues

- **`Remove-ODSConnectionString`** in `settings-management.psm1` references a non-existent `DataAccessIntegrationTests` key, causing incorrect ODS connection string removal for some test projects.
- **`Update-PackageName`** in `database-template-source.psm1` references `$Settings` from parent scope — fragile implicit dependency.
- **`repositories.json`** is referenced in docs/code but is not present in the repo root; it may be generated at runtime or expected from a sibling repo.
- **`Application/EdFi.Ods.Standard`** and `Application/EdFi.Ods.Extensions.*` are referenced but live in separate repositories; they are resolved via the path-resolver module at runtime.

---

## Observations on the ODS InitDev Design

These notes describe patterns and trade-offs observed in the ODS pipeline. They are historical context for understanding what DMS-916 chose to adopt, adapt, or omit — not DMS requirements or forward-looking recommendations.

### Patterns that worked well in ODS

- **Single `$Settings` hashtable** flows through all steps — simple, debuggable, easy to override per layer
- **Strategy pattern for database lifecycle** — ODS cleanly separates engine-specific logic (SQL Server vs PostgreSQL) at the lifecycle layer
- **`Invoke-Task` wrapper** — provides uniform timing and error reporting across all 17 steps
- **Skip flags** (`-NoRebuild`, `-NoDeploy`) — reduce re-run cost after partial failures without restructuring the pipeline
- **`configuration.packages.json`** — single source of truth for tool and package version pins

### Trade-offs and constraints in the ODS design

- **Implicit scope dependencies** — several modules reference `$Settings` from parent scope rather than receiving it as a parameter; creates implicit coupling that complicates isolated testing
- **No resume or checkpoint** — a failure at step 12 requires re-running from step 1
- **Deep module nesting** — 18 imported modules with cross-dependencies make individual modules hard to exercise in isolation
- **Settings merge complexity** — the multi-layer deep merge (parameters -> config -> defaults -> plugins -> overrides) is powerful but makes it hard to trace which layer supplied a given value
- **No dry-run mode** — ODS provides no mechanism to preview what the pipeline would do without executing it
- **NuGet coupling** — tool installation, plugin resolution, and template downloads all go through NuGet; alternative package registries require re-abstraction at each of these points
