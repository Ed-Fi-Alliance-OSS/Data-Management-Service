# Authorization E2E Test Proposal with Claim Set Management

## Overview
This proposal outlines a simple, demonstrable authorization scenario that showcases the Configuration Management Service's claim set upload feature. The test will demonstrate how a client's access to resources can be dynamically modified by uploading new claim sets.

## Scenario: Granting Access to Student Records

### Business Context
A school district initially deploys an Ed-Fi client application that only has access to school organizational data (schools, calendars, bell schedules). Later, they decide to grant this same client access to student demographic information without changing the client credentials.

### Technical Implementation

#### Initial State
- **Client**: Uses the built-in `E2E-RelationshipsWithEdOrgsOnlyClaimSet` claim set
- **Current Access**: Can read/write school organizational data (schools, academicWeeks, bellSchedules, classPeriods, etc.)
- **Restricted Access**: Cannot access student-related resources

#### Test Flow

1. **Demonstrate Initial Restriction**
   - Client attempts to GET `/ed-fi/students` 
   - Expected: 403 Forbidden response
   - Client attempts to POST a new student record
   - Expected: 403 Forbidden response

2. **Upload Enhanced Claim Set**
   - POST to `/management/upload-claims` with a new claim set called `E2E-OrgDataPlusStudentsClaimSet`
   - This claim set includes:
     - All existing organizational data permissions (from RelationshipsWithEdOrgsOnly)
     - New permissions for `ed-fi/students` resource with Read and Create actions
     - Authorization strategy: `NoFurtherAuthorizationRequired` for simplicity

3. **Verify Enhanced Access**
   - Client attempts to POST a new student record
   - Expected: 201 Created response
   - Client attempts to GET `/ed-fi/students`
   - Expected: 200 OK response with the created student

4. **Optional: Restore Original State**
   - POST to `/management/reload-claims` to restore hybrid mode
   - Client attempts to GET `/ed-fi/students` again
   - Expected: 403 Forbidden response (access revoked)

### Sample Claim Set Upload Payload

```json
{
  "claims": {
    "claimSets": [
      {
        "claimSetName": "E2E-OrgDataPlusStudentsClaimSet",
        "isSystemReserved": false
      }
    ],
    "claimsHierarchy": [
      {
        "name": "http://ed-fi.org/identity/claims/domains/edFiTypes",
        "claimSets": [
          {
            "name": "E2E-OrgDataPlusStudentsClaimSet",
            "actions": [
              { "name": "Create" },
              { "name": "Read" },
              { "name": "Update" },
              { "name": "Delete" }
            ]
          }
        ],
        "children": [
          {
            "name": "http://ed-fi.org/identity/claims/ed-fi/schools",
            "claimSets": [
              {
                "name": "E2E-OrgDataPlusStudentsClaimSet",
                "actions": [
                  { "name": "Create" },
                  { "name": "Read" },
                  { "name": "Update" },
                  { "name": "Delete" }
                ],
                "authorizationStrategyOverrides": [
                  {
                    "actionName": "Create",
                    "authorizationStrategies": ["NoFurtherAuthorizationRequired"]
                  },
                  {
                    "actionName": "Read",
                    "authorizationStrategies": ["NoFurtherAuthorizationRequired"]
                  },
                  {
                    "actionName": "Update",
                    "authorizationStrategies": ["NoFurtherAuthorizationRequired"]
                  },
                  {
                    "actionName": "Delete",
                    "authorizationStrategies": ["NoFurtherAuthorizationRequired"]
                  }
                ]
              }
            ]
          },
          {
            "name": "http://ed-fi.org/identity/claims/ed-fi/students",
            "claimSets": [
              {
                "name": "E2E-OrgDataPlusStudentsClaimSet",
                "actions": [
                  { "name": "Create" },
                  { "name": "Read" }
                ],
                "authorizationStrategyOverrides": [
                  {
                    "actionName": "Create",
                    "authorizationStrategies": ["NoFurtherAuthorizationRequired"]
                  },
                  {
                    "actionName": "Read",
                    "authorizationStrategies": ["NoFurtherAuthorizationRequired"]
                  }
                ]
              }
            ]
          }
        ]
      }
    ]
  }
}
```

### Why This Scenario Is Ideal for Demo

1. **Simple to Explain**: "The client starts with access to school data only, then we grant it access to student data"
2. **Clear Business Value**: Shows how permissions can be adjusted without changing client credentials
3. **Visible Impact**: The 403→200 status code change is immediately obvious
4. **Real-World Relevance**: Adding student access to an organizational data client is a common scenario
5. **Minimal Setup**: Uses existing claim sets as a base, only adds one new resource permission
6. **Reversible**: Can demonstrate both granting and revoking access

### Test File Location
- Create new test file: `src/dms/tests/EdFi.DataManagementService.Tests.E2E/Features/Authorization/ClaimSetUploadAuthorization.feature`

### Prerequisites
- DMS E2E test environment must be running (`pwsh ./setup-local-dms.ps1`)
- Client must be configured with `E2E-RelationshipsWithEdOrgsOnlyClaimSet` initially
- At least one school record must exist in the system (created in Background section)

### Success Criteria
1. Initial requests to student endpoints return 403
2. Claim set upload succeeds with 200 response
3. Subsequent requests to student endpoints return appropriate success codes (201/200)
4. Created student data is retrievable
5. Optional: Reload restores original restrictions

## Alternative Simpler Scenario (if needed)

If the above proves too complex, a simpler alternative:
- Start with a claim set that has READ-ONLY access to schools
- Upload a claim set that adds CREATE/UPDATE/DELETE permissions for schools
- Demonstrate that the client can now modify school data