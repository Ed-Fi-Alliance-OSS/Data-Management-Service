---
jira: DMS-1152
jira_url: https://edfi.atlassian.net/browse/DMS-1152
---

# Story: API-Based Seed Delivery for Bootstrap

## Description

Implement the story-aligned API-based seed path for developer bootstrap. This slice replaces the current
direct-SQL bootstrap intent with JSONL-based API loading through the repo-pinned BulkLoadClient resolution
path. The target state for DMS-916 is replacement, not supplementation. Operational delivery is blocked on
one named external dependency: **ODS-6738** (BulkLoadClient JSONL support). DMS-1119 owns future published
seed package distribution for deployment and agency provisioning, but it does not block the DMS-916
developer bootstrap path.

The slice includes seed-source selection, the dedicated DMS-dependent `SeedLoader` credential flow for the
seed-delivery phase, per-year loading for the existing `-SchoolYearRange` workflow, and the
bootstrap-side guardrails needed to keep the seed path deterministic and rerun-tolerant once the required
BulkLoadClient contract is delivered. The authorization
contract for built-in seed delivery is also part of this slice: the design fixes one authoritative
`SeedLoader` inventory for core templates and requires extension seed support to travel with extension
security metadata rather than ad hoc grant derivation. CMS-only smoke-test credential creation is a separate
workflow concern: it may run earlier against the target instances selected by
`configure-local-dms-instance.ps1`, but it is not part of this story's dependency chain and its credentials
are never reused for seed delivery. Custom `-SeedDataPath`
directories remain supported as compatible payload sources when the run's root bootstrap manifest and staged
schema/security inputs are intended to cover them, and `-AdditionalNamespacePrefix` may declare
agency or custom namespace prefixes for SeedLoader vendor authorization. Bootstrap does not
preflight-certify arbitrary JSONL content.

This slice is the single owner of built-in seed-support advertisement. An extension may advertise built-in
seed support only when this story's `SeedLoader` contract is delivered end to end.
This story defines only the DMS bootstrap consumer boundary for BulkLoadClient: the pinned-resolution path,
the invocation shape DMS depends on, and the pass-through result handling. It also owns the repo-local
`Minimal` and `Populated` JSONL developer seed assets used by `-SeedTemplate`. It does not broaden DMS-916
into owning BulkLoadClient product design, published seed package distribution, or non-bootstrap runtime
behavior.

## External Blockers

This story is design-complete for DMS-916 but is **not implementation-ready end to end**. One external
dependency must land before operational delivery is possible:

- **ODS-6738** — BulkLoadClient JSONL support: the `--input-format jsonl` and `--data <directory>` flags
  required by the DMS bootstrap consumption contract do not yet exist in BulkLoadClient. The bootstrap
  design is defined and ready; delivery waits on the ODS team.

This blocker does not introduce a second normative seed mode. The direct-SQL path is **not** the design target
state; it is the current gap this story closes once the external dependency lands. No DMS-916 artifact
normalizes the legacy direct-SQL path as an ongoing or permanent alternative.

## Acceptance Criteria

- `-LoadSeedData` remains a wrapper-level opt-in. When it is absent from `bootstrap-local-dms.ps1`,
  wrapper bootstrap does not invoke seed delivery. Direct invocation of `load-dms-seed-data.ps1` always
  loads seed data and does not accept `-LoadSeedData`.
- When seed delivery runs, bootstrap resolves BulkLoadClient through the repo-pinned package path rather than
  through a global tool or `$PATH` requirement.
- Bootstrap fails fast if the pinned BulkLoadClient package cannot be resolved or if the required JSONL
  interface is unavailable.
- Bootstrap also fails fast when a built-in seed source is selected but the required repo-local JSONL
  directory cannot be resolved.
- Supported seed inputs match the design:
  - repo-local Ed-Fi seed templates selected by `-SeedTemplate`,
  - developer-supplied JSONL directories selected by `-SeedDataPath`.
- When seed delivery runs without an explicit seed-source flag, bootstrap defaults to the built-in `Minimal`
  seed template in standard Modes 1 and 2 only.
- In expert `-ApiSchemaPath` mode, seed delivery requires explicit `-SeedDataPath`; bootstrap must not fall
  back to built-in `Minimal` or `Populated` seed templates in that mode.
- In expert `-ApiSchemaPath` mode, `-SeedTemplate` is invalid because bootstrap-managed seed selection is
  disabled.
- `-SeedTemplate` and `-SeedDataPath` are mutually exclusive. Providing both is a bootstrap validation
  error rather than an implementation-defined precedence rule.
- The built-in seed inventories are authoritative and deterministic:
  - `Minimal` loads all core descriptor resources plus `schoolYearTypes`,
  - `Populated` loads `Minimal` plus `localEducationAgencies`, `schools`, `courses`, `students`, and
    `studentSchoolAssociations`.
- The embedded top-level `SeedLoader` claim set includes the core permissions required by those built-in
  inventories.
