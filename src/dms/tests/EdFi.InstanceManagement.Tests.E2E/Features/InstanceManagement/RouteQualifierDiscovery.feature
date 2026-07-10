# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

@InstanceCleanup @instance-management-ci-shard-1
Feature: Route Qualifier Discovery API
    Verify that the discovery API properly reflects tenant and route qualifiers in URLs.
    When multi-tenancy is enabled, tenant is the first URL segment followed by route qualifiers.

    Background:
        Given the system is configured with route qualifiers
          And I am authenticated to the Configuration Service as system admin
          And tenant "Tenant_RouteQualifier" is set up with a vendor and instances:
              | Route       |
              | 255901/2024 |
              | 255901/2025 |

    Scenario: Discovery endpoint with tenant and full route qualifiers returns URLs with route context
         When a GET request is made to discovery endpoint with route "Tenant_RouteQualifier/255901/2024"
          And the oauth url from the discovery response is used to request a DMS token
         Then it should respond with 200
          And the DMS token should be available
          And the urls should be
              """
                {
                    "dependencies": "http://localhost:8080/Tenant_RouteQualifier/255901/2024/metadata/dependencies",
                    "openApiMetadata": "http://localhost:8080/Tenant_RouteQualifier/255901/2024/metadata/specifications",
                    "oauth": "http://localhost:8080/Tenant_RouteQualifier/255901/2024/oauth/token",
                    "tokenInfo": "http://localhost:8080/Tenant_RouteQualifier/255901/2024/oauth/token_info",
                    "dataManagementApi": "http://localhost:8080/Tenant_RouteQualifier/255901/2024/data",
                    "changeQueries": "http://localhost:8080/Tenant_RouteQualifier/255901/2024/changeQueries/v1/",
                    "xsdMetadata": "http://localhost:8080/Tenant_RouteQualifier/255901/2024/metadata/xsd"
                }
              """

    Scenario: Discovery endpoint with tenant and partial route qualifier returns URLs with mixed context
         When a GET request is made to discovery endpoint with route "Tenant_RouteQualifier/255901"
         Then it should respond with 200
          And the urls should be
              """
                {
                    "dependencies": "http://localhost:8080/Tenant_RouteQualifier/255901/{schoolYear}/metadata/dependencies",
                    "openApiMetadata": "http://localhost:8080/Tenant_RouteQualifier/255901/{schoolYear}/metadata/specifications",
                    "oauth": "http://localhost:8080/Tenant_RouteQualifier/255901/{schoolYear}/oauth/token",
                    "tokenInfo": "http://localhost:8080/Tenant_RouteQualifier/255901/{schoolYear}/oauth/token_info",
                    "dataManagementApi": "http://localhost:8080/Tenant_RouteQualifier/255901/{schoolYear}/data",
                    "changeQueries": "http://localhost:8080/Tenant_RouteQualifier/255901/{schoolYear}/changeQueries/v1/",
                    "xsdMetadata": "http://localhost:8080/Tenant_RouteQualifier/255901/{schoolYear}/metadata/xsd"
                }
              """

    Scenario: Discovery endpoint with tenant only returns URLs with route qualifier placeholders
         When a GET request is made to discovery endpoint with route "Tenant_RouteQualifier"
         Then it should respond with 200
          And the urls should be
              """
                {
                    "dependencies": "http://localhost:8080/Tenant_RouteQualifier/{districtId}/{schoolYear}/metadata/dependencies",
                    "openApiMetadata": "http://localhost:8080/Tenant_RouteQualifier/{districtId}/{schoolYear}/metadata/specifications",
                    "oauth": "http://localhost:8080/Tenant_RouteQualifier/{districtId}/{schoolYear}/oauth/token",
                    "tokenInfo": "http://localhost:8080/Tenant_RouteQualifier/{districtId}/{schoolYear}/oauth/token_info",
                    "dataManagementApi": "http://localhost:8080/Tenant_RouteQualifier/{districtId}/{schoolYear}/data",
                    "changeQueries": "http://localhost:8080/Tenant_RouteQualifier/{districtId}/{schoolYear}/changeQueries/v1/",
                    "xsdMetadata": "http://localhost:8080/Tenant_RouteQualifier/{districtId}/{schoolYear}/metadata/xsd"
                }
              """

    Scenario: Discovery endpoint at root returns URLs with all placeholders
         When a GET request is made to discovery endpoint with route ""
         Then it should respond with 200
          And the urls should be
              """
                {
                    "dependencies": "http://localhost:8080/{tenant}/{districtId}/{schoolYear}/metadata/dependencies",
                    "openApiMetadata": "http://localhost:8080/{tenant}/{districtId}/{schoolYear}/metadata/specifications",
                    "oauth": "http://localhost:8080/{tenant}/{districtId}/{schoolYear}/oauth/token",
                    "tokenInfo": "http://localhost:8080/{tenant}/{districtId}/{schoolYear}/oauth/token_info",
                    "dataManagementApi": "http://localhost:8080/{tenant}/{districtId}/{schoolYear}/data",
                    "changeQueries": "http://localhost:8080/{tenant}/{districtId}/{schoolYear}/changeQueries/v1/",
                    "xsdMetadata": "http://localhost:8080/{tenant}/{districtId}/{schoolYear}/metadata/xsd"
                }
              """
