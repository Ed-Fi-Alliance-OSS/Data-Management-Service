Feature: JWT Token Introspection
    Other than the Discovery endpoint, all HTTP requests against DMS endpoints
    require a JSON Web Token (JWT) for authorizating the request. The DMS will
    perform self-inspection to confirm the validity of the token, rather than
    querying the OAuth provider.

    Rule: Discovery API does not require an authorization token
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
