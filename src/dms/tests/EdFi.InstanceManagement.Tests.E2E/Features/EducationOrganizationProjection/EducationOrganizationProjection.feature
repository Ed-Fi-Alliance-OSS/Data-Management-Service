# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

@InstanceCleanup @InstanceFixture @EducationOrganizationProjection @instance-management-ci-shard-1
Feature: Education organization projection
    The education organization projection read through a real Configuration Service and DMS. Every
    credential is provisioned through CMS endpoints: a claim set imported through CMS, granting only
    the projection service claim that CMS seeds, and an application with no data stores and no
    education organizations. The hierarchy is written through the resource API by an application on
    the shipped EdFiSandbox claim set, which can create and delete education organizations and
    descriptors (the fixture application's claim set cannot write state agencies or service
    centers), and each scenario removes what it wrote.

    Route 255901/2025 of Tenant_255901 holds no education organization outside these scenarios, so
    the walk is an exact list. Route 255902/2024 of Tenant_255902 holds none at all.

    Background:
        Given I am authenticated to the Configuration Service as system admin
          And the "resource" credential is an application in tenant "Tenant_255901" with claim set "EdFiSandbox" and the tenant's data stores
          And these education organizations are written through the resource API at tenant "Tenant_255901" instance "255901/2025" with the "resource" credential:
              | type                   | id     | nameOfInstitution             | shortNameOfInstitution | references                                      |
              | StateEducationAgency   | 1      | Projection E2E State          | PE SEA                 |                                                 |
              | EducationServiceCenter | 10     | Projection E2E Region Ten     |                        | stateEducationAgency=1                          |
              | LocalEducationAgency   | 100    | Projection E2E District 100   | PE 100                 | educationServiceCenter=10, stateEducationAgency=1 |
              | LocalEducationAgency   | 101    | Projection E2E District 101   |                        | parentLocalEducationAgency=100                  |
              | School                 | 100001 | Projection E2E School 100001  |                        | localEducationAgency=100                        |
              | School                 | 101001 | Escuela Proyección 101001     |                        | localEducationAgency=101                        |
              | School                 | 900001 | Projection E2E School 900001  |                        |                                                 |
          And the "projection" credential is an application in tenant "Tenant_255901" whose claim set "ProjectionE2E" grants only Read on the "educationOrganizationProjection" service claim
          And claim sets are reloaded for tenant "Tenant_255901"

    Scenario: Discovery advertises the projection and token URL templates and the contract version
         When the Discovery document of tenant "Tenant_255901" is read
         Then the Discovery document advertises:
              | member                                           | value                                                                                              |
              | urls.oauth                                       | http://localhost:8080/Tenant_255901/{districtId}/{schoolYear}/oauth/token                          |
              | urls.educationOrganizationProjection             | http://localhost:8080/Tenant_255901/{districtId}/{schoolYear}/management/education-organizations   |
              | educationOrganizationProjection.contractVersions | ["educationOrganizationProjection.v1"]                                                             |

    Scenario: A projection-only credential walks the hierarchy two items at a time through the Discovery URLs
         When the "projection" credential walks the projection at tenant "Tenant_255901" instance "255901/2025" for the data store of instance "255901/2025" with limit 2
         Then the walk returned these pages:
              | page | educationOrganizationId | nameOfInstitution            | shortNameOfInstitution | discriminator               | parentId |
              | 1    | 1                       | Projection E2E State         | PE SEA                 | edfi.StateEducationAgency   | null     |
              | 1    | 10                      | Projection E2E Region Ten    | null                   | edfi.EducationServiceCenter | 1        |
              | 2    | 100                     | Projection E2E District 100  | PE 100                 | edfi.LocalEducationAgency   | 10       |
              | 2    | 101                     | Projection E2E District 101  | null                   | edfi.LocalEducationAgency   | 100      |
              | 3    | 100001                  | Projection E2E School 100001 | null                   | edfi.School                 | 100      |
              | 3    | 101001                  | Escuela Proyección 101001    | null                   | edfi.School                 | 101      |
              | 4    | 900001                  | Projection E2E School 900001 | null                   | edfi.School                 | null     |

    Scenario: Only a credential holding the projection claim in the requested tenant reads the projection
        Given the "identity" credential is an application in tenant "Tenant_255901" whose claim set "ProjectionE2EIdentity" grants only Read on the "identity" service claim
          And the "other tenant's projection" credential is an application in tenant "Tenant_255902" whose claim set "ProjectionE2EOtherTenant" grants only Read on the "educationOrganizationProjection" service claim
          And claim sets are reloaded for tenant "Tenant_255901"
          And claim sets are reloaded for tenant "Tenant_255902"
         # Authentication, client binding and the service claim are decided before any parameter is
         # read, so a denied credential with invalid parameters is still denied rather than told
         # which parameter is wrong.
         Then projection requests respond as follows:
              | credential                | tenant        | instance    | query                                 | status | type                                                  |
              | no credential             | Tenant_255901 | 255901/2025 | dataStoreId=<255901/2025>             | 401    | urn:ed-fi:api:security:authentication                 |
              | an unknown token          | Tenant_255901 | 255901/2025 | dataStoreId=<255901/2025>             | 401    | urn:ed-fi:api:security:authentication                 |
              | resource                  | Tenant_255901 | 255901/2025 | dataStoreId=<255901/2025>             | 403    | urn:ed-fi:api:security:authorization:                 |
              | resource                  | Tenant_255901 | 255901/2025 | dataStoreId=abc&limit=0               | 403    | urn:ed-fi:api:security:authorization:                 |
              | identity                  | Tenant_255901 | 255901/2025 | dataStoreId=<255901/2025>             | 403    | urn:ed-fi:api:security:authorization:                 |
              | other tenant's projection | Tenant_255901 | 255901/2025 | dataStoreId=<255901/2025>             | 401    | urn:ed-fi:api:security:authentication                 |
              | projection                | Tenant_255901 | 255901/2025 | dataStoreId=abc&limit=0               | 400    | urn:ed-fi:api:bad-request:parameter-validation-failed |
         # The other tenant's credential reads its own tenant's store, which holds no education
         # organization: one empty page, and nothing written in Tenant_255901.
         When the "other tenant's projection" credential walks the projection at tenant "Tenant_255902" instance "255902/2024" for the data store of instance "255902/2024" with limit 2
         Then the walk returned one empty page

    Scenario: A data store answers only in its own tenant and route context
         Then projection requests respond as follows:
              | credential | tenant        | instance    | query                     | status | type                                                             |
              | projection | Tenant_255901 | 255901/2024 | dataStoreId=<255901/2025> | 404    | urn:ed-fi:api:education-organization-projection:target-not-found |
              | projection | Tenant_255901 | 255901/2025 | dataStoreId=<255902/2024> | 404    | urn:ed-fi:api:education-organization-projection:target-not-found |
              | projection | Tenant_255901 | 255901/2025 | dataStoreId=2147483647    | 404    | urn:ed-fi:api:education-organization-projection:target-not-found |

    Scenario: The projection credential is denied by the resource API, reading or writing
         When the "projection" credential sends GET to resource "schools" at tenant "Tenant_255901" instance "255901/2025"
         Then it should respond with 403
          And the response body "type" should be "urn:ed-fi:api:authorization-denied"
         When the "projection" credential sends POST to resource "schools" at tenant "Tenant_255901" instance "255901/2025" with body:
              """
              {
                  "schoolId": 900002,
                  "nameOfInstitution": "Projection E2E Not Written",
                  "educationOrganizationCategories": [ { "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#ProjectionE2E" } ],
                  "gradeLevels": [ { "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#ProjectionE2E" } ]
              }
              """
         Then it should respond with 403
          And the response body "type" should be "urn:ed-fi:api:authorization-denied"
         When the "resource" credential sends GET to resource "schools" at tenant "Tenant_255901" instance "255901/2025" with query "schoolId=900002"
         Then it should respond with 200
          And the response should be an empty array
