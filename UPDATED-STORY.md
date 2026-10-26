---
jira: DMS-1618
jira_url: https://edfi.atlassian.net/browse/DMS-1618
---

# Story: Add Merge, Resolve, and Verify Commands to Security Metadata Tools Using Shared CMS Claims Code

## Description

For the **8.1 release**, add `merge`, `resolve`, and `verify` commands to Security Metadata Tools so users can prepare a complete security policy offline, inspect its effective permissions, and compare those permissions with a running Configuration Service (CMS).

Hybrid mode currently composes the embedded base policy with claim-set fragments during CMS startup. Users need the same capability before deployment, including a complete document suitable for Filesystem mode or `upload-claims`. They also need to understand the permissions resulting from inheritance, defaults, and authorization-strategy overrides.

The intended policy files can differ from the stored policy: startup loading only populates an empty claims database, and `/v3/claimSets` operations can change the stored hierarchy. Verification must therefore read current authorization metadata through CMS's API, rather than compare against its startup files.

The former `CmsHierarchy Transform` command used separate merge logic and an outdated base policy. Extract the existing CMS composition and resolution implementation into a shared library used by both CMS and the CLI. Share the embedded base policy and validation schema as well, avoiding a second implementation or independently maintained copy of those artifacts.

A successful `verify` means **CMS's current effective security metadata matches the expected policy**. It does not prove that an Ed-Fi API instance has refreshed its authorization cache or that live API requests enforce every strategy correctly.

### Executive Summary

In 8.1, add merge, resolve, and verify commands built on a library extracted from the Configuration Service's existing claims composition and permission-resolution code. Users can generate a complete policy offline, inspect effective permissions, and detect differences from a running Configuration Service. Preserve CMS behavior, make incomplete CLI results fail clearly, prove parity with service-level tests, and document the offline preparation and verification workflow.

Design and implementation references:

- `docs/CLAIMS-LOADING-GUIDE.md` — loading modes, fragments, and claims management workflow.
- `src/config/clis/EdFi.DmsConfigurationService.SecurityMetadataTools/README.md` — existing CLI conventions.
- `src/config/clis/EdFi.DmsConfigurationService.SecurityMetadataTools/VERSIONING.md` — tool packaging and release policy.
- `src/config/backend/EdFi.DmsConfigurationService.Backend/Claims/ClaimsFragmentComposer.cs` — current fragment discovery and composition behavior. `ApplyParentResourceClaim` appends children when an entry matches an existing claim but ignores that entry's `claimSets` and `_defaultAuthorizationStrategiesForCrud`.
- `src/config/backend/EdFi.DmsConfigurationService.Backend/AuthorizationMetadata/AuthorizationMetadataResponseFactory.cs` — current inheritance, defaults, and override resolution.
- `src/config/backend/EdFi.DmsConfigurationService.Backend/AuthorizationMetadata/AuthorizationMetadataResponse.cs` — current authorization hashing sorts strategy names before deduplication.
- `reference/design/backend-redesign/design-docs/auth.md` — Execution order and Custom view-based authorization strategy explain why Namespace/custom-view ordering is observable in failure behavior.
- `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Modules/AuthorizationMetadataModule.cs` — existing `/v3/authorizationMetadata` endpoint.

Dependencies:

- DMS-1616 — removal of the old Transform workflow from public documentation and documentation of Hybrid loading.
- DMS-1617 — renaming and publishing Security Metadata Tools as a .NET tool.

## Scope (What This Story Is Talking About)

- Owns extraction of shared claims composition, resolution, and supporting contracts/artifacts, with CMS continuing to use that implementation.
- Owns the three CLI commands, complete-document validation, deterministic output, structured internal diagnostics, and effective-permission comparison.
- Owns read-only verification of all claim sets for one CMS target and selected tenant per invocation.
- Owns parity tests, installed-tool verification, and documentation for the 8.1 workflow.

Outside this story:

- Changes to authorization semantics, fragment precedence, or CMS startup fallback behavior, except correcting hash-collision handling in shared authorization equality and deduplication as specified below.
- Arbitrary merge-base files, individual claim-set filters, multiple output formats, or multi-target orchestration. Selection among CMS's bundled Data Standard bases is in scope.
- Policy upload, repair, or synchronization by these commands; deployment remains a separate existing workflow.
- Live Ed-Fi API authorization probes, DMS cache inspection or invalidation, and evaluation of whether imported strategies are supported by DMS.
- New public CMS endpoints, direct database access from the CLI, and a standalone NuGet distribution of the shared library.
- Cross-version compatibility guarantees beyond the documented matching tool/CMS release combination.

