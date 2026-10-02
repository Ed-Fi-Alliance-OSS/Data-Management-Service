# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

@InstanceCleanup @InstanceFixture @instance-management-identity-plugin
Feature: Identity Fixture Plugin
    Verify the DMS-owned identity surface through the deployed plugin path: the DMS container mounts
    and allowlists Acme.IdentityFixture, so identity requests reach the fixture rather than the
    operation-not-supported default. This feature carries no CI shard tag and runs only through its
    own filter, with an environment file from New-IdentityPluginEnvironmentFile.ps1. The fixture keeps
    persons and jobs in memory and the runner does not guarantee scenario order, so every scenario,
    the restart one included, issues its own persons and tokens and relies on no state another
    scenario left behind.

    Background:
        Given the identity fixture plugin is loaded
          And I am authenticated to the Configuration Service as system admin
          And tenant "Tenant_255901" has an identity-only application with claim set "EdFiSandbox"
          And a token is minted with the "initial" client's current credentials

    Scenario: The fixture is loaded and answers instead of the operation-not-supported default
        Then the DMS log names "Acme.IdentityFixture" in a plugin inventory line
        When a GET request is made to identity route "Tenant_255901/255901/2024/identity/v2/identities/0123456789abcdef0123456789abcdef"
        Then it should respond with 404
         And the response body "type" should be "urn:ed-fi:api:identities:not-found"

    Scenario: Synchronous create, get-by-id, find and search reach the fixture
        When a person is created through identity route "Tenant_255901/255901/2024/identity/v2/identities" with body:
            """
            { "LastSurname": "Rivera", "FirstName": "Ana" }
            """
        Then it should respond with 200
         And the create response should be a bare issued UniqueId
        When a GET request is made to identity route "Tenant_255901/255901/2024/identity/v2/identities" for the issued UniqueId
        Then it should respond with 200
         And the response property "UniqueId" should be the issued UniqueId
         And the response body "LastSurname" should be "Rivera"
         And the response body "FirstName" should be "Ana"
        When a POST request is made to identity route "Tenant_255901/255901/2024/identity/v2/identities/find" with the issued UniqueId substituted into body:
            """
            ["<UniqueId>"]
            """
        Then it should respond with 200
         And the response body "Status" should be "Complete"
         And search group 0 of the response should list the issued UniqueId
        When a POST request is made to identity route "Tenant_255901/255901/2024/identity/v2/identities/search" with the issued UniqueId substituted into body:
            """
            [{ "LastSurname": "Rivera", "FirstName": "Ana" }]
            """
        Then it should respond with 200
         And the response body "Status" should be "Complete"
         And search group 0 of the response should list the issued UniqueId

    Scenario: An asynchronous find is accepted with a Location that polls from incomplete to complete
        When a person is created through identity route "Tenant_255901/255901/2024/identity/v2/identities" with body:
            """
            { "LastSurname": "Okafor", "FirstName": "Ngozi" }
            """
        Then it should respond with 200
        When a POST request is made to identity route "Tenant_255901/255901/2024/identity/v2/identities/find" with the issued UniqueId substituted into body:
            """
            ["<UniqueId>", "~fixture:async"]
            """
        Then it should respond with 202
         And the response should carry a Location to an identity results route
        When the results Location is polled until it completes
        Then it should respond with 200
         And the results polls should have answered "Incomplete" then "Complete"
         And the response body "Status" should be "Complete"
         And search group 0 of the response should list the issued UniqueId
         And search group 1 of the response should be empty

    Scenario: A get-by-id and a results poll leave no UniqueId and no request token in the DMS container log
        When a person is created through identity route "Tenant_255901/255901/2024/identity/v2/identities" with body:
            """
            { "LastSurname": "Haddad", "FirstName": "Samir" }
            """
        Then it should respond with 200
        Given the DMS container log position is recorded
          And a new unique correlation id is used for identity requests
        When a GET request is made to identity route "Tenant_255901/255901/2024/identity/v2/identities" for the issued UniqueId
        Then it should respond with 200
        Given a new unique correlation id is used for identity requests
        When a POST request is made to identity route "Tenant_255901/255901/2024/identity/v2/identities/find" with the issued UniqueId substituted into body:
            """
            ["<UniqueId>", "~fixture:async"]
            """
        Then it should respond with 202
         And the response should carry a Location to an identity results route
        When the results Location is polled until it completes
        Then it should respond with 200
         And the DMS container log shows the frontend and core completion events for every correlation id used
         And no line of the DMS container log since the recorded position contains an issued UniqueId or request token

    Scenario: A request token issued before a DMS container restart answers identity not-found afterwards
        When a POST request is made to identity route "Tenant_255901/255901/2024/identity/v2/identities/find" with the issued UniqueId substituted into body:
            """
            ["~fixture:async"]
            """
        Then it should respond with 202
         And the response should carry a Location to an identity results route
        When the results Location is polled once
        Then it should respond with 200
        When the DMS container is restarted
         And the results Location is polled once
        Then it should respond with 404
         And the response body "type" should be "urn:ed-fi:api:identities:not-found"
