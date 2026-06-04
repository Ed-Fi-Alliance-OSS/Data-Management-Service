@ResetClaimsetsAfterScenario
@reset-data-before-scenario
Feature: Security configuration ProblemDetails for relational authorization

    Rule: Relational authorization misconfiguration returns canonical ProblemDetails

        @relational-backend
        @relational-ci-shard-3
        @security-configuration
        Scenario: Empty matched authorization strategy list returns security configuration ProblemDetails
            Given a claim set is uploaded to CMS that grants "school" access to "E2E-SecurityConfigurationNoStrategiesClaimSet" with no authorization strategies
              And the claim set upload to CMS should be successful
            Given the claimSet "E2E-SecurityConfigurationNoStrategiesClaimSet" is authorized with educationOrganizationIds ""
             When a GET request is made to "/ed-fi/schools"
             Then it should respond with 500
              And the response headers include
                  """
                  {
                      "content-type": "application/problem+json"
                  }
                  """
              And the response body has a non-empty correlationId
              And the response body is
                  """
                  {
                      "type": "urn:ed-fi:api:system:configuration:security",
                      "title": "Security Configuration Error",
                      "status": 500,
                      "detail": "A security configuration problem was detected. The request cannot be authorized.",
                      "correlationId": null,
                      "validationErrors": {},
                      "errors": [
                          "No authorization strategies were defined for the requested action 'Read' against resource URIs ['http://ed-fi.org/identity/claims/ed-fi/school'] matched by the caller's claim 'http://ed-fi.org/identity/claims/ed-fi/school'."
                      ]
                  }
                  """

        @relational-backend
        @relational-ci-shard-3
        @security-configuration
        Scenario: Unknown authorization strategy returns security configuration ProblemDetails
            Given a claim set is uploaded to CMS that grants "school" access to "E2E-SecurityConfigurationUnknownStrategyClaimSet" using authorization strategy "SecurityConfigurationUnknownStrategy"
              And the claim set upload to CMS should be successful
            Given the claimSet "E2E-SecurityConfigurationUnknownStrategyClaimSet" is authorized with educationOrganizationIds ""
             When a GET request is made to "/ed-fi/schools"
             Then it should respond with 500
              And the response headers include
                  """
                  {
                      "content-type": "application/problem+json"
                  }
                  """
              And the response body has a non-empty correlationId
              And the response body is
                  """
                  {
                      "type": "urn:ed-fi:api:system:configuration:security",
                      "title": "Security Configuration Error",
                      "status": 500,
                      "detail": "A security configuration problem was detected. The request cannot be authorized.",
                      "correlationId": null,
                      "validationErrors": {},
                      "errors": [
                          "Could not find authorization strategy implementations for the following strategy names: 'SecurityConfigurationUnknownStrategy'."
                      ]
                  }
                  """

        @relational-backend
        @relational-ci-shard-3
        @security-configuration
        Scenario: Authenticated client whose claim set is missing from refreshed CMS metadata returns security configuration ProblemDetails
            Given a claim set is uploaded to CMS that grants "school" access to "E2E-SecurityConfigurationMissingMetadataClaimSet"
              And the claim set upload to CMS should be successful
            Given the claimSet "E2E-SecurityConfigurationMissingMetadataClaimSet" is authorized with educationOrganizationIds ""
            When a claim set is uploaded to CMS that grants "student" access to "E2E-SecurityConfigurationOtherClaimSet"
            Then the claim set upload to CMS should be successful
            When a POST request is made to DMS management endpoint "/management/reload-claimsets"
            Then the DMS claimsets reload should be successful
            When a GET request is made to "/ed-fi/schools"
            Then it should respond with 500
             And the response headers include
                 """
                 {
                     "content-type": "application/problem+json"
                 }
                 """
             And the response body has a non-empty correlationId
             And the response body is
                 """
                 {
                     "type": "urn:ed-fi:api:system:configuration:security",
                     "title": "Security Configuration Error",
                     "status": 500,
                     "detail": "A security configuration problem was detected. The request cannot be authorized.",
                     "correlationId": null,
                     "validationErrors": {},
                     "errors": [
                         "No security metadata has been configured for this resource."
                     ]
                 }
                 """
