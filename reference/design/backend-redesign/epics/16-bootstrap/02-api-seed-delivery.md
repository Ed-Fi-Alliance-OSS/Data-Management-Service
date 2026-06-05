---
jira: DMS-1152
jira_url: https://edfi.atlassian.net/browse/DMS-1152
---

# Story: API-Based XML Seed Delivery for Bootstrap

## Description

Implement the story-aligned API-based seed path for developer bootstrap. This slice delivers the API-based
loading flow through the repo-pinned BulkLoadClient resolution path; it is the forward replacement for the
current direct-SQL bootstrap intent. Because the planned new seed-file format will not land in time, the
DMS-916 seed payload contract is legacy ODS XML interchange files. The target state remains replacement of
the direct-SQL path, not supplementation. Per bootstrap-design.md §6.4 (line 1250), the deletion of the
direct-SQL path is **gated on Story 04 XSD-staging verification**; this slice ships the API-based flow
alongside the retained `setup-database-template.psm1` until the gate closes.

The slice includes seed-source selection, the dedicated DMS-dependent `SeedLoader` credential flow for the
seed-delivery phase, a deterministic REST precondition that POSTs the required `SchoolYearType` rows before
any XML pass (because v5.x data standards model `SchoolYearType` as a closed XSD enumeration that no bulk
interchange can carry), per-year loading for the existing `-SchoolYearRange` workflow, and the bootstrap-side
guardrails needed to keep the seed path deterministic with the existing BulkLoadClient XML mode. The
authorization contract for built-in seed delivery is also part of this slice: the design fixes one
authoritative `SeedLoader` inventory for core templates and requires built-in extension seed-package loading
to travel with extension security metadata rather than ad hoc grant derivation. CMS-only smoke-test credential
creation is a separate workflow concern: it may run earlier against the target data stores selected by
`configure-local-data-store.ps1`, but it is not part of this story's dependency chain and its credentials
are never reused for seed delivery.

Custom `-SeedDataPath` directories remain supported as compatible payload sources when the run's root
bootstrap manifest and staged schema/security inputs are intended to cover them, and
`-AdditionalNamespacePrefix` may declare agency or custom namespace prefixes for SeedLoader vendor
authorization. Bootstrap does not preflight-certify arbitrary XML payload content.

This slice is the single owner of built-in seed-package advertisement. An extension may advertise a built-in
seed package only when this story's `SeedLoader` contract is delivered end to end. This story defines only the
DMS bootstrap consumer boundary for BulkLoadClient: the pinned-resolution path, the invocation shape DMS
depends on, and the pass-through result handling. It also owns the package-backed `Minimal` and `Populated`
developer seed templates used by `-SeedTemplate`, materialized into the ignored `.bootstrap` workspace at
runtime. The API-based XML seed path applies to both local and published DMS images: the
`bootstrap-local-dms.ps1` and `bootstrap-published-dms.ps1` wrappers sequence the corresponding start script
followed by `load-dms-seed-data.ps1`. The legacy direct-SQL `setup-database-template.psm1` path remains on
`start-local-dms.ps1` / `start-published-dms.ps1` pending the bootstrap-design.md §6.4 Story 04 XSD-staging
gate; its deletion is out of scope for this story. This story does not broaden DMS-916 into owning
BulkLoadClient product design or non-bootstrap runtime behavior.

## Implementation Readiness Gates

The implementation target is ODS XML interchange loading through the repo-pinned BulkLoadClient XML mode.
End-to-end implementation is gated by DMS-owned compatibility work:

- The embedded top-level `SeedLoader` claim set and required core permissions must be added before any
  built-in seed source can be advertised or exercised. This is the first DMS-owned deliverable from this
  story even though the full credential and seed-loading flow lands later in the slice.
- The repo-pinned BulkLoadClient XML mode must work against DMS discovery, dependency metadata, OAuth, XSD
  metadata or staged XSD files, and data endpoints.
- Story 04 must make file-based XSD endpoint behavior BulkLoadClient-compatible for the bootstrap path, or
  seed loading must pass a staged XSD directory with `-x`.
