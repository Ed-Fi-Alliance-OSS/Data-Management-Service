# Additional Claimsets for Claims Fragment Composition

## Overview

This directory contains JSON fragment files that extend the base Ed-Fi claims configuration when the DMS Configuration Service is running in hybrid mode. These fragments are automatically discovered and composed with the embedded base claims during service startup to create a complete claims hierarchy.

## Fragment Composition Process

The claims composition system works as follows:

1. **Base Claims Loading**: The service loads the embedded `Claims.json` from assembly resources
2. **Fragment Discovery**: All files matching the pattern `*-claimset.json` are discovered in this directory
3. **Sequential Application**: Fragments are applied in alphabetical order (hence the numeric prefixes)
4. **Final Composition**: The result is a merged claims document with all fragments integrated

## File Naming Convention

Fragment files must follow this naming pattern:
- **Format**: `{number}-{description}-claimset.json`
- **Examples**: `001-namespace-claimset.json`, `002-nofurtherauth-claimset.json`
- **Ordering**: Files are processed in alphabetical order, so numeric prefixes control the application sequence

## Current Fragment Files

### 001-namespace-claimset.json
**Claim Set Name**: `E2E-NameSpaceBasedClaimSet`
- Demonstrates namespace-based authorization strategies
- Includes resources: schoolYearTypes, surveys, absenceEventCategoryDescriptors
- Authorization: Mix of NoFurtherAuthorizationRequired and NamespaceBased strategies

### 002-nofurtherauth-claimset.json
**Claim Set Name**: `E2E-NoFurtherAuthRequiredClaimSet`
- Demonstrates unrestricted access patterns
- Includes resource: academicWeeks
- Authorization: NoFurtherAuthorizationRequired for all CRUD operations

### 003-edorgsonly-claimset.json
**Claim Set Name**: `E2E-RelationshipsWithEdOrgsOnlyClaimSet`
- Demonstrates education organization relationship-based authorization
- Includes resources: assessments, bellSchedules
- Authorization: RelationshipsWithEdOrgsOnly strategy

### 004-sample-extension-claimset.json
**Claim Set Name**: `SampleExtensionResourceClaims`
- Demonstrates extension resource claims
- Includes custom extension resources with various authorization patterns
- Used for testing extension-based authorization scenarios

### 005-homograph-extension-claimset.json
**Claim Set Name**: `HomographExtensionResourceClaims`
- Demonstrates homograph extension resources
- Includes resources with similar names but different namespaces
- Used for testing namespace collision handling

## Fragment Structure

Each fragment file must contain:

```json
{
  "name": "ClaimSetName",
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
              "name": "AuthorizationStrategyName",
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

## Configuration Requirements

For these fragments to be loaded, the Configuration Service must be configured with:

```bash
# Set claims source to hybrid mode (embedded base + fragments)
DMS_CONFIG_CLAIMS_SOURCE=Hybrid

# Path to this directory (in container)
DMS_CONFIG_CLAIMS_DIRECTORY=/app/additional-claims
```

## Usage in Docker

The fragments are typically mounted into the container via Docker volumes:

```yaml
volumes:
  - ./src/config/backend/EdFi.DmsConfigurationService.Backend/Deploy/AdditionalClaimsets:/app/additional-claims:ro
```

### Docker Environment Configuration

```yaml
environment:
  - DMS_CONFIG_CLAIMS_SOURCE=Hybrid
  - DMS_CONFIG_CLAIMS_DIRECTORY=/app/additional-claims
```

## Testing

These fragments are primarily used for:
- **E2E Testing**: Validating claims composition in integration tests
- **Development**: Testing custom authorization scenarios
- **CI/CD**: Automated testing of claims management features

See `/src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/Features/ClaimsManagement.feature` for test scenarios that validate fragment composition.

## Adding New Fragments

To add a new fragment:

1. Create a new JSON file following the naming convention
2. Ensure the claim set name is unique
3. Define resource claims with appropriate authorization strategies
4. Test the composition by running the E2E tests
5. Document the fragment's purpose in this README

## Important Notes

- **Validation**: All composed claims are validated against the claims JSON schema
- **Order Matters**: Fragments are applied sequentially, so later fragments can override earlier ones

## Troubleshooting

Common issues and solutions:

1. **Fragment not loaded**: Ensure file follows `*-claimset.json` pattern
2. **Invalid composition**: Check fragment JSON structure matches schema
3. **Missing claim sets**: Verify `DMS_CONFIG_CLAIMS_SOURCE=Hybrid` and `DMS_CONFIG_CLAIMS_DIRECTORY` is correctly set

For detailed debugging, check the Configuration Service logs for entries like:
- "Discovered X fragment files in path"
- "Applying fragment: filename"
- "Successfully composed claims from X fragments"
