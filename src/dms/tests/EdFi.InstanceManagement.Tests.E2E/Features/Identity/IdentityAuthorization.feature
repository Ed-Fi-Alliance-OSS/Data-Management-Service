# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

@InstanceCleanup @InstanceFixture @identity @instance-management-ci-shard-1
Feature: Identity Authorization
    Verify that identity operations enforce tenant existence, client-tenant binding, and
    service-claim authorization against real Configuration Service and DMS instances, and that
    the identity surface is discoverable and carries a no-store response policy. Tenant_255901
    and Tenant_255902 are pre-registered by the suite-owned fixture.

    Background:
        Given I am authenticated to the Configuration Service as system admin

    Scenario: A token minted in another tenant is rejected before authorization is checked
         Given I am authenticated to DMS with credentials for tenant "Tenant_255902"
         When a GET request is made to identity route "Tenant_255901/255901/2024/identity/v2/identities/605943412"
         Then it should respond with 401

    Scenario: An unknown tenant is reported distinctly from an unsupported operation
         Given I am authenticated to DMS with credentials for tenant "Tenant_255901"
         When a GET request is made to identity route "NonExistentTenant/255901/2024/identity/v2/identities/605943412"
         Then it should respond with 404
          And the response body "type" should be "urn:ed-fi:api:not-found"

    Scenario: A claim set without the identity claim is forbidden
         Given I am authenticated to DMS with credentials for tenant "Tenant_255901"
         When a GET request is made to identity route "Tenant_255901/255901/2024/identity/v2/identities/605943412"
         Then it should respond with 403

    Scenario: A Create-only claim set reaches the capability gate for create but is forbidden for read
         Given tenant "Tenant_255901" has an identity-only application with claim set "SeedLoader"
          And a token is minted with the "initial" client's current credentials
         When a POST request is made to identity route "Tenant_255901/255901/2024/identity/v2/identities" with body:
              """
              {}
              """
         Then it should respond with 404
          And the response body "type" should be "urn:ed-fi:api:identities:operation-not-supported"
         When a GET request is made to identity route "Tenant_255901/255901/2024/identity/v2/identities/605943412"
         Then it should respond with 403

    Scenario: Discovery lists the identity URL for a valid tenant
         When a GET request is made to discovery endpoint with route "Tenant_255901"
         Then it should respond with 200
          And the discovery response should list an identity URL ending with "/identity/v2/"

    Scenario: The metadata specification listing includes Identity under Other
         When a GET request is made to metadata specifications for tenant "Tenant_255901" instance "255901/2024"
         Then it should respond with 200
          And the metadata specifications response lists "Identity" under "Other"

    Scenario: The identity swagger document is served under /identity/v2
         When a GET request is made to identity swagger for tenant "Tenant_255901" instance "255901/2024"
         Then it should respond with 200
          And the swagger document's first server URL ends with "/identity/v2"

    Scenario: Every identity response carries Cache-Control no-store
         Given I am authenticated to DMS with credentials for tenant "Tenant_255901"
         When a GET request is made to identity route "Tenant_255901/255901/2024/identity/v2/identities/605943412"
         Then the response header "Cache-Control" should be "no-store"
