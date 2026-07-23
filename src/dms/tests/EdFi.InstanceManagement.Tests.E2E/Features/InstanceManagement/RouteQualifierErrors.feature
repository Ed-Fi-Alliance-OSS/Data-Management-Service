# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

@InstanceCleanup @InstanceFixture @instance-management-ci-shard-1
Feature: Route Qualifier Error Handling
    Verify error handling for invalid route qualifiers. Tenant_255901 owns instances 255901/2024
    and 255901/2025, pre-registered by the suite-owned fixture.

    Background:
        Given I am authenticated to DMS with credentials for tenant "Tenant_255901"

    Scenario: Invalid district ID returns 404
        When a GET request is made to tenant "Tenant_255901" instance "999999/2024" resource "contentClassDescriptors"
        Then it should respond with 404

    Scenario: Invalid school year returns 404
        When a GET request is made to tenant "Tenant_255901" instance "255901/2099" resource "contentClassDescriptors"
        Then it should respond with 404

    Scenario: Missing route qualifiers returns error
        When a GET request is made without route qualifiers to resource "contentClassDescriptors"
        Then it should respond with 404 or 400
