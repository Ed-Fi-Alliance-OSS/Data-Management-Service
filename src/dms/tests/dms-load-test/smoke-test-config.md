# DMS Load Test Smoke Test Configuration

## Overview
This configuration provides a minimal smoke test setup for the DMS load testing tool.

## Prerequisites
1. DMS running locally via E2E Docker setup
2. k6 installed on the system
3. Node.js dependencies installed (`npm install`)

## Configuration

### Environment Variables (.env)
```bash
# Local DMS Instance
API_BASE_URL=http://localhost:8080/api
OAUTH_TOKEN_URL=http://localhost:8080/api/oauth/token
CLIENT_ID=DmsConfigurationService
CLIENT_SECRET=s3creT@09

# Minimal Test Configuration
VUS_LOAD_PHASE=1           # Single virtual user
VUS_READWRITE_PHASE=1      # Single virtual user
DURATION_LOAD_PHASE=30s    # 30 seconds
DURATION_READWRITE_PHASE=30s # 30 seconds

# Minimal Data Scale
SCHOOL_COUNT=2
STUDENT_COUNT=2
STAFF_COUNT=2
COURSES_PER_SCHOOL=2
SECTIONS_PER_COURSE=2
```

## Running the Smoke Test

```bash
# From dms-load-test directory
k6 run src/scenarios/smoke.js
```

## Key Findings

### Authentication
- OAuth endpoint requires Basic authentication (not form-based)
- Must encode client credentials as Base64 in Authorization header
- Scope `edfi_admin_api/full_access` is required

### API Endpoints
- Base API: `http://localhost:8080/api`
- OAuth token: `http://localhost:8080/api/oauth/token`
- Dependencies: `http://localhost:8080/api/metadata/dependencies`
- Data resources: `http://localhost:8080/api/data/v3/ed-fi/*`

### Known Issues
1. **Permission Errors (403)**: The DmsConfigurationService client gets 403 errors when trying to create Ed-Fi resources. This client appears to have configuration service permissions but not data resource permissions.

2. **Client Registration**: New clients can be registered via:
   ```bash
   curl -X POST http://localhost:8081/config/connect/register \
     -H "Content-Type: application/x-www-form-urlencoded" \
     -d "ClientId=<client>&ClientSecret=<password>&DisplayName=<name>"
   ```
   Password must be 8-12 chars with uppercase, lowercase, number, and special character.

## Code Modifications Made

### 1. Authentication Fix (src/config/sharedAuth.js & auth.js)
- Added `import encoding from 'k6/encoding'`
- Changed to Basic auth: `Authorization: Basic ${encoding.b64encode(clientId:clientSecret)}`
- Added scope parameter: `grant_type=client_credentials&scope=edfi_admin_api/full_access`

### 2. Dependencies URL Fix (src/utils/dependencies.js)
- Changed from: `/api/metadata/data/v3/dependencies`
- Changed to: `/api/metadata/dependencies`

## Next Steps
1. ✅ Created test client with proper Ed-Fi resource permissions
2. ✅ Implemented E2E-NoFurtherAuthRequiredClaimSet for the test client
3. ✅ Fixed 403 authorization errors - test now authenticates successfully
4. ⚠️ Fix 400 validation errors - appears to be a data format issue, not authorization

## Solution Summary
The 403 errors were resolved by:
1. Creating a sys-admin client for initial setup
2. Using the sys-admin client to create a vendor via Config Service API
3. Creating an application with the E2E-NoFurtherAuthRequiredClaimSet claim set
4. Using the generated client credentials for the load test

The load test now successfully authenticates and has proper permissions. The remaining 400 errors appear to be data validation issues unrelated to authorization.