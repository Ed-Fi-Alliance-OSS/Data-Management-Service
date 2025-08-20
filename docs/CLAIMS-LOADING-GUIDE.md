# Claims Loading Deployment Guide

## Overview

The Ed-Fi DMS Configuration Service supports three distinct modes for loading and managing claims authorization data based on the `DMS_CONFIG_CLAIMS_SOURCE` configuration. This guide covers the deployment, configuration, and management of all claims loading modes, from embedded defaults to dynamic fragment composition and direct API uploads.

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
      "resources": [
        {
          "name": "ed-fi/academicWeeks",
          "actions": [
            {
              "name": "Create",
              "authorizationStrategies": [
                {
                  "name": "NamespaceBased"
                }
              ]
            },
            {
              "name": "Read",
              "authorizationStrategies": [
                {
                  "name": "NoFurtherAuthorizationRequired"
                }
              ]
            },
            {
              "name": "Update",
              "authorizationStrategies": [
                {
                  "name": "NamespaceBased"
                }
              ]
            },
            {
              "name": "Delete",
              "authorizationStrategies": [
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
```

### Key Components

#### Claim Sets Section
- **claimSetName**: Unique identifier for the claim set
- **isSystemReserved**: Boolean indicating if the claim set is protected from modification

#### Claims Hierarchy Section
- **name**: Domain or resource identifier
- **defaultAuthorization**: Default authorization strategies for all resources in the domain
- **resources**: Specific resource claims with their authorization strategies
- **actions**: CRUD operations (Create, Read, Update, Delete, ReadChanges)
- **authorizationStrategies**: Security strategies applied to each action

## Claims Loading Modes

### Mode 1: Embedded Only (Production Default)

The most secure mode for production deployments. Uses only the built-in Claims.json embedded in the assembly.

**Configuration:**
```bash
# Set claims source to embedded mode
DMS_CONFIG_CLAIMS_SOURCE=Embedded
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
DMS_CONFIG_CLAIMS_SOURCE=Hybrid

# Path to fragment files directory
DMS_CONFIG_CLAIMS_DIRECTORY=/app/claims-fragments
```

**Characteristics:**
- Base security from embedded claims
- Extensible through fragments

### Mode 3: Filesystem Only (Complete External Control)

Loads claims entirely from external filesystem sources without embedded base claims.

**Configuration:**
```bash
# Set claims source to filesystem mode
DMS_CONFIG_CLAIMS_SOURCE=Filesystem

# Path to claims files directory
DMS_CONFIG_CLAIMS_DIRECTORY=/app/claims-files
```

### Mode 4: Direct Upload (Dynamic Management)

Complete replacement of claims via the management API. The uploaded structure must match the full Claims.json format.

**Configuration:**
```bash
# Required for management endpoints
DMS_CONFIG_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING=true

# Optional: Can be combined with any base mode
DMS_CONFIG_CLAIMS_SOURCE=Embedded  # or Hybrid, or Filesystem
DMS_CONFIG_CLAIMS_DIRECTORY=/app/claims-fragments  # if using Hybrid or Filesystem
```

**Characteristics:**
- Complete control over claims
- Runtime modifications possible
- Highest flexibility
- Requires careful security consideration

## Fragment-Based Extension

### Fragment File Structure

Fragment files extend the claims hierarchy by adding new claim sets. Each fragment must follow the naming pattern `*-claimset.json` and contains only the resource claims to be added:

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
      "_defaultAuthorizationStrategiesForCRUD": []
    }
  ]
}
```

### Fragment File Naming Convention

Files must follow the pattern: `{number}-{description}-claimset.json`

Examples:
- `001-namespace-claimset.json`
- `002-nofurtherauth-claimset.json`
- `003-edorgsonly-claimset.json`

### Fragment Composition Process

1. **Discovery**: System scans the configured directory for `*-claimset.json` files
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
        "resources": [
          {
            "name": "ed-fi/students",
            "actions": [
              {
                "name": "Create",
                "authorizationStrategies": [
                  {"name": "NamespaceBased"}
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
- The uploaded structure completely replaces the current claims
- Must include both `claimSets` and `claimsHierarchy` sections
- Subject to JSON Schema validation
- Returns a reload ID header for tracking

### Reload Claims from Fragments

Recomposes claims from embedded base and fragment files:

```bash
curl -X POST -H "Authorization: Bearer $TOKEN" \
  http://localhost:8081/management/reload-claims
```

Response includes:
- X-Reload-Id header with unique identifier
- Status of reload operation
- Any validation errors encountered

### Get Current Claims

Retrieve the currently active claims configuration:

```bash
curl -H "Authorization: Bearer $TOKEN" \
  http://localhost:8081/management/current-claims
```

Returns the full Claims.json structure currently in use.

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
1. Create complete claims files or fragments
2. Validate JSON syntax of each file
3. Place files in designated directory
4. Set required environment variables

#### For Upload Mode:
1. Enable dynamic loading environment variable
2. Prepare full Claims.json structure for upload
3. Validate against JSON Schema before upload

### Configuration Mode Summary

| Mode | DMS_CONFIG_CLAIMS_SOURCE | DMS_CONFIG_CLAIMS_DIRECTORY | Use Case |
|------|--------------------------|----------------------------|----------|
| Embedded | `Embedded` | Not used | Production default, highest security |
| Hybrid | `Hybrid` | Required | Development with fragment extensions |
| Filesystem | `Filesystem` | Required | Complete external control |
| Upload | Any | Optional | Dynamic management via API |

## Security and Production Considerations

### Security Warnings

⚠️ **CRITICAL**: The `DMS_CONFIG_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING` flag should **NEVER** be enabled in production environments.

This flag enables:
- Runtime modification of security policies
- Potential elevation of privileges
- Bypass of intended authorization controls

## Troubleshooting

### Common Configuration Issues

1. **Claims not loading from filesystem**
   - Verify `DMS_CONFIG_CLAIMS_SOURCE` is set to `Hybrid` or `Filesystem`
   - Check that `DMS_CONFIG_CLAIMS_DIRECTORY` points to the correct directory
   - Ensure fragment files follow the `*-claimset.json` naming pattern

2. **Management endpoints returning 404**
   - Verify `DMS_CONFIG_DANGEROUSLY_ENABLE_UNRESTRICTED_CLAIMS_LOADING=true` is set
   - Check that the service is running with the correct environment variables

3. **Fragment files not being discovered**
   - Confirm files are in the directory specified by `DMS_CONFIG_CLAIMS_DIRECTORY`
   - Verify file permissions allow the service to read the files
   - Check service logs for fragment discovery messages
