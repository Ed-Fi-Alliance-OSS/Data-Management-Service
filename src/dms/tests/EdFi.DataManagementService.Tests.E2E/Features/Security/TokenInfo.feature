Feature: OAuth Token Info Endpoint
    The /oauth/token_info endpoint provides comprehensive information about an
    OAuth bearer token, including client details, authorized resources, education
    organizations, and permissions. This endpoint helps API consumers understand
    their token's capabilities and scope.

    Rule: Token Info endpoint accepts valid tokens and returns comprehensive information

        @DMS-902
        Scenario: 01 Valid token returns active status with comprehensive information
            Given the claimSet "EdFiSandbox" is authorized with namespacePrefixes "uri://ed-fi.org"
             When a POST request is made to "/oauth/token_info" with the current bearer token
             Then it should respond with 200
              And the token info response body is
                  """
                  {
                      "active": true
                  }
                  """
              And the token info response should contain "client_id"
              And the token info response should contain "namespace_prefixes"
              And the token info response should contain "claim_set"

        @DMS-902
        Scenario: 02 Token info returns education organizations when present
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901"
              And the system has these "schools"
                  | schoolId | nameOfInstitution | gradeLevels                                                                        | educationOrganizationCategories                                                                                    |
                  | 255901   | Test School       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School" }] |
             When a POST request is made to "/oauth/token_info" with the current bearer token
             Then it should respond with 200
              And the token info response should contain "education_organizations"
              And the token info response should have at least 1 education organization

        @DMS-902
        Scenario: 03 Token info accepts form-encoded request body
            Given the claimSet "EdFiSandbox" is authorized with namespacePrefixes "uri://ed-fi.org"
             When a POST request is made to "/oauth/token_info" with form-encoded token
             Then it should respond with 200
              And the token info response body is
                  """
                  {
                      "active": true
                  }
                  """

    Rule: Token Info endpoint rejects invalid or missing tokens

        @DMS-902
        Scenario: 04 Missing token in request body returns error
             Given the claimSet "EdFiSandbox" is authorized with namespacePrefixes "uri://ed-fi.org"
              When a POST request is made to "/oauth/token_info" with empty body but valid authorization header
             Then it should respond with 401

        @DMS-902
        Scenario: 05 Missing Authorization header returns error
             When a POST request is made to "/oauth/token_info" without authorization header
             Then it should respond with 401

        @DMS-902
        Scenario: 06 Token mismatch between Authorization header and body returns error
             Given the claimSet "EdFiSandbox" is authorized with namespacePrefixes "uri://ed-fi.org"
              When a POST request is made to "/oauth/token_info" with mismatched tokens
             Then it should respond with 401

        @DMS-902
        Scenario: 07 Invalid token format returns error
             When a POST request is made to "/oauth/token_info" with an invalid token
             Then it should respond with 401

