# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

@InstanceCleanup
Feature: Tenant Segregation
    Verify that users with access to one tenant cannot access another tenant's data.
    Each tenant has its own application with credentials that should only work for that tenant.

    Background:
        Given I am authenticated to the Configuration Service as system admin
          And tenant "Tenant_255901" is set up with a vendor and instances:
              | Route       |
              | 255901/2024 |
              | 255901/2025 |
          And tenant "Tenant_255901" has an application for district "255901"
          And tenant "Tenant_255902" is set up with a vendor and instances:
              | Route       |
              | 255902/2024 |
          And tenant "Tenant_255902" has an application for district "255902"

    # Positive tests - users can access their own tenant's instances

    Scenario: User with Tenant_255901 credentials can access instance 255901/2024
        Given I am authenticated to DMS with credentials for tenant "Tenant_255901"
         When a POST request is made to tenant "Tenant_255901" instance "255901/2024" resource "contentClassDescriptors" with body:
              """
              {
                  "codeValue": "TenantTest-255901-2024",
                  "shortDescription": "Test descriptor for tenant segregation",
                  "description": "Descriptor created by Tenant_255901 credentials",
                  "namespace": "uri://ed-fi.org/ContentClassDescriptor"
              }
              """
         Then it should respond with success

    Scenario: User with Tenant_255902 credentials can access instance 255902/2024
        Given I am authenticated to DMS with credentials for tenant "Tenant_255902"
         When a POST request is made to tenant "Tenant_255902" instance "255902/2024" resource "contentClassDescriptors" with body:
              """
              {
                  "codeValue": "TenantTest-255902-2024",
                  "shortDescription": "Test descriptor for tenant segregation",
                  "description": "Descriptor created by Tenant_255902 credentials",
                  "namespace": "uri://ed-fi.org/ContentClassDescriptor"
              }
              """
         Then it should respond with success

    # Negative tests - users cannot access other tenant's instances

    Scenario: User with Tenant_255901 credentials cannot access Tenant_255902 instance via correct tenant URL
        Given I am authenticated to DMS with credentials for tenant "Tenant_255901"
         When a GET request is made to tenant "Tenant_255902" instance "255902/2024" resource "contentClassDescriptors"
         Then it should respond with 404

    Scenario: User with Tenant_255902 credentials cannot access Tenant_255901 instance via correct tenant URL
        Given I am authenticated to DMS with credentials for tenant "Tenant_255902"
         When a GET request is made to tenant "Tenant_255901" instance "255901/2024" resource "contentClassDescriptors"
         Then it should respond with 404

    # Cross-tenant URL manipulation tests - mismatched tenant and route qualifiers

    Scenario: User cannot access instance by using wrong tenant in URL path
        Given I am authenticated to DMS with credentials for tenant "Tenant_255901"
        # Trying to access 255902/2024 (which belongs to Tenant_255902) via Tenant_255901's URL
         When a GET request is made to tenant "Tenant_255901" instance "255902/2024" resource "contentClassDescriptors"
         Then it should respond with 404

    Scenario: User with Tenant_255902 credentials cannot access via Tenant_255901 URL even with their own route
        Given I am authenticated to DMS with credentials for tenant "Tenant_255902"
        # Trying to access via wrong tenant URL but with route qualifiers that exist in their tenant
         When a GET request is made to tenant "Tenant_255901" instance "255902/2024" resource "contentClassDescriptors"
         Then it should respond with 404

    # Verify data isolation - data created in one tenant doesn't appear in another

    Scenario: Data created in Tenant_255901 is not visible in Tenant_255902
        Given I am authenticated to DMS with credentials for tenant "Tenant_255901"
         When a POST request is made to tenant "Tenant_255901" instance "255901/2024" resource "contentClassDescriptors" with body:
              """
              {
                  "codeValue": "IsolationTest-OnlyIn255901",
                  "shortDescription": "Should only exist in Tenant_255901",
                  "description": "This descriptor should not be visible from Tenant_255902",
                  "namespace": "uri://ed-fi.org/ContentClassDescriptor"
              }
              """
         Then it should respond with success
        Given I am authenticated to DMS with credentials for tenant "Tenant_255902"
         When a GET request is made to tenant "Tenant_255902" instance "255902/2024" resource "contentClassDescriptors"
         Then it should respond with 200
          And the response should not contain "IsolationTest-OnlyIn255901"
