# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

@InstanceCleanup @InstanceFixture @identity @instance-management-ci-shard-1
Feature: Identity Revocation
    Verify that removing the identity claim from a claim set and reloading claim sets for one
    tenant-name spelling revokes identity authorization for that spelling while a differently
    cased spelling of the same tenant keeps authorizing until its own cached claim set entry
    naturally expires, after which both spellings are denied. The claim-set cache key is spelling
    sensitive by design (an operational limitation this feature tests rather than normalizes).
    Tenant_255901 is pre-registered by the suite-owned fixture.

    Background:
        Given I am authenticated to the Configuration Service as system admin
         And tenant "Tenant_255901" has an identity-only application with claim set "IdentityE2ERevocationClaimSet" granting identity actions "Create,Read,Update"
         And a token is minted with the "initial" client's current credentials

    Scenario: Reloading one tenant-name spelling denies it while a differently-cased spelling keeps authorizing until it expires
         Given every identity operation for tenant "TENANT_255901" instance "255901/2024" using the "initial" client's token responds with 404 and problem type "urn:ed-fi:api:identities:operation-not-supported"
          And every identity operation for tenant "tenant_255901" instance "255901/2024" using the "initial" client's token responds with 404 and problem type "urn:ed-fi:api:identities:operation-not-supported"
         When the identity claim is removed from claim set "IdentityE2ERevocationClaimSet"
          And claim sets are reloaded for tenant "TENANT_255901"
         Then every identity operation for tenant "TENANT_255901" instance "255901/2024" using the "initial" client's token responds with 403
          And every identity operation for tenant "tenant_255901" instance "255901/2024" using the "initial" client's token responds with 404 and problem type "urn:ed-fi:api:identities:operation-not-supported"
         When 16 seconds elapse for the claim set cache to expire
         Then every identity operation for tenant "tenant_255901" instance "255901/2024" using the "initial" client's token responds with 403
