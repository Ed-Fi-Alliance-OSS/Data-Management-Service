# DMS-1119: Sysadmin Seed Data Provisioning — API-Based Seed Loading for DMS Deployments (DRAFT)

> **Status: DRAFT / WIP** — This document extracts sysadmin and agency deployment
> concerns from the broader DMS-916 bootstrap design, focusing on multi-tenancy,
> credential scoping, production deployment procedures, and agency seed data
> packaging. Content needs review and refinement for the sysadmin/agency persona.

## 1. Introduction

This document covers the **sysadmin and education agency deployment persona**: agencies
deploying DMS in production or staging environments — including those with extensions
— need a supported, versioned, and repeatable way to load seed and reference data
through the DMS API instead of relying on direct SQL access. The API-based loading
approach enables deployments in environments where direct database access is
restricted or unavailable, and ensures that seed data is validated against the live
API just like application data.

This document was split from DMS-916, which covers the **developer bootstrap
experience** (see `reference/design/initdev/initdev-design.md`). DMS-916 focuses on
getting a local development environment running quickly with sensible defaults;
DMS-1119 focuses on the concerns specific to deploying a production or agency-managed
DMS instance with extension-aware, versioned seed data packages.

### 1.1 Relationship to DMS-916

Shared infrastructure is documented in DMS-916 and applies here **by reference**:

| Concern | DMS-916 Section | Notes |
|---------|----------------|-------|
| BulkLoadClient interface spec (CLI flags, auth, JSONL format, error handling) | Section 6.1 | Contract for ODS team; see ODS-6738 |
| Seed data NuGet package format and naming conventions | Section 6.2.1 | `EdFi.Dms.Seed.<Template>.<Version>.nupkg` |
| Core seed packages (Minimal, Populated) | Section 6.2.2 | Descriptor-only vs. sample data |
| Extension seed packages and numeric prefix ranges (01–49 core, 50–99 extensions) | Section 6.2.4 | Collision detection before BulkLoadClient invocation |
| `-LoadSeedData` bootstrap behavior (resolve, download, merge, invoke, check, clean up) | Section 6.3.1 | 5-step sequence within the bootstrap pipeline |
| Credential bootstrapping (SeedLoader claimset, namespace prefixes) | Section 7 | Seed Loader application separate from smoke test |
| `-Extensions` parameter (schema, claimset, and seed selection) | Section 8 | Drives three coordinated actions automatically |
| Deprecation of direct-SQL path (`setup-database-template.psm1`) | Section 6.4 | Removal checklist for implementation ticket |

This document focuses on concerns **not covered** in DMS-916: production deployment
procedures, agency-specific seed data packaging, multi-tenant credential scoping
strategies, and seed data versioning lifecycle.

## Table of Contents

- [1. Introduction](#1-introduction)
- [2. Multi-Tenant Seed Data and Credential Scoping](#2-multi-tenant-seed-data-and-credential-scoping)
- [3. Production Deployment Procedures](#3-production-deployment-procedures)
- [4. Agency Seed Data Packaging](#4-agency-seed-data-packaging)
- [5. Seed Data Versioning and Lifecycle](#5-seed-data-versioning-and-lifecycle)
- [6. Follow-Up Implementation Tickets](#6-follow-up-implementation-tickets)

## 2. Multi-Tenant Seed Data and Credential Scoping

### 2.1 Multi-Tenancy Dimensions

DMS supports two dimensions of instance partitioning relevant to seed data loading:

- **School-year partitioning** — The `-SchoolYearRange` parameter (e.g., `"2022-2026"`)
  creates one DMS instance per school year. Each instance has a `schoolYear` route
  context attached via `Add-DmsInstanceRouteContext` in `Dms-Management.psm1`.
- **Explicit tenant partitioning** — `Add-Tenant` in `Dms-Management.psm1` creates a
  named tenant in the Config Service. Tenant-aware deployments combine a tenant
  identifier with instance routing.

For developer bootstrap, school-year partitioning is the common case. For production
agency deployments, explicit tenant partitioning is more relevant — agencies may
operate multiple tenants (e.g., per-district or per-SEA) each with distinct extension
configurations and seed data requirements.

### 2.2 Per-Instance Seed Data Loading

When multiple instances exist (school-year or tenant-based), seed data must be loaded
into each instance independently. The BulkLoadClient `--year` flag routes requests to
a school-year-specific instance (see DMS-916 Section 6.1.1). The loading script loops
over the instance set and invokes BulkLoadClient once per instance:

```powershell
foreach ($year in $SchoolYearRange) {
    EdFi.BulkLoadClient `
        --input-format jsonl `
        --data        $tempSeedDir `
        --base-url    $dmsBaseUrl `
        --token-url   $cmsTokenUrl `
        --key         $seedKey `
        --secret      $seedSecret `
        --year        $year
}
```

Key points:

- The same `$tempSeedDir` (merged core + extension JSONL files) is reused for every
  instance — only the target differs. Seed packages are downloaded and extracted once
  outside the loop.
- If any BulkLoadClient invocation exits non-zero, the script halts before proceeding
  to subsequent instances.
- When only one instance exists, BulkLoadClient is invoked once without `--year`.
- The credential variables `$seedKey` / `$seedSecret` are created before the loop and
  shared across all invocations (see [2.4 Credential Scoping](#24-credential-scoping-across-instances)).

### 2.3 Per-Tenant Extension Configuration

The database uses a **superset schema** — all extension tables are deployed regardless
of tenant configuration or the `-Extensions` parameter value. Schema provisioning runs
once per environment, not once per tenant.

Extension selection controls the **API surface** available to each tenant, not the DDL.
The `-Extensions` parameter (defined in DMS-916 Section 8) applies globally during
bootstrap: the same extension claimsets and ApiSchema overlays are active for all
instances created during a given bootstrap run.

Per-tenant extension overrides — where Tenant A exposes TPDM resources and Tenant B
does not — are a **Config Service configuration concern**, not a bootstrap script
concern. The Config Service stores per-tenant settings (claimset assignments, API
resource visibility) that control which endpoints are reachable for each tenant at
runtime. The bootstrap script provisions the superset and leaves runtime routing to
the Config Service.

**Summary of responsibility boundaries:**

| Concern | Owner |
|---------|-------|
| Extension DDL (CREATE TABLE for extension tables) | Schema deployment hook (once per environment) |
| Extension claimsets loaded at container startup | `-Extensions` parameter → temp directory mount |
| Per-tenant API surface (which endpoints are exposed) | Config Service runtime configuration |
| Extension seed data loading | Bootstrap loop — same JSONL files per instance |

### 2.4 Credential Scoping Across Instances

Seed loading credentials must be able to POST resources to every tenant and instance
in the environment. Two options:

**Option A — Single seed-loader application with cross-instance permissions (recommended for simple deployments)**

A single `Seed Loader` application record is created once during credential bootstrap
(see DMS-916 Section 7). The application is bound to the education organization IDs
and namespace prefixes that cover all instances. The same `$seedKey` / `$seedSecret`
pair is passed to every BulkLoadClient invocation.

This approach is simple to implement, requires no changes to the credential bootstrap
loop, and is consistent with how the smoke test application works today. It is
appropriate for development environments and production deployments with a single
agency or simple topology.

**Option B — Separate credentials per tenant (recommended for multi-agency production)**

Create a distinct vendor/application/key-secret pair for each tenant or school-year
instance. Each BulkLoadClient invocation uses the credentials specific to that
instance. This provides stronger isolation — a credential compromise in one tenant
does not affect others — but requires the provisioning script to loop over credential
creation as well as seed loading, and requires the Config Service to support
per-instance application scoping cleanly.

Option B is appropriate for production multi-tenant deployments where agencies require
credential isolation, such as federated deployments with multiple SEAs each managing
their own instances. When using Option B, namespace prefixes must be partitioned
across agencies to prevent one tenant's credentials from accessing another tenant's
resources.

## 3. Production Deployment Procedures

> **Status: Placeholder** — This section outlines the production deployment workflow
> at a conceptual level. Detailed procedures will be developed in follow-up tickets.

### 3.1 Pre-Flight Validation

Before invoking BulkLoadClient against a production environment, the deployment
script or sysadmin should verify:

- **BulkLoadClient is installed** and available on `$PATH` (see DMS-916 Section 6.3.3)
- **DMS and CMS are healthy** — poll health endpoints before proceeding
- **Credentials are valid** — perform a test token request against the CMS token
  endpoint before starting the seed load
- **Schema version compatibility** — verify the loaded ApiSchema version matches the
  seed data package version (e.g., Data Standard 5.2 seed data against a 5.2 schema)
- **Network connectivity** — verify the host can reach DMS and CMS endpoints

### 3.2 Seed Loading Invocation

Production seed loading uses the same BulkLoadClient interface as developer bootstrap
(DMS-916 Section 6.1), but typically with:

- Explicit credential management (Option A or B from Section 2.4)
- `--continue-on-error` for idempotent re-runs (409 Conflict on duplicates is non-fatal)
- Per-instance `--year` loops for school-year-partitioned deployments
- Audit logging of the invocation (who, when, which seed package version, which instance)

### 3.3 Post-Load Validation

After seed loading completes:

- **Verify record counts** — query DMS API endpoints to confirm expected descriptor
  counts, education organization counts, etc.
- **Smoke test** — run `Invoke-NonDestructiveApiTests.ps1` or equivalent against
  the loaded environment using smoke test credentials
- **Log the outcome** — record seed package version, load timestamp, success/failure
  status, and any 4xx/5xx errors encountered

### 3.4 Rollback Strategy

If seed loading fails mid-run or produces corrupt data:

- **Partial failure with `--continue-on-error`**: Inspect BulkLoadClient logs to
  identify which files/resources failed. Re-run with corrected data or fix the
  underlying issue and re-invoke (409s on already-loaded records are safe).
- **Full rollback**: Restore the database from a pre-seed snapshot. Production
  environments should take a database snapshot before seed loading begins.
- **Descriptor corruption**: If descriptors are loaded incorrectly, the API will
  reject dependent resources. Fix the descriptor data and re-run; the API's
  referential integrity checks prevent cascading corruption.

## 4. Agency Seed Data Packaging

> **Status: Placeholder** — This section outlines the agency packaging workflow at
> a conceptual level. Detailed procedures will be developed in follow-up tickets.

### 4.1 Custom Extension Seed Packages

Agencies deploying DMS with custom extensions (beyond the Ed-Fi provided TPDM, Sample,
Homograph) need to produce their own seed NuGet packages. The packaging format is
defined in DMS-916 Section 6.2:

- Package ID pattern: `EdFi.Dms.Seed.<ExtensionName>.<Version>.nupkg`
- JSONL files use numeric prefixes in the `50–99` range (extension range)
- Each line is a complete JSON resource body with a `resourceType` field
- Files are UTF-8 encoded, one JSON object per line

### 4.2 Namespace Prefix Allocation

Agencies creating custom extensions must register namespace prefixes to avoid
collisions:

- Core Ed-Fi uses `uri://ed-fi.org`
- TPDM uses `uri://tpdm.ed-fi.org`
- Agency extensions should use agency-specific URIs (e.g., `uri://myagency.edu`)

When packaging seed data for custom extensions, all resource bodies must use the
agency's registered namespace prefix. The Seed Loader application's namespace prefix
list must include the agency prefix (see DMS-916 Section 7.2 for dynamic namespace
prefix computation).

### 4.3 Numeric Prefix Coordination

Multiple extension seed packages share the `50–99` range. Agencies must coordinate
prefix assignments to avoid collisions:

| Range | Reserved For |
|-------|-------------|
| 50–59 | TPDM extension seeds |
| 60–69 | Sample extension seeds |
| 70–79 | Homograph extension seeds |
| 80–99 | Agency-specific extensions |

The bootstrap script detects numeric prefix collisions before invoking BulkLoadClient
and aborts with a clear error. Agencies should verify their prefix assignments before
publishing packages.

## 5. Seed Data Versioning and Lifecycle

> **Status: Placeholder** — This section outlines versioning concerns at a conceptual
> level. Detailed design will be developed in follow-up tickets.

### 5.1 Version Alignment

Seed data packages are tied to a specific Ed-Fi Data Standard version. The package
version (e.g., `5.2.0`) must match the ApiSchema version loaded by the DMS instance.
Loading 5.1 seed data against a 5.2 schema — or vice versa — may produce validation
errors or missing resources.

The bootstrap script should validate version compatibility before invoking
BulkLoadClient. The mechanism for this check (comparing package metadata to the
loaded schema version) is a follow-up design concern.

### 5.2 Mid-Year Updates

Agencies may need to update seed data mid-year (e.g., new descriptors added by a
state education agency). The update workflow:

1. Publish a new version of the seed NuGet package with updated JSONL files
2. Invoke BulkLoadClient with `--continue-on-error` against the running instance
3. Existing records return 409 (safe); new records are created
4. Verify updated record counts via API

This workflow is idempotent by design — the same package can be re-applied safely.

### 5.3 Package Lifecycle

Seed packages follow standard NuGet versioning. Agencies should:

- Use semantic versioning aligned with the Ed-Fi Data Standard version
- Mark deprecated packages as unlisted on the NuGet feed
- Document which seed package versions are compatible with which DMS releases

## 6. Follow-Up Implementation Tickets

The tickets below capture implementation work specific to the sysadmin and agency
deployment persona covered by this document. Shared infrastructure tickets
(BulkLoadClient JSONL support, multi-year seed loading loop, seed NuGet package
creation) are tracked in DMS-916 Section 13 and are not duplicated here.

### 6.1 Cross-Team Dependencies

| Dependency | Owner | DMS-916 Reference |
|-----------|-------|-------------------|
| BulkLoadClient JSONL support (`--input-format jsonl`) | ODS team (ODS-6738) | Section 6.1 |
| Core seed NuGet packages (Minimal, Populated) | DMS team | Ticket 1 |
| Multi-year seed loading loop (`--year` flag) | DMS team | Ticket 6 |
| Seed Loader credential bootstrap (`SeedLoader` claimset) | DMS team | Ticket 7 |

### 6.2 DMS-1119 Tickets

**1. Create extension seed data NuGet packages (e.g., TPDM)**
Produce one or more extension-scoped seed NuGet packages (e.g., `EdFi.Dms.Seed.TPDM`)
containing JSONL seed files in the `50–99` numeric range per the convention in
[Section 4.3](#43-numeric-prefix-coordination). Each package must be usable
independently alongside the core seed packages and must not reuse numeric prefixes
occupied by other extension packages.

**2. Document agency seed data packaging workflow**
Create documentation covering the workflow for education agencies to produce and
publish their own extension seed data NuGet packages, including naming conventions,
numeric prefix range assignments (Section 4.3), namespace prefix registration
(Section 4.2), and the process for registering packages with the DMS bootstrap
tooling.

**3. Write deployment guide for sysadmin seed loading**
Create a deployment guide for sysadmins covering the end-to-end process of loading
seed data into a production or staging DMS environment using the BulkLoadClient and
NuGet seed packages. Include pre-flight checks (Section 3.1), invocation examples,
post-load validation (Section 3.3), rollback procedures (Section 3.4), and
troubleshooting steps.

**4. Create production credential provisioning guide**
Document the process for provisioning seed loader credentials in production
environments, covering both Option A (single cross-instance application) and Option B
(per-tenant credentials) from [Section 2.4](#24-credential-scoping-across-instances),
with guidance on when each option is appropriate.

**5. Implement seed data version compatibility check**
Add a pre-flight check to the seed loading workflow that validates the seed package
version against the loaded ApiSchema version before invoking BulkLoadClient. Exit with
a clear error if the versions are incompatible (Section 5.1).

**6. Set up NuGet feed infrastructure for agency seed data distribution**
Establish NuGet feed hosting, access control, and package lifecycle policies for
distributing seed data packages to agencies. Includes feed discovery, authentication,
and CI/CD pipeline for publishing agency-created seed packages.
