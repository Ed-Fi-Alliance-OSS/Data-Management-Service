Feature: OAuth Token Info Endpoint
    The /oauth/token_info endpoint provides comprehensive information about an
    OAuth bearer token, including client details, authorized resources, education
    organizations, and permissions. This endpoint helps API consumers understand
    their token's capabilities and scope.

    Rule: Token Info endpoint accepts valid tokens and returns comprehensive information

        @DMS-902
        Scenario: 01 Valid token returns active status with comprehensive information
            Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"
             When a POST request is made to "/oauth/token_info" with the current bearer token
             Then it should respond with 200
              And the response body is
                  """
                  {
                      "active": true
                  }
                  """
              And the token info response should contain "client_id"
              And the token info response should contain "namespace_prefixes"
              And the token info response should contain "claim_set"
              And the token info response should contain "resources"

        @DMS-902
        Scenario: 02 Token info returns education organizations when present
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901"
             When a POST request is made to "/oauth/token_info" with the current bearer token
             Then it should respond with 200
              And the token info response should contain "education_organizations"
              And the token info response should have at least 1 education organization

        @DMS-902
        Scenario: 03 Token info returns resources with correct endpoint paths
            Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"
             When a POST request is made to "/oauth/token_info" with the current bearer token
             Then it should respond with 200
              And the token info resources should use pluralized endpoint names
              And the token info resources should include operations

        @DMS-902
        Scenario: 04 Token info accepts form-encoded request body
            Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"
             When a POST request is made to "/oauth/token_info" with form-encoded token
             Then it should respond with 200
              And the response body is
                  """
                  {
                      "active": true
                  }
                  """

    Rule: Token Info endpoint rejects invalid or missing tokens

        @DMS-902
        Scenario: 05 Missing token returns error
             When a POST request is made to "/oauth/token_info" without a token
             Then it should respond with 400

        @DMS-902
        Scenario: 06 Invalid token returns inactive status
             When a POST request is made to "/oauth/token_info" with an invalid token
             Then it should respond with 200
              And the token is marked as inactive

        @DMS-902
        Scenario: 07 Malformed token returns inactive status
             When a POST request is made to "/oauth/token_info" with token "not-a-valid-jwt"
             Then it should respond with 200
              And the token is marked as inactive

    Rule: Token Info endpoint does not require authentication (introspection is self-contained)

        @DMS-902
        Scenario: 08 Token info endpoint accessible without Authorization header
             When a POST request is made to "/oauth/token_info" without authorization
             Then it should respond with 400
