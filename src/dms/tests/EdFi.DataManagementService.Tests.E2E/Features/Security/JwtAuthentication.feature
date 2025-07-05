Feature: JWT Authentication
    The DMS supports JWT authentication for secure access to API endpoints.
    When JWT authentication is enabled, all data endpoints require a valid JWT token.
    The system validates tokens using OIDC metadata from the configured authority.

    Background:
        Given JWT authentication is enabled

    Rule: Data endpoints require valid JWT tokens when authentication is enabled

        Scenario: Accept data endpoint requests with valid JWT token
            Given the API client is authorized with a valid JWT token
             When a GET request is made to "/ed-fi/students"
             Then it should respond with 200

        Scenario: Reject data endpoint requests without authorization header
            Given there is no Authorization header
             When a GET request is made to "/ed-fi/students"
             Then it should respond with 401
              And the response should contain a WWW-Authenticate header

        Scenario: Reject data endpoint requests with expired JWT token
            Given the API client has an expired JWT token
             When a GET request is made to "/ed-fi/students"
             Then it should respond with 401

        Scenario: Reject data endpoint requests with invalid JWT signature
            Given the API client has a JWT token with invalid signature
             When a GET request is made to "/ed-fi/students"
             Then it should respond with 401

        Scenario: Reject data endpoint requests with non-Bearer authorization
            Given the Authorization header is "Basic dXNlcjpwYXNzd29yZA=="
             When a GET request is made to "/ed-fi/students"
             Then it should respond with 401

        Scenario: Accept POST requests with valid JWT token
            Given the API client is authorized with a valid JWT token
             When a POST request is made to "/ed-fi/gradeLevelDescriptors" with
                  """
                  {
                    "namespace": "uri://ed-fi.org/GradeLevelDescriptor",
                    "codeValue": "First Grade",
                    "shortDescription": "First Grade"
                  }
                  """
             Then it should respond with 201

    Rule: JWT tokens extract client authorizations for data access control

        Scenario: JWT token with namespace restrictions limits data access
            Given the API client has a JWT token with namespace prefix "uri://ed-fi.org"
             When a POST request is made to "/ed-fi/academicWeekDescriptors" with
                  """
                  {
                    "namespace": "uri://disallowed.org/AcademicWeekDescriptor",
                    "codeValue": "Week 1",
                    "shortDescription": "First Week"
                  }
                  """
             Then it should respond with 403

        Scenario: JWT token with education organization restrictions limits data access
            Given the API client has a JWT token restricted to education organization 123
             When a GET request is made to "/ed-fi/schools?schoolId=999"
             Then it should respond with 200
              And the response body should be an empty array

    Rule: Discovery endpoints remain accessible without authentication

        Scenario: Accept root endpoint requests without JWT token
            Given there is no Authorization header
             When a GET request is made to "/"
             Then it should respond with 200

        Scenario: Accept metadata endpoint requests without JWT token
            Given there is no Authorization header
             When a GET request is made to "/metadata"
             Then it should respond with 200

    Rule: Token endpoint accepts Basic authentication for backward compatibility

        Scenario: Accept token endpoint requests with Basic authentication
            Given the Authorization header is Basic authentication for a valid client
             When a POST request is made to "/oauth/token" with
                  """
                  grant_type=client_credentials
                  """
             Then it should respond with 200
              And the response should contain an access_token

    Rule: JWT role-based authentication controls access to metadata endpoints

        @RequiresServiceRole
        Scenario: Accept metadata requests with service role JWT token
            Given the API client has a JWT token with "service" role
             When a GET request is made to "/metadata/specifications"
             Then it should respond with 200

        @RequiresServiceRole
        Scenario: Reject metadata requests without service role
            Given the API client has a JWT token without "service" role
             When a GET request is made to "/metadata/specifications"
             Then it should respond with 403

    Rule: JWT authentication can be disabled via configuration

        @DisableJWT
        Scenario: Accept requests without tokens when JWT is disabled
            Given JWT authentication is disabled
              And there is no Authorization header
             When a GET request is made to "/ed-fi/students"
             Then it should respond with 200

    Rule: Client-specific JWT rollout allows gradual migration

        @ClientSpecificRollout
        Scenario: Enforce JWT for enabled clients only
            Given JWT authentication is enabled for client "pilot-client"
              And the API client "pilot-client" has a valid JWT token
             When a GET request is made to "/ed-fi/students"
             Then it should respond with 200

        @ClientSpecificRollout
        Scenario: Skip JWT validation for non-enabled clients
            Given JWT authentication is enabled for client "pilot-client"
              And the API client "other-client" uses traditional authentication
             When a GET request is made to "/ed-fi/students"
             Then it should respond with 200