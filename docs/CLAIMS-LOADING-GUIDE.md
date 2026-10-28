# Claims Loading Deployment Guide

## Overview

The Ed-Fi API Configuration Service supports three distinct modes for loading and managing claims authorization data based on the `CONFIGURATION_SERVICE_CLAIMS_SOURCE` configuration. This guide covers the deployment, configuration, and management of all claims loading modes, from embedded defaults to dynamic fragment composition and direct API uploads.

### Claims System Architecture

The claims system consists of two main components:

1. **Claim Sets**: Define named collections of permissions (similar to roles)
2. **Claims Hierarchy**: Defines resource claims and their authorization strategies

These components work together to control access to Ed-Fi resources through the DMS platform.

```
┌─────────────────────────────────────────────────────────┐
│                    Claims Loading Flow                  │
├─────────────────────────────────────────────────────────┤
│                                                         │
│  1. Embedded Claims.json (Base)                         │
│                ↓                                        │
│  2. Claims Fragment Files (Optional Transformations)    │
│                ↓                                        │
│  3. Final Composed Claims (Validated)                   │
│                                                         │
└─────────────────────────────────────────────────────────┘
```

## Understanding Claims Structure

### Claims.json Structure

The Claims.json file contains two main sections and is used as:
- The initial embedded claims loaded from assembly resources
- The structure accepted by the upload management API
- The final composed result after transformations from additional claimsets (extensions, etc.)

```json
{
  "claimSets": [
    {
      "claimSetName": "SISVendor",
      "isSystemReserved": true
    },
    {
      "claimSetName": "CustomClaimSet",
      "isSystemReserved": false
    }
  ],
  "claimsHierarchy": [
    {
      "name": "http://ed-fi.org/identity/claims/domains/edFiTypes",
      "defaultAuthorization": {
        "actions": [
          {
            "name": "Read",
            "authorizationStrategies": [
              {
                "name": "NoFurtherAuthorizationRequired"
              }
            ]
          }
        ]
      },
      "claims": [
        {
          "name": "http://ed-fi.org/identity/claims/ed-fi/academicWeeks",
          "claimSets": [
            {
              "name": "CustomClaimSet",
              "actions": [
                {
                  "name": "Create",
                  "authorizationStrategyOverrides": [
                    {
                      "name": "NamespaceBased"
                    }
                  ]
                },
                {
                  "name": "Read",
                  "authorizationStrategyOverrides": [
                    {
                      "name": "NoFurtherAuthorizationRequired"
                    }
                  ]
                },
                {
                  "name": "Update",
                  "authorizationStrategyOverrides": [
                    {
                      "name": "NamespaceBased"
                    }
                  ]
                },
                {
                  "name": "Delete",
                  "authorizationStrategyOverrides": [
                    {
                      "name": "NamespaceBased"
                    }
                  ]
                }
              ]
            }
          ]
        }
      ]
    }
  ]
}
```

### Key Components

#### Claim Sets Section
- **claimSetName**: Unique identifier for the claim set
- **isSystemReserved**: Boolean indicating if the claim set is protected from modification

#### Claims Hierarchy Section
- **name**: Domain or resource claim identifier
- **defaultAuthorization**: Default authorization strategies inherited by the claim and its children
- **claims**: Nested child claims, such as the resource claims within a domain
- **claimSets**: Per-claim-set grants on a claim, each naming a claim set and the actions it may perform
- **actions**: CRUD operations (Create, Read, Update, Delete, ReadChanges)
- **authorizationStrategies**: Security strategies on a `defaultAuthorization` action
- **authorizationStrategyOverrides**: Strategies replacing the inherited default for a claim set action

### Per-Data-Standard Embedded Claims

