# Fragment-Based Claims Management Deployment Guide

## Overview

This guide covers the deployment and configuration of the Fragment-Based Claims Management system for Ed-Fi DMS Configuration Service.

## Fragment File Naming Convention

Fragment files must follow the naming pattern: `*-claims.json`

Examples:
- `001-namespace-claimset-claims.json`
- `002-noauth-claimset-claims.json`
- `sample-extension-claims.json`
- `homograph-extension-claims.json`

## Required Configuration Environment Variables

### For Hybrid Mode (Recommended)
```bash
# Enable claims path processing
DMS_CONFIG_USE_CLAIMS_PATH=true

# Use embedded base claims as foundation
DMS_CONFIG_USE_EMBEDDED_BASE_CLAIMS=true

# Path to fragment files directory
DMS_CONFIG_CLAIMS_PATH=/app/claims-fragments

# Enable dynamic claims loading for management endpoints
DMS_CONFIG_DANGEROUSLY_ENABLE_DYNAMIC_CLAIMS_LOADING=true
```

### For Pure File System Mode
```bash
DMS_CONFIG_USE_CLAIMS_PATH=true
DMS_CONFIG_USE_EMBEDDED_BASE_CLAIMS=false
DMS_CONFIG_CLAIMS_PATH=/app/claims-fragments
DMS_CONFIG_DANGEROUSLY_ENABLE_DYNAMIC_CLAIMS_LOADING=true
```

## Fragment File Structure

Each fragment file must contain:
```json
{
  "name": "FragmentName",
  "resourceClaims": [
    {
      "isParent": true|false,
      "name": "claim-name-or-uri",
      "children": [
        { "name": "child-claim-uri" }
      ],
      "authorizationStrategyOverridesForCRUD": [
        {
          "actionName": "Create|Read|Update|Delete",
          "authorizationStrategies": [
            { "name": "AuthorizationStrategyName" }
          ]
        }
      ]
    }
  ]
}
```

## Deployment Steps

### 1. Prepare Fragment Files
- Ensure all fragment files follow the naming convention
- Validate JSON syntax of each fragment
- Place fragments in the configured directory

### 2. Configure Environment
- Set required environment variables
- Ensure claims directory exists and is accessible
- Verify container/application has read access to fragment files

### 3. Start Service
- Start the DMS Configuration Service
- Monitor logs for fragment discovery and processing
- Verify service startup completes successfully

### 4. Test Fragment Loading
```bash
# Test reload endpoint
curl -X POST http://localhost:8081/management/reload-claims

# Verify current claims
curl http://localhost:8081/management/current-claims
```

## Management Operations

### Reload Claims from Fragments
```bash
curl -X POST http://localhost:8081/management/reload-claims
```

### Upload Claims Directly
```bash
curl -X POST -H "Content-Type: application/json" \
  -d '{"claims": {"claimSets": [...], "claimsHierarchy": [...]}}' \
  http://localhost:8081/management/upload-claims
```

### Get Current Claims
```bash
curl http://localhost:8081/management/current-claims
```

## Fragment Management Best Practices

### 1. Version Control
- Store fragment files in version control
- Use descriptive commit messages for fragment changes
- Tag releases for production deployments

### 2. Fragment Organization
- Use numbered prefixes for load order dependency
- Group related claims in the same fragment
- Keep fragments focused on specific functional areas

### 3. Testing
- Test fragment changes in development environment first
- Use management endpoints to verify composition results
- Validate against JsonSchema before deployment

### 4. Monitoring
- Monitor reload endpoint responses for errors
- Check application logs for fragment processing messages
- Verify claim composition after fragment updates

## Production Deployment Checklist

- [ ] Fragment files validated and properly named
- [ ] Environment variables configured correctly
- [ ] Claims directory exists with proper permissions
- [ ] Service starts successfully and discovers fragments
- [ ] Management endpoints respond correctly
- [ ] Claims composition validates against schema
- [ ] All required claimSets and hierarchy items present
- [ ] Monitoring and logging configured

## Troubleshooting

### Fragment Not Loading
1. Check file naming convention (`*-claims.json`)
2. Verify JSON syntax is valid
3. Ensure file is in configured claims directory
4. Check file permissions for read access

### Validation Errors
1. Review fragment content structure
2. Verify required properties are present
3. Check for empty arrays that should be null
4. Validate against JSON schema requirements

### Service Startup Issues
1. Verify environment variables are set
2. Check claims directory exists and is accessible
3. Review application startup logs
4. Ensure base claims are available (if using embedded)

## Support

For additional support:
- Review application logs for detailed error messages
- Use management endpoints to inspect current state
- Consult Ed-Fi DMS Configuration Service documentation
- Check fragment composition validation reports