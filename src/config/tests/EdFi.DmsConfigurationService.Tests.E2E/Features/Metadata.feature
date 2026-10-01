Feature: Metadata endpoints

        Background:
            Given the system is ready for E2E testing

        @MssqlRepresentative
        Scenario: 01 Get service information with metadata URLs
             When a GET request is made to "/"
             Then it should respond with 200
              And the response body is
                  """
                  {
                      "version": "{*}",
                      "applicationName": "Ed-Fi API Configuration Service",
                      "informationalVersion": "{*}",
                      "build": "{*}",
                      "urls": {
                          "openApiMetadata": "{*}",
                          "tenancy": "{*}"
                      },
                      "specificationVersion": "{*}"
                  }
                  """
              And the response contains metadata URLs
              And the response body field "build" is non-empty and matches pattern "^\d+\.\d+\.\d+\.\d+$"

        Scenario: 02 Get OpenAPI specification
             When a GET request is made to "/metadata/specifications"
             Then it should respond with 200
              And the response should be valid JSON
              And the response body contains OpenAPI specification
                  | Field      | Value                               |
                  | openapi    | 3.1.1                               |
                  | info.title | Ed-Fi API Configuration Service API |

        Scenario: 03 Verify OpenAPI specification structure
             When a GET request is made to "/metadata/specifications"
             Then it should respond with 200
              And the OpenAPI specification should have required sections
                  | Section    |
                  | info       |
                  | paths      |
                  | components |
                  | servers    |
                  | tags       |
                  | security   |

        Scenario: 04 Verify OpenAPI components section
             When a GET request is made to "/metadata/specifications"
             Then it should respond with 200
              And the OpenAPI components should include
                  | Component       |
                  | schemas         |
                  | responses       |
                  | parameters      |
                  | securitySchemes |

        Scenario: 05 Verify metadata endpoints are documented
             When a GET request is made to "/metadata/specifications"
             Then it should respond with 200
              And the OpenAPI paths should include
                  | Path                   |
                  | /v3/vendors            |
                  | /v3/applications       |
                  | /v3/claimSets          |
                  | /v3/authorizationMetadata |

        Scenario: 06 Service information URLs should be accessible
            Given a GET request is made to "/"
             When the response URLs are extracted
             Then each metadata URL should be valid
                  | URL Field       |
                  | openApiMetadata |
                  | tenancy         |

        Scenario: 07 Verify the api client dataStoreIds contract is documented
             When a GET request is made to "/metadata/specifications"
             Then it should respond with 200
              And the OpenAPI specification field "components.schemas.ApiClientInsertCommand.properties.dataStoreIds.description" contains
                  | Text                     |
                  | Optional                 |
                  | empty array              |
                  | no Data Store assignment |
                  | null is rejected         |
                  | caller's tenant          |
              And the OpenAPI specification field "components.schemas.ApiClientUpdateCommand.properties.dataStoreIds.description" contains
                  | Text                              |
                  | full replacement                  |
                  | omitting the property             |
                  | removes every existing assignment |
                  | null is rejected                  |
                  | caller's tenant                   |
              And the response body contains OpenAPI specification
                  | Field                                                                                  | Value |
                  | paths./v3/apiClients.post.requestBody.content.application/json.example.dataStoreIds     | []    |
                  | paths./v3/apiClients/{id}.put.requestBody.content.application/json.example.dataStoreIds | []    |

        # Single-tenant only: on a multi-tenant stack the list holds every tenant, and Tenants.feature creates
        # tenants. An empty list alone does not mean multi-tenancy is off (DMS-1508 AC4).
        # The multi-tenant listing is covered by Tenants.feature.
        @DMS-1508
        Scenario: 08 Tenancy lists no tenants without authentication when multi-tenancy is disabled
             When an unauthenticated GET request is made to "/tenancy"
             Then it should respond with 200
              And the response body is
                  """
                  {
                      "tenants": []
                  }
                  """

        @DMS-1508
        Scenario: 09 Tenancy is documented as anonymous in the OpenAPI specification
             When a GET request is made to "/metadata/specifications"
             Then it should respond with 200
              And the response body contains OpenAPI specification
                  | Field                       | Value |
                  | paths./tenancy.get.security | []    |
