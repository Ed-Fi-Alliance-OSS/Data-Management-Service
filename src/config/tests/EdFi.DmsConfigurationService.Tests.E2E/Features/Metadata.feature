Feature: Metadata endpoints

        Background:
            Given the system is ready for E2E testing

        Scenario: 01 Get service information with metadata URLs
             When a GET request is made to "/"
             Then it should respond with 200
              And the response body is
                  """
                  {
                      "version": "{ignore}",
                      "applicationName": "Ed-Fi Alliance DMS Configuration Service",
                      "informationalVersion": "{ignore}",
                      "urls": {
                          "openApiMetadata": "{ignore}"
                      }
                  }
                  """
              And the response contains metadata URLs

        Scenario: 02 Get OpenAPI specification
             When a GET request is made to "/metadata/specifications"
             Then it should respond with 200
              And the response should be valid JSON
              And the response body contains OpenAPI specification
                  | Field      | Value                               |
                  | openapi    | 3.1.1                               |
                  | info.title | Ed-Fi DMS Configuration Service API |

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

        Scenario: 05 Verify OAuth2 security scheme in specification
             When a GET request is made to "/metadata/specifications"
             Then it should respond with 200
              And the OpenAPI specification should have OAuth2 security scheme
                  | Scope                | Description |
                  | admin                | {ignore}    |
                  | readOnly             | {ignore}    |
                  | authMetadataReadOnly | {ignore}    |

        Scenario: 06 Verify metadata endpoints are documented
             When a GET request is made to "/metadata/specifications"
             Then it should respond with 200
              And the OpenAPI paths should include
                  | Path                     |
                  | /                        |
                  | /metadata/specifications |
                  | /health                  |
                  | /authorizationMetadata   |

        Scenario: 07 Service information URLs should be accessible
            Given a GET request is made to "/"
             When the response URLs are extracted
             Then each metadata URL should be valid
                  | URL Field       |
                  | openApiMetadata |
