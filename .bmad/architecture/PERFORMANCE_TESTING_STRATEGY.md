# DMS Performance Testing Strategy

## Executive Summary

This document outlines a comprehensive performance testing strategy for the Ed-Fi Data Management Service (DMS) using Grafana K6. The strategy leverages a **single unified ApiSchema.json file** and a **single unified ClaimSet file** across all performance tests, ensuring consistency, simplicity, and maintainability.

### Key Design Decisions

1. **Single ApiSchema.json File**: One simplified schema containing School, Student, Staff, and their associations is used for ALL performance tests
2. **Single ClaimSet File**: One unrestricted access ClaimSet is used in tandem with the ApiSchema for ALL performance tests
3. **Focused Resource Set**: Schema contains only the core Ed-Fi resources needed for comprehensive performance testing (School, Student, Staff, StudentSchoolAssociation, StaffEducationOrganizationAssignmentAssociation)
4. **No Authorization Overhead**: ClaimSet removes all authorization strategies to isolate DMS performance metrics

These unified configuration files ensure:
- **Consistency** - All tests operate with identical schemas and permissions
- **Simplicity** - Single configuration to maintain and deploy
- **Comparability** - Test results are directly comparable across scenarios
- **Focus** - Performance measurements reflect DMS capabilities, not configuration complexity

## Architecture Overview

### Core Architecture Principles

1. **Schema Simplification**: Use custom ApiSchema.json files to minimize document dependencies
2. **Authorization Optimization**: Deploy simplified claimsets via CMS for unrestricted testing
3. **Realistic Load Simulation**: Model real-world Ed-Fi implementation patterns
4. **Progressive Scaling**: Start small and incrementally increase load complexity
5. **Observability First**: Comprehensive metrics collection and analysis