The embedded base `Claims.json` is stored per Data Standard version under
`src/config/backend/EdFi.DmsConfigurationService.Claims/Claims/Standards/ds<NN>/Claims.json`
(`ds52`, `ds61`, ...). CMS passes `ClaimsOptions:DataStandardVersion`
(Compose environment variable `CONFIGURATION_SERVICE_DATA_STANDARD_VERSION`) to the
shared CMS claims library; offline `merge` passes `--data-standard-version`
instead. Both default to `5.2` and fail if the requested version's resource is absent.
In CMS, this selection applies to the **Embedded** and **Hybrid** modes below
(both load the embedded base); **Filesystem** mode reads its base from disk instead. See
[Data Standard Versions](./DATA-STANDARD-VERSIONS.md) for the full version mechanism.

## Claims Loading Modes

### Mode 1: Embedded Only (Production Default)

The most secure mode for production deployments. Uses only the built-in Claims.json embedded in the assembly.

**Configuration:**
```bash
# Set claims source to embedded mode
CONFIGURATION_SERVICE_CLAIMS_SOURCE=Embedded
```

**Characteristics:**
- No external dependencies
- Immutable at runtime
- Most secure for production

### Mode 2: Hybrid Mode (Development/Testing)

Starts with embedded Claims.json and applies fragment transformations to extend or modify the base claims.

**Configuration:**
```bash
# Set claims source to hybrid mode
CONFIGURATION_SERVICE_CLAIMS_SOURCE=Hybrid

# Path to fragment files directory
CONFIGURATION_SERVICE_CLAIMS_DIRECTORY=/app/claims-fragments
```

**Characteristics:**
- Base security from embedded claims
- Extensible through fragments

### Mode 3: Filesystem Only (Complete External Control)

Loads claims entirely from external filesystem sources without embedded base claims.

**Configuration:**
```bash
# Set claims source to filesystem mode
CONFIGURATION_SERVICE_CLAIMS_SOURCE=Filesystem

# Path to claims files directory
CONFIGURATION_SERVICE_CLAIMS_DIRECTORY=/app/claims-files
```

### Mode 4: Direct Upload (Dynamic Management)

Complete replacement of claims via the management API. The uploaded structure must match the full Claims.json format.

**Configuration:**
```bash
# Required for management endpoints
CONFIGURATION_SERVICE_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING=true

# Optional: Can be combined with any base mode
CONFIGURATION_SERVICE_CLAIMS_SOURCE=Embedded  # or Hybrid, or Filesystem
CONFIGURATION_SERVICE_CLAIMS_DIRECTORY=/app/claims-fragments  # if using Hybrid or Filesystem
```

**Characteristics:**
- Complete control over claims
- Runtime modifications possible
- Highest flexibility
- Requires careful security consideration

## Fragment-Based Extension

### Fragment File Structure

Fragment files extend the claims hierarchy. Each fragment must follow the naming pattern `*-claims.json`. A fragment can add resource claims to the hierarchy and grant actions on resource claims that already exist.

A fragment can define one claim set. Its non-parent resource claims (those without `"isParent": true`) grant actions under the fragment's top-level `name`, or under the file name when `name` is absent. When the base claims do not already declare that name, it is registered as a new system-reserved claim set. Because it is system-reserved, the Management API cannot change or delete it, and removing the fragment later does not remove the claim set. A name must follow the Management API claim set name rules (non-empty, at most 256 characters, no white space). A fragment whose name contains white space or is longer than 256 characters is still applied, but its claim set is not registered and an error is logged. A fragment with an empty `name` fails claims validation, and the whole claims load is rejected. Parent resource claims (`"isParent": true`) only add hierarchy nodes and attach actions to claim sets that already exist; a fragment containing only parent entries defines no claim set.

A non-parent entry's `name` is resolved to a resource claim in two steps.
First, the name is compared as written with the last two URI segments of each claim, ignoring case, for example `ed-fi/candidateDemographics` or `domains/systemDescriptors`.
Only if that finds no claim is the singular form tried: a final `ies` becomes `y` and otherwise a final `s` is dropped, so `ed-fi/schools` resolves to `ed-fi/school` and `ed-fi/localEducationAgencies` resolves to `ed-fi/localEducationAgency`.
Other plurals match nothing, because the singular form of `ed-fi/people` is `ed-fi/people` and that of `ed-fi/studentPathPhaseStatuses` is `ed-fi/studentPathPhaseStatuse`, so write the claim name itself.
Because only the last two URI segments are compared, a full claim URI does not match.
An entry that matches no claim is skipped and a warning is logged naming the fragment file, the claim set and the name, but the service still starts and the claim set is still registered.

A fragment that defines a claim set looks like this:

```json
{
  "name": "CustomClaimSet",
  "resourceClaims": [
    {
      "id": 1,
      "name": "ed-fi/resourceName",
      "actions": [
        {
          "name": "Create",
          "enabled": true
        },
        {
          "name": "Read",
          "enabled": true
        },
        {
          "name": "Update",
          "enabled": true
        },
        {
          "name": "Delete",
          "enabled": true
        }
      ],
      "children": [],
      "authorizationStrategyOverridesForCRUD": [
        {
          "actionId": 1,
          "actionName": "Create",
          "authorizationStrategies": [
            {
              "authStrategyId": 1,
              "name": "NamespaceBased",
              "isInheritedFromParent": false
            }
          ]
        }
      ],
      "_defaultAuthorizationStrategiesForCrud": []
    }
  ]
}
```

This four-enabled-actions example requests **only Create**, through
`authorizationStrategyOverridesForCRUD`, subject to name matching and existing
precedence. Non-parent `actions` and `enabled` are informational: enabled actions
without overrides grant nothing; overrides request grants even for disabled actions
or when `actions` is absent; absent/empty overrides add nothing. Flags do not deny
or revoke inherited/existing grants. Replace `ed-fi/resourceName` with an existing
claim; an unmatched target makes the CLI merge fail.

### Fragment File Naming Convention

Files must follow the pattern: `{number}-{description}-claims.json`

Examples:
- `004-sample-extension-claims.json`
- `005-homograph-extension-claims.json`

### Fragment Composition Process

1. **Discovery**: System scans the configured directory for `*-claims.json` files
2. **Ordering**: Files are sorted alphabetically (use numeric prefixes for control)
3. **Transformation**: Each fragment is applied sequentially to the base claims
4. **Validation**: Final composition is validated against JSON Schema
5. **Loading**: Validated claims become the active authorization configuration

## Management Operations

### Upload Claims Directly

The upload endpoint accepts the **full Claims.json structure**, which is a superset of the fragment schema. This means it includes both `claimSets` and `claimsHierarchy` sections:

```bash
# Upload complete claims structure
curl -X POST -H "Content-Type: application/json" \
  -H "Authorization: Bearer $TOKEN" \
  -d '{
    "claimSets": [
      {
        "claimSetName": "CustomVendor",
        "isSystemReserved": false
      }
    ],
    "claimsHierarchy": [
      {
        "name": "http://ed-fi.org/identity/claims/domains/edFiTypes",
        "defaultAuthorization": {
          "actions": [
            {
              "name": "Read",
              "authorizationStrategies": [
                {"name": "NoFurtherAuthorizationRequired"}
              ]
            }
          ]
        },
        "claims": [
          {
            "name": "http://ed-fi.org/identity/claims/ed-fi/students",
            "claimSets": [
              {
                "name": "CustomVendor",
                "actions": [
                  {
                    "name": "Create",
                    "authorizationStrategyOverrides": [
                      {"name": "NamespaceBased"}
                    ]
                  }
                ]
              }
            ]
          }
        ]
      }
    ]
  }' \
  http://localhost:8081/management/upload-claims
```

**Important Notes:**
- The uploaded document becomes the active claims configuration. Internally, persisted
  non-system-reserved claim-set rows are replaced while existing system-reserved claim-set rows are
  not deleted.
- Must include both `claimSets` and `claimsHierarchy` sections
- The request body is the claims document itself. It is not nested under a `claims` property; a body
  shaped that way is rejected with HTTP 400 naming the missing `claimSets` and `claimsHierarchy`
  properties.
