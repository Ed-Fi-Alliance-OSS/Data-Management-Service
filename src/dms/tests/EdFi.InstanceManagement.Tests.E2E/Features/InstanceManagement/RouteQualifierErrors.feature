# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

@InstanceCleanup
Feature: Route Qualifier Error Handling
    Verify error handling for invalid route qualifiers

    Background:
        Given I am authenticated to the Configuration Service as system admin
          And tenant "Tenant_ErrorTest" is set up with a vendor and instances:
              | Route       |
              | 255901/2024 |
              | 255901/2025 |
              | 255902/2024 |
          And tenant "Tenant_ErrorTest" has an application for district "255901"
          And I am authenticated to DMS with credentials for tenant "Tenant_ErrorTest"

    Scenario: Invalid district ID returns 404
        When a GET request is made to tenant "Tenant_ErrorTest" instance "999999/2024" resource "contentClassDescriptors"
        Then it should respond with 404

    Scenario: Invalid school year returns 404
        When a GET request is made to tenant "Tenant_ErrorTest" instance "255901/2099" resource "contentClassDescriptors"
        Then it should respond with 404

    Scenario: Missing route qualifiers returns error
        When a GET request is made without route qualifiers to resource "contentClassDescriptors"
        Then it should respond with 404 or 400