## Acceptance Criteria

### Shared implementation and unchanged CMS behavior

- CMS and the CLI invoke the same composition and permission-resolution implementation; the CLI contains no independent inheritance, default, override, or fragment-application algorithm.
- The shared library has no dependency on a running web host or database. CMS retains repository access and HTTP handling in adapters outside the shared resolution logic.
- Resolution accepts the claim-set catalog and hierarchy as inputs. CMS supplies its stored catalog and hierarchy; offline commands supply both from the complete claims document. Catalog entries with no grants remain represented.
- The tool and CMS built from the same source use the same embedded base policies, Data Standard selection logic, and claims validation schema. Moving artifacts must preserve their availability in the installed tool package.
- Extraction preserves CMS's public authorization metadata response contract and existing loading, resolution, and fallback behavior, with the narrowly scoped correction that distinct authorizations must not be treated as equivalent solely because their hashes collide. Diagnostic plumbing must not silently turn an existing CMS continuation into a startup failure.
- Existing `convert-ods-xml` behavior remains compatible.

### Command contracts

| Command | Required inputs | Optional inputs | Result |
| --- | --- | --- | --- |
| `merge` | `--fragments <directory>`, `--output <file>` | `--data-standard-version <version>`, `--force` | Complete claims JSON composed from the selected bundled CMS base and fragments. |
| `resolve` | `--input <file>`, `--output <file>` | `--force` | Resolved authorization metadata JSON for every claim set in the input. |
| `verify` | `--input <file>`, `--cms-url <base-url>`, `--access-token-env <variable-name>` | `--tenant <tenant-name>` | Text report comparing expected effective permissions with current CMS metadata. |

- `resolve` and `verify` accept a complete claims document containing `claimSets` and `claimsHierarchy`, including a document produced by `merge` or the existing conversion command. They do not accept fragment directories or resolved output as their input.
- All commands expose their inputs, output behavior, and exit codes through `--help`.
- `merge` and `resolve` work without network access, CMS, or a database.
- `verify` reads an already-issued bearer token from the named environment variable. Token acquisition remains outside the tool, allowing the same command to work with either supported CMS identity provider. Missing or empty credentials fail before an HTTP request.
- The CMS base URL supports deployments under a path prefix. When provided, `--tenant` selects the existing CMS `Tenant` header for the request; when omitted, CMS's existing tenant-selection rules apply. Tenant selection is routing, not an authorization boundary.
- Credentials are never written to output or diagnostics. HTTP verification uses the endpoint's existing authentication and authorization requirements, with no privilege or feature-gate changes.

### Complete-document integrity

- After schema validation, all three new commands check complete-document integrity: `merge` checks its composed document before writing; `resolve` and `verify` check their input before resolving it.
- Validate catalog claim-set names against the existing CMS naming rules, including length and whitespace restrictions, through one shared validation routine also used by fragment registration. Reject invalid names without normalizing or renaming them. Schema validation alone does not enforce all of these rules.
- Reject duplicate claim-set names in the catalog and hierarchy claim-set references to undeclared catalog entries. Use `StringComparer.OrdinalIgnoreCase` for catalog uniqueness and reference lookup.
- Require exact claim-name/URI uniqueness across the entire complete-document hierarchy, including non-leaf nodes and separate branches. Use `StringComparer.Ordinal` without URI normalization. Reject duplicates even when their policy agrees, identifying the duplicate name and both locations.
- Integrity failures return exit code `1` with a diagnostic identifying the invalid or duplicate name or undeclared reference and its location. Preserve existing output files and create no partial output.
- These checks apply to the new CLI commands; preserve CMS's existing validation and continuation behavior.

### Merge behavior

