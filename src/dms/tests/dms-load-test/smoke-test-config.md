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
- Data resources: `http://localhost:8080/api/data/ed-fi/*`

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

### 3. API Path Fix (src/scenarios/smoke.js, src/utils/api.js)
- Changed from: `/data/v3/ed-fi/...`
- Changed to: `/data/ed-fi/...`
- The DMS API does not use v3 in the path

### 4. PUT Request Fix (src/scenarios/smoke.js)
- Added `id` field to request body for PUT operations
- DMS requires the id in both the URL and the request body for updates

### 5. DELETE Request Fix (src/utils/api.js)
- Fixed http.del() call to include null body parameter
- Changed from: `http.del(url, params)`
- Changed to: `http.del(url, null, params)`
- The k6 http.del() signature requires: (url, body, params)

## Solution Summary

### Authorization Issues (403 Errors) - RESOLVED
1. Created a sys-admin client for initial setup
2. Used the sys-admin client to create a vendor via Config Service API
3. Created an application with the E2E-NoFurtherAuthRequiredClaimSet claim set
4. Used the generated client credentials for the load test

### Data Validation Issues (400 Errors) - RESOLVED
1. Fixed incorrect API paths from `/data/v3/ed-fi/` to `/data/ed-fi/`
2. Added required `id` field to PUT request bodies
3. Corrected OAuth authentication to use Basic auth with proper encoding

### Current Status
✅ Authentication works correctly
✅ Resources can be created (POST)
✅ Resources can be read (GET)
✅ Resources can be updated (PUT) 
✅ Resources can be deleted (DELETE)
✅ Batch operations work
✅ Dependencies are fetched correctly

The smoke test now runs successfully with minimal errors, demonstrating proper authorization and correct API usage patterns.