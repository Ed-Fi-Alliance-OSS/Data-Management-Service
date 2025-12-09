# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

@InstanceCleanup
Feature: Tenant-Aware Discovery API
    Verify that the discovery API validates tenants when multi-tenancy is enabled.
    Invalid tenants should return 404, valid tenants should return discovery information,
    and root URL should return placeholders.

    Background:
        Given I am authenticated to the Configuration Service as system admin
          And tenant "Tenant_Discovery_255901" is set up with a vendor and instances:
              | Route       |
              | 255901/2024 |

    # Valid tenant tests

    Scenario: Discovery endpoint with valid tenant returns OK with tenant in URLs
         When a GET request is made to discovery endpoint with route "Tenant_Discovery_255901"
         Then it should respond with 200
          And the response should contain "Tenant_Discovery_255901"
          And the response should contain "dataManagementApi"

    Scenario: Discovery endpoint with valid tenant and route qualifiers returns OK
         When a GET request is made to discovery endpoint with route "Tenant_Discovery_255901/255901/2024"
         Then it should respond with 200
          And the response should contain "255901/2024"

    # Invalid tenant tests

    Scenario: Discovery endpoint with non-existent tenant returns 404
         When a GET request is made to discovery endpoint with route "NonExistentTenant"
         Then it should respond with 404

    Scenario: Discovery endpoint with invalid tenant format still returns 404
         When a GET request is made to discovery endpoint with route "Tenant_That_Does_Not_Exist_12345"
         Then it should respond with 404

    # Root URL tests

    Scenario: Discovery endpoint at root returns placeholders
         When a GET request is made to discovery endpoint with route ""
         Then it should respond with 200
          And the response should contain "{tenant}"

    # XSD endpoint tests

    Scenario: XSD metadata endpoint with valid tenant returns OK
         When a GET request is made to XSD metadata endpoint with tenant "Tenant_Discovery_255901"
         Then it should respond with 200
          And the response should contain "ed-fi"

    Scenario: XSD metadata endpoint with invalid tenant returns 404
         When a GET request is made to XSD metadata endpoint with tenant "NonExistentTenant"
         Then it should respond with 404