- Subject to JSON Schema validation
- Returns the new reload ID in the response body: `{ "success": true, "reloadId": "<guid>" }`

### Reload Claims from Fragments

Recomposes claims from embedded base and fragment files:

```bash
curl -X POST -H "Authorization: Bearer $TOKEN" \
  http://localhost:8081/management/reload-claims
```

On success the response body carries the status and the new reload ID:

```json
{ "success": true, "reloadId": "<guid>" }
```

A failed reload is returned as an Ed-Fi problem-details error response, not as an error list on a
success body.

### Get Current Claims

Retrieve the currently active claims configuration:

```bash
curl -H "Authorization: Bearer $TOKEN" \
  http://localhost:8081/management/current-claims
```

Returns the full Claims.json structure currently in use, along with an `X-Reload-Id` response header
identifying the loaded claims. This is the only endpoint that sets that header, and the body is
exactly the document the upload endpoint accepts, so the response can be uploaded again without
modification.

### Round-Trip: Download and Re-Upload

Because both endpoints use the same two-section document, a downloaded file can be uploaded as-is:

```bash
# Download the active claims
curl -H "Authorization: Bearer $TOKEN" \
  http://localhost:8081/management/current-claims > claims.json

# Upload it unchanged (or after editing claims.json)
curl -X POST -H "Content-Type: application/json" \
  -H "Authorization: Bearer $TOKEN" \
  --data-binary @claims.json \
  http://localhost:8081/management/upload-claims
```

No wrapping, re-indenting or property renaming is required in between. The upload responds with HTTP
200 and a new reload ID.

## Offline preparation and verification (8.1)

Use the .NET 10 [Security Metadata Tools](../src/config/clis/EdFi.DmsConfigurationService.SecurityMetadataTools/README.md)
from the corresponding CMS release/build. Run `security-metadata-tools --version`
to record the installed build; see the authoritative
[version allocation and release policy](../src/config/clis/EdFi.DmsConfigurationService.SecurityMetadataTools/VERSIONING.md).
For a published release, find the matching tool version in the
`EdFi.Api.SecurityMetadataTools` package entry in `EdFi.Api.SecurityMetadataTools-SBOM.zip`,
attached to the corresponding [GitHub release](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/releases).
Tool and app patch numbers need not match. There is no cross-release compatibility
guarantee. `merge --data-standard-version` must match
`ClaimsOptions:DataStandardVersion`, configured in Compose through
`CONFIGURATION_SERVICE_DATA_STANDARD_VERSION`. Both use **5.2** by default
(including unset/whitespace selection); **6.1** is explicit. Unsupported bundled
versions fail with exit `1` and preserve the destination.

### Prepare and inspect offline

With the matching tool installed, start in an empty working directory. This Bash
example creates an `Offline` claim set granting Read access to students:

```sh
mkdir -p fragments policy-output
cat > fragments/offline-claims.json <<'JSON'
{
  "name": "Offline",
  "resourceClaims": [
    {
      "name": "ed-fi/students",
      "authorizationStrategyOverridesForCRUD": [
        {
          "actionName": "Read",
          "authorizationStrategies": [{ "name": "NoFurtherAuthorizationRequired" }]
        }
      ]
    }
  ]
}
JSON
security-metadata-tools merge \
  --fragments ./fragments \
  --data-standard-version 5.2 --output policy-output/Claims.json
security-metadata-tools resolve \
  --input policy-output/Claims.json --output policy-output/authorization-metadata.json
```

Omit the version flag for the same 5.2 result; use `--data-standard-version 6.1`
when CMS is configured for 6.1. A readable directory containing no fragments
produces the selected base. Merge uses the shared CMS discovery, order, matching,
and precedence, with no arbitrary base-file option. CLI composition uses invariant
culture to match the default CMS container. CMS deployments with a custom culture
must use invariant culture for the same fragment ordering.