- Select the bundled base policy with `--data-standard-version`, using the shared CMS selection logic and the same default as `ClaimsOptions:DataStandardVersion` (currently `5.2`). The selected version must match CMS's configured Data Standard for Hybrid parity. A version without a bundled base fails clearly with exit code `1` and leaves the destination unchanged.
- Use the base policies bundled with the tool's corresponding CMS build; do not maintain tool-specific base documents.
- Use CMS's existing recursive `*-claims.json` discovery, exclusions, application order, name matching, and fragment precedence. Do not add a different CLI ordering rule.
- A valid, readable directory containing no fragments produces the base policy. A missing or unreadable directory is a CLI failure.
- Register fragment-defined claim sets using the same behavior as CMS and emit a complete document with both `claimSets` and `claimsHierarchy`.
- For non-parent fragment entries, the entire `actions` list, including `enabled`, is informational for composition. Requested grants come from `authorizationStrategyOverridesForCRUD`, subject to existing CMS matching and precedence. Enabled actions without overrides add no grants; overrides request grants even when the corresponding action is disabled or absent from `actions`. Absent or empty override lists add no grants. These flags neither deny nor revoke existing or inherited permissions.
- Validate the final document with the shared CMS validator before writing it. Output is directly consumable by Filesystem mode and the existing `upload-claims` workflow without manual restructuring.
- Identical inputs under the same supported execution environment produce identical output. Serialization must not reorder fragments before composition or otherwise alter order-sensitive policy semantics.

### Resolution behavior and output

- Validate the complete input before resolving it.
- Resolve the same leaf-resource claims, granted actions, inherited grants, nearest applicable defaults, and authorization-strategy overrides that CMS exposes through `/v3/authorizationMetadata`.
- Defaults select authorization strategies for granted actions; they must not independently grant actions that CMS does not grant.
- Authorization equality and deduplication in the shared resolver compare action and authorization-strategy values structurally, preserving CMS's existing order-insensitive comparison of action and strategy collections, case-sensitive name matching, and first-encountered representative. Hashes may accelerate lookup but must not establish equality on their own. Correct both the authorization model's hash-only equality and the resolver's hash-only lookup; preserve CMS's ID allocation approach and public response shape.
- Output a JSON array using the existing `/v3/authorizationMetadata` claim-set shape, including claim names, authorization references, actions, and strategy names. Represent claim sets with no grants and resource claims with empty action lists consistently with CMS.
- After shared resolution, canonicalize claim-set, resource-claim, authorization-record, and action collections in the offline output serializer using stable ordering and reproducible authorization-reference IDs. Preserve each action's returned strategy sequence; do not sort strategy arrays or collapse distinct sequences. This presentation step must preserve resolved permissions and must not change CMS resolution, ID allocation, or response behavior.
- The output describes resolved permissions. It is not a replacement claims document and is not intended for `upload-claims`.

### Verification and comparison

- Validate and resolve the expected complete document locally using the shared implementation.
- Read all current claim-set authorization metadata from the existing `GET /v3/authorizationMetadata` endpoint, without a claim-set filter. Use the selected target and tenant for the entire comparison.
- Require resource claim URI uniqueness within each returned claim set, using `StringComparer.Ordinal` without URI normalization. The same URI in different claim sets is valid. Repeated URIs within one claim set are an execution failure with exit code `1`, even when their dereferenced permissions agree; do not merge, select, or silently deduplicate entries.
- Compare every expected and actual claim set, including empty claim sets. An extra claim set on either side is a mismatch.
- Compare effective claim-set membership, resource claim URIs, granted actions, and authorization strategies. Dereference authorization IDs before comparing; compare actual values rather than IDs, serialized bytes, or hash codes alone.
- Use one fixed comparison policy: compare claim-set names with `StringComparer.OrdinalIgnoreCase`; compare resource claim URIs, action names, and authorization-strategy names with `StringComparer.Ordinal`. Compare each action's strategy sequence positionally after dereferencing authorization IDs. Ignore claim-set, resource-claim, authorization-record, and action collection order. Do not normalize names or URIs or add strategy-specific execution rules to the CLI.
- Ignore database/generated IDs and catalog properties such as `isSystemReserved` that do not describe effective permissions. Raw hierarchy differences that resolve to identical metadata are not mismatches. Verification compares the resulting metadata and cannot detect sequence differences already erased by shared resolution's order-insensitive deduplication.
- Report missing and unexpected claim sets, resource claims, and actions, plus differing strategy names or order. Each difference identifies its claim set, resource claim, and action where applicable, and distinguishes expected from actual values. An order-only strategy difference reports the expected and actual sequences and returns exit code `2`.
- Produce a stable, human-readable report on stdout. On success, explicitly state that CMS authorization metadata matches; do not claim that live Ed-Fi API enforcement was tested.
- Authentication failures, authorization failures, network failures, non-success HTTP responses, and malformed or internally inconsistent response payloads are execution failures, not policy mismatches. Never report a match using partial or unavailable metadata.
- Verification performs no writes, uploads, claim-set edits, reloads, or cache invalidation. Its result describes the metadata returned by that request, not a guarantee against subsequent policy edits.

