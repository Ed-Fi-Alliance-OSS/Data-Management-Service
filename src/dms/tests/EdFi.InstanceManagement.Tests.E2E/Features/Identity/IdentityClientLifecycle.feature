# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

@InstanceCleanup @InstanceFixture @identity @instance-management-ci-shard-1
Feature: Identity Client Lifecycle
    Verify the full CMS-managed lifecycle of an identity-only API client against real
    Configuration Service and DMS instances: an application with an empty Data Store assignment
    can still authenticate, mint tokens, and reach every identity operation, while a resource
    route stays out of reach for the same client. Tenant_255901 is pre-registered by the
    suite-owned fixture.

    Background:
        Given I am authenticated to the Configuration Service as system admin

    Scenario: An identity-only application's clients pass authentication, tenant existence, binding, and authorization for every identity operation
         Given tenant "Tenant_255901" has an identity-only application with claim set "EdFiSandbox"
          And a second API client is added to the application with an empty Data Store assignment
         Then the "initial" client should have an empty Data Store assignment
          And the "second" client should have an empty Data Store assignment
         When the "initial" client is updated retaining its empty Data Store assignment
          And the "second" client is updated retaining its empty Data Store assignment
         Then the "initial" client should have an empty Data Store assignment
          And the "second" client should have an empty Data Store assignment
         When the "initial" client's credentials are reset
          And a token is minted with the "initial" client's current credentials
          And a token is minted with the "second" client's current credentials
         Then every identity operation for tenant "Tenant_255901" instance "255901/2024" using the "initial" client's token responds with 404 and problem type "urn:ed-fi:api:identities:operation-not-supported"
          And every identity operation for tenant "Tenant_255901" instance "255901/2024" using the "second" client's token responds with 404 and problem type "urn:ed-fi:api:identities:operation-not-supported"
          And a GET request for resource "contentClassDescriptors" at tenant "Tenant_255901" instance "255901/2024" using the "initial" client's token responds with 403
          And a GET request for resource "contentClassDescriptors" at tenant "Tenant_255901" instance "255901/2024" using the "second" client's token responds with 403
         When the "second" client is deleted
         Then a GET request for resource "contentClassDescriptors" at tenant "Tenant_255901" instance "255901/2024" using the "second" client's token responds with 403
