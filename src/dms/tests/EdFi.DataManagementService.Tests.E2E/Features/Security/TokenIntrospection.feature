Feature: JWT Token Introspection
    Other than the Discovery endpoint, all HTTP requests against DMS endpoints
    require a JSON Web Token (JWT) for authorizating the request. The DMS will
    perform self-inspection to confirm the validity of the token, rather than
    querying the OAuth provider.

    Rule: Discovery API does not require an authorization token
            Given there is no Authorization header

        @API-213
        Scenario: Accept root endpoint requests that do not contain a token
             When a GET request is made to "/"
             Then it should respond with 200

        @API-214
        Scenario: Accept dependencies endpoint requests that do not contain a token
             When a GET request is made to "/metadata/dependencies"
             Then it should respond with 200

        @API-215
        Scenario: Accept OpenAPI specifications endpoint requests that do not contain a token
             When a GET request is made to "/metadata/specifications"
             Then it should respond with 200

        @API-216
        Scenario: Accept XSD endpoint requests that do not contain a token
             When a GET request is made to "/metadata/xsd"
             Then it should respond with 200

    Rule: Resource API does not accept requests without a token
            Given there is no Authorization header
        @API-217
        Scenario: Reject a Resource endpoint GET request that does not contain a token
             When a GET request is made to "/ed-fi/academicWeeks"
             Then it should respond with 401

        @API-218
        Scenario: Reject a Resource endpoint POST request that does not contain a token
             When  a POST request is made to "/ed-fi/academicWeeks" with
                  """
                  {
                   "weekIdentifier": "WeekIdentifier1",
                   "schoolReference": {
                     "schoolId": 9999
                   },
                   "beginDate": "2023-09-11",
                   "endDate": "2023-09-11",
                   "totalInstructionalDays": 300
                  }
                  """
             Then it should respond with 401

        @API-219
        Scenario: Reject a Resource endpoint PUT request that does not contain a token
             When a PUT request is made to "/ed-fi/academicWeeks/1" with
                  """
                       {
                        "weekIdentifier": "WeekIdentifier1",
                        "schoolReference": {
                          "schoolId": 9999
                        },
                        "beginDate": "2023-09-11",
                        "endDate": "2023-09-11",
                        "totalInstructionalDays": 300
                       }
                  """
             Then it should respond with 401

        @API-220
        Scenario: Reject a Resource endpoint DELETE request that does not contain a token
             When a DELETE request is made to "/ed-fi/academicWeeks/1"
             Then it should respond with 401

    Rule: Resource API accepts a valid token

        @API-229
        Scenario: Accept a Resource endpoint GET request where the token is valid
            Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"
             When a GET request is made to "/ed-fi/academicWeeks"
             Then it should respond with 200

        Scenario: Reject a Resource endpoint GET request where the token signature is manipulated
            Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"
              And the token signature is manipulated
             When a GET request is made to "/ed-fi/academicWeeks"
             Then it should respond with 401
