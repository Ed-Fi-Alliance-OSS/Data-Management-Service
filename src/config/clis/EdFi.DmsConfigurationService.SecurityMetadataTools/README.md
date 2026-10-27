# Ed-Fi API Security Metadata Tools

A .NET 10 command-line tool for preparing Ed-Fi API Configuration Service (CMS)
security policy offline and comparing it with current CMS authorization metadata.
Version 8.1 adds `merge`, `resolve`, and `verify` alongside `convert-ods-xml`.

## Commands and file handling

```sh
security-metadata-tools --help
security-metadata-tools --version
security-metadata-tools merge --help
security-metadata-tools resolve --help
security-metadata-tools verify --help
security-metadata-tools convert-ods-xml --help
```

| Command | Required flags | Optional flags | Result |
| --- | --- | --- | --- |
| `convert-ods-xml` | `--input <xml>`, `--output <json>` | `--force` | Complete claims document |
| `merge` | `--fragments <directory>`, `--output <json>` | `--data-standard-version <version>`, `--force` | Complete claims document |
| `resolve` | `--input <json>`, `--output <json>` | `--force` | Resolved authorization metadata array |
| `verify` | `--input <json>`, `--cms-url <base-url>`, `--access-token-env <variable-name>` | `--tenant <tenant-name>` | Text comparison report |

A **complete claims document** has top-level `claimSets` and `claimsHierarchy`.
Merge and conversion produce this document; resolve and verify accept it.
Resolved metadata is the `/v3/authorizationMetadata` JSON array describing leaf
claims, actions, authorization references, and strategies. It is for inspection;
it cannot be passed to resolve, verify, or `upload-claims` as a complete document.

File-producing commands require an existing parent directory. Successful commands
write JSON only to the destination, with no success output on stdout. Existing
files require `--force`; processing, validation, and serialization finish before
a temporary file in the destination directory is atomically moved into place.
Failures preserve existing destination bytes and create no partial destination.
`--force` permits replacement; it does not bypass validation. Diagnostics use
stderr with control characters normalized for safe display. Argument errors can
also print parser usage help (conversion prints that help to stdout).
Help and version output use stdout.

| Exit code | Meaning | Output |
| --- | --- | --- |
| `0` | Successful conversion, merge, resolution, or complete verification match | File for file-producing commands; match report on stdout for verify |
| `1` | Invalid input, incomplete composition, authentication/authorization, HTTP, response validation, or other execution failure | Diagnostics on stderr |
| `2` | Valid expected and received metadata differ | Stable difference report on stdout |

## Prepare → inspect → deploy → verify (8.1)

Use the tool from the corresponding CMS release/build and record
`security-metadata-tools --version` to identify the installed build. Tool patch
allocation follows [VERSIONING.md](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/src/config/clis/EdFi.DmsConfigurationService.SecurityMetadataTools/VERSIONING.md);
the tool and app patch numbers need not be equal. Cross-release compatibility is
not guaranteed. Select the same Data Standard as CMS's
`ClaimsOptions:DataStandardVersion` (Compose variable
`CONFIGURATION_SERVICE_DATA_STANDARD_VERSION`). The shared default is **5.2**,
including unset/whitespace selection; explicit **6.1** selects its bundled base.
Unsupported versions, for example `9.9`, exit `1` without replacing output.
Only bundled bases are selectable. For example, this unsupported selection fails
with exit `1`, leaves any existing `Claims.json` intact even with `--force`, and
prints the unavailable version on stderr:

```sh
security-metadata-tools merge --fragments ./fragments \
  --data-standard-version 9.9 --output Claims.json --force
```

From the repository root, this Bash example uses the reviewed Hybrid fragments
that the service parity tests run for both Data Standards. Use a fresh output
directory, or add `--force` when intentionally replacing its files:

```sh
mkdir -p policy-output
security-metadata-tools merge \
  --fragments src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/TestData/Claims/SecurityMetadataTools/Hybrid/fragments \
  --data-standard-version 5.2 --output policy-output/Claims.json
security-metadata-tools resolve \
  --input policy-output/Claims.json --output policy-output/authorization-metadata.json
```

