---
design: DMS-916
---

# Story: API-Based Seed Delivery for Bootstrap

## Description

Implement the story-aligned API-based seed path for developer bootstrap. This slice replaces the current
direct-SQL bootstrap intent with JSONL-based API loading through the repo-pinned BulkLoadClient resolution
path. The target state for DMS-916 is replacement, not supplementation. Operational delivery remains blocked
until the external JSONL dependency lands; that blocker does not create a second normative seed mode in this
design.

The slice includes seed-source selection, the dedicated DMS-dependent `SeedLoader` credential flow for the
`-LoadSeedData` continuation, per-year loading for the existing `-SchoolYearRange` workflow, and the
bootstrap-side guardrails needed to keep the seed path deterministic and safe to re-run. The authorization
contract for built-in seed delivery is also part of this slice: the design fixes one authoritative
`SeedLoader` inventory for core templates and requires extension seed support to travel with extension
security metadata rather than ad hoc grant derivation. CMS-only smoke-test credential creation is a separate
workflow concern: it may run earlier against the step-7-selected target set, but it is not part of this
story's dependency chain and its credentials are never reused for seed delivery. Custom `-SeedDataPath`
directories remain supported as compatible payload sources when the run's staged schema/security inputs are
intended to cover them, but bootstrap does not preflight-certify arbitrary JSONL content.

This slice is also the prerequisite that unlocks any Story 00 extension mapping entry advertising built-in
seed support, because the top-level embedded `SeedLoader` claim set is delivered here.
This story defines only the DMS bootstrap consumer boundary for BulkLoadClient: the pinned-resolution path,
the invocation shape DMS depends on, and the pass-through result handling. It does not broaden DMS-916 into
owning BulkLoadClient product design, packaging, or non-bootstrap runtime behavior.

## Acceptance Criteria

- `-LoadSeedData` remains opt-in. When it is absent, bootstrap does not load seed data.
- When `-LoadSeedData` is set, bootstrap resolves BulkLoadClient through the repo-pinned package path rather
  than through a global tool or `$PATH` requirement.
- Bootstrap fails fast if the pinned BulkLoadClient package cannot be resolved or if the required JSONL
  interface is unavailable.
- Bootstrap also fails fast when a built-in seed source is selected but the required published seed artifact
  package cannot be resolved from the configured bootstrap artifact source.
- Supported seed inputs match the design:
  - Ed-Fi-provided seed packages selected by `-SeedTemplate`,
  - developer-supplied JSONL directories selected by `-SeedDataPath`.
- When `-LoadSeedData` is used without an explicit seed-source flag, bootstrap defaults to the built-in
  `Minimal` seed template in standard Modes 1 and 2 only.
- In expert `-ApiSchemaPath` mode, `-LoadSeedData` requires explicit `-SeedDataPath`; bootstrap must not
  fall back to built-in `Minimal` or `Populated` seed templates in that mode.
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
  run's staged schema/security inputs, including selected extensions and additive `-ClaimsDirectoryPath`
  fragments when present.
- `-SeedDataPath` is supported when the run's staged schema/security inputs are intended to cover that data.
- Bootstrap does not inspect arbitrary `-SeedDataPath` JSONL files to certify authorization completeness or
  derive new claims from payload content. Payload-level authorization or schema mismatches remain
  BulkLoadClient or DMS runtime failures.
- When `-Extensions` is used and `-SeedDataPath` is not supplied, bootstrap resolves the required extension
  seed packages and fails fast if an expected extension package is missing.
- Extensions without a built-in seed package in the documented mapping remain schema/security-only unless the
  developer supplies `-SeedDataPath`.
- When `-LoadSeedData` is used with an extension that has no built-in seed package in the mapping, bootstrap
  emits an informational warning rather than silently implying extension seed data was loaded.
- When a built-in extension seed source is selected, the selected extension security fragment(s) must also
  attach the required `SeedLoader` permissions for every resource emitted by that extension's seed package.
- Bootstrap stages seed files into one repo-local workspace and fails fast on filename collisions before
  invoking BulkLoadClient.
- Future automatic merging of more than one built-in seed source is supported only when those published
  artifacts already honor the external JSONL ordering contract consumed by BulkLoadClient.
- Seed loading surfaces the tool's terminal summary or terminal error diagnostics; bootstrap passes those
  diagnostics through rather than inventing a second accounting layer or a DMS-owned result taxonomy.