### Diagnostics, exit codes, and file handling

- Shared composition results expose machine-readable diagnostics for errors and skipped or unapplied input; the CLI must not infer failure by parsing log text.
- Preserve CMS's existing handling of these conditions through its adapter. The CLI fails when invalid input, skipped fragments, unapplied requested grants, or failed claim-set registration would otherwise produce an incomplete policy.
- Grants superseded by existing CMS precedence rules are not composition failures. Invalid or unresolvable input, unreadable or skipped fragments, and failed claim-set registration are failures.
- Nonempty `claimSets` or `_defaultAuthorizationStrategiesForCrud` on an `isParent: true` entry matching an existing claim are unapplied input, even when they repeat existing policy. Return a structured diagnostic identifying the fragment, claim, and ignored field path; the CLI prints it to stderr, fails `merge` with exit code `1`, and leaves the destination unchanged. Do not implement an equivalence check. Absent or empty collections do not trigger this diagnostic. Preserve CMS composition and continuation behavior.
- Apply the same unapplied-input rule to nonempty `children`, `claimSets`, or `_defaultAuthorizationStrategiesForCrud` on non-parent entries; nonempty `authorizationStrategyOverridesForCRUD` on parent entries; and nonempty `claimSets`, `_defaultAuthorizationStrategiesForCrud`, `authorizationStrategyOverridesForCRUD`, or further `children` on children whose names alone are copied by `ApplyParentResourceClaim`. Name-only children remain supported. Use the actual fragment JSON field name `_defaultAuthorizationStrategiesForCrud` in diagnostics and documentation; binding remains case-insensitive. Export IDs and descriptive flags are not requested policy.
- The informational `actions` list on non-parent entries does not cause unapplied-input diagnostics or CLI failure. Do not add flag/override consistency validation. Independently invalid input, unmatched grant targets, and other incomplete-composition conditions still fail.
- Include retired `*-claimset.json` files in skipped-input diagnostics, so an accidentally outdated filename cannot result in an apparently successful base-only policy. Unrelated files outside the fragment conventions are not errors.
- Ordinary informational diagnostics do not cause failure. Errors identify the relevant file and claim/claim set where available, with strings normalized for safe diagnostic output.
- New commands use the following exit codes:

  | Exit code | Meaning |
  | --- | --- |
  | `0` | Successful merge or resolution, or a complete verification match. |
  | `1` | Invalid arguments/input, incomplete composition, validation failure, I/O failure, or verification execution failure. |
  | `2` | Verification completed successfully and found a policy mismatch. |

- Operational diagnostics go to stderr. `merge` and `resolve` write JSON only to the requested file, not stdout.
- Follow the existing conversion command's file behavior: the parent directory must exist; an existing destination requires `--force`; complete processing, validation, and serialization before replacement; stage output beside the destination and replace only on success.
- Input, validation, composition, resolution, and write failures leave an existing destination unchanged and do not leave a new partial output. Clean up temporary files.

### Testing