Omit `--data-standard-version` for the same 5.2 base. For a CMS deployment configured
with `ClaimsOptions:DataStandardVersion=6.1`, use `--data-standard-version 6.1` and
review that version's result. Resolution includes inherited grants, nearest
applicable defaults and overrides. Defaults choose strategies for granted actions;
they do not grant actions by themselves. Offline metadata has stable collection
ordering and reproducible authorization IDs while preserving returned strategy
sequences. Identical inputs in the same supported environment yield identical bytes.

Deploy `policy-output/Claims.json` separately using the existing Filesystem startup
or upload operation in the
[claims-loading guide](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/docs/CLAIMS-LOADING-GUIDE.md#offline-preparation-and-verification-81).
For Filesystem startup, mount it unchanged as `Claims.json` in the configured claims
directory of a fresh CMS database. Startup seeds only an empty database; restarting
with changed files does not replace stored policy. For an authorized upload,
post the unchanged file to `/management/upload-claims` with unrestricted claims
loading enabled. Upload preserves existing reserved catalog rows, so they can
still appear as unexpected sets during verification.

After deployment, supply an already-issued CMS bearer token through the environment.
This Bash prompt keeps the token out of command arguments; token acquisition is
external to the tool and works with either supported CMS identity provider:

```sh
read -r -s -p 'CMS bearer token: ' CMS_ACCESS_TOKEN
printf '\n'
export CMS_ACCESS_TOKEN
security-metadata-tools verify --input policy-output/Claims.json \
  --cms-url http://localhost:8081 --access-token-env CMS_ACCESS_TOKEN
# With tenant routing, add: --tenant tenant-name
unset CMS_ACCESS_TOKEN
```

`--access-token-env` is the variable's **name**, never the token itself. Missing or
empty credentials fail before HTTP. The URL is an HTTP(S) CMS base URL and may
include a deployment prefix; omit userinfo, query, and fragment. Verification reads
all metadata with one authenticated `GET /v3/authorizationMetadata`. The token must
satisfy that endpoint's existing role/scope requirements; verification does not
require unrestricted claims loading. `--tenant` sends the optional `Tenant` header
for routing/data partitioning, not an authorization boundary. Verification acquires
no tokens and performs no upload, edit, reload, or cache invalidation.

## Complete-document and merge restrictions

The three new commands validate the CMS schema and then require:

- Catalog names of 1–256 UTF-16 code units with no whitespace, preserved unchanged.
  Catalog uniqueness and hierarchy reference lookup use `OrdinalIgnoreCase`;
  every hierarchy claim-set reference must name a declared catalog entry.
- Claim names/URIs unique across the **entire hierarchy**, including non-leaf nodes
  and separate branches, using `Ordinal` comparison with no URI normalization.
  Even identical policy at duplicate names is rejected, with both locations.

These are CLI restrictions; they do not change CMS validation, loading, or responses.

Merge shares CMS's recursive `*-claims.json` discovery, exclusions, existing
`OrderBy` file order, exact-name-then-singular matching, and first-association
precedence. Numeric filename prefixes make intended order clear. A readable empty
directory produces the bundled base. Missing/unreadable directories, unreadable or
malformed fragments, unmatched grants, failed registration, and retired
`*-claimset.json` files make composition incomplete and exit `1`. Unrelated files
are ignored. Grants superseded by existing precedence are not errors.

The following **nonempty** policy fields are unapplied input and fail merge, even
when they repeat the existing policy. Absent/empty collections do not trigger these
diagnostics. Field binding is case-insensitive; diagnostics use the spelling below.

| Entry | Unapplied fields |
| --- | --- |
| Parent matching an existing claim | `claimSets`, `_defaultAuthorizationStrategiesForCrud` |
| Any parent | `authorizationStrategyOverridesForCRUD` |
| Non-parent | `children`, `claimSets`, `_defaultAuthorizationStrategiesForCrud` |
| Child whose name alone is copied | `claimSets`, `_defaultAuthorizationStrategiesForCrud`, `authorizationStrategyOverridesForCRUD`, further `children` |

New parents can apply their claim sets and defaults. Name-only children are accepted
when actually appended. Requested children discarded because an existing matched
claim has no child collection, or because the child's name is missing/empty, fail
merge with an indexed child/name location. Null policy entries discarded from
non-parent `authorizationStrategyOverridesForCRUD` or new-parent
`_defaultAuthorizationStrategiesForCrud` also fail, with indexed locations.
CMS retains its existing continuation/fallback behavior for these conditions.

For non-parent entries, `actions` and `enabled` are informational. Export IDs and
descriptive flags are informational too. Requested grants come from
`authorizationStrategyOverridesForCRUD`, subject to matching and precedence:
enabled actions without overrides add no grants; overrides request their actions
even when `actions` is absent or the action is disabled; absent/empty overrides add
nothing. These flags do not deny or revoke inherited/existing permissions. The
claims guide's four-enabled-actions/Create-only example requests **only Create**,
subject to matching and precedence; it does not request Read, Update, or Delete.

## What verification compares

Verification compares **all** expected and actual claim sets, including empty sets.
Extra or missing sets are mismatches; there is no claim-set filter. It dereferences
authorization IDs and compares effective claims, actions, and strategy values.
Claim-set names use `OrdinalIgnoreCase`; claim URIs, action names, and strategy
names use `Ordinal`. No names or URIs are normalized.

Each action's strategy sequence is compared **positionally**, including multiplicity.
An order-only change returns `2` with expected and actual sequences. Claim-set,
claim, authorization-record, and action collection order is ignored, as are
generated/database IDs and catalog flags such as `isSystemReserved`. Different
hierarchies that resolve identically match. Shared resolution deduplicates equal
authorizations using order-insensitive action/strategy comparison and retains the
first representative; verify cannot detect ordering already erased by that step.

Received metadata must have unique resource URIs **within each claim set**, using
`Ordinal` without normalization. The same URI in different sets is valid; duplicates
within one set fail with `1` even when permissions agree. Malformed/inconsistent
metadata, unavailable/partial responses, and HTTP/authentication failures also exit
`1`, never a match or policy mismatch. Reports identify missing/unexpected sets,
claims, actions, and differing strategies with expected/actual context.

Startup files can differ from stored policy after an upload or administrative edit.
A match describes current CMS metadata returned by that request. It does not prove
DMS authorization-cache freshness, live API enforcement, support for every strategy,
or protection against later policy edits.

## Conversion rules

Support is limited to the [verified ODS XML export format and provenance](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/tree/main/src/config/clis/EdFi.DmsConfigurationService.SecurityMetadataTools.Tests.Unit/Fixtures/OdsSecurityMetadata).

- Claim-set names lose only literal spaces (`U+0020`) and hyphens (`U+002D`).
  Remaining whitespace, including nonbreaking spaces, and control characters are
  rejected. Names must contain 1–256 UTF-16 code units after normalization;
  remaining spelling, case, and valid non-ASCII characters are preserved.
  Distinct source names that collide case-insensitively fail conversion.
- Catalog entries and hierarchy references use the same mapped names. Every
  emitted catalog entry has `isSystemReserved: false`; uploading an existing
  reserved name preserves its stored reserved status.
- Only the exact, case-sensitive leading `http://ed-fi.org/ods/identity/claims/`
  prefix migrates to `http://ed-fi.org/identity/claims/`. A nonempty suffix with
  no `/` also gains `ed-fi/`: legacy `academicWeek` becomes
  `http://ed-fi.org/identity/claims/ed-fi/academicWeek`. Domain, service, and
  already-namespaced suffixes retain their paths. Already-current, external,
  and near-match URIs remain unchanged. URI collisions fail conversion.
- Nested policy, actions, authorization strategy names, and overrides are
  preserved. Populated `validationRuleSet` and `validationRuleSetOverride`
  attributes fail conversion; absent or empty values are accepted.

Conversion validates the complete document against the CMS claims schema.
Conversion and loading do not establish runtime support for every imported
strategy; review compatibility with the Ed-Fi API deployment enforcing it.

## Maintainer versioning

See [VERSIONING.md](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/src/config/clis/EdFi.DmsConfigurationService.SecurityMetadataTools/VERSIONING.md) for the committed tool patch, shared version
calculation, release preparation, and recovery policy.