- Built-in `Minimal` and `Populated` seed XML must resolve from the pinned `Ed-Fi-Data-Standard` GitHub
  repository tag (default `v5.2.0`) and be materialized into the ignored `.bootstrap` workspace at runtime.
  Bulk-load XSDs for the built-in templates are sourced from the same tag (`Schemas/Bulk/`) so the XML and
  XSD versions are aligned by construction and BulkLoadClient runs with XSD validation enabled.

These gates do not introduce a second normative seed mode. The direct-SQL path is **not** the design target
state; it is the current gap this story closes through API-based XML loading. While `setup-database-template.psm1`
remains operationally retained until the §6.4 Story 04 gate closes, no DMS-916 artifact normalizes the legacy
direct-SQL path as an ongoing or permanent alternative.

## Acceptance Criteria

- `-LoadSeedData` remains a wrapper-level opt-in. When it is absent from `bootstrap-local-dms.ps1`,
  wrapper bootstrap does not invoke seed delivery. Direct invocation of `load-dms-seed-data.ps1` always
  loads seed data and does not accept `-LoadSeedData`.
- When seed delivery runs, bootstrap resolves BulkLoadClient through the repo-pinned package path rather than
  through a global tool or `$PATH` requirement.
- Bootstrap fails fast if the pinned BulkLoadClient package cannot be resolved or if the XML loading
  interface required by this story is unavailable.
- Bootstrap also fails fast when a built-in seed source is selected but the pinned Ed-Fi-Data-Standard
  repository tag cannot be fetched or extracted into the ignored `.bootstrap` workspace.
- Supported seed inputs match the design:
  - repo-tag-backed Ed-Fi XML seed templates selected by `-SeedTemplate`,
  - developer-supplied XML interchange directories selected by `-SeedDataPath`.
- When seed delivery runs without an explicit seed-source flag, bootstrap defaults to the built-in `Minimal`
  seed template in standard Modes 1 and 2 only.
- In expert `-ApiSchemaPath` mode, seed delivery requires explicit `-SeedDataPath`; bootstrap must not fall
  back to built-in `Minimal` or `Populated` seed templates in that mode.
- In expert `-ApiSchemaPath` mode, `-SeedTemplate` is invalid because bootstrap-managed seed selection is
  disabled.
- `-SeedTemplate` and `-SeedDataPath` are mutually exclusive. Providing both is a bootstrap validation
  error rather than an implementation-defined precedence rule.
- The built-in seed inventories are authoritative and deterministic and mirror the pinned
  Ed-Fi-Data-Standard repository tag contents:
  - `Minimal` loads the core descriptor tier: every `*.xml` file from the repo's `Descriptors/` directory.
    `SchoolYearType` rows for years 1991 through 2037 are created by a REST precondition
    (`POST /data/ed-fi/schoolYearTypes`) rather than through XML interchange, because v5.x data
    standards model `SchoolYearType` as a closed XSD enumeration that is not loadable through any bulk
    interchange XSD. Because the original bootstrap design treated `schoolYearType` as a non-endpoint
    type, the parent `edFiTypes` claim defaultAuthorization only grants `Read`. The SeedLoader `Create`
    grant on `http://ed-fi.org/identity/claims/ed-fi/schoolYearType` therefore declares an explicit
    `authorizationStrategyOverrides` entry of `NoFurtherAuthorizationRequired` — without it the REST
    precondition POST resolves to zero authorization strategies and 403s before any seed XML is loaded.
    This is the only SeedLoader grant in the embedded `Claims.json` that carries an override; see
    `bootstrap-design.md` §7.2 "schoolYearType override exception" for the design rationale.
  - `Populated` loads `Minimal` plus every non-`*Descriptor.xml` interchange from the repo's
    `Samples/Sample XML/` directory, and also routes every `*Descriptor.xml` file in `Samples/Sample XML/`
    into the descriptors tier so sample-only descriptor values load before the resource pass. When a
    sample-side descriptor file name collides with one from `Descriptors/` (e.g. `DiagnosisDescriptor.xml`
    at v5.2.0), the sample-side payload wins because it carries the richer Populated-tier descriptor values.
    This covers the full v5.x sample inventory — including (but not limited to)
    `EducationOrganization.xml`, `EducationOrgCalendar.xml`, `MasterSchedule.xml`, `Standards.xml`,
    `StaffAssociation.xml`, `Contact.xml`, `Student.xml`, `StudentEnrollment.xml`, `StudentProgram.xml`,
    `StudentDiscipline.xml`, `StudentHealth.xml`, `StudentTranscript.xml`, `StudentGradebook.xml`,
    `StudentSchoolAttendance.xml`, `StudentSectionAttendance.xml`, the `StudentGrade-*` interchanges,
    every `AssessmentMetadata-*` and `StudentAssessment-*` interchange, `Finance.xml`, `Survey.xml`,
    `PostSecondaryEvent.xml`, and `StudentTransportation.xml`. Top-level resources emitted span
    `AccountabilityRating`, `BellSchedule`, `Calendar`, `CalendarDate`, `ClassPeriod`,
    `CommunityOrganization`, `CommunityProvider`, `CommunityProviderLicense`, `Contact`,
    `Course`, `CourseOffering`, `CourseTranscript`, `CrisisEvent`, `EducationServiceCenter`,
    `Grade`, `GraduationPlan`, `GradingPeriod`, `LearningStandard`, `LocalAccount`,
    `LocalEducationAgency`, `Location`, `OrganizationDepartment`, `Person`, `PostSecondaryInstitution`,
    `Program`, `Section`, `School`, `Session`, `Staff`, `StaffEducationOrganizationAssignmentAssociation`,
    `StaffEducationOrganizationEmploymentAssociation`, `StaffSectionAssociation`, `Student`,
    `StudentAcademicRecord`, `StudentAssessment`, `StudentAssessmentRegistration`,
    `StudentEducationOrganizationAssociation`,
    `StudentEducationOrganizationResponsibilityAssociation`, `StudentHealth`,
    `StudentSchoolAssociation`, `StudentSchoolAttendanceEvent`, `StudentSectionAssociation`,
    `StudentSectionAttendanceEvent`, and the various student program associations (CTE, Migrant,
    Special Education, Homeless, Language Instruction, School Food Service, Neglected or Delinquent),
    among others present in the sample package.