- `-SeedDataPath` bypasses seed-package download, but schema/security compatibility still comes from the
  run's root bootstrap manifest, which records the staged schema/security inputs selected earlier, including
  selected extensions and staged claims fingerprints.
- `-SeedDataPath` is supported when the run's root bootstrap manifest and staged schema/security inputs are
  intended to cover that data.
- `load-dms-seed-data.ps1` reads `eng/docker-compose/.bootstrap/bootstrap-manifest.json` by default, or an
  explicit `-BootstrapManifestPath` override, before resolving seed sources. It fails fast when the manifest
  is missing, malformed, unsupported, incomplete, or incompatible with the requested seed-source flags.
- The root bootstrap manifest remains intentionally small: version, schema selection mode (`Standard` or
  `ApiSchemaPath`), selected mapped extensions, `EffectiveSchemaHash`, ApiSchema and claims fingerprints,
  relative ApiSchema manifest path, relative claims directory, expected claims-verification checks, and
  extension namespace prefixes. It must not carry built-in seed-package entries, resource definitions, claim
  grants, instance IDs, credentials, URLs, Docker state, environment settings, seed file paths, phase progress,
  or resume checkpoints.
- Bootstrap does not inspect arbitrary `-SeedDataPath` JSONL files to certify authorization completeness or
  derive new claims from payload content. Payload-level authorization or schema mismatches remain
  BulkLoadClient or DMS runtime failures.
- `-AdditionalNamespacePrefix` is an optional additive seed-phase input. The `SeedLoader` vendor namespace
  prefix list is the baseline seed prefixes plus selected extension prefixes plus any additional values,
  de-duplicated before vendor creation. This parameter does not replace baseline prefixes, infer extensions,
  or synthesize claims.
- When selected extensions are present in the bootstrap manifest and `-SeedDataPath` is not supplied,
  bootstrap resolves the required extension seed packages and fails fast if an expected extension package is
  missing.
- Extensions without a built-in seed package in the seed catalog remain schema/security-only unless the
  developer supplies `-SeedDataPath`.
- When seed delivery runs with an extension that has no built-in seed package in the seed catalog, bootstrap
  emits an informational warning rather than silently implying extension seed data was loaded.
- When a built-in extension seed source is selected, the selected extension security fragment(s) must also
  attach the required `SeedLoader` permissions for every resource emitted by that extension's seed package.
- Bootstrap stages seed files into one repo-local workspace and fails fast on filename collisions before
  invoking BulkLoadClient.
- Future automatic merging of more than one built-in seed source is supported only when those sources already
  honor the external JSONL ordering contract consumed by BulkLoadClient.
- Seed loading surfaces the tool's terminal summary or terminal error diagnostics; bootstrap passes those
  diagnostics through rather than inventing a second accounting layer or a DMS-owned result taxonomy.
- `configure-local-dms-instance.ps1` is the sole phase that creates or selects DMS instance IDs for the
  run. `SeedLoader` credential bootstrap and every BulkLoadClient invocation receive those instance IDs
  via in-memory forwarding within a single wrapper invocation, or via explicit `-InstanceId`/`-SchoolYear`
  selectors in a manual phase flow; they must not perform their own CMS instance creation, broad
  target-selection policy, or non-selector-driven discovery pass.
  See [`EPIC.md`](EPIC.md) Scope Guardrails for the selector resolution rules.
- BulkLoadClient invocation uses:
  - the DMS base URL supplied to `load-dms-seed-data.ps1 -DmsBaseUrl` for the current flow,
  - the token URL derived from `load-dms-seed-data.ps1 -IdentityProvider`,
  - the bootstrap-created key/secret for the dedicated `SeedLoader` application.
- `load-dms-seed-data.ps1` accepts `-EnvironmentFile` and uses the shared local-settings helper to resolve
  CMS URL, auth defaults, tenant scope, and the Docker-local DMS URL. Direct seed invocation does not rely on
  process environment variables left behind by an earlier `start-local-dms.ps1` invocation.
- `load-dms-seed-data.ps1` consumes schema mode, selected extensions, and extension namespace prefixes from
  the bootstrap manifest. It owns seed-catalog lookup for built-in extension seed-package metadata,
  does not accept `-Extensions`, `-ApiSchemaPath`, or `-ClaimsDirectoryPath`, and must not infer those values
  from prior shell state or by inspecting JSONL payloads.
- Docker-hosted seed loading may use the DMS base URL resolved from the selected env file, normally
  `http://localhost:8080`. IDE-hosted seed loading must pass the IDE DMS endpoint explicitly; when the thin
  wrapper is orchestrating
  `-InfraOnly -DmsBaseUrl -LoadSeedData`, it forwards the same `-DmsBaseUrl` value to this phase after the
  post-provision health wait succeeds, and it forwards the selected `-IdentityProvider` value so this
  phase resolves the matching token endpoint.
- The `SeedLoader` credential flow is separate from the CMS-only smoke-test credential flow, includes the
  baseline seed namespaces plus the selected extension namespace prefixes plus any
  `-AdditionalNamespacePrefix` values, and does not reuse or depend on smoke-test credentials.
