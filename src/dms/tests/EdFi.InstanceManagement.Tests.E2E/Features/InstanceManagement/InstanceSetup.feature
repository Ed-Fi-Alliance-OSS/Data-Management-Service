# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

@InstanceCleanup
Feature: Instance Setup for Multi-Instance Testing
    Set up vendors, instances with route contexts, and applications for multi-instance testing

    Background:
        Given I am authenticated to the Configuration Service as system admin

    Scenario: Create vendor for multi-instance testing
        When I create a vendor with the following details:
            | Company                    | Multi-District Test Vendor |
            | ContactName                | Test Admin                 |
            | ContactEmailAddress        | admin@testdistrict.edu     |
            | NamespacePrefixes          | uri://ed-fi.org,uri://testdistrict.edu |
        Then the vendor should be created successfully
        And the vendor ID should be stored

    Scenario: Create DMS instances with route contexts
        Given a vendor exists
        When I create an instance with the following details:
            | InstanceType     | District                           |
            | InstanceName     | District 255901 - School Year 2024 |
            | ConnectionString | host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice_d255901_sy2024; |
        And I add route context "districtId" with value "255901" to the instance
        And I add route context "schoolYear" with value "2024" to the instance
        Then the instance should be created successfully
        When I create an instance with the following details:
            | InstanceType     | District                           |
            | InstanceName     | District 255901 - School Year 2025 |
            | ConnectionString | host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice_d255901_sy2025; |
        And I add route context "districtId" with value "255901" to the instance
        And I add route context "schoolYear" with value "2025" to the instance
        Then the instance should be created successfully
        When I create an instance with the following details:
            | InstanceType     | District                           |
            | InstanceName     | District 255902 - School Year 2024 |
            | ConnectionString | host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice_d255902_sy2024; |
        And I add route context "districtId" with value "255902" to the instance
        And I add route context "schoolYear" with value "2024" to the instance
        Then the instance should be created successfully
        And 3 instances should be created

    Scenario: Create application with access to multiple instances
        Given a vendor exists
        And 3 instances exist with route contexts
        When I create an application with the following details:
            | ApplicationName          | Multi-District Test App              |
            | ClaimSetName             | E2E-NoFurtherAuthRequiredClaimSet    |
            | EducationOrganizationIds | 255901,255902                        |
        Then the application should be created successfully
        And the application credentials should be stored