### System Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                     K6 Performance Test Suite                    │
├─────────────────────────────────────────────────────────────────┤
│                         Test Orchestration                       │
│  ┌─────────────┐  ┌──────────────┐  ┌────────────────┐        │
│  │   Schema    │  │   ClaimSet   │  │     Test       │        │
│  │   Manager   │  │   Manager    │  │   Executor     │        │
│  └─────────────┘  └──────────────┘  └────────────────┘        │
├─────────────────────────────────────────────────────────────────┤
│                      Performance Scenarios                       │
│  ┌─────────────┐  ┌──────────────┐  ┌────────────────┐        │
│  │   Baseline  │  │    Load      │  │    Stress      │        │
│  │    Tests    │  │    Tests     │  │    Tests       │        │
│  └─────────────┘  └──────────────┘  └────────────────┘        │
├─────────────────────────────────────────────────────────────────┤
│                         DMS Platform                             │
│  ┌─────────────┐  ┌──────────────┐  ┌────────────────┐        │
│  │     DMS     │  │     CMS      │  │  PostgreSQL    │        │
│  │     API     │  │  Config API  │  │   OpenSearch   │        │
│  └─────────────┘  └──────────────┘  └────────────────┘        │
└─────────────────────────────────────────────────────────────────┘
```

## Test Environment Setup

### 1. Schema Configuration

#### Unified Performance Test Schema

**Important**: A single ApiSchema.json file is used across ALL performance tests to ensure consistency and simplify test maintenance. This schema is a focused subset of the full Ed-Fi Data Standard, containing only the essential resources needed for comprehensive performance testing.

##### Core Resources in Performance Test Schema
The unified schema includes the following primary resources and their essential dependencies:
- **School** - Base education organization entity
- **Student** - Core student entity with minimal required fields
- **Staff** - Core staff entity with minimal required fields
- **StudentSchoolAssociation** - Links students to schools
- **StaffEducationOrganizationAssignmentAssociation** - Links staff to education organizations
- **Supporting Resources** - Only those directly referenced by the above entities (e.g., descriptors, education organization references)

##### Schema Source
The performance test schema is derived from the full Ed-Fi Data Standard ApiSchema (example: `/home/brad/work/dms-root/MetaEd-js/packages/metaed-plugin-edfi-api-schema/test/integration/artifact/v7_3/ds-5.2-api-schema-authoritative.json`) but simplified to:
- Remove unnecessary dependencies
- Minimize referential complexity
- Focus on core CRUD and query operations
- Enable high-volume concurrent testing without constraint violations

##### Single Schema File Location
```javascript
// config/performance-test-schema.json
// This file is generated once and used by all performance tests
{
  "apiSchemaVersion": "1.0.0",
  "projectSchema": {
    "projectName": "Ed-Fi",
    "projectVersion": "5.0.0",
    "description": "Ed-Fi Data Standard for performance testing",
    "abstractResources": {},
    "caseInsensitiveEndpointNameMapping": {
      "schools": "schools",
      "students": "students",
      "staffs": "staffs",
      "studentschoolassociations": "studentSchoolAssociations",
      "staffeducationorganizationassignmentassociations": "staffEducationOrganizationAssignmentAssociations"
    },
    "educationOrganizationHierarchy": {},
    "educationOrganizationTypes": [],
    "isExtensionProject": false,
    "projectEndpointName": "ed-fi",
    "resourceNameMapping": {
      "School": "schools",
      "Student": "students",
      "Staff": "staffs",
      "StudentSchoolAssociation": "studentSchoolAssociations",
      "StaffEducationOrganizationAssignmentAssociation": "staffEducationOrganizationAssignmentAssociations"
    },
    "resourceSchemas": {
      // School - simplified from full schema
      "schools": {
        "resourceName": "School",
        "allowIdentityUpdates": false,
        "identityJsonPaths": ["$.schoolId"],
        "isDescriptor": false,
        "isSchoolYearEnumeration": false,
        "isSubclass": true,
        "equalityConstraints": [],
        "documentPathsMapping": {
          "SchoolId": {
            "isPartOfIdentity": true,
            "isReference": false,
            "isRequired": true,
            "path": "$.schoolId",
            "type": "number"
          },
          "NameOfInstitution": {
            "isPartOfIdentity": false,
            "isReference": false,
            "isRequired": true,
            "path": "$.nameOfInstitution",
            "type": "string"
          },
          "EducationOrganizationCategoryDescriptor": {
            "isDescriptor": true,
            "isPartOfIdentity": false,
            "isReference": true,
            "isRequired": false,
            "path": "$.educationOrganizationCategories[*].educationOrganizationCategoryDescriptor",
            "projectName": "Ed-Fi",
            "resourceName": "EducationOrganizationCategoryDescriptor",
            "type": "string"
          },
          "GradeLevelDescriptor": {
            "isDescriptor": true,
            "isPartOfIdentity": false,
            "isReference": true,
            "isRequired": false,
            "path": "$.gradeLevels[*].gradeLevelDescriptor",
            "projectName": "Ed-Fi",
            "resourceName": "GradeLevelDescriptor",
            "type": "string"
          }
        },
        "jsonSchemaForInsert": {
          "type": "object",
          "properties": {
            "schoolId": { "type": "integer" },
            "nameOfInstitution": { "type": "string" },
            "educationOrganizationCategories": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "properties": {
                  "educationOrganizationCategoryDescriptor": { "type": "string" }
                },
                "required": ["educationOrganizationCategoryDescriptor"]
              }
            },
            "gradeLevels": {
              "type": "array",
              "minItems": 1,
              "items": {
                "type": "object",
                "properties": {
                  "gradeLevelDescriptor": { "type": "string" }
                },
                "required": ["gradeLevelDescriptor"]
              }
            }
          },
          "required": ["schoolId", "nameOfInstitution", "educationOrganizationCategories", "gradeLevels"]
        }
      },
      // Student - core fields only
      "students": {
        "resourceName": "Student",
        "allowIdentityUpdates": false,
        "identityJsonPaths": ["$.studentUniqueId"],
        "isDescriptor": false,
        "isSchoolYearEnumeration": false,
        "isSubclass": false,
        "equalityConstraints": [],
        "documentPathsMapping": {
          "StudentUniqueId": {
            "isPartOfIdentity": true,
            "isReference": false,
            "isRequired": true,
            "path": "$.studentUniqueId",
            "type": "string"
          },
          "Name.FirstName": {
            "isPartOfIdentity": false,
            "isReference": false,
            "isRequired": true,
            "path": "$.firstName",
            "type": "string"
          },
          "Name.LastSurname": {
            "isPartOfIdentity": false,
            "isReference": false,
            "isRequired": true,
            "path": "$.lastSurname",
            "type": "string"
          },
          "BirthData.BirthDate": {
            "isPartOfIdentity": false,
            "isReference": false,
            "isRequired": true,
            "path": "$.birthDate",
            "type": "date"
          }
        },
        "jsonSchemaForInsert": {
          "type": "object",
          "properties": {
            "studentUniqueId": { "type": "string" },
            "firstName": { "type": "string" },
            "lastSurname": { "type": "string" },
            "birthDate": { "type": "string", "format": "date" }
          },
          "required": ["studentUniqueId", "firstName", "lastSurname", "birthDate"]
        }
      },
      // StudentSchoolAssociation - links students to schools
      "studentSchoolAssociations": {
        "resourceName": "StudentSchoolAssociation",
        "allowIdentityUpdates": true,
        "identityJsonPaths": [
          "$.studentReference.studentUniqueId",
          "$.schoolReference.schoolId",
          "$.entryDate"
        ],
        "isDescriptor": false,
        "isSchoolYearEnumeration": false,
        "isSubclass": false,
        "equalityConstraints": [],
        "documentPathsMapping": {
          "Student": {
            "isDescriptor": false,
            "isPartOfIdentity": true,
            "isReference": true,
            "isRequired": true,
            "projectName": "Ed-Fi",
            "referenceJsonPaths": [
              {
                "identityJsonPath": "$.studentUniqueId",
                "referenceJsonPath": "$.studentReference.studentUniqueId",
                "type": "string"
              }
            ],
            "resourceName": "Student"
          },
          "School": {
            "isDescriptor": false,
            "isPartOfIdentity": true,
            "isReference": true,
            "isRequired": true,
            "projectName": "Ed-Fi",
            "referenceJsonPaths": [
              {
                "identityJsonPath": "$.schoolId",
                "referenceJsonPath": "$.schoolReference.schoolId",
                "type": "number"
              }
            ],
            "resourceName": "School"
          },
          "EntryDate": {
            "isPartOfIdentity": true,
            "isReference": false,
            "isRequired": true,
            "path": "$.entryDate",
            "type": "date"
          },
          "EntryGradeLevelDescriptor": {
            "isDescriptor": true,
            "isPartOfIdentity": false,
            "isReference": true,
            "isRequired": true,
            "path": "$.entryGradeLevelDescriptor",
            "projectName": "Ed-Fi",
            "resourceName": "GradeLevelDescriptor",
            "type": "string"
          }
        },
        "jsonSchemaForInsert": {
          "type": "object",
          "properties": {
            "studentReference": {
              "type": "object",
              "properties": {
                "studentUniqueId": { "type": "string" }
              },
              "required": ["studentUniqueId"]
            },
            "schoolReference": {
              "type": "object",
              "properties": {
                "schoolId": { "type": "integer" }
              },
              "required": ["schoolId"]
            },
            "entryDate": { "type": "string", "format": "date" },
            "entryGradeLevelDescriptor": { "type": "string" }
          },
          "required": ["studentReference", "schoolReference", "entryDate", "entryGradeLevelDescriptor"]
        }
      },
      // Staff - core fields only
      "staffs": {
        "resourceName": "Staff",
        "allowIdentityUpdates": false,
        "identityJsonPaths": ["$.staffUniqueId"],
        "isDescriptor": false,
        "isSchoolYearEnumeration": false,
        "isSubclass": false,
        "equalityConstraints": [],
        "documentPathsMapping": {
          "StaffUniqueId": {
            "isPartOfIdentity": true,
            "isReference": false,
            "isRequired": true,
            "path": "$.staffUniqueId",
            "type": "string"
          },
          "Name.FirstName": {
            "isPartOfIdentity": false,
            "isReference": false,
            "isRequired": true,
            "path": "$.firstName",
            "type": "string"
          },
          "Name.LastSurname": {
            "isPartOfIdentity": false,
            "isReference": false,
            "isRequired": true,
            "path": "$.lastSurname",
            "type": "string"
          }
        },
        "jsonSchemaForInsert": {
          "type": "object",
          "properties": {
            "staffUniqueId": { "type": "string" },
            "firstName": { "type": "string" },
            "lastSurname": { "type": "string" }
          },
          "required": ["staffUniqueId", "firstName", "lastSurname"]
        }
      },
      // StaffEducationOrganizationAssignmentAssociation - links staff to schools
      "staffEducationOrganizationAssignmentAssociations": {
        "resourceName": "StaffEducationOrganizationAssignmentAssociation",
        "allowIdentityUpdates": false,
        "identityJsonPaths": [
          "$.staffReference.staffUniqueId",
          "$.educationOrganizationReference.educationOrganizationId",
          "$.staffClassificationDescriptor",
          "$.beginDate"
        ],
        "isDescriptor": false,
        "isSchoolYearEnumeration": false,
        "isSubclass": false,
        "equalityConstraints": [],
        "documentPathsMapping": {
          "Staff": {
            "isDescriptor": false,
            "isPartOfIdentity": true,
            "isReference": true,
            "isRequired": true,
            "projectName": "Ed-Fi",
            "referenceJsonPaths": [
              {
                "identityJsonPath": "$.staffUniqueId",
                "referenceJsonPath": "$.staffReference.staffUniqueId",
                "type": "string"
              }
            ],
            "resourceName": "Staff"
          },
          "EducationOrganization": {
            "isDescriptor": false,
            "isPartOfIdentity": true,
            "isReference": true,
            "isRequired": true,
            "projectName": "Ed-Fi",
            "referenceJsonPaths": [
              {
                "identityJsonPath": "$.educationOrganizationId",
                "referenceJsonPath": "$.educationOrganizationReference.educationOrganizationId",
                "type": "number"
              }
            ],
            "resourceName": "EducationOrganization"
          },
          "StaffClassificationDescriptor": {
            "isDescriptor": true,
            "isPartOfIdentity": true,
            "isReference": true,
            "isRequired": true,
            "path": "$.staffClassificationDescriptor",
            "projectName": "Ed-Fi",
            "resourceName": "StaffClassificationDescriptor",
            "type": "string"
          },
          "BeginDate": {
            "isPartOfIdentity": true,
            "isReference": false,
            "isRequired": true,
            "path": "$.beginDate",
            "type": "date"
          }
        },
        "jsonSchemaForInsert": {
          "type": "object",
          "properties": {
            "staffReference": {
              "type": "object",
              "properties": {
                "staffUniqueId": { "type": "string" }
              },
              "required": ["staffUniqueId"]
            },
            "educationOrganizationReference": {
              "type": "object",
              "properties": {
                "educationOrganizationId": { "type": "integer" }
              },
              "required": ["educationOrganizationId"]
            },
            "staffClassificationDescriptor": { "type": "string" },
            "beginDate": { "type": "string", "format": "date" }
          },
          "required": ["staffReference", "educationOrganizationReference", "staffClassificationDescriptor", "beginDate"]
        }
      }
    }
  }
}
```

#### Schema Deployment Script
```javascript
// setup/deploySchema.js
import { ApiSchemaUploader } from '../utils/schemaUploader.js';
import performanceSchema from '../config/performance-test-schema.json';

export async function deployPerformanceTestSchema() {
  const uploader = new ApiSchemaUploader({
    dmsUrl: __ENV.DMS_URL,
    authToken: __ENV.ADMIN_TOKEN
  });
  
  // Use the single unified performance test schema
  await uploader.uploadSchema(performanceSchema);
  
  console.log('✅ Single performance test schema deployed successfully');
  console.log('   Resources included: School, Student, Staff,');
  console.log('                      StudentSchoolAssociation, StaffEducationOrganizationAssignmentAssociation');
}
```

### 2. Authorization Configuration

#### Unified Performance Test ClaimSet

**Important**: A single ClaimSet file is used in tandem with the single ApiSchema.json file across ALL performance tests. This ensures consistent authorization behavior and simplifies test configuration management.

##### Single ClaimSet Configuration
```json
// config/performance-test-claimset.json
// This file is used by all performance tests - no authorization restrictions
// Format matches the Ed-Fi Claims.json structure used by CMS
{
  "claims": {
    "claimSets": [
      {
        "claimSetName": "PerformanceTest-UnrestrictedAccess",
        "isSystemReserved": false
      }
    ],
    "claimsHierarchy": [
      {
        "name": "http://ed-fi.org/identity/claims/domains/edFiTypes",
        "claims": [
          {
            "name": "http://ed-fi.org/identity/claims/ed-fi/school",
            "claimSets": [
              {
                "name": "PerformanceTest-UnrestrictedAccess",
                "actions": [
                  {
                    "name": "Create",
                    "authorizationStrategyOverrides": [
                      { "name": "NoFurtherAuthorizationRequired" }
                    ]
                  },
                  {
                    "name": "Read",
                    "authorizationStrategyOverrides": [
                      { "name": "NoFurtherAuthorizationRequired" }
                    ]
                  },
                  {
                    "name": "Update",
                    "authorizationStrategyOverrides": [
                      { "name": "NoFurtherAuthorizationRequired" }
                    ]
                  },
                  {
                    "name": "Delete",
                    "authorizationStrategyOverrides": [
                      { "name": "NoFurtherAuthorizationRequired" }
                    ]
                  }
                ]
              }
            ]
          },
          {
            "name": "http://ed-fi.org/identity/claims/ed-fi/student",
            "claimSets": [
              {
                "name": "PerformanceTest-UnrestrictedAccess",
                "actions": [
                  {
                    "name": "Create",
                    "authorizationStrategyOverrides": [
                      { "name": "NoFurtherAuthorizationRequired" }
                    ]
                  },
                  {
                    "name": "Read",
                    "authorizationStrategyOverrides": [
                      { "name": "NoFurtherAuthorizationRequired" }
                    ]
                  },
                  {
                    "name": "Update",
                    "authorizationStrategyOverrides": [
                      { "name": "NoFurtherAuthorizationRequired" }
                    ]
                  },
                  {
                    "name": "Delete",
                    "authorizationStrategyOverrides": [
                      { "name": "NoFurtherAuthorizationRequired" }
                    ]
                  }
                ]
              }
            ]
          },
          {
            "name": "http://ed-fi.org/identity/claims/ed-fi/studentSchoolAssociation",
            "claimSets": [
              {
                "name": "PerformanceTest-UnrestrictedAccess",
                "actions": [
                  {
                    "name": "Create",
                    "authorizationStrategyOverrides": [
                      { "name": "NoFurtherAuthorizationRequired" }
                    ]
                  },
                  {
                    "name": "Read",
                    "authorizationStrategyOverrides": [
                      { "name": "NoFurtherAuthorizationRequired" }
                    ]
                  },
                  {
                    "name": "Update",
                    "authorizationStrategyOverrides": [
                      { "name": "NoFurtherAuthorizationRequired" }
                    ]
                  },
                  {
                    "name": "Delete",
                    "authorizationStrategyOverrides": [
                      { "name": "NoFurtherAuthorizationRequired" }
                    ]
                  }
                ]
              }
            ]
          },
          {
            "name": "http://ed-fi.org/identity/claims/ed-fi/staff",
            "claimSets": [
              {
                "name": "PerformanceTest-UnrestrictedAccess",
                "actions": [
                  {
                    "name": "Create",
                    "authorizationStrategyOverrides": [
                      { "name": "NoFurtherAuthorizationRequired" }
                    ]
                  },
                  {
                    "name": "Read",
                    "authorizationStrategyOverrides": [
                      { "name": "NoFurtherAuthorizationRequired" }
                    ]
                  },
                  {
                    "name": "Update",
                    "authorizationStrategyOverrides": [
                      { "name": "NoFurtherAuthorizationRequired" }
                    ]
                  },
                  {
                    "name": "Delete",
                    "authorizationStrategyOverrides": [
                      { "name": "NoFurtherAuthorizationRequired" }
                    ]
                  }
                ]
              }
            ]
          },
          {
            "name": "http://ed-fi.org/identity/claims/ed-fi/staffEducationOrganizationAssignmentAssociation",
            "claimSets": [
              {
                "name": "PerformanceTest-UnrestrictedAccess",
                "actions": [
                  {
                    "name": "Create",
                    "authorizationStrategyOverrides": [
                      { "name": "NoFurtherAuthorizationRequired" }
                    ]
                  },
                  {
                    "name": "Read",
                    "authorizationStrategyOverrides": [
                      { "name": "NoFurtherAuthorizationRequired" }
                    ]
                  },
                  {
                    "name": "Update",
                    "authorizationStrategyOverrides": [
                      { "name": "NoFurtherAuthorizationRequired" }
                    ]
                  },
                  {
                    "name": "Delete",
                    "authorizationStrategyOverrides": [
                      { "name": "NoFurtherAuthorizationRequired" }
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
}
```

##### ClaimSet Purpose
The unified performance test ClaimSet:
- **Removes authorization bottlenecks** - No authorization checks to impact performance measurements
- **Simplifies test execution** - Single client credential works for all operations
- **Ensures consistency** - All tests use identical authorization configuration
- **Focuses on DMS performance** - Isolates core DMS performance from authorization overhead

#### ClaimSet Deployment
```javascript
// setup/deployClaimSet.js
import performanceClaimSet from '../config/performance-test-claimset.json';
import http from 'k6/http';

export async function deployPerformanceTestClaimSet() {
  // Step 1: Upload the claimset to CMS via the upload-claims endpoint
  const uploadResponse = http.post(
    `${__ENV.CMS_URL}/config/management/upload-claims`,
    JSON.stringify(performanceClaimSet),
    {
      headers: {
        'Content-Type': 'application/json',
        'Authorization': `Bearer ${__ENV.ADMIN_TOKEN}`
      }
    }
  );
  
  check(uploadResponse, {
    'Claimset uploaded successfully': (r) => r.status === 200
  });
  
  const reloadId = uploadResponse.json('reloadId');
  console.log(`✅ ClaimSet uploaded with reloadId: ${reloadId}`);
  
  // Step 2: Create a vendor and application with this claimset
  const vendor = await createVendor({
    company: "Performance Test Vendor",
    contactName: "Perf Test",
    contactEmailAddress: "perftest@example.com",
    namespacePrefixes: "uri://ed-fi.org"
  });
  
  const application = await createApplication({
    vendorId: vendor.id,
    applicationName: "Performance Test Application",
    claimSetName: "PerformanceTest-UnrestrictedAccess"
  });
  
  // Step 3: Store credentials for use across all tests
  await saveTestCredentials({
    clientKey: application.key,
    clientSecret: application.secret
  });
  
  // Step 4: Trigger DMS to reload claimsets from CMS
  const reloadResponse = http.post(
    `${__ENV.DMS_URL}/management/reload-claimsets`,
    null,
    { headers: { 'Authorization': `Bearer ${__ENV.ADMIN_TOKEN}` } }
  );
  
  check(reloadResponse, {
    'DMS reloaded claimsets': (r) => r.status === 200
  });
  
  console.log('✅ Single performance test claimset deployed and synced');
  console.log('   ClaimSet: PerformanceTest-UnrestrictedAccess');
  console.log('   Resources: School, Student, Staff,');
  console.log('              StudentSchoolAssociation, StaffEducationOrganizationAssignmentAssociation');
  console.log('   Authorization: NoFurtherAuthorizationRequired (full access)');
}
```

#### Test Credential Management
```javascript
// utils/credentials.js
// Single credential set used across all performance tests
let cachedToken = null;
let tokenExpiry = 0;

export async function getPerformanceTestToken() {
  if (cachedToken && Date.now() < tokenExpiry) {
    return cachedToken;
  }
  
  // Use the single performance test client credentials
  const response = await http.post(`${__ENV.AUTH_URL}/oauth/token`, {
    grant_type: 'client_credentials',
    client_id: 'performance-test-client',
    client_secret: __ENV.PERF_TEST_CLIENT_SECRET
  });
  
  cachedToken = response.json.access_token;
  tokenExpiry = Date.now() + (response.json.expires_in * 1000) - 60000; // Refresh 1 min early
  
  return cachedToken;
}
```

### 3. Test Data Generation

#### Optimized Data Generators
```javascript
// generators/performanceData.js
export class PerformanceDataGenerator {
  constructor(config) {
    this.schoolCount = config.schoolCount || 100;
    this.studentsPerSchool = config.studentsPerSchool || 500;
    this.staffPerSchool = config.staffPerSchool || 50;
    this.schoolCache = new Map();
  }
  
  generateSchool(index) {
    const schoolId = 100000 + index;
    const school = {
      schoolId,
      nameOfInstitution: `Performance Test School ${index}`,
      educationOrganizationCategories: [
        { educationOrganizationCategoryDescriptor: "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School" }
      ],
      gradeLevels: [
        { gradeLevelDescriptor: "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade" },
        { gradeLevelDescriptor: "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade" },
        { gradeLevelDescriptor: "uri://ed-fi.org/GradeLevelDescriptor#Eleventh grade" },
        { gradeLevelDescriptor: "uri://ed-fi.org/GradeLevelDescriptor#Twelfth grade" }
      ]
    };
    
    this.schoolCache.set(schoolId, school);
    return school;
  }
  
  generateStudent(schoolId, index) {
    return {
      studentUniqueId: `PERF${schoolId}S${index}`,
      firstName: `FirstName${index}`,
      lastSurname: `LastName${index}`,
      birthDate: "2010-01-01",
      schoolReference: { schoolId }
    };
  }
  
  generateStaff(index) {
    return {
      staffUniqueId: `PERFSTAFF${index}`,
      firstName: `StaffFirst${index}`,
      lastSurname: `StaffLast${index}`
    };
  }
  
  generateStaffEducationOrganizationAssignment(staffUniqueId, schoolId, index) {
    return {
      staffReference: { staffUniqueId },
      educationOrganizationReference: { educationOrganizationId: schoolId },
      staffClassificationDescriptor: "uri://ed-fi.org/StaffClassificationDescriptor#Teacher",
      beginDate: "2024-08-01"
    };
  }
  
  generateBatchData() {
    const data = {
      schools: [],
      students: [],
      staff: [],
      studentSchoolAssociations: [],
      staffEducationOrganizationAssignmentAssociations: []
    };
    
    // Generate schools
    for (let i = 0; i < this.schoolCount; i++) {
      data.schools.push(this.generateSchool(i));
    }
    
    // Generate students distributed across schools
    for (let schoolIndex = 0; schoolIndex < this.schoolCount; schoolIndex++) {
      const schoolId = 100000 + schoolIndex;
      for (let studentIndex = 0; studentIndex < this.studentsPerSchool; studentIndex++) {
        const student = this.generateStudent(schoolId, studentIndex);
        data.students.push(student);
        
        // Generate student-school association
        data.studentSchoolAssociations.push({
          studentReference: { studentUniqueId: student.studentUniqueId },
          schoolReference: { schoolId },
          entryDate: "2024-08-01",
          entryGradeLevelDescriptor: "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
        });
      }
      
      // Generate staff for each school
      for (let staffIndex = 0; staffIndex < this.staffPerSchool; staffIndex++) {
        const globalStaffIndex = schoolIndex * this.staffPerSchool + staffIndex;
        const staff = this.generateStaff(globalStaffIndex);
        data.staff.push(staff);
        
        // Generate staff-school assignment
        data.staffEducationOrganizationAssignmentAssociations.push(
          this.generateStaffEducationOrganizationAssignment(staff.staffUniqueId, schoolId, staffIndex)
        );
      }
    }
    
    return data;
  }
}
```

## Performance Test Scenarios

### 1. Baseline Performance Tests

#### Single User Validation
```javascript
// scenarios/baseline.js
export const options = {
  scenarios: {
    baseline_single_user: {
      executor: 'constant-vus',
      vus: 1,
      duration: '5m',
    }
  },
  thresholds: {
    http_req_duration: ['p(95)<1000'], // 95% of requests under 1s
    http_req_failed: ['rate<0.01'],    // Less than 1% errors
  }
};

export default function() {
  const school = dataGenerator.generateSchool(1);
  const response = http.post(`${API_URL}/ed-fi/schools`, JSON.stringify(school), {
    headers: { 
      'Content-Type': 'application/json',
      'Authorization': `Bearer ${getToken()}`
    }
  });
  
  check(response, {
    'status is 201': (r) => r.status === 201,
    'has location header': (r) => r.headers['Location'] !== undefined
  });
  
  sleep(1);
}
```

#### Concurrent User Ramp-up
```javascript
// scenarios/concurrent.js
export const options = {
  scenarios: {
    concurrent_rampup: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: [
        { duration: '2m', target: 10 },   // Ramp to 10 users
        { duration: '5m', target: 10 },   // Stay at 10
        { duration: '2m', target: 50 },   // Ramp to 50
        { duration: '5m', target: 50 },   // Stay at 50
        { duration: '2m', target: 100 },  // Ramp to 100
        { duration: '5m', target: 100 },  // Stay at 100
        { duration: '2m', target: 0 },    // Ramp down
      ],
      gracefulRampDown: '30s',
    }
  },
  thresholds: {
    http_req_duration: ['p(95)<2000', 'p(99)<5000'],
    http_req_failed: ['rate<0.05'],
  }
};
```

### 2. Load Testing Scenarios

#### Bulk Data Loading
```javascript
// scenarios/bulkLoad.js
export const options = {
  scenarios: {
    bulk_load: {
      executor: 'shared-iterations',
      vus: 50,
      iterations: 100000, // Total entities to create
      maxDuration: '1h',
    }
  },
  thresholds: {
    http_req_duration: ['p(95)<3000'],
    http_req_failed: ['rate<0.05'],
    iteration_duration: ['p(95)<5000'],
  }
};

export default function() {
  const batch = [];
  const batchSize = 100;
  
  // Create batch of entities
  for (let i = 0; i < batchSize; i++) {
    batch.push(dataGenerator.generateStudent(__VU, __ITER * batchSize + i));
  }
  
  // Submit batch in parallel
  const responses = http.batch(
    batch.map(entity => [
      'POST',
      `${API_URL}/ed-fi/students`,
      JSON.stringify(entity),
      { headers: getHeaders() }
    ])
  );
  
  // Verify all succeeded
  responses.forEach(r => {
    check(r, { 'created': (res) => res.status === 201 });
  });
}
```

#### Mixed CRUD Operations
```javascript
// scenarios/mixedCrud.js
export const options = {
  scenarios: {
    mixed_crud: {
      executor: 'constant-arrival-rate',
      rate: 1000,        // 1000 requests per second
      timeUnit: '1s',
      duration: '30m',
      preAllocatedVUs: 100,
      maxVUs: 200,
    }
  }
};

export default function() {
  const operation = selectOperation();
  
  switch(operation) {
    case 'CREATE':
      performCreate();
      break;
    case 'READ':
      performRead();
      break;
    case 'UPDATE':
      performUpdate();
      break;
    case 'DELETE':
      performDelete();
      break;
    case 'QUERY':
      performQuery();
      break;
  }
}

function selectOperation() {
  const weights = {
    CREATE: 0.2,   // 20%
    READ: 0.4,     // 40%
    UPDATE: 0.2,   // 20%
    DELETE: 0.05,  // 5%
    QUERY: 0.15    // 15%
  };
  
  const random = Math.random();
  let cumulative = 0;
  
  for (const [op, weight] of Object.entries(weights)) {
    cumulative += weight;
    if (random < cumulative) return op;
  }
  
  return 'READ';
}
```

### 3. Stress Testing Scenarios

#### Spike Testing
```javascript
// scenarios/spike.js
export const options = {
  scenarios: {
    spike_test: {
      executor: 'ramping-vus',
      startVUs: 10,
      stages: [
        { duration: '2m', target: 10 },    // Baseline
        { duration: '30s', target: 500 },  // Spike to 500 users
        { duration: '3m', target: 500 },   // Stay at spike
        { duration: '30s', target: 10 },   // Drop to baseline
        { duration: '3m', target: 10 },    // Recovery period
      ],
    }
  },
  thresholds: {
    http_req_duration: ['p(95)<10000'], // Allow higher latency during spike
    http_req_failed: ['rate<0.1'],      // Allow up to 10% errors
  }
};
```

#### Sustained Load Testing
```javascript
// scenarios/sustained.js
export const options = {
  scenarios: {
    sustained_load: {
      executor: 'constant-vus',
      vus: 200,
      duration: '4h', // 4-hour sustained test
    }
  },
  thresholds: {
    http_req_duration: ['p(95)<3000', 'p(99)<5000'],
    http_req_failed: ['rate<0.01'],
    http_reqs: ['rate>100'], // Minimum 100 req/s throughput
  }
};
```

### 4. Query Performance Testing

#### OpenSearch Query Performance
```javascript
// scenarios/queryPerformance.js
export default function() {
  const queries = [
    // Simple query
    { endpoint: '/ed-fi/students?limit=100' },
    
    // Filtered query
    { endpoint: '/ed-fi/students?firstName=John&limit=50' },
    
    // Complex query with joins
    { endpoint: '/ed-fi/studentSchoolAssociations?schoolId=100001&limit=100' },
    
    // Aggregation query
    { endpoint: '/ed-fi/students?totalCount=true&limit=25' },
    
    // Date range query
    { endpoint: '/ed-fi/students?birthDate>=2010-01-01&birthDate<=2010-12-31' }
  ];
  
  const query = queries[Math.floor(Math.random() * queries.length)];
  
  const response = http.get(`${API_URL}${query.endpoint}`, {
    headers: getHeaders()
  });
  
  check(response, {
    'query successful': (r) => r.status === 200,
    'has results': (r) => JSON.parse(r.body).length > 0
  });
  
  // Track query-specific metrics
  queryDuration.add(response.timings.duration, { query: query.endpoint });
}
```

## Metrics and Monitoring

### Key Performance Indicators (KPIs)

| Metric | Target | Critical Threshold |
|--------|--------|-------------------|
| **Response Time (p95)** | < 2 seconds | > 5 seconds |
| **Response Time (p99)** | < 5 seconds | > 10 seconds |
| **Throughput** | > 100 req/s | < 50 req/s |
| **Error Rate** | < 1% | > 5% |
| **Concurrent Users** | 100+ | System failure |
| **Database Connection Pool** | < 80% utilized | > 95% utilized |
| **Memory Usage** | < 80% | > 95% |
| **CPU Usage** | < 70% | > 90% |

### Custom Metrics Collection

```javascript
// metrics/custom.js
import { Counter, Trend, Gauge, Rate } from 'k6/metrics';

// Custom metrics
export const apiErrors = new Counter('api_errors');
export const entityCreationTime = new Trend('entity_creation_time');
export const activeConnections = new Gauge('active_connections');
export const cacheHitRate = new Rate('cache_hit_rate');

// Track operation-specific metrics
export const operationMetrics = {
  school: {
    creates: new Counter('school_creates'),
    createTime: new Trend('school_create_time'),
  },
  student: {
    creates: new Counter('student_creates'),
    createTime: new Trend('student_create_time'),
  },
  query: {
    count: new Counter('query_count'),
    responseTime: new Trend('query_response_time'),
    resultSize: self.Trend('query_result_size'),
  }
};
```

### Real-time Monitoring Dashboard

```javascript
// monitoring/dashboard.js
export function setupMonitoring() {
  // Export to InfluxDB
  if (__ENV.INFLUXDB_URL) {
    options.ext = {
      loadimpact: {
        projectID: __ENV.PROJECT_ID,
        name: `DMS Performance Test - ${new Date().toISOString()}`
      }
    };
  }
  
  // Console output configuration
  options.summaryTrendStats = [
    'min', 'med', 'avg', 'max', 'p(90)', 'p(95)', 'p(99)'
  ];
  
  // Threshold alerts
  options.thresholds = {
    ...options.thresholds,
    'api_errors': ['count<100'],
    'entity_creation_time': ['p(95)<3000'],
    'query_response_time': ['p(95)<2000'],
  };
}
```

## Test Execution Pipeline

### 1. Environment Preparation

```bash
#!/bin/bash
# setup/prepare-environment.sh

echo "🚀 Preparing DMS Performance Test Environment with Unified Configuration"

# 1. Start DMS stack
cd ../../EdFi.DataManagementService.Tests.E2E
pwsh ./setup-local-dms.ps1
cd ../dms-load-test

# 2. Wait for services to be ready
./wait-for-services.sh

# 3. Deploy the SINGLE unified ApiSchema.json for all tests
echo "📋 Deploying unified performance test schema..."
k6 run setup/deployUnifiedSchema.js

# 4. Deploy the SINGLE unified ClaimSet for all tests  
echo "🔐 Deploying unified performance test claimset..."
k6 run setup/deployUnifiedClaimSet.js

# 5. Verify configuration
echo "✓ Schema: config/performance-test-schema.json"
echo "✓ ClaimSet: config/performance-test-claimset.json"
echo "✓ Client: performance-test-client"

echo "✅ Environment ready - Single schema and claimset deployed for ALL performance tests"
```

#### Configuration Files Structure
```
dms-load-test/
├── config/
│   ├── performance-test-schema.json      # SINGLE schema for ALL tests
│   └── performance-test-claimset.json    # SINGLE claimset for ALL tests
├── setup/
│   ├── deployUnifiedSchema.js            # Deploys the single schema
│   └── deployUnifiedClaimSet.js          # Deploys the single claimset
└── scenarios/
    ├── baseline.js                        # Uses unified config
    ├── concurrent.js                      # Uses unified config
    ├── bulkLoad.js                        # Uses unified config
    └── ...                                # All scenarios use same config
```

### 2. Progressive Test Execution

```bash
#!/bin/bash
# run-performance-suite.sh

# Configuration
export DMS_URL=${DMS_URL:-"http://localhost:5198"}
export RESULTS_DIR="results/$(date +%Y%m%d_%H%M%S)"
mkdir -p $RESULTS_DIR

# Test progression
TESTS=(
  "scenarios/baseline.js:Baseline Validation"
  "scenarios/concurrent.js:Concurrent Users"
  "scenarios/bulkLoad.js:Bulk Data Loading"
  "scenarios/mixedCrud.js:Mixed CRUD Operations"
  "scenarios/queryPerformance.js:Query Performance"
  "scenarios/spike.js:Spike Testing"
  "scenarios/sustained.js:Sustained Load"
)

for test_spec in "${TESTS[@]}"; do
  IFS=':' read -r test_file test_name <<< "$test_spec"
  
  echo "🧪 Running: $test_name"
  
  k6 run \
    --out json=$RESULTS_DIR/${test_file##*/}.json \
    --out influxdb=$INFLUXDB_URL \
    --summary-export=$RESULTS_DIR/${test_file##*/}_summary.json \
    $test_file
  
  if [ $? -ne 0 ]; then
    echo "❌ Test failed: $test_name"
    break
  fi
  
  echo "✅ Completed: $test_name"
  echo "⏸️  Cooldown period (30s)..."
  sleep 30
done

# Generate report
./generate-report.sh $RESULTS_DIR
```

### 3. Results Analysis

```javascript
// analysis/reporter.js
export class PerformanceReporter {
  constructor(resultsDir) {
    this.resultsDir = resultsDir;
  }
  
  generateReport() {
    const report = {
      timestamp: new Date().toISOString(),
      environment: {
        dmsUrl: __ENV.DMS_URL,
        testProfile: __ENV.TEST_PROFILE,
      },
      summary: {},
      scenarios: [],
      recommendations: []
    };
    
    // Analyze each scenario
    for (const scenario of this.loadScenarioResults()) {
      const analysis = this.analyzeScenario(scenario);
      report.scenarios.push(analysis);
      
      // Generate recommendations
      if (analysis.p95 > 3000) {
        report.recommendations.push(
          `High p95 latency (${analysis.p95}ms) in ${scenario.name}. Consider query optimization.`
        );
      }
      
      if (analysis.errorRate > 0.01) {
        report.recommendations.push(
          `Error rate ${(analysis.errorRate * 100).toFixed(2)}% exceeds target in ${scenario.name}.`
        );
      }
    }
    
    return report;
  }
  
  analyzeScenario(scenario) {
    return {
      name: scenario.name,
      duration: scenario.duration,
      vus: scenario.vus,
      iterations: scenario.iterations,
      rps: scenario.rps,
      p50: scenario.metrics.http_req_duration.p50,
      p95: scenario.metrics.http_req_duration.p95,
      p99: scenario.metrics.http_req_duration.p99,
      errorRate: scenario.metrics.http_req_failed.rate,
      throughput: scenario.metrics.http_reqs.rate
    };
  }
}
```

## Best Practices

### 1. Test Data Management

- **Isolation**: Use dedicated test schemas to avoid production interference
- **Cleanup**: Implement automatic cleanup between test runs
- **Seeding**: Pre-populate reference data for consistent baselines
- **Versioning**: Version test data alongside test scripts

### 2. Performance Optimization

- **Token Caching**: Reuse authentication tokens across VUs
- **Connection Pooling**: Configure appropriate K6 connection limits
- **Batch Operations**: Group related operations to reduce overhead
- **Resource Limits**: Set appropriate memory and CPU limits for K6

### 3. Result Interpretation

- **Baseline Comparison**: Always compare against established baselines
- **Trend Analysis**: Track performance trends over time
- **Bottleneck Identification**: Use metrics to identify system bottlenecks
- **Capacity Planning**: Use results to inform scaling decisions

### 4. Continuous Integration

```yaml
# .github/workflows/performance-tests.yml
name: Performance Tests

on:
  schedule:
    - cron: '0 2 * * *' # Daily at 2 AM
  workflow_dispatch:

jobs:
  performance-test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v3
      
      - name: Setup K6
        run: |
          sudo apt-key adv --keyserver hkp://keyserver.ubuntu.com:80 --recv-keys C5AD17C747E3415A3642D57D77C6C491D6AC1D69
          echo "deb https://dl.k6.io/deb stable main" | sudo tee /etc/apt/sources.list.d/k6.list
          sudo apt-get update
          sudo apt-get install k6
      
      - name: Start DMS Stack
        run: |
          cd src/dms/tests/EdFi.DataManagementService.Tests.E2E
          pwsh ./setup-local-dms.ps1
      
      - name: Run Performance Tests
        run: |
          cd src/dms/tests/dms-load-test
          ./run-performance-suite.sh
      
      - name: Upload Results
        uses: actions/upload-artifact@v3
        with:
          name: performance-results
          path: src/dms/tests/dms-load-test/results/
      
      - name: Comment on PR
        if: github.event_name == 'pull_request'
        uses: actions/github-script@v6
        with:
          script: |
            const fs = require('fs');
            const results = JSON.parse(fs.readFileSync('results/summary.json'));
            const comment = `## Performance Test Results
            - P95 Latency: ${results.p95}ms
            - Throughput: ${results.throughput} req/s
            - Error Rate: ${results.errorRate}%`;
            github.rest.issues.createComment({
              issue_number: context.issue.number,
              owner: context.repo.owner,
              repo: context.repo.repo,
              body: comment
            });
```

## Troubleshooting Guide

### Common Issues and Solutions

| Issue | Symptom | Solution |
|-------|---------|----------|
| **Token Exhaustion** | 429 errors | Implement SharedAuthManager pattern |
| **Memory Leaks** | K6 OOM errors | Use SharedArray for read-only data |
| **Connection Limits** | Connection refused | Increase PostgreSQL max_connections |
| **Slow Queries** | High p99 latency | Check OpenSearch indexing, add indices |
| **Data Inconsistency** | Reference validation failures | Ensure proper test data ordering |
| **Network Timeouts** | Request timeouts | Increase K6 timeout settings |

### Debugging Scripts

```bash
# Debug authentication issues
./test-auth.sh

# Verify schema deployment
curl -X GET $DMS_URL/metadata/schemas

# Check claimset configuration
curl -X GET $CMS_URL/claimsets

# Monitor real-time metrics
k6 run --http-debug scenarios/debug.js

# Analyze PostgreSQL performance
docker exec -it postgres psql -U postgres -c "SELECT * FROM pg_stat_activity;"

# Check OpenSearch health
curl -X GET localhost:9200/_cluster/health?pretty
```

## Conclusion

This performance testing strategy provides a comprehensive framework for evaluating DMS performance at scale. By leveraging simplified schemas, streamlined authorization, and progressive test scenarios, teams can effectively identify performance bottlenecks and ensure the system meets production requirements.

Key success factors:
- **Start Simple**: Begin with baseline tests and progressively increase complexity
- **Monitor Everything**: Collect comprehensive metrics for analysis
- **Automate Execution**: Integrate into CI/CD pipeline for continuous validation
- **Iterate and Improve**: Use results to drive performance optimizations

The strategy is designed to be extensible and adaptable to specific implementation requirements while maintaining consistency and reliability in performance testing practices.