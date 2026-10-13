Feature: Client credentials at the token endpoint
    POST /oauth/token accepts client credentials in an Authorization: Basic header
    or in the request body, either form-encoded or JSON, but never both at once.
    See reference/adr-oauth-token-client-credentials-DMS-1604.md.

  Background:
    Given a DMS client is provisioned with namespacePrefixes "uri://ed-fi.org"

  Rule: Each supported request type issues a token that authorizes a resource request

    @DMS-1604
    @e2e-ci-shard-1
    Scenario: 01 Form body with a Basic Authorization header
      When a token request is made with a Basic Authorization header, content type "application/x-www-form-urlencoded" and body
        """
        grant_type=client_credentials
        """
      Then it should respond with 200
      And the token response carries an access token
      When the access token from the token response is used for authorization
      And a GET request is made to "/ed-fi/academicWeeks"
      Then it should respond with 200

    @DMS-1604
    @e2e-ci-shard-1
    Scenario: 02 Form body carrying the client credentials
      When a token request is made with content type "application/x-www-form-urlencoded" and body
        """
        grant_type=client_credentials&client_id={clientId}&client_secret={clientSecret}
        """
      Then it should respond with 200
      And the token response carries an access token
      When the access token from the token response is used for authorization
      And a GET request is made to "/ed-fi/academicWeeks"
      Then it should respond with 200

    @DMS-1604
    @e2e-ci-shard-1
    Scenario: 03 JSON body carrying the client credentials
      When a token request is made with content type "application/json" and body
        """
        {
          "grant_type": "client_credentials",
          "client_id": "{clientId}",
          "client_secret": "{clientSecret}"
        }
        """
      Then it should respond with 200
      And the token response carries an access token
      When the access token from the token response is used for authorization
      And a GET request is made to "/ed-fi/academicWeeks"
      Then it should respond with 200

    @DMS-1604
    @e2e-ci-shard-1
    Scenario: 04 JSON body with a Basic Authorization header and no body credentials still works
      When a token request is made with a Basic Authorization header, content type "application/json" and body
        """
        {
          "grant_type": "client_credentials"
        }
        """
      Then it should respond with 200
      And the token response carries an access token

  Rule: Requests with missing, partial, or non-Basic credentials are rejected as the ODS/API rejects them

    @DMS-1604
    @e2e-ci-shard-1
    Scenario: 05 A request with no client credentials is rejected
      When a token request is made with content type "application/x-www-form-urlencoded" and body
        """
        grant_type=client_credentials
        """
      Then it should respond with 401
      And the response body is
        """
        {
          "detail": "Client credentials must be supplied in an Authorization: Basic header or as client_id and client_secret in the request body",
          "type": "urn:ed-fi:api:unauthorized",
          "title": "invalid_request",
          "status": 401,
          "validationErrors": {},
          "errors": []
        }
        """

    @DMS-1604
    @e2e-ci-shard-1
    Scenario: 06 A JSON body with a client_id and a null client_secret is rejected
      When a token request is made with content type "application/json" and body
        """
        {
          "grant_type": "client_credentials",
          "client_id": "{clientId}",
          "client_secret": null
        }
        """
      Then it should respond with 401
      And the response body is
        """
        {
          "detail": "Client credentials must be supplied in an Authorization: Basic header or as client_id and client_secret in the request body",
          "type": "urn:ed-fi:api:unauthorized",
          "title": "invalid_request",
          "status": 401,
          "validationErrors": {},
          "errors": []
        }
        """

    @DMS-1604
    @e2e-ci-shard-1
    Scenario: 07 A Bearer Authorization header is rejected even with credentials in the body
      When a token request is made with Authorization header "Bearer some-access-token", content type "application/x-www-form-urlencoded" and body
        """
        grant_type=client_credentials&client_id={clientId}&client_secret={clientSecret}
        """
      Then it should respond with 401
      And the response body is
        """
        {
          "detail": "The Authorization header must use the Basic scheme",
          "type": "urn:ed-fi:api:unauthorized",
          "title": "invalid_client",
          "status": 401,
          "validationErrors": {},
          "errors": []
        }
        """

  Rule: Requests with two mechanisms are rejected

    @DMS-1604
    @e2e-ci-shard-1
    Scenario: 08 A form body with credentials and a Basic Authorization header is rejected
      When a token request is made with a Basic Authorization header, content type "application/x-www-form-urlencoded" and body
        """
        grant_type=client_credentials&client_id={clientId}&client_secret={clientSecret}
        """
      Then it should respond with 400
      And the response body is
        """
        {
          "detail": "Only one client authentication mechanism may be used",
          "type": "urn:ed-fi:api:bad-request",
          "title": "Bad Request",
          "status": 400,
          "validationErrors": {},
          "errors": []
        }
        """

    @DMS-1604
    @e2e-ci-shard-1
    Scenario: 09 A JSON body with credentials and a Basic Authorization header is rejected
      When a token request is made with a Basic Authorization header, content type "application/json" and body
        """
        {
          "grant_type": "client_credentials",
          "client_id": "{clientId}",
          "client_secret": "{clientSecret}"
        }
        """
      Then it should respond with 400
      And the response body is
        """
        {
          "detail": "Only one client authentication mechanism may be used",
          "type": "urn:ed-fi:api:bad-request",
          "title": "Bad Request",
          "status": 400,
          "validationErrors": {},
          "errors": []
        }
        """

  Rule: A wrong secret is rejected the same way wherever it was sent

    @DMS-1604
    @e2e-ci-shard-1
    Scenario: 10 A wrong secret in the body gets the same 401 as a wrong secret in the Basic header
      When a token request is made with a Basic Authorization header carrying a wrong secret, content type "application/x-www-form-urlencoded" and body
        """
        grant_type=client_credentials
        """
      Then it should respond with 401
      And the token response is stored as "wrong Basic secret"
      When a token request is made with content type "application/x-www-form-urlencoded" and body
        """
        grant_type=client_credentials&client_id={clientId}&client_secret=wrong-secret
        """
      Then it should respond with 401
      And the token response body matches the one stored as "wrong Basic secret", apart from the correlationId

  Rule: A null grant type is unsupported, not a server error

    @DMS-1604
    @e2e-ci-shard-1
    Scenario: 11 A JSON body with a null grant_type is rejected as an unsupported grant type
      When a token request is made with content type "application/json" and body
        """
        {
          "grant_type": null,
          "client_id": "{clientId}",
          "client_secret": "{clientSecret}"
        }
        """
      Then it should respond with 400
      And the response body is
        """
        {
          "detail": "Unsupported grant type",
          "type": "urn:ed-fi:api:bad-request",
          "title": "Bad Request",
          "status": 400,
          "validationErrors": {},
          "errors": []
        }
        """