- Wrapper `-LoadSeedData` does not require `-AddSmokeTestCredentials`; smoke-test credential creation
  remains an independent CMS-only workflow concern outside this story's dependency path.
- When `-SchoolYearRange` is used, bootstrap prepares the seed workspace once and invokes BulkLoadClient once
  per year with `--year <school-year>`.
- In that existing school-year workflow, `--year` targets the route-qualified DMS path by placing the
  school-year segment before `/data` (for example, `{base-url}/{schoolYear}/data/ed-fi/students`);
  bootstrap must not invent an ODS-style `{base-url}/data/v3/{year}/...` convention.
- When self-contained CMS identity is selected for that flow, the token URL carries the matching context
  path at `/connect/token/{schoolYear}`. Keycloak token URLs remain provider-native.

## Tasks

1. Implement repo-pinned BulkLoadClient resolution and pre-flight validation for the JSONL bootstrap path.
2. Implement seed-source selection for `-SeedTemplate` and `-SeedDataPath` by reading the root bootstrap
   manifest first, including the default `Minimal` behavior when seed delivery runs without an explicit
   seed-source flag in standard Modes 1 and 2, the expert-mode rule that manifest
   `schema.selectionMode = ApiSchemaPath` requires explicit `-SeedDataPath` for seed delivery,
   validation that rejects `-SeedTemplate` when the bootstrap manifest came from `-ApiSchemaPath`,
   mutual-exclusion validation between `-SeedTemplate` and `-SeedDataPath`, extension-package resolution for
   standard extension mode using the seed catalog and manifest-selected extension names, and fail-fast
   handling when required repo-local seed assets or future built-in extension seed artifacts are unavailable.
3. Implement the deterministic `SeedLoader` contract for built-in seed sources, including the core
   permissions in embedded claims metadata, the required extension `SeedLoader` coverage in staged
   extension security fragments for built-in extension seed sources, the bootstrap-side preflight failure
   when the embedded claims metadata is missing the top-level `SeedLoader` claim set, and the staged-input
   compatibility boundary for custom `-SeedDataPath` directories, including explicit
   `-AdditionalNamespacePrefix` values used only for SeedLoader vendor authorization.
   This task is what allows built-in extension seed support to be advertised at all.
4. Implement seed-workspace creation, JSONL extraction/copying for both built-in artifacts and
   `-SeedDataPath`, collision detection, and deterministic cleanup behavior. Ordering of directory
   consumption, if required, remains part of the external BulkLoadClient JSONL contract rather than a
   bootstrap-owned rule.
5. Implement the DMS-dependent `SeedLoader` credential-bootstrap path, including the baseline plus
   extension plus `-AdditionalNamespacePrefix` namespace-prefix list, while keeping it distinct from and not
   dependent on the CMS-only smoke-test credential flow.
6. Implement the BulkLoadClient invocation path, including `-BootstrapManifestPath` handling,
   `-DmsBaseUrl` handling for the BulkLoadClient target endpoint, explicit `-IdentityProvider` handling for token-url selection, repo-aligned school-year route
   qualification, the per-year loop for `-SchoolYearRange`, self-contained per-year
   `/connect/token/{schoolYear}` token-url construction through the same provider-to-token-endpoint helper
   used by `start-local-dms.ps1`, shared `-EnvironmentFile` local-settings resolution for CMS, tenant,
   identity-provider defaults, and Docker-local DMS URL, pass-through of terminal tool diagnostics, and use
   of the instance IDs
   resolved by `configure-local-dms-instance.ps1` (forwarded in-memory by the wrapper, or
   supplied via explicit `-InstanceId`/`-SchoolYear` selectors in a manual phase flow) without performing CMS
   instance creation, broad target-selection policy, or non-selector-driven discovery during seed delivery.
   Retry policy, request batching, endpoint inference internals, result-taxonomy details, and other tool-owned
   runtime behavior remain outside this story.
7. Keep wrapper seed loading opt-in through `-LoadSeedData` without introducing a second suppressor flag or
   control plane on `load-dms-seed-data.ps1`.

## Out of Scope

- A global BulkLoadClient installation requirement.
- DMS-owned redesign of BulkLoadClient beyond the documented bootstrap consumer boundary.
- Enhancing or extending the legacy direct-SQL path.
- Folding smoke, E2E, or integration test execution into the seed-delivery flow.
- Benchmark thresholds or performance sign-off gates for this story.
- Re-deriving schema or claims selection in `load-dms-seed-data.ps1`.
- Dynamic claim-set synthesis from arbitrary `-SeedDataPath` JSONL files.
- Namespace-prefix discovery from arbitrary `-SeedDataPath` JSONL files.

## Design References

- [`../../design-docs/bootstrap/bootstrap-design.md`](../../design-docs/bootstrap/bootstrap-design.md), Sections 5, 6, 7.2-7.3, 9.2, 10, and 14.3
