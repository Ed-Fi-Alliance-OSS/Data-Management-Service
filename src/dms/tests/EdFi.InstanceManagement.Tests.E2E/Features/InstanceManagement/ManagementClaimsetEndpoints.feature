# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

@InstanceCleanup
Feature: Management Claimset Endpoints
    Verify that the claimset management endpoints (view-claimsets and reload-claimsets)
    are tenant-aware in multi-tenant deployments.
    - Endpoints without tenant should return 404 in multi-tenant mode
    - Endpoints with valid tenant should return 200
    - Endpoints with invalid tenant should return 404
    - Tenant validation should be case-insensitive

    Background:
        Given I am authenticated to the Configuration Service as system admin
          And tenant "Tenant_Claimset_Test" is set up with a vendor and instances:
              | Route       |
              | 255901/2024 |

    # View Claimsets endpoint tests

    Scenario: View claimsets without tenant returns 404 in multi-tenant mode
         When a GET request is made to view-claimsets endpoint without tenant
         Then it should respond with 404

    Scenario: View claimsets with valid tenant returns 200
         When a GET request is made to view-claimsets endpoint with tenant "Tenant_Claimset_Test"
         Then it should respond with 200

    Scenario: View claimsets with invalid tenant returns 404
         When a GET request is made to view-claimsets endpoint with tenant "NonExistentTenant"
         Then it should respond with 404

    Scenario: View claimsets tenant validation is case-insensitive
         When a GET request is made to view-claimsets endpoint with tenant "tenant_claimset_test"
         Then it should respond with 200

    # Reload Claimsets endpoint tests

    Scenario: Reload claimsets without tenant returns 404 in multi-tenant mode
         When a POST request is made to reload-claimsets endpoint without tenant
         Then it should respond with 404

    Scenario: Reload claimsets with valid tenant returns 200
         When a POST request is made to reload-claimsets endpoint with tenant "Tenant_Claimset_Test"
         Then it should respond with 200

    Scenario: Reload claimsets with invalid tenant returns 404
         When a POST request is made to reload-claimsets endpoint with tenant "NonExistentTenant"
         Then it should respond with 404

    Scenario: Reload claimsets tenant validation is case-insensitive
         When a POST request is made to reload-claimsets endpoint with tenant "TENANT_CLAIMSET_TEST"
         Then it should respond with 200
