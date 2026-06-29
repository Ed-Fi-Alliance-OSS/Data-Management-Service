# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# These scenarios require the relational backend in the Instance Management E2E harness,
# which is enabled in DMS-1239. They are committed @ignore until that lands. They were
# verified green locally against a throwaway relational harness (see plan Task 6).

@InstanceCleanup @changequeries-isolation @ignore @DMS-1239 @instance-management-ci-shard-2
Feature: ChangeQueries isolation across tenants and instances
    Verify that change versions, /deletes, /keyChanges, and ReadChanges authorization are
    isolated per (tenant, instance) database. Tenant_255901 owns instances 255901/2024 and
    255901/2025; Tenant_255902 owns 255902/2024. Each route maps to a separate database.

    Background:
        Given I am authenticated to the Configuration Service as system admin
          And tenant "Tenant_255901" is set up with a vendor and instances:
              | Route       |
              | 255901/2024 |
              | 255901/2025 |
          And tenant "Tenant_255902" is set up with a vendor and instances:
              | Route       |
              | 255902/2024 |
          And tenant "Tenant_255901" has an application for district "255901"
          And tenant "Tenant_255902" has an application for district "255902"
          And I am authenticated to DMS with credentials for tenant "Tenant_255901"

        Scenario: 01 ChangeQueries surface works end-to-end inside a route-qualified instance
            # availableChangeVersions
             When a POST request is made to tenant "Tenant_255901" instance "255901/2024" resource "contentClassDescriptors" with body:
                  """
                  {
                      "codeValue": "CQSmoke-2024",
                      "shortDescription": "Smoke",
                      "description": "Smoke",
                      "namespace": "uri://ed-fi.org/ContentClassDescriptor"
                  }
                  """
             Then it should respond with success
              And the location should be stored as "smokeDescriptor"
             Then the newest change version for tenant "Tenant_255901" instance "255901/2024" is greater than 0
            # /deletes
             When a DELETE request is made for stored location "smokeDescriptor"
             Then it should respond with 204
             When a GET request is made to deletes for tenant "Tenant_255901" instance "255901/2024" resource "contentClassDescriptors"
             Then it should respond with 200
              And the response should contain "CQSmoke-2024"
            # /keyChanges (classPeriod identity update)
             When a POST request is made to tenant "Tenant_255901" instance "255901/2024" resource "gradeLevelDescriptors" with body:
                  """
                  { "codeValue": "Tenth Grade", "shortDescription": "Tenth Grade", "description": "Tenth Grade", "namespace": "uri://ed-fi.org/GradeLevelDescriptor" }
                  """
             Then it should respond with success
             When a POST request is made to tenant "Tenant_255901" instance "255901/2024" resource "educationOrganizationCategoryDescriptors" with body:
                  """
                  { "codeValue": "School", "shortDescription": "School", "description": "School", "namespace": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor" }
                  """
             Then it should respond with success
             When a POST request is made to tenant "Tenant_255901" instance "255901/2024" resource "schools" with body:
                  """
                  {
                    "schoolId": 2559010101,
                    "nameOfInstitution": "CQ Smoke School",
                    "gradeLevels": [ { "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" } ],
                    "educationOrganizationCategories": [ { "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School" } ]
                  }
                  """
             Then it should respond with success
             When a POST request is made to tenant "Tenant_255901" instance "255901/2024" resource "classPeriods" with body:
                  """
                  { "classPeriodName": "CQSmoke-Period-A", "schoolReference": { "schoolId": 2559010101 } }
                  """
             Then it should respond with success
              And the location should be stored as "smokeClassPeriod"
             When a PUT request is made for stored location "smokeClassPeriod" with body:
                  """
                  { "id": "{id}", "classPeriodName": "CQSmoke-Period-B", "schoolReference": { "schoolId": 2559010101 } }
                  """
             Then it should respond with 204
             When a GET request is made to keyChanges for tenant "Tenant_255901" instance "255901/2024" resource "classPeriods"
             Then it should respond with 200
              And the response should contain "CQSmoke-Period-B"

        Scenario: 02 availableChangeVersions counter is isolated across instances of the same tenant
             When I capture the newest change version for tenant "Tenant_255901" instance "255901/2024" as "inst1Baseline"
             When I capture the newest change version for tenant "Tenant_255901" instance "255901/2025" as "inst2Baseline"
             When a POST request is made to tenant "Tenant_255901" instance "255901/2024" resource "contentClassDescriptors" with body:
                  """
                  { "codeValue": "CQCounter-Inst", "shortDescription": "x", "description": "x", "namespace": "uri://ed-fi.org/ContentClassDescriptor" }
                  """
             Then it should respond with success
             Then the newest change version for tenant "Tenant_255901" instance "255901/2024" is greater than captured "inst1Baseline"
              And the newest change version for tenant "Tenant_255901" instance "255901/2025" equals captured "inst2Baseline"

        Scenario: 03 availableChangeVersions counter is isolated across tenants
             When I am authenticated to DMS with credentials for tenant "Tenant_255902"
             When I capture the newest change version for tenant "Tenant_255902" instance "255902/2024" as "tenantBBaseline"
             When I am authenticated to DMS with credentials for tenant "Tenant_255901"
             When I capture the newest change version for tenant "Tenant_255901" instance "255901/2024" as "tenantABaseline"
             When a POST request is made to tenant "Tenant_255901" instance "255901/2024" resource "contentClassDescriptors" with body:
                  """
                  { "codeValue": "CQCounter-Tenant", "shortDescription": "x", "description": "x", "namespace": "uri://ed-fi.org/ContentClassDescriptor" }
                  """
             Then it should respond with success
             Then the newest change version for tenant "Tenant_255901" instance "255901/2024" is greater than captured "tenantABaseline"
             When I am authenticated to DMS with credentials for tenant "Tenant_255902"
             Then the newest change version for tenant "Tenant_255902" instance "255902/2024" equals captured "tenantBBaseline"

        Scenario: 04 A delete tracked in one instance is not returned by another instance of the same tenant
             When a POST request is made to tenant "Tenant_255901" instance "255901/2024" resource "contentClassDescriptors" with body:
                  """
                  { "codeValue": "CQDelInst", "shortDescription": "x", "description": "x", "namespace": "uri://ed-fi.org/ContentClassDescriptor" }
                  """
             Then it should respond with success
              And the location should be stored as "delInstDescriptor"
             When a DELETE request is made for stored location "delInstDescriptor"
             Then it should respond with 204
             When a GET request is made to deletes for tenant "Tenant_255901" instance "255901/2024" resource "contentClassDescriptors"
             Then it should respond with 200
              And the response should contain "CQDelInst"
             When a GET request is made to deletes for tenant "Tenant_255901" instance "255901/2025" resource "contentClassDescriptors"
             Then it should respond with 200
              And the response should not contain "CQDelInst"

        Scenario: 05 A delete tracked in one tenant is not returned by another tenant
             When a POST request is made to tenant "Tenant_255901" instance "255901/2024" resource "contentClassDescriptors" with body:
                  """
                  { "codeValue": "CQDelTenant", "shortDescription": "x", "description": "x", "namespace": "uri://ed-fi.org/ContentClassDescriptor" }
                  """
             Then it should respond with success
              And the location should be stored as "delTenantDescriptor"
             When a DELETE request is made for stored location "delTenantDescriptor"
             Then it should respond with 204
             When a GET request is made to deletes for tenant "Tenant_255901" instance "255901/2024" resource "contentClassDescriptors"
             Then it should respond with 200
              And the response should contain "CQDelTenant"
             When I am authenticated to DMS with credentials for tenant "Tenant_255902"
             When a GET request is made to deletes for tenant "Tenant_255902" instance "255902/2024" resource "contentClassDescriptors"
             Then it should respond with 200
              And the response should not contain "CQDelTenant"

        Scenario: 06 A key change tracked in one instance is not returned by another instance of the same tenant
             When a POST request is made to tenant "Tenant_255901" instance "255901/2024" resource "gradeLevelDescriptors" with body:
                  """
                  { "codeValue": "Tenth Grade", "shortDescription": "Tenth Grade", "description": "Tenth Grade", "namespace": "uri://ed-fi.org/GradeLevelDescriptor" }
                  """
             Then it should respond with success
             When a POST request is made to tenant "Tenant_255901" instance "255901/2024" resource "educationOrganizationCategoryDescriptors" with body:
                  """
                  { "codeValue": "School", "shortDescription": "School", "description": "School", "namespace": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor" }
                  """
             Then it should respond with success
             When a POST request is made to tenant "Tenant_255901" instance "255901/2024" resource "schools" with body:
                  """
                  {
                    "schoolId": 2559010102,
                    "nameOfInstitution": "CQ KC Inst School",
                    "gradeLevels": [ { "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" } ],
                    "educationOrganizationCategories": [ { "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School" } ]
                  }
                  """
             Then it should respond with success
             When a POST request is made to tenant "Tenant_255901" instance "255901/2024" resource "classPeriods" with body:
                  """
                  { "classPeriodName": "CQKCInst-A", "schoolReference": { "schoolId": 2559010102 } }
                  """
             Then it should respond with success
              And the location should be stored as "kcInstClassPeriod"
             When a PUT request is made for stored location "kcInstClassPeriod" with body:
                  """
                  { "id": "{id}", "classPeriodName": "CQKCInst-B", "schoolReference": { "schoolId": 2559010102 } }
                  """
             Then it should respond with 204
             When a GET request is made to keyChanges for tenant "Tenant_255901" instance "255901/2024" resource "classPeriods"
             Then it should respond with 200
              And the response should contain "CQKCInst-B"
             When a GET request is made to keyChanges for tenant "Tenant_255901" instance "255901/2025" resource "classPeriods"
             Then it should respond with 200
              And the response should not contain "CQKCInst"

        Scenario: 07 A key change tracked in one tenant is not returned by another tenant
             When a POST request is made to tenant "Tenant_255901" instance "255901/2024" resource "gradeLevelDescriptors" with body:
                  """
                  { "codeValue": "Tenth Grade", "shortDescription": "Tenth Grade", "description": "Tenth Grade", "namespace": "uri://ed-fi.org/GradeLevelDescriptor" }
                  """
             Then it should respond with success
             When a POST request is made to tenant "Tenant_255901" instance "255901/2024" resource "educationOrganizationCategoryDescriptors" with body:
                  """
                  { "codeValue": "School", "shortDescription": "School", "description": "School", "namespace": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor" }
                  """
             Then it should respond with success
             When a POST request is made to tenant "Tenant_255901" instance "255901/2024" resource "schools" with body:
                  """
                  {
                    "schoolId": 2559010103,
                    "nameOfInstitution": "CQ KC Tenant School",
                    "gradeLevels": [ { "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" } ],
                    "educationOrganizationCategories": [ { "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School" } ]
                  }
                  """
             Then it should respond with success
             When a POST request is made to tenant "Tenant_255901" instance "255901/2024" resource "classPeriods" with body:
                  """
                  { "classPeriodName": "CQKCTenant-A", "schoolReference": { "schoolId": 2559010103 } }
                  """
             Then it should respond with success
              And the location should be stored as "kcTenantClassPeriod"
             When a PUT request is made for stored location "kcTenantClassPeriod" with body:
                  """
                  { "id": "{id}", "classPeriodName": "CQKCTenant-B", "schoolReference": { "schoolId": 2559010103 } }
                  """
             Then it should respond with 204
             When a GET request is made to keyChanges for tenant "Tenant_255901" instance "255901/2024" resource "classPeriods"
             Then it should respond with 200
              And the response should contain "CQKCTenant-B"
             When I am authenticated to DMS with credentials for tenant "Tenant_255902"
             When a GET request is made to keyChanges for tenant "Tenant_255902" instance "255902/2024" resource "classPeriods"
             Then it should respond with 200
              And the response should not contain "CQKCTenant"

        Scenario: 08 A client authorized for one tenant is denied change-query reads on another tenant
             When a GET request is made to deletes for tenant "Tenant_255902" instance "255902/2024" resource "contentClassDescriptors"
             Then it should respond with 404
             When a GET request is made to keyChanges for tenant "Tenant_255902" instance "255902/2024" resource "classPeriods"
             Then it should respond with 404

        Scenario: 09 A NamespaceBased ReadChanges filter is evaluated against the correct instance database
             When tenant "Tenant_255901" has an application for district "255901" with claim set "EdFiSandbox"
             When I am authenticated to DMS with credentials for tenant "Tenant_255901"
             When a POST request is made to tenant "Tenant_255901" instance "255901/2024" resource "educationContents" with body:
                  """
                  {
                    "contentIdentifier": "CQNs-2024",
                    "namespace": "uri://ed-fi.org",
                    "learningResourceMetadataURI": "uri://ed-fi.org/CQNs-2024",
                    "shortDescription": "CQ namespaced content"
                  }
                  """
             Then it should respond with success
              And the location should be stored as "nsContent"
             When a DELETE request is made for stored location "nsContent"
             Then it should respond with 204
             When a GET request is made to deletes for tenant "Tenant_255901" instance "255901/2024" resource "educationContents"
             Then it should respond with 200
              And the response should contain "CQNs-2024"
             When a GET request is made to deletes for tenant "Tenant_255901" instance "255901/2025" resource "educationContents"
             Then it should respond with 200
              And the response should not contain "CQNs-2024"