- Both built-in inventories run as a deterministic two-step sequence:
  1. A REST precondition POSTs the required `SchoolYearType` rows for the configured year range against
     `POST /data/ed-fi/schoolYearTypes` using the `SeedLoader` credential. This step is required by both
     `Minimal` and `Populated` because v5.x data standards model `SchoolYearType` as a closed XSD enumeration
     that is not loadable through bulk XML.
  2. BulkLoadClient is invoked against a staged XML interchange directory. For `Minimal`, that directory
     contains only descriptor interchanges. For `Populated`, bootstrap invokes BulkLoadClient twice in
     order — first against the descriptor directory, then against the resource directory — so that the
     descriptor tier is fully materialized before any resource references attempt to resolve descriptors.
- The embedded top-level `SeedLoader` claim set includes the core permissions required by those built-in
  inventories. For `Populated`, this covers the descriptor and `schoolYearType` claims required by
  `Minimal` plus the resource-domain claims needed to write the full Populated inventory: `people`,
  `relationshipBasedData`, `educationOrganizations`, `primaryRelationships`, `educationStandards`,
  `assessmentMetadata`, `surveyDomain`, `educationContent`, `finance`, `studentHealth`,
  `communityProviderLicense`, `tpdm`, and `identity`.
- `-SeedDataPath` bypasses seed-package download, but schema/security compatibility still comes from the
  run's root bootstrap manifest, which records the staged schema/security inputs selected earlier, including
  selected extensions and staged claims fingerprints.
- `-SeedDataPath` is supported when the run's root bootstrap manifest and staged schema/security inputs are
  intended to cover that data.
- The default `SeedLoader` application credentials minted for a Phase 1 run are scoped to the
  v5.x Sample Data top-level EdOrg envelope (the LEA, SEA, and standard school IDs shipped in
  the pinned Ed-Fi-Data-Standard Sample XML inventory). `-SeedDataPath` payloads referencing
  EdOrgs outside that envelope surface as `403 Forbidden` from resources whose claims use the
  `RelationshipsWithEdOrgsAndPeople` authorization strategy (for example, `Section`,
  `CourseOffering`, `StudentSchoolAssociation`). DMS-1152 does not expose a per-run custom
  EdOrg-scope parameter on `load-dms-seed-data.ps1`; out-of-envelope custom payloads must either
  reduce to the default envelope or be loaded against credentials provisioned outside this
  story's scope.
- `load-dms-seed-data.ps1` reads `eng/docker-compose/.bootstrap/bootstrap-manifest.json` by default, or an
  explicit `-BootstrapManifestPath` override, before resolving seed sources. It fails fast when the manifest
  is missing, malformed, unsupported, incomplete, or incompatible with the requested seed-source flags.
- The root bootstrap manifest remains intentionally small: version, schema selection mode (`Standard` or
  `ApiSchemaPath`), selected extensions, `EffectiveSchemaHash`, ApiSchema and claims fingerprints,
  relative ApiSchema manifest path, relative claims directory, expected claims-verification checks, and
  extension namespace prefixes. It must not carry built-in seed-package entries, resource definitions, claim
  grants, data store IDs, credentials, URLs, Docker state, environment settings, seed file paths, phase progress,
  or resume checkpoints.
- Bootstrap does not inspect arbitrary `-SeedDataPath` XML files to certify authorization completeness or
  derive new claims from payload content. Payload-level authorization or schema mismatches remain
  BulkLoadClient or DMS runtime failures.
- `-AdditionalNamespacePrefix` is an optional additive seed-phase input. The `SeedLoader` vendor namespace
  prefix list is the baseline seed prefixes plus selected extension prefixes plus any additional values,
  de-duplicated before vendor creation. This parameter does not replace baseline prefixes, infer extensions,
  or synthesize claims.
- When selected extensions are present in the bootstrap manifest and `-SeedDataPath` is not supplied,
  bootstrap resolves any built-in extension seed packages advertised by the seed catalog and fails fast if an
  advertised package is missing.
- Extensions without a built-in seed package in the seed catalog remain schema/security-only unless the
  developer supplies `-SeedDataPath`.
- When seed delivery runs with an extension that has no built-in seed package in the seed catalog, bootstrap
  emits an informational warning rather than silently implying extension seed data was loaded.
- When a built-in extension seed source is selected, the selected extension security fragment(s) must also
  attach the required `SeedLoader` permissions for every resource emitted by that extension's seed package.
- Bootstrap stages the selected XML interchange files into tier-specific ignored BulkLoadClient data
  directories before invocation, materializing BulkLoadClient-discoverable target paths such as
  `InterchangeName.xml`, `InterchangeName-*.xml`, and `InterchangeName/*.xml`. Already-compatible names are
  preserved; non-contract wrapper directories may be stripped only when the file, immediate parent folder,
  selected source directory, XML root element, or `xsi:schemaLocation` identifies a known interchange from core
  or extension interchange XSD filenames. The staging step must fail before CMS SeedLoader credential creation
  or BulkLoadClient invocation if a source XML cannot be mapped to a known interchange or if two source files
  would materialize to the same relative path.
- Seed loading relies on BulkLoadClient dependency and interchange ordering metadata, not bootstrap-defined
  filename prefixes or numeric ordering.
- Seed loading surfaces the tool's terminal summary or terminal error diagnostics; bootstrap passes those
  diagnostics through rather than inventing a second accounting layer or a DMS-owned result taxonomy.
- `configure-local-data-store.ps1` is the sole phase that creates or selects DMS data store IDs for the
  run. `SeedLoader` credential bootstrap and every BulkLoadClient invocation receive those data store IDs
  via in-memory forwarding within a single wrapper invocation, or via explicit `-DataStoreId`/`-SchoolYear`
  selectors in a manual phase flow; they must not perform their own CMS data store creation, broad
  target-selection policy, or non-selector-driven discovery pass. The only permitted no-selector case is
  the existing phase-command rerun convenience: if CMS contains exactly one route-unqualified data store
  in the current tenant scope, seed delivery may auto-select that data store. Zero data stores, multiple
  data stores, or one route-qualified data store must fail fast with selector guidance.
  See [`EPIC.md`](EPIC.md) Scope Guardrails for the selector resolution rules.
- BulkLoadClient invocation uses:
  - the DMS base URL supplied to `load-dms-seed-data.ps1 -DmsBaseUrl` for the current flow,
  - the OAuth URL derived from `load-dms-seed-data.ps1 -IdentityProvider`,
  - the bootstrap-created key/secret for the dedicated `SeedLoader` application,
  - a writable BulkLoadClient working directory,
  - either a staged XSD directory or a DMS XSD metadata endpoint compatible with BulkLoadClient XML mode.
- `load-dms-seed-data.ps1` accepts `-EnvironmentFile` and uses the shared local-settings helper to resolve
  CMS URL, auth defaults, tenant scope, and the Docker-local DMS URL. Direct seed invocation does not rely on
  process environment variables left behind by an earlier `start-local-dms.ps1` invocation.
- `load-dms-seed-data.ps1` consumes schema mode, selected extensions, and extension namespace prefixes from
  the bootstrap manifest. It owns seed-catalog lookup for built-in extension seed-package metadata,
  does not accept `-Extensions`, `-ApiSchemaPath`, or `-ClaimsDirectoryPath`, and must not infer those values
  from prior shell state or by inspecting XML payloads.
- Docker-hosted seed loading may use the DMS base URL resolved from the selected env file, normally
  `http://localhost:8080`. IDE-hosted seed loading must pass the IDE DMS endpoint explicitly; when the thin
  wrapper is orchestrating
  `-InfraOnly -DmsBaseUrl -LoadSeedData`, it forwards the same `-DmsBaseUrl` value to this phase after the
  post-provision health wait succeeds, and it forwards the selected `-IdentityProvider` value so this
  phase resolves the matching OAuth endpoint.
- The `SeedLoader` credential flow is separate from the CMS-only smoke-test credential flow, includes the
  baseline seed namespaces plus the selected extension namespace prefixes plus any
  `-AdditionalNamespacePrefix` values, and does not reuse or depend on smoke-test credentials.
- Wrapper `-LoadSeedData` does not require `-AddSmokeTestCredentials`; smoke-test credential creation
  remains an independent CMS-only workflow concern outside this story's dependency path.
- The bootstrap wrapper materializes `eng/docker-compose/.bootstrap/.env.derived` with seed-delivery
  tolerant overrides (`FAILURE_RATIO=0.95` for the BulkLoadClient circuit breaker, plus a
  Sample/Homograph `SCHEMA_PACKAGES` filter when no `-SeedDataPath` is supplied) before invoking the
  start and seed phases. This is a wrapper-only convenience for the common developer path. Direct
  phase invocation (`pwsh load-dms-seed-data.ps1 -EnvironmentFile <path>`) does not materialize
  `.env.derived`; callers must supply an env file already configured for seed delivery. The
  derived-env behavior remains a wrapper-level concern so each phase command keeps its supplied env
  file authoritative, consistent with the `command-boundaries.md` phase-ownership contract.
- When `-SchoolYearRange` is used, bootstrap stages each tier's seed workspace per tier and invokes
  BulkLoadClient once per tier per year against the route-qualified DMS base URL and matching
  OAuth URL for each `(tier, year)` pair. The per-tier staging is intentional because descriptor and
  resource tiers source from different directories; descriptor rows persist between tier invocations,
  so wiping the staged XML between passes is safe.
- In that existing school-year workflow, the school-year segment appears before `/data` (for example,
  `{base-url}/{schoolYear}/data/ed-fi/students`); bootstrap must not invent an ODS-style
  `{base-url}/data/v3/{year}/...` convention.
- When self-contained CMS identity is selected for that flow, the OAuth URL carries the matching context
  path at `/connect/token/{schoolYear}`. Keycloak token URLs remain provider-native.

## Tasks

1. Implement repo-pinned BulkLoadClient resolution and pre-flight validation for the XML bootstrap path.
2. Implement seed-source selection for `-SeedTemplate` and `-SeedDataPath` by reading the root bootstrap
   manifest first, including the default `Minimal` behavior when seed delivery runs without an explicit
   seed-source flag in standard Modes 1 and 2, the expert-mode rule that manifest
   `schema.selectionMode = ApiSchemaPath` requires explicit `-SeedDataPath` for seed delivery,
   validation that rejects `-SeedTemplate` when the bootstrap manifest came from `-ApiSchemaPath`,
   mutual-exclusion validation between `-SeedTemplate` and `-SeedDataPath`, extension-package resolution for
   standard extension mode using the seed catalog and manifest-selected extension names, and fail-fast
   handling when required package-backed seed assets or catalog-advertised built-in extension seed artifacts are unavailable.
3. Implement the deterministic `SeedLoader` contract for built-in seed sources, including the core
   permissions in embedded claims metadata, the required extension `SeedLoader` coverage in staged
   extension security fragments for built-in extension seed sources, the bootstrap-side preflight failure
   when the embedded claims metadata is missing the top-level `SeedLoader` claim set, and the staged-input
   compatibility boundary for custom `-SeedDataPath` directories, including explicit
   `-AdditionalNamespacePrefix` values used only for SeedLoader vendor authorization.
   This task is what allows built-in extension seed packages to be advertised at all.
4. Implement seed-workspace creation, XML interchange materialization for both built-in artifacts and
   `-SeedDataPath`, BulkLoadClient-discoverable target-path materialization with pre-invocation invalid-path
   and collision failure, the
   `SchoolYearType` REST precondition for the configured year range, and cleanup behavior. For built-in
   sources the workspace is split into a descriptor subdirectory and a resource subdirectory so the
   `Populated` flow can sequence BulkLoadClient invocations correctly. Ordering remains owned by
   BulkLoadClient dependency/interchange metadata rather than bootstrap filename prefixes.
5. Implement the DMS-dependent `SeedLoader` credential-bootstrap path, including the baseline plus
   extension plus `-AdditionalNamespacePrefix` namespace-prefix list, while keeping it distinct from and not
   dependent on the CMS-only smoke-test credential flow.
6. Implement the BulkLoadClient invocation path, including `-BootstrapManifestPath` handling,
   `-DmsBaseUrl` handling for the BulkLoadClient target endpoint, explicit `-IdentityProvider` handling for
   OAuth URL selection, repo-aligned school-year route qualification, the per-year loop for
   `-SchoolYearRange`, self-contained per-year `/connect/token/{schoolYear}` OAuth URL construction through
   the same provider-to-token-endpoint helper used by `start-local-dms.ps1`, shared `-EnvironmentFile`
   local-settings resolution for CMS, tenant, identity-provider defaults, and Docker-local DMS URL,
   pass-through of terminal tool diagnostics, XSD input selection (`-x` staged directory or `-z` metadata URL),
   and use of the data store IDs resolved by `configure-local-data-store.ps1` without performing CMS data store
   creation, broad target-selection policy, or non-selector-driven discovery during seed delivery. For
   `Populated`, the invocation path sequences a descriptor pass followed by a resource pass against the
   separated workspace subdirectories using a single in-process SeedLoader credential set.
   Retry policy, request batching, endpoint inference internals, result-taxonomy details, and other tool-owned
   runtime behavior remain outside this story.
7. Keep wrapper seed loading opt-in through `-LoadSeedData` without introducing a second suppressor flag or
   control plane on `load-dms-seed-data.ps1`.

## Forward Scaffolding and Epic-Level Activation Gates

This story ships several pieces of infrastructure that are present in the code today but **dormant
pending sibling stories in the bootstrap epic**. They are not dead code; the epic activates them in a
specific order, and removing them now would force re-implementation in their activating story.
Reviewers proposing to delete this scaffolding should first confirm the activating story (referenced
below) has been re-scoped or cancelled.

| Forward-investment surface | Where it lives | Activating story | What activates it |
|---|---|---|---|
| Pinned `Ed-Fi-Data-Standard` v5.2.0 GitHub fetch (`data-standard.psm1`) | `eng/docker-compose/data-standard.psm1` | Story 06 | Standard-mode schema selection (`selectionMode = "Standard"`) |
| `Initialize-CoreSeedSource` + built-in `Minimal`/`Populated` materialization | `eng/docker-compose/load-dms-seed-data.ps1` (BuiltIn branches) | Story 06 | `-SeedTemplate` becomes reachable from wrappers |
| Extension seed catalog (`seed-catalog.json`, `Resolve-ExtensionSeedSources`) | `eng/docker-compose/seed-catalog.json` + `load-dms-seed-data.ps1` | Story 06 | Built-in extension seed packages |
| `BulkLoadClient` XSD validation against `<dataStandardRoot>/Schemas/Bulk/` (BuiltIn) | `load-dms-seed-data.ps1` BuiltIn XSD branch | Story 06 | Standard-mode `-SeedTemplate` runs |
| `-SeedDataPath` end-to-end runnability against arbitrary user schemas | `prepare-dms-schema.ps1` XSD staging + `Get-SeedXsdDirectory` | Story 04 | `prepare-dms-schema.ps1` learns to stage XSDs for schemas that ship without an `xsd/` directory |
| Removal of direct-SQL `setup-database-template.psm1` | `start-(local\|published)-dms.ps1` direct-SQL path | Gated by bootstrap-design.md §6.4 (Story 04 verification) | XSD-staging compatibility for the bootstrap path |

