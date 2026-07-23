# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

@InstanceCleanup @InstanceFixture @instance-management-ci-shard-2
Feature: OWASP tenant isolation
    Validate cross-tenant isolation for DMS instance routes.
    Requests authenticated for one tenant must not read or write data in another tenant.
    Tenant_255901 (instance 255901/2024) and Tenant_255902 (instance 255902/2024) are
    pre-registered by the suite-owned fixture.

        Scenario: Cross-tenant reads are denied
            Given I am authenticated to DMS with credentials for tenant "Tenant_255901"
             When a GET request is made to tenant "Tenant_255902" instance "255902/2024" resource "contentClassDescriptors"
             Then it should respond with 404

        Scenario: Cross-tenant writes are denied
            Given I am authenticated to DMS with credentials for tenant "Tenant_255902"
             When a POST request is made to tenant "Tenant_255901" instance "255901/2024" resource "contentClassDescriptors" with body:
                  """
                  {
                      "codeValue": "OwaspTenantWrite-255902-to-255901",
                      "shortDescription": "Cross-tenant write should be blocked",
                      "description": "Attempted write to another tenant",
                      "namespace": "uri://ed-fi.org/ContentClassDescriptor"
                  }
                  """
             Then it should respond with 404
