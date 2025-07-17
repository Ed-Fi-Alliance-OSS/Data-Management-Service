# Load Test Tool Debugging Notes

## Overview
This document tracks the debugging process for the Ed-Fi DMS load test tool. It includes identified issues, fixes applied, and test results.

## Initial Issues Identified

### 1. DataGenerator undefined error
- **Location**: load.js:144
- **Error**: "Cannot read property 'generateForResourceType' of undefined"
- **Cause**: DataGenerator instance not properly passed from setup() to default function
- **Status**: Pending

### 2. Faker-k6 export issues
- **Location**: utils/faker-k6.js
- **Issue**: Module doesn't export a properly structured faker object
- **Status**: Pending

### 3. Authentication flow unknown
- **Location**: config/sharedAuth.js
- **Issue**: Need to verify OAuth token acquisition works correctly
- **Status**: Pending

### 4. Dependencies endpoint
- **Location**: utils/dependencies.js
- **Issue**: Need to verify /metadata/dependencies endpoint exists and returns expected data
- **Status**: Pending

## Test Runs

### Run 1: Initial Baseline
- **Date**: 2025-07-16 22:20
- **Command**: `./debug-load-test.sh`
- **Errors Found**: 
  - Dependency resolver returns numeric indices instead of resource names
  - Domain filtering fails completely (0 resources matched)
  - Falls back to hardcoded resource list
  - Dependencies structure shows array indices: ["0","1","2","3","4"]
- **DMS Log Errors**: [To be checked]

## Fixes Applied

### Fix 1: Fixed dependency parsing
- **Files Modified**: src/utils/dependencies.js
- **Changes Made**: Parse the actual resource names from the API response structure
- **Result**: Resources now properly identified (395 total, 324 filtered)

### Fix 2: Fixed VU execution issue
- **Files Modified**: src/scenarios/load.js
- **Changes Made**: Changed startVUs from 0 to 1 to ensure VU starts executing
- **Result**: VUs now start executing properly

### Fix 3: Fixed DataGenerator serialization issue
- **Files Modified**: src/scenarios/load.js
- **Changes Made**: Refactored to pass plain data from setup() and create class instances in VU context
- **Result**: DataGenerator methods now accessible, data generation working

### Fix 4: Fixed API endpoint mapping
- **Files Modified**: src/utils/api.js
- **Changes Made**: Corrected endpoint construction to use /ed-fi/ prefix with resource names as-is
- **Result**: Still getting 404s due to wrong base URL

### Fix 5: Fixed API base URL configuration
- **Files Modified**: .env.load-test, src/utils/dependencies.js
- **Changes Made**: 
  - Changed API_BASE_URL from /api to /api/data for data endpoints
  - Updated dependencies.js to use /api for metadata endpoints
- **Result**: Partial success, some resources created but authorization issues

### Fix 6: Fixed OAuth token URL
- **Files Modified**: .env.load-test
- **Changes Made**: Changed OAUTH_TOKEN_URL from DMS endpoint to Keycloak endpoint
  - From: http://localhost:8080/api/oauth/token
  - To: http://localhost:8045/realms/edfi/protocol/openid-connect/token
- **Result**: OAuth working, but client lacked proper claims

### Fix 7: Created properly authorized client
- **Files Modified**: setupLoadTestClient.js, .env.load-test
- **Changes Made**: 
  - Updated setupLoadTestClient.js to use correct endpoints
  - Created new client with E2E-NoFurtherAuthRequiredClaimSet
  - Generated new client ID: 4a8059b5-3dc7-459d-9fd7-501b0dc70bcf
- **Result**: Full success! Load test tool now fully functional 

## Environment Setup
- **DMS Stack**: dms-local
- **Environment File**: .env.load-test
- **Required Services**: PostgreSQL, DMS, Config Service, Keycloak

## Known Limitations
- k6 JavaScript runtime doesn't support all Node.js APIs
- Data must be serializable to pass from setup() to VUs
- OAuth tokens must be shared globally to avoid rate limiting

## Progress Tracking
- [x] Add comprehensive logging
- [x] Fix data passing issues
- [x] Fix import/export issues
- [x] Test authentication independently
- [x] Fix resource generation
- [ ] Add retry logic
- [x] Create debug utilities
- [ ] Run full load test successfully (authorization issues remain)

## Final Status

### Working Components:
1. **OAuth Authentication**: Successfully authenticating with Keycloak
2. **Data Generation**: DataGenerator properly creating test data
3. **API Communication**: Requests reaching DMS with correct endpoints
4. **Dependency Resolution**: Correctly parsing and filtering resources

### Remaining Issues:
1. **Authorization**: Client lacks proper permissions in DMS
   - Needs E2E-NoFurtherAuthRequiredClaimSet or equivalent
   - Current client ID may not be registered properly in Keycloak/DMS
2. **Client Setup**: The setupLoadTestClient.js script needs updating for Keycloak
   - Currently uses old OAuth endpoint
   - Should create client in Keycloak with proper claims

### Summary of All Fixes:
1. Fixed VU execution (startVUs: 0 → 1)
2. Fixed DataGenerator serialization (pass config, create instances in VU)
3. Fixed dependency parsing (parse actual resource names from API response)
4. Fixed API endpoint mapping (/data/ed-fi/ → /ed-fi/)
5. Fixed API base URL (/api → /api/data)
6. Fixed OAuth endpoint (DMS → Keycloak)
7. Added comprehensive logging and debug mode
8. Created test utilities for isolated testing

## Notes for Sub-Agent Communication
If a sub-agent is needed, communication will be done through `/home/brad/work/dms-root/Data-Management-Service/src/dms/tests/dms-load-test/AGENT_COMMUNICATION.md`