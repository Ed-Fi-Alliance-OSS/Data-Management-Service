# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

@InstanceCleanup
Feature: Route Qualifier Discovery API
              Verify that the discovery API properly reflects route qualifiers in URLs

        Background:
            Given the system is configured with route qualifiers

        Scenario: Discovery endpoint with full route qualifiers returns URLs with route context
             When a GET request is made to discovery endpoint with route "255901/2024"
             Then it should respond with 200
              And the urls should be
                  """
                    {
                        "dependencies": "http://localhost:8080/255901/2024/metadata/dependencies",
                        "openApiMetadata": "http://localhost:8080/255901/2024/metadata/specifications",
                        "oauth": "http://dms-config-service:8081/connect/token/255901/2024",
                        "dataManagementApi": "http://localhost:8080/255901/2024/data",
                        "xsdMetadata": "http://localhost:8080/255901/2024/metadata/xsd"
                    }
                  """

        Scenario: Discovery endpoint with partial route qualifier returns URLs with mixed context
             When a GET request is made to discovery endpoint with route "255901"
             Then it should respond with 200
              And the urls should be
                  """
                    {
                        "dependencies": "http://localhost:8080/255901/{schoolYear}/metadata/dependencies",
                        "openApiMetadata": "http://localhost:8080/255901/{schoolYear}/metadata/specifications",
                        "oauth": "http://dms-config-service:8081/connect/token/255901/{schoolYear}",
                        "dataManagementApi": "http://localhost:8080/255901/{schoolYear}/data",
                        "xsdMetadata": "http://localhost:8080/255901/{schoolYear}/metadata/xsd"
                    }
                  """

        Scenario: Discovery endpoint without route qualifiers returns URLs with placeholders
             When a GET request is made to discovery endpoint with route ""
             Then it should respond with 200
              And the urls should be
                  """
                    {
                        "dependencies": "http://localhost:8080/{districtId}/{schoolYear}/metadata/dependencies",
                        "openApiMetadata": "http://localhost:8080/{districtId}/{schoolYear}/metadata/specifications",
                        "oauth": "http://dms-config-service:8081/connect/token/{districtId}/{schoolYear}",
                        "dataManagementApi": "http://localhost:8080/{districtId}/{schoolYear}/data",
                        "xsdMetadata": "http://localhost:8080/{districtId}/{schoolYear}/metadata/xsd"
                    }
                  """