`Claims.json` is a **complete claims document** with `claimSets` and
`claimsHierarchy`. Deploy that file. `authorization-metadata.json` is the resolved
metadata array for inspection, with leaf permissions, inherited grants, defaults,
and overrides. Defaults select strategies for granted actions, not new grants.
Resolved metadata cannot be used as the input to resolve, verify, or upload.
The existing `convert-ods-xml --input export.xml --output Claims.json` command also
produces a complete document suitable for this workflow.

All file-producing commands require an existing parent directory. Add `--force`
only to permit replacement of an existing output. Validation and serialization
complete before same-directory staging and atomic publication; failures preserve
existing bytes and create no partial destination. JSON goes only to the output
file; failures use safely rendered stderr. Help/version use stdout; parser usage
help for conversion argument errors may also use stdout.

### Deploy the complete document separately

For **Filesystem startup**, mount the generated file unchanged as `Claims.json`
in an otherwise empty claims directory, then start CMS against a fresh database:

```sh
CONFIGURATION_SERVICE_CLAIMS_SOURCE=Filesystem
CONFIGURATION_SERVICE_CLAIMS_MOUNT_SOURCE=/absolute/path/to/claims
CONFIGURATION_SERVICE_CLAIMS_DIRECTORY=/app/additional-claims
CONFIGURATION_SERVICE_DATA_STANDARD_VERSION=5.2
CONFIGURATION_SERVICE_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING=false
```

These are Compose environment-file settings. Replace `/absolute/path/to/claims`
with a host directory containing only a copy of the generated `policy-output/Claims.json`.
The repository's local and published Compose configurations mount that directory
at `/app/additional-claims`. Do not include the fragments already merged into the file.
With direct application configuration, use
`ClaimsOptions__ClaimsSource`, `ClaimsOptions__ClaimsDirectory`, and
`ClaimsOptions__DataStandardVersion`. Filesystem loads the file rather than an
embedded base. Keep the tool's selected Data Standard aligned with the deployment.
Startup loads only an empty database, so a restart against existing storage does
not apply changed files.

For an authorized **upload** in an environment where unrestricted loading is
permitted, enable the existing management gate and send the generated bytes:

```sh
curl --fail-with-body -X POST \
  -H "Authorization: Bearer $CMS_ACCESS_TOKEN" \
  -H 'Content-Type: application/json' \
  --data-binary @policy-output/Claims.json \
  http://localhost:8081/management/upload-claims
```

