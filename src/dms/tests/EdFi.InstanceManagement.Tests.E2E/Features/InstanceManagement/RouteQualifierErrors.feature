# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

@InstanceCleanup
Feature: Route Qualifier Error Handling
    Verify error handling for invalid route qualifiers

    Background:
        Given the system is configured with route qualifiers
        And I have completed instance setup with 3 instances
        And I am authenticated to DMS with application credentials

    Scenario: Invalid district ID returns 404
        When a GET request is made to instance "999999/2024" and resource "contentClassDescriptors"
        Then it should respond with 404

    Scenario: Invalid school year returns 404
        When a GET request is made to instance "255901/2099" and resource "contentClassDescriptors"
        Then it should respond with 404

    Scenario: Missing route qualifiers returns error
        When a GET request is made without route qualifiers to resource "contentClassDescriptors"
        Then it should respond with 404 or 400