This story's runnable surface is intentionally narrow: the SeedLoader claim set + ClaimsDataLoader
integration, the BulkLoadClient consumption contract, the wrapper sequencing, the manifest contract,
and the bootstrap-derived env profile. Everything else is contract scaffolding for the stories above.

### Deferred Wrapper Flags

`command-boundaries.md §3.7` describes the eventual `bootstrap-local-dms.ps1` parameter surface.
DMS-1152 delivers a subset; the remaining flags are activated by other stories in the epic. Listing
them explicitly so reviewers see the deferral pattern and don't re-litigate as "wrapper looks
incomplete":

| `command-boundaries.md §3.7` flag | Status in DMS-1152 | Activating story / workflow |
|---|---|---|
| `-LoadSeedData`, `-SeedTemplate`, `-SeedDataPath`, `-AdditionalNamespacePrefix` | Delivered | — |
| `-EnableKafkaUI`, `-EnableSwaggerUI`, `-EnableConfig`, `-EnvironmentFile`, `-IdentityProvider` | Delivered (forwarded to `start-(local\|published)-dms.ps1`) | — |
| `-SchoolYearRange` | Delivered (forwarded; the year-loop iterates seed phase per year when seed loading is on) | — |
| `-AddExtensionSecurityMetadata` | Delivered (forwarded to start phase) | — |
| `-Extensions`, `-ApiSchemaPath` | **Not in wrapper.** Developers invoke `prepare-dms-schema.ps1` directly today. | Story 03 (schema phase consolidation into wrapper) |
| `-ClaimsDirectoryPath` | **Not in wrapper.** Developers invoke `prepare-dms-claims.ps1` directly today. | Story 03 |
| `-InfraOnly`, `-DmsBaseUrl` | **Not in wrapper.** | DMS-1153 (IDE-hosted workflow) |
| `-Rebuild` / `-r` | **Not in wrapper.** Developers pass to `start-(local\|published)-dms.ps1` directly. | Future hygiene |
| `-AddSmokeTestCredentials` | **Not in wrapper.** Owned by `configure-local-data-store.ps1` per `command-boundaries.md`. | Story 03 |

## Out of Scope

- A global BulkLoadClient installation requirement.
- DMS-owned redesign of BulkLoadClient beyond the documented bootstrap consumer boundary.
- Enhancing or extending the legacy direct-SQL path.
- JSONL seed-loading support; the DMS-1152 target is XML interchange loading.
- Folding smoke, E2E, or integration test execution into the seed-delivery flow.
- Benchmark thresholds or performance sign-off gates for this story.
- Re-deriving schema or claims selection in `load-dms-seed-data.ps1`.
- Dynamic claim-set synthesis from arbitrary `-SeedDataPath` XML files.
- Namespace-prefix discovery from arbitrary `-SeedDataPath` XML files.
- A wrapper-level `-EducationOrganizationIds` flag (or any second parameter surface) for
  arbitrary seed-specific EdOrg scoping. Per `bootstrap-design.md §7.2`, the SeedLoader
  envelope uses the top-level LEA/SEA IDs from the standard bootstrap path; custom EdOrg
  payloads require resolving the broader authorization model elsewhere, not at the
  seed-time surface.

## Design References

- [`../../design-docs/bootstrap/bootstrap-design.md`](../../design-docs/bootstrap/bootstrap-design.md), Sections 5, 6, 7.2-7.3, 9.2, 10, and 14.2
- [`../../design-docs/bootstrap/command-boundaries.md`](../../design-docs/bootstrap/command-boundaries.md), Section 3.6 (`load-dms-seed-data.ps1`) and Section 3.7 (`bootstrap-local-dms.ps1`)
- [`../../design-docs/bootstrap/responsibility-inventory.md`](../../design-docs/bootstrap/responsibility-inventory.md), API-based sample data loading and credential bootstrapping rows