- Per [`INDEX.md`](INDEX.md), Step 7 remains the only target-resolution phase for the run. `SeedLoader`
  credential bootstrap and every BulkLoadClient invocation consume the already selected target set and must
  not perform a second CMS discovery pass.
- BulkLoadClient invocation uses:
  - the DMS base URL for the current flow,
  - the token URL derived from the selected identity provider,
  - the bootstrap-created key/secret for the dedicated `SeedLoader` application.
- The `SeedLoader` credential flow is separate from the CMS-only smoke-test credential flow, includes the
  baseline seed namespaces plus the selected extension namespace prefixes, and does not reuse or depend on
  smoke-test credentials.
- `-LoadSeedData` does not require `-AddSmokeTestCredentials`; smoke-test credential creation remains an
  independent CMS-only workflow concern outside this story's dependency path.
- When `-SchoolYearRange` is used, bootstrap prepares the seed workspace once and invokes BulkLoadClient once
  per year with `--year <school-year>`.
- In that existing school-year workflow, `--year` targets the route-qualified DMS path by placing the
  school-year segment before `/data` (for example, `{base-url}/{schoolYear}/data/ed-fi/students`);
  bootstrap must not invent an ODS-style `{base-url}/data/v3/{year}/...` convention.
- When self-contained CMS identity is selected for that flow, the token URL carries the matching context
  path at `/connect/token/{schoolYear}`. Keycloak token URLs remain provider-native.

## Tasks

1. Implement repo-pinned BulkLoadClient resolution and pre-flight validation for the JSONL bootstrap path.
2. Implement seed-source selection for `-SeedTemplate` and `-SeedDataPath`, including the default
   `Minimal` behavior when `-LoadSeedData` is set without an explicit seed-source flag in standard Modes 1
   and 2, the expert-mode rule that `-ApiSchemaPath` requires explicit `-SeedDataPath` for `-LoadSeedData`,
   validation that rejects `-ApiSchemaPath` with `-SeedTemplate`, mutual-exclusion validation between
   `-SeedTemplate` and `-SeedDataPath`, extension-package resolution for standard extension mode, and
   fail-fast handling when required built-in seed artifacts are unavailable.
3. Implement the deterministic `SeedLoader` contract for built-in seed sources, including the core
   permissions in embedded claims metadata, the required extension `SeedLoader` coverage in staged
   extension security fragments for built-in extension seed sources, the bootstrap-side preflight failure
   when the embedded claims metadata is missing the top-level `SeedLoader` claim set, and the staged-input
   compatibility boundary for custom `-SeedDataPath` directories.
   This task is what unblocks Story 00 from advertising any built-in extension seed-support mapping entry.
4. Implement seed-workspace creation, JSONL extraction/copying for both built-in artifacts and
   `-SeedDataPath`, collision detection, and deterministic cleanup behavior. Ordering of directory
   consumption, if required, remains part of the external BulkLoadClient JSONL contract rather than a
   bootstrap-owned rule.
5. Implement the DMS-dependent `SeedLoader` credential-bootstrap path, keeping it distinct from and not
   dependent on the CMS-only smoke-test credential flow.
6. Implement the BulkLoadClient invocation path, including identity-provider-aware token-url selection,
   repo-aligned school-year route qualification, the per-year loop for `-SchoolYearRange`, self-contained
   per-year `/connect/token/{schoolYear}` token-url construction, pass-through of terminal tool diagnostics,
   and strict reuse of the step-7-selected target set without a second CMS discovery pass during seed
   delivery. Retry policy, request batching, endpoint inference internals, result-taxonomy details, and other
   tool-owned runtime behavior remain outside this story.
7. Keep seed loading opt-in through `-LoadSeedData` without introducing a second suppressor flag or control
   plane.

## Out of Scope

- A global BulkLoadClient installation requirement.
- DMS-owned redesign of BulkLoadClient beyond the documented bootstrap consumer boundary.
- Enhancing or extending the legacy direct-SQL path.
- Folding smoke, E2E, or integration test execution into the seed-delivery flow.
- Dynamic claim-set synthesis from arbitrary `-SeedDataPath` JSONL files.

## Design References

- [`../bootstrap-design.md`](../bootstrap-design.md), Sections 5, 6, 7.2-7.3, 9.2, 10, and 14.3
