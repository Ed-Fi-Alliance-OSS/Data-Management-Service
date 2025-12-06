# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

@InstanceCleanup
Feature: Instance Setup for Multi-Instance Testing
             Set up tenants, vendors, instances with route contexts, and applications for multi-instance testing

        Background:
            Given I am authenticated to the Configuration Service as system admin

        Scenario: Create tenant and vendor for District 255901
            Given I am working with tenant "Tenant_255901"
             When I create a vendor with the following details:
                  | Company             | District 255901 Vendor                   |
                  | ContactName         | Test Admin                               |
                  | ContactEmailAddress | admin@district255901.edu                 |
                  | NamespacePrefixes   | uri://ed-fi.org,uri://district255901.edu |
             Then the vendor should be created successfully
              And the vendor ID should be stored

        Scenario: Create tenant and vendor for District 255902
            Given I am working with tenant "Tenant_255902"
             When I create a vendor with the following details:
                  | Company             | District 255902 Vendor                   |
                  | ContactName         | Test Admin                               |
                  | ContactEmailAddress | admin@district255902.edu                 |
                  | NamespacePrefixes   | uri://ed-fi.org,uri://district255902.edu |
             Then the vendor should be created successfully
              And the vendor ID should be stored

        Scenario: Create DMS instances for District 255901
            Given I am working with tenant "Tenant_255901"
              And a vendor exists
             When I create an instance with the following details:
                  | InstanceType     | District                                                                                                                |
                  | InstanceName     | District 255901 - School Year 2024                                                                                      |
                  | ConnectionString | host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice_d255901_sy2024; |
              And I add route context "districtId" with value "255901" to the instance
              And I add route context "schoolYear" with value "2024" to the instance
             Then the instance should be created successfully
             When I create an instance with the following details:
                  | InstanceType     | District                                                                                                                |
                  | InstanceName     | District 255901 - School Year 2025                                                                                      |
                  | ConnectionString | host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice_d255901_sy2025; |
              And I add route context "districtId" with value "255901" to the instance
              And I add route context "schoolYear" with value "2025" to the instance
             Then the instance should be created successfully
              And 2 instances should be created

        Scenario: Create DMS instance for District 255902
            Given I am working with tenant "Tenant_255902"
              And a vendor exists
             When I create an instance with the following details:
                  | InstanceType     | District                                                                                                                |
                  | InstanceName     | District 255902 - School Year 2024                                                                                      |
                  | ConnectionString | host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice_d255902_sy2024; |
              And I add route context "districtId" with value "255902" to the instance
              And I add route context "schoolYear" with value "2024" to the instance
             Then the instance should be created successfully
              And 1 instances should be created

        Scenario: Create application for District 255901
            Given tenant "Tenant_255901" is set up with a vendor and instances:
                  | Route       |
                  | 255901/2024 |
                  | 255901/2025 |
             When I create an application with the following details:
                  | ApplicationName          | District 255901 Test App          |
                  | ClaimSetName             | E2E-NoFurtherAuthRequiredClaimSet |
                  | EducationOrganizationIds | 255901                            |
             Then the application should be created successfully
              And the application credentials should be stored

        Scenario: Create application for District 255902
            Given tenant "Tenant_255902" is set up with a vendor and instances:
                  | Route       |
                  | 255902/2024 |
             When I create an application with the following details:
                  | ApplicationName          | District 255902 Test App          |
                  | ClaimSetName             | E2E-NoFurtherAuthRequiredClaimSet |
                  | EducationOrganizationIds | 255902                            |
             Then the application should be created successfully
              And the application credentials should be stored
