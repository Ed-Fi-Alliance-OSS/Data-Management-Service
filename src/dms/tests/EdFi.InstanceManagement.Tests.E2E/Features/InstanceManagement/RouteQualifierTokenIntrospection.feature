# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

@InstanceCleanup @InstanceFixture @instance-management-ci-shard-1
Feature: Route Qualifier Token Introspection
    Verify that the token_info endpoint advertised by routed Discovery documents identifies the
    owning tenant, and rejects missing, mismatched and cross-tenant credentials.

    Background:
        Given the system is configured with route qualifiers

    Scenario Outline: Advertised token info identifies the owning tenant on each fixture route
        Given tenant "<tenant>" has a fresh token from discovery route "<route>"
        When the advertised token info endpoint is called with matching bearer and body tokens
        Then it should respond with 200
        And token info identifies fixture tenant "<tenant>" without caching

        Examples:
            | tenant        | route                     |
            | Tenant_255901 | Tenant_255901/255901/2024 |
            | Tenant_255901 | Tenant_255901/255901/2025 |
            | Tenant_255902 | Tenant_255902/255902/2024 |

    Scenario: Advertised token info requires bearer authentication
        Given tenant "Tenant_255901" has a fresh token from discovery route "Tenant_255901/255901/2024"
        When the advertised token info endpoint is called without bearer authentication
        Then it should respond with 401
        And the response requires bearer authentication

    Scenario: Advertised token info rejects a mismatched body token
        Given tenant "Tenant_255901" has a fresh token from discovery route "Tenant_255901/255901/2024"
        When the advertised token info endpoint is called with a nonmatching body token
        Then it should respond with 400
        And the response reports a token identity mismatch

    Scenario: Advertised token info rejects credentials from another tenant
        Given tenant "Tenant_255901" has a fresh token from discovery route "Tenant_255901/255901/2024"
        When the token info endpoint advertised for route "Tenant_255902/255902/2024" is called with the original tenant token
        Then it should respond with 404
        And the response contains no active token info
