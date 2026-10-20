Feature: Client credentials at the token endpoint
    POST /oauth/token accepts client credentials in an Authorization: Basic header
    or in the request body, either form-encoded or JSON, but never both at once. Token failures
    use the OAuth error response contract specified by DMS-1606.
    See reference/adr-oauth-token-client-credentials-DMS-1604.md.

  Background:
    Given a DMS client is provisioned with namespacePrefixes "uri://ed-fi.org"

  Rule: Each supported request type issues a token that authorizes a resource request

    @DMS-1604
    @DMS-1606
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
    @DMS-1606
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
    @DMS-1606
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
    @DMS-1606
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

    @DMS-1604
    @DMS-1606
    @e2e-ci-shard-1
    Scenario: 13 Empty body credential fields with a Basic Authorization header count as omitted
      When a token request is made with a Basic Authorization header, content type "application/x-www-form-urlencoded" and body
        """
        grant_type=client_credentials&client_id=&client_secret=
        """
      Then it should respond with 200
      And the token response carries an access token

  Rule: Token request authentication failures use the OAuth error contract

    @DMS-1604
    @DMS-1606
    @e2e-ci-shard-1
    Scenario: 05 A request with no client credentials is rejected
      When a token request is made with content type "application/x-www-form-urlencoded" and body
        """
        grant_type=client_credentials
        """
      Then the token error is 401 "invalid_client" with description "Client credentials must be supplied in an Authorization: Basic header or as client_id and client_secret in the request body"

    @DMS-1604
    @DMS-1606
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
      Then the token error is 400 "invalid_request" with description "Client credentials must be supplied in an Authorization: Basic header or as client_id and client_secret in the request body"

    @DMS-1604
    @DMS-1606
    @e2e-ci-shard-1
    Scenario: 07 A Bearer Authorization header is rejected even with credentials in the body
      When a token request is made with Authorization header "Bearer some-access-token", content type "application/x-www-form-urlencoded" and body
        """
        grant_type=client_credentials&client_id={clientId}&client_secret={clientSecret}
        """
      Then the token error is 401 "invalid_client" with description "The Authorization header must use the Basic scheme"

    @DMS-1604
    @DMS-1606
    @e2e-ci-shard-1
    Scenario: 12 An empty Authorization header is rejected even with credentials in the body
      When a token request is made with Authorization header "", content type "application/x-www-form-urlencoded" and body
        """
        grant_type=client_credentials&client_id={clientId}&client_secret={clientSecret}
        """
      Then the token error is 400 "invalid_request" with description "The Authorization header must not be empty"

  Rule: Requests with two mechanisms are rejected

    @DMS-1604
    @DMS-1606
    @e2e-ci-shard-1
    Scenario: 08 A form body with credentials and a Basic Authorization header is rejected
      When a token request is made with a Basic Authorization header, content type "application/x-www-form-urlencoded" and body
        """
        grant_type=client_credentials&client_id={clientId}&client_secret={clientSecret}
        """
      Then the token error is 400 "invalid_request" with description "Only one client authentication mechanism may be used"

    @DMS-1604
    @DMS-1606
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
      Then the token error is 400 "invalid_request" with description "Only one client authentication mechanism may be used"

  Rule: A wrong secret is rejected the same way wherever it was sent

    @DMS-1604
    @DMS-1606
    @e2e-ci-shard-1
    Scenario: 10 A wrong secret in the body gets the same 401 as a wrong secret in the Basic header
      When a token request is made with a Basic Authorization header carrying a wrong secret, content type "application/x-www-form-urlencoded" and body
        """
        grant_type=client_credentials
        """
      Then the token error is 401 "invalid_client" with provider-supplied description
      And the token response is stored as "wrong Basic secret"
      When a token request is made with content type "application/x-www-form-urlencoded" and body
        """
        grant_type=client_credentials&client_id={clientId}&client_secret=wrong-secret
        """
      Then the token error is 401 "invalid_client" with provider-supplied description
      And the token response body matches the one stored as "wrong Basic secret"

  Rule: A null grant type is unsupported, not a server error

    @DMS-1604
    @DMS-1606
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
      Then the token error is 400 "invalid_request" with description "The grant_type request parameter must be supplied"

  Rule: Token endpoint parsing and request limits keep OAuth error responses

    @DMS-1606
    @e2e-ci-shard-1
    Scenario: 14 Repeated form parameters are rejected before authentication
      When a token request is made with content type "application/x-www-form-urlencoded" and body
        """
        client_id=one&client_id=two&client_secret=secret&grant_type=client_credentials
        """
      Then the token error is 400 "invalid_request" with description "A request parameter was included more than once"

    @DMS-1606
    @e2e-ci-shard-1
    Scenario: 15 Malformed JSON is rejected with a safe request description
      When a token request is made with content type "application/json" and body
        """
        {"grant_type":
        """
      Then the token error is 400 "invalid_request" with description "The token request body must be a valid JSON object with string-valued fields"

    @DMS-1606
    @e2e-ci-shard-1
    Scenario: 16 A credential longer than 2048 characters is rejected
      When a token request is made with content type "application/x-www-form-urlencoded" and body
        """
        grant_type=client_credentials&client_id={oversizedClientId}&client_secret={clientSecret}
        """
      Then the token error is 400 "invalid_request" with description "client_id and client_secret must each be at most 2048 characters"

    @DMS-1606
    @e2e-ci-shard-1
    Scenario: 17 A malformed Basic value is rejected as invalid client authentication
      When a token request is made with Authorization header "Basic malformed", content type "application/x-www-form-urlencoded" and body
        """
        grant_type=client_credentials
        """
      Then the token error is 401 "invalid_client" with provider-supplied description