- Keep detailed behavior and failure cases in focused unit tests. Use a small set of service and installed-package scenarios for integration coverage; a full cross-product of scenarios, backends, identity providers, Data Standards, and loading modes is not required.
- Cover bundled Data Standards `5.2` and `6.1`, the shared CMS default when the option is omitted, and failure for an unsupported version. Assert version-specific policy content and include Hybrid/offline parity for each supported base without duplicating the full test matrix.
- Add focused unit fixtures with exact expected permissions for parent inheritance, child grants, defaults, override precedence, empty strategy lists, multiple strategies, empty claim sets, and leaf claims with no granted actions.
- Add a deterministic collision regression test proving that distinct authorizations with the same lookup hash remain distinct and that each resource retains its exact expected actions and strategies. Cover structural equality as well as resolver deduplication; reordered equivalent action and strategy collections must still deduplicate and retain the first-encountered representative.
- Include an explicit fragment-order regression fixture in which reversing application order changes the final policy. Confirm the CLI and CMS use the same existing order and that grants superseded by that precedence do not cause CLI failure.
- Cover differing and repeated ignored policy on `isParent: true` entries matching existing claims, plus absent and empty collections. Assert structured diagnostics, stderr output, exit code `1`, and destination preservation for nonempty ignored policy; absent and empty collections must not trigger this diagnostic.
- Cover every additional ignored policy field on non-parent entries, parent entries, and children whose names alone are copied. Assert the fragment, claim, and field path in structured diagnostics, exit code `1`, and destination preservation for nonempty collections; absent and empty collections and supported name-only children must not trigger these diagnostics.
- Test non-parent action flags using an isolated claim set with no other grants: enabled actions without overrides add no grants; a disabled action with an override grants that action; an override grants its action when `actions` is absent; absent or empty overrides add no grants. Assert exact composed grants and resolved permissions, successful CLI output, and no unapplied-input diagnostic caused solely by these flags.
- Validate deterministic resolved output and comparison behavior under permutations of semantically unordered metadata and changed authorization IDs. Exclude strategy arrays from those permutations. Assert that serialization preserves returned strategy sequences and that an order-only difference reports both sequences and returns exit code `2`. Confirm that real action or strategy changes still produce mismatches.
- Add focused comparison tests proving that case-only claim-set name differences match, while case-only differences in resource claim URIs, action names, and authorization-strategy names produce mismatches.
- Cover complete-document integrity checks in all three new commands. Reject catalog names that violate CMS length or whitespace rules, exact and case-only duplicate catalog names, and hierarchy references to undeclared claim sets; accept valid names unchanged and references that differ from their catalog entry only by case. Include schema-valid documents with invalid catalog names for `resolve` and `verify`. Assert exit code `1`, a useful diagnostic, and protected output-file behavior for failures.
- In all three new commands, reject duplicate hierarchy claim names with identical or conflicting policy, including duplicates across branches and involving non-leaf nodes. Assert exit code `1`, diagnostics identifying both locations, and destination preservation with no partial output. For CMS metadata, reject repeated resource URIs within one claim set even when permissions agree, and accept reuse across different claim sets. Cover ordinal case sensitivity and the absence of URI normalization.
- Test invalid JSON/schema, missing and unreadable paths, invalid claim-set names, skipped or unapplied fragments, retired filenames, and a valid empty fragment directory. Assert the exit code, useful diagnostic, and destination-file behavior.
- Exercise verification success, missing and unexpected entries, changed strategies, empty claim-set differences, invalid authorization references, authentication/authorization failures, HTTP failures, and malformed responses.
- Service-level parity tests load a fixture through actual CMS Hybrid composition and compare its API metadata with offline `merge` followed by `resolve`. Also load the complete merged document through supported complete-document loading paths and verify the same effective result.
- Run parity and stored-policy drift coverage against both PostgreSQL and SQL Server CMS backends. Make a deterministic `/v3/claimSets` policy edit after loading and assert that `verify` reports the exact resulting mismatch while the original files remain unchanged.
- Include reviewed expected metadata in tests so parity is not established solely by invoking the same shared function twice. Preserve existing CMS regression coverage through extraction.
- Test bearer-token and tenant-header handling and protect tokens from diagnostic output. Verification must not require unrestricted claims-management endpoints to be enabled.
- Extend installed-package checks to exercise the new offline commands and confirm the shared assembly, both embedded bases, and schema are packaged correctly. Include installed-tool verification against CMS in the appropriate service test lane.

### Documentation and 8.1 delivery

- Update tool help, the tool README, and the claims-loading guide with a complete merge → resolve → deploy → verify example. Show `--data-standard-version` matching CMS's configured `ClaimsOptions:DataStandardVersion`, and document the shared default and unsupported-version failure. Deployment uses existing supported loading/upload operations as a separate step.
- Explain exact all-claim-set comparison, exit codes, token and tenant inputs, file replacement rules, and the difference between complete claims documents and resolved metadata.
- Document the new commands' hierarchy-wide claim-name uniqueness requirement and per-claim-set resource URI uniqueness requirement for CMS metadata. Otherwise schema-valid documents containing duplicate hierarchy names are unsupported by these commands. These CLI restrictions do not change CMS loading, resolution, or response behavior.
- Document positional strategy-sequence comparison and the limitation that verification cannot detect ordering differences already erased by shared resolution's deduplication.
- Explain failures for all specified ignored policy fields, including policy on children whose names alone are copied, and the informational role of non-parent `actions` flags. Use `_defaultAuthorizationStrategiesForCrud` for the fragment defaults field. Clarify that the existing four-enabled-actions/Create-only fragment example requests only a Create grant, subject to CMS matching and precedence.
- Document that startup files do not necessarily represent the stored policy, that verification reads current CMS metadata, and that it does not test DMS cache freshness or live API authorization.
- Require the documented matching tool/CMS release combination and selected Data Standard base for Hybrid parity. Shared code alone does not guarantee equality across different releases or embedded bases. Retain a usable tool `--version` for identifying the installed build; no new compatibility-negotiation endpoint is required.
- Restore supported offline merging documentation using `merge`; do not restore the obsolete `Transform` command.
- Deliver through the existing 8.1 tool packaging and versioning policy. This story does not create an independent shared-library publication or alter release allocation rules.

## Tasks

1. Extract composition, resolution, supporting contracts, and shared base/schema access and Data Standard selection into a library reachable by CMS and the CLI. Keep repository loading and HTTP response handling in CMS adapters.
2. Add structured composition diagnostics and adapt CMS to retain its existing continuation/fallback behavior. Add regression coverage before changing callers.
3. Implement `merge` with shared Data Standard base selection, the existing fragment semantics, strict CLI treatment of incomplete results, complete-document validation, and protected file output.
4. Implement `resolve` using the shared resolver, the complete input catalog and hierarchy, and deterministic metadata serialization. Correct hash-only authorization equality and deduplication with structural comparisons.
5. Implement `verify` against `/v3/authorizationMetadata`, with environment-supplied bearer authentication, optional tenant selection, semantic comparison, textual differences, and the defined exit codes.
6. Add unit, CMS parity, complete-document loading, and API-edit drift tests; run the applicable CMS suites against both supported database backends.
7. Update installed-package verification and CI coverage, preserving existing conversion and packaging checks.
8. Update help and documentation for the 8.1 workflow, matching release requirement, and verification guarantee.

## Clarifying Questions and Answers

### Questions 1

1. Beyond the specified ignored policy on matching parent entries, which discarded fragment fields must make `merge` fail? `ClaimsFragmentComposer` copies only names from parent `children`, ignores `children`, `claimSets`, and `_defaultAuthorizationStrategiesForCrud` on non-parent entries, and ignores `authorizationStrategyOverridesForCRUD` on parent entries. Should nonempty policy in each of these locations produce an unapplied-input diagnostic, or are any informational export fields like non-parent `actions`? This determines the diagnostic and regression-test scope without changing CMS composition.

2. How should the new commands handle duplicate resource claim URIs? The shared schema permits repeated hierarchy claim names, and the resolver can emit multiple claims with the same URI but different authorizations within one claim set. Should complete-document integrity reject these duplicates, and should `verify` classify duplicate resource claims in CMS metadata as execution failures even when their permissions agree? Define the uniqueness scope and comparison behavior while preserving CMS's existing loading and resolution semantics.


### Answers 1

1. Treat the other discarded policy-bearing fields as unapplied input, following this story's requirement to fail incomplete composition. Fail `merge` for nonempty `children`, `claimSets`, or `_defaultAuthorizationStrategiesForCrud` on a non-parent entry; nonempty `authorizationStrategyOverridesForCRUD` on a parent entry; and nonempty `claimSets`, defaults, overrides, or further `children` on a child whose name alone is copied by `ApplyParentResourceClaim`. Name-only children of parent entries remain supported. Use the actual fragment JSON field name `_defaultAuthorizationStrategiesForCrud` in diagnostics and documentation; the composer binds it case-insensitively. Keep the specified non-parent `actions`/`enabled` fields informational, and do not treat export IDs or descriptive flags as requested policy. Absent or empty ignored collections do not fail, and policy actually superseded by CMS precedence remains exempt. Do not compare ignored policy with existing policy for equivalence. Emit structured diagnostics identifying the fragment, claim, and field path, return exit code `1`, and preserve the destination. CMS must retain its existing composition and continuation behavior. Expand the story's diagnostic criteria and focused regression tests to cover these discarded fields, including policy on name-only children.

2. Require exact claim-name/URI uniqueness across the entire complete-document hierarchy, including non-leaf nodes and separate branches, using `StringComparer.Ordinal` without URI normalization. All three new commands must reject duplicate hierarchy names during complete-document integrity validation with exit code `1`, identifying the duplicate name and both locations, preserving existing destination files, and leaving no partial output. In CMS authorization metadata, require resource claim URI uniqueness within each claim set; the same URI may appear in different claim sets. `verify` must treat repeated URIs within one claim set as an execution failure with exit code `1`, even when their dereferenced permissions agree, without merging, selecting, or silently deduplicating entries. Apply these restrictions only to the new CLI commands; preserve CMS loading, resolution, and response behavior. Document that otherwise schema-valid documents containing duplicate hierarchy names are unsupported by these commands. Add focused tests for identical and conflicting duplicates, duplicates across branches and involving non-leaf nodes, and valid reuse across different claim sets.