Supply an already-issued admin token through `CMS_ACCESS_TOKEN`; use the role,
scope, and feature-gate requirements under [Endpoint Authentication](#endpoint-authentication).
If the deployment uses tenant routing, also send `-H 'Tenant: tenant-name'`.
Do not wrap, reserialize, or edit the merged catalog before upload. Existing
system-reserved rows are retained, so a previously populated target may contain
extra sets that verification reports. Upload is a separate operation from verify.

### Compare with current stored policy

With an already-issued CMS bearer token, this Bash example prompts without echoing
it or placing its value in the verify arguments:

```sh
read -r -s -p 'CMS bearer token: ' CMS_ACCESS_TOKEN
printf '\n'
export CMS_ACCESS_TOKEN
security-metadata-tools verify --input policy-output/Claims.json \
  --cms-url http://localhost:8081 --access-token-env CMS_ACCESS_TOKEN
# Add --tenant tenant-name when tenant routing is required.
unset CMS_ACCESS_TOKEN
```

The three shown flags are required. `--tenant` is the only optional verification
flag; it sends the `Tenant` routing header, which provides data partitioning, not
authorization. The base HTTP(S) URL may include a deployment prefix but no userinfo,
query, or fragment. `--access-token-env` names an environment variable containing
the token; missing/empty credentials fail before HTTP. Token acquisition remains
external to the tool for either supported CMS identity provider.

Verification reads all sets from `GET /v3/authorizationMetadata` using its existing
authentication requirements. It requires neither unrestricted claims loading nor
a new endpoint. It does not upload, edit, reload, or invalidate caches.

| Exit | Meaning | Stream |
| --- | --- | --- |
| `0` | Successful merge/resolve, or all CMS metadata matches | Verify match report on stdout; merge/resolve JSON only in file |
| `1` | Invalid/incomplete input, HTTP/authentication/authorization failure, malformed response, or execution failure | Diagnostics on stderr |
| `2` | Valid metadata differs | Stable expected/actual difference report on stdout |

Comparison includes empty sets and every expected/actual set. Set names compare
with `OrdinalIgnoreCase`; resource URIs, action names, and strategy names with
`Ordinal`, without normalization. Authorization IDs are dereferenced. Generated
IDs, `isSystemReserved`, and claim-set/claim/authorization/action collection order
are ignored. Strategy sequences compare positionally, including multiplicity;
order-only changes return `2`. Raw hierarchy differences resolving identically
match. Shared resolution's order-insensitive deduplication keeps the first
representative; ordering differences already erased there cannot be detected.

The three new commands enforce the shared schema plus catalog names of 1–256
UTF-16 code units satisfying the existing CMS naming rules, case-insensitive
catalog uniqueness and reference lookup, and references only to declared sets.
Claim names/URIs must be ordinally unique across the entire complete-document
hierarchy, including parents
and separate branches, even for identical policy. Received CMS metadata must have
ordinally unique URIs within each set (reuse across different sets is valid).
Duplicates or inconsistent metadata are exit `1`, not mismatches. These CLI
restrictions do not alter CMS loading, validation, or response behavior.

Startup files may differ from stored policy after administrative edits or uploads.
A match describes only the CMS metadata returned by that request; it does not
establish DMS cache freshness, live API enforcement, strategy support, or immunity
from later policy edits.

### Strict merge diagnostics

CMS retains its existing continuation/fallback behavior. The offline merge rejects
incomplete composition: missing/unreadable directories, unreadable or malformed
fragments, fragments with missing or null `resourceClaims`, unmatched grants,
invalid registration, and retired `*-claimset.json` files. An explicitly empty
`resourceClaims` array is valid. Unrelated files and ordinary informational
diagnostics are not errors; existing precedence superseding a grant is not an error.

These nonempty fields are unapplied requested policy and fail merge even if their
values repeat existing policy; absent/empty collections do not cause this error:

| Entry | Unapplied fields |
| --- | --- |
| Existing matched parent | `claimSets`, `_defaultAuthorizationStrategiesForCrud` |
| Any parent | `authorizationStrategyOverridesForCRUD` |
| Non-parent | `children`, `claimSets`, `_defaultAuthorizationStrategiesForCrud` |
| Child whose name alone is copied | `claimSets`, `_defaultAuthorizationStrategiesForCrud`, `authorizationStrategyOverridesForCRUD`, further `children` |

New parents can apply defaults and claim-set grants. Name-only children that are
actually appended succeed. Children discarded because an existing matched claim
has no child collection or because the child name is missing/empty fail with an
indexed location. Discarded null policy entries in non-parent overrides or
new-parent defaults also fail. Diagnostics retain fragment, claim/claim-set and
JSON location context and use `_defaultAuthorizationStrategiesForCrud`; binding
remains case-insensitive. Export IDs/descriptive flags and non-parent
`actions`/`enabled` are informational, with the Create-only behavior explained in
[Fragment File Structure](#fragment-file-structure).

For developer instructions to reproduce the deployment and drift checks, see the
[isolated policy test README](../src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/TestData/Claims/SecurityMetadataTools/README.md).

## Deployment Steps

### 1. Choose Loading Mode

Determine the appropriate mode based on your environment:
- **Production**: Use embedded only mode
- **Development/Testing**: Use hybrid mode with fragments
- **Dynamic Environments**: Enable upload capabilities

### 2. Prepare Configuration

#### For Embedded Only:
No additional configuration needed.

#### For Hybrid Mode:
1. Create fragment files following naming conventions
2. Validate JSON syntax of each fragment
3. Place fragments in designated directory
4. Set required environment variables

#### For Filesystem Mode:
1. Prepare a complete `Claims.json` (for example, with `merge` above), optionally accompanied by `*-claims.json` fragments that have not already been merged into it
2. Validate JSON syntax of each file
3. Place files in designated directory
4. Set required environment variables

#### For Upload Mode:
1. Enable dynamic loading environment variable
2. Prepare full Claims.json structure for upload
3. Validate against JSON Schema before upload

### Configuration Mode Summary

| Mode | CONFIGURATION_SERVICE_CLAIMS_SOURCE | CONFIGURATION_SERVICE_CLAIMS_DIRECTORY | Use Case |
|------|--------------------------|----------------------------|----------|
| Embedded | `Embedded` | Not used | Production default, highest security |
| Hybrid | `Hybrid` | Required | Development with fragment extensions |
| Filesystem | `Filesystem` | Required | Complete external control |
| Upload | Any | Optional | Dynamic management via API |

### Upgrading an Existing Deployment: E2E Claim Sets

Earlier releases shipped six test-only claim sets in the embedded base claims:

- `E2E-NameSpaceBasedClaimSet`
- `E2E-NoFurtherAuthRequiredClaimSet`
- `E2E-RelationshipsWithEdOrgsOnlyClaimSet`
- `E2E-RelationshipsWithEdOrgsOnlyInvertedClaimSet`
- `E2E-RelationshipsWithEdOrgsOnlyMixedStrategyClaimSet`
- `E2E-RelationshipsWithEdOrgsOnlyOrInvertedClaimSet`

New deployments no longer get them. The Configuration Service loads claims at startup only into an
empty database, so a database provisioned by an earlier release keeps these claim sets and their
grants after an upgrade. They are system-reserved, so neither a claims reload nor
`DELETE /v3/claimSets/{id}` removes them. To remove them:

1. Confirm that no application in any tenant uses one of them. System-reserved claim sets are
   shared by every tenant, and `GET /v3/applications` returns only the calling tenant's
   applications, so query the table directly:

   ```sql
   SELECT ApplicationName, ClaimSetName FROM dmscs.Application
   WHERE ClaimSetName IN (
       'E2E-NameSpaceBasedClaimSet',
       'E2E-NoFurtherAuthRequiredClaimSet',
       'E2E-RelationshipsWithEdOrgsOnlyClaimSet',
       'E2E-RelationshipsWithEdOrgsOnlyInvertedClaimSet',
       'E2E-RelationshipsWithEdOrgsOnlyMixedStrategyClaimSet',
       'E2E-RelationshipsWithEdOrgsOnlyOrInvertedClaimSet'
   );
   ```

   On PostgreSQL, quote the identifiers: `"dmscs"."Application"`, `"ApplicationName"` and
   `"ClaimSetName"`. Move every application this returns to another claim set first.
   Applications are not tied to claim set rows, so an application left on a removed claim set has
   every DMS request fail with a security-configuration error.
2. Delete the six rows from the `dmscs.ClaimSet` table:

   ```sql
   DELETE FROM dmscs.ClaimSet
   WHERE ClaimSetName IN (
       'E2E-NameSpaceBasedClaimSet',
       'E2E-NoFurtherAuthRequiredClaimSet',
       'E2E-RelationshipsWithEdOrgsOnlyClaimSet',
       'E2E-RelationshipsWithEdOrgsOnlyInvertedClaimSet',
       'E2E-RelationshipsWithEdOrgsOnlyMixedStrategyClaimSet',
       'E2E-RelationshipsWithEdOrgsOnlyOrInvertedClaimSet'
   );
   ```

   On PostgreSQL, quote the identifiers: `"dmscs"."ClaimSet"` and `"ClaimSetName"`.

The six claim sets' grants stay in the stored claims hierarchy. They have no effect unless a claim
set with one of these names is created again, which would inherit them, so do not reuse these
names. Do not use `POST /management/reload-claims` to clear them: a reload replaces the whole
stored claims document, which deletes every claim set that is not system-reserved and every grant
that is not in the configured claims source.

### Upgrading an Existing Deployment: Fragment Grant Names

Earlier releases converted every non-parent fragment entry's name to its singular form before matching it, so an entry naming a claim that ends in `s`, such as `domains/educationOrganizations` or `ed-fi/candidateDemographics`, matched nothing and was skipped without a warning.
Such an entry now matches its claim and grants the actions named in its `authorizationStrategyOverridesForCRUD` list; its `actions` list, including any `enabled` value, is not used.
A database whose claims are already loaded keeps them after an upgrade, so the change applies the next time claims are composed from fragments: when the service starts on an empty database, or after a claims reload.
Before that happens, review your fragments for entries that never took effect, and remove any whose access you do not want to grant.

## Security and Production Considerations

### Security Warnings

⚠️ **CRITICAL**: The `CONFIGURATION_SERVICE_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING` flag should **NEVER** be enabled in production environments.

This flag enables:
- Runtime modification of security policies
- Potential elevation of privileges
- Bypass of intended authorization controls

### Endpoint Authentication

Every `/management/*` claims endpoint requires a valid Configuration Service bearer token that satisfies **both** of the following, in addition to the dangerous flag above:

1. **The configuration-service role** — enforced by `SecurityConstants.ServicePolicy`. The token must carry the configured configuration-service role (the same role every other Configuration Service management endpoint requires); a token for a different role is rejected even when its scope would otherwise be sufficient.
2. **The endpoint-appropriate scope:**
   - `POST /management/upload-claims` and `POST /management/reload-claims` require the **admin** scope.
   - `GET /management/current-claims` requires the **read-only or admin** scope.

Status behavior:

- Missing or invalid bearer token → `401`.
- Authenticated token that lacks the required configuration-service role or scope → `403`.
- Fully authorized request while dynamic claims loading is disabled → `404`.

The `Authorization: Bearer $TOKEN` header shown in the examples above is enforced.

## Troubleshooting

### Common Configuration Issues

1. **Claims not loading from filesystem**
   - Verify `CONFIGURATION_SERVICE_CLAIMS_SOURCE` is set to `Hybrid` or `Filesystem`
   - Check that `CONFIGURATION_SERVICE_CLAIMS_DIRECTORY` points to the correct directory
   - Ensure fragment files follow the `*-claims.json` naming pattern

2. **Management endpoints returning 404**
   - Verify `CONFIGURATION_SERVICE_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING=true` is set
   - Check that the service is running with the correct environment variables

3. **Fragment files not being discovered**
   - Confirm files are in the directory specified by `CONFIGURATION_SERVICE_CLAIMS_DIRECTORY`
   - Verify file permissions allow the service to read the files
   - Check service logs for fragment discovery messages
   - A warning that a fragment file "is not read" names a file still using the pre-8.1
     `*-claimset.json` suffix; rename it to end in `-claims.json`. Fragments are read only when
     an empty database is seeded, on a claims reload, and on the first
     `GET /management/current-claims`, so the warning does not appear at every startup

4. **A fragment grant has no effect**
   - Check the service log for a warning containing "matches no resource claim"; it names the fragment file, the claim set and the entry that was skipped
   - The warning is logged only when claims are composed from the fragments, which a restart against a database that already has claims does not do, so a missing warning after such a restart does not mean the entry matched
   - Write the claim's own name, as described in [Fragment File Structure](#fragment-file-structure)
   - The service loads claims at startup only into an empty database, so restarting after a fragment changes does not change claims that are already loaded
   - Confirm the grant with `GET /v3/authorizationMetadata?claimSetName=<name>`; it lists leaf resource claims only, so a grant on a domain such as `domains/systemDescriptors` appears on the claims under that domain, not on the domain itself
