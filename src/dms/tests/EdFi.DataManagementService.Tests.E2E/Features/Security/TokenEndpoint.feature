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
    Scenario Outline: 39 Requests with neither body credential nonblank are rejected
      When a token request is made with content type "<contentType>" and body
        """
        <body>
        """
      Then the token error is 401 "invalid_client" with description "Client credentials must be supplied in an Authorization: Basic header or as client_id and client_secret in the request body"

      Examples:
        | contentType                       | body                                                                          |
        | application/json                  | {"grant_type":"client_credentials"}                                           |
        | application/json                  | {"grant_type":"client_credentials","client_id":null,"client_secret":null}     |
        | application/json                  | {"grant_type":"client_credentials","client_id":"","client_secret":""}         |
        | application/json                  | {"grant_type":"client_credentials","client_id":"  ","client_secret":"  "}     |
        | application/x-www-form-urlencoded | grant_type=client_credentials&client_id=&client_secret=                        |
        | application/x-www-form-urlencoded | grant_type=client_credentials&client_id=%20%20&client_secret=%20%20            |

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

  Rule: Missing and unsupported grant types have distinct OAuth errors

    @DMS-1606
    @e2e-ci-shard-1
    Scenario Outline: 38 Grant validation precedes malformed Basic header parsing
      When a token request is made with Authorization header "Basicxyz:secret", content type "application/json" and body
        """
        <body>
        """
      Then the token error is 400 "<error>" with description "<description>"

      Examples:
        | body                      | error                  | description                                        |
        | {"grant_type":"password"} | unsupported_grant_type | Unsupported grant type                             |
        | {}                        | invalid_request        | The grant_type request parameter must be supplied  |
        | {"grant_type":null}       | invalid_request        | The grant_type request parameter must be supplied  |

    @DMS-1604
    @DMS-1606
    @e2e-ci-shard-1
    Scenario: 11 A JSON body with a null grant_type is rejected as a missing grant type
      When a token request is made with content type "application/json" and body
        """
        {
          "grant_type": null,
          "client_id": "{clientId}",
          "client_secret": "{clientSecret}"
        }
        """
      Then the token error is 400 "invalid_request" with description "The grant_type request parameter must be supplied"

    @DMS-1606
    @e2e-ci-shard-1
    Scenario: 33 A present unsupported grant type is rejected
      When a token request is made with content type "application/x-www-form-urlencoded" and body
        """
        grant_type=password&client_id={clientId}&client_secret={clientSecret}
        """
      Then the token error is 400 "unsupported_grant_type" with description "Unsupported grant type"

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
    Scenario: 34 Invalid form decoding is rejected with a safe request description
      When a token request is made with content type "application/x-www-form-urlencoded" and body
        """
        grant_type=client_credentials&client_id=%00&client_secret=withheld-form-secret
        """
      Then the token error is 400 "invalid_request" with description "The token request body must be valid form data"

    @DMS-1606
    @e2e-ci-shard-1
    Scenario Outline: 35 Repeated form parameters stay client errors across the parser count limit
      When a token request is made with <count> repeated grant_type form parameters
      Then the token error is 400 "invalid_request" with description "<description>"

      Examples:
        | count | description                                    |
        | 1024  | A request parameter was included more than once |
        | 1025  | The token request body must be valid form data  |

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
    Scenario: 36 An unsupported JSON charset is rejected as a request error
      When a token request is made with content type "application/json; charset=dms-unknown" and body
        """
        {"grant_type":"client_credentials","client_id":"{clientId}","client_secret":"{clientSecret}"}
        """
      Then the token error is 400 "invalid_request" with description "The token request body declares an unsupported JSON charset"

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
    Scenario: 17 A malformed Basic-like Authorization value is rejected safely
      When a token request is made with Authorization header "Basicxyz:secret", content type "application/x-www-form-urlencoded" and body
        """
        grant_type=client_credentials
        """
      Then the token error is 400 "invalid_request" with description "Malformed Authorization header"

  Rule: Provider response failures preserve the OAuth token contract

    @DMS-1606 @TokenContractProxy
    Scenario: 18 A JSON provider 401 is normalized and challenged
      When a token request is made with fixture Basic client ID "dms1606-401-object"
      Then the token error is 401 "invalid_client" with description "Fixture rejected credentials"

    @DMS-1606 @TokenContractProxy
    Scenario: 19 An HTML provider 401 uses a safe OAuth fallback
      When a token request is made with fixture Basic client ID "dms1606-401-html"
      Then the token error is 401 "invalid_client" with description "The upstream identity service rejected the request credentials."

    @DMS-1606 @TokenContractProxy
    Scenario: 20 A provider invalid_scope response is preserved for body authentication
      When a token request is made with content type "application/x-www-form-urlencoded" and body
        """
        grant_type=client_credentials&client_id=dms1606-400-invalid-scope&client_secret=fixture-secret
        """
      Then the token error is 400 "invalid_scope" with description "Fixture rejected request"

    @DMS-1606 @TokenContractProxy
    Scenario: 21 A provider invalid_client response after Basic authentication becomes 401
      When a token request is made with fixture Basic client ID "dms1606-400-invalid-client"
      Then the token error is 401 "invalid_client" with description "Fixture rejected request"

    @DMS-1606 @TokenContractProxy
    Scenario: 22 A provider invalid_client response after body authentication remains 400
      When a token request is made with content type "application/x-www-form-urlencoded" and body
        """
        grant_type=client_credentials&client_id=dms1606-400-invalid-client&client_secret=fixture-secret
        """
      Then the token error is 400 "invalid_client" with description "Fixture rejected request"

    @DMS-1606 @TokenContractProxy
    Scenario: 23 An unknown provider 400 is safely classified as a gateway error
      When a token request is made with fixture Basic client ID "dms1606-400-unknown"
      Then the token error is 502 "server_error" with description "The upstream identity service did not return a usable response. Contact your system administrator with the X-Correlation-ID response header, decoded once using UTF-8 percent decoding, which identifies the corresponding server log entry."

    @DMS-1606 @TokenContractProxy
    Scenario Outline: 24 A canonical token limit from either provider contract retains its limit
      When a token request is made with fixture Basic client ID "<clientId>"
      Then the token error is 429 "temporarily_unavailable" with description "Too many access tokens have been requested (limit is 7). Access tokens should be reused until they expire."

      Examples:
        | clientId                 |
        | dms1606-429-legacy       |
        | dms1606-429-management   |

    @DMS-1606 @TokenContractProxy
    Scenario: 25 A generic provider 429 with hostile fields uses the safe fallback
      When a token request is made with fixture Basic client ID "dms1606-429-generic"
      Then the token error is 429 "temporarily_unavailable" with description "The number of allowed requests has been exceeded. Retry the request later."

    @DMS-1606 @TokenContractProxy
    Scenario: 26 A provider conflict requests a retry without disclosing its body
      When a token request is made with fixture Basic client ID "dms1606-409"
      Then the token error is 503 "temporarily_unavailable" with description "The service is temporarily unable to handle the request. Retry the request later."

    @DMS-1606 @TokenContractProxy
    Scenario: 27 A provider server error uses the safe gateway response
      When a token request is made with fixture Basic client ID "dms1606-500"
      Then the token error is 502 "server_error" with description "The upstream identity service did not return a usable response. Contact your system administrator with the X-Correlation-ID response header, decoded once using UTF-8 percent decoding, which identifies the corresponding server log entry."

    @DMS-1606 @TokenContractProxy
    Scenario: 28 A provider connection abort uses the safe gateway response
      When a token request is made with fixture Basic client ID "dms1606-abort"
      Then the token error is 502 "server_error" with description "The upstream identity service did not return a usable response. Contact your system administrator with the X-Correlation-ID response header, decoded once using UTF-8 percent decoding, which identifies the corresponding server log entry."

    @DMS-1606 @TokenContractProxy @TokenContractQualified
    Scenario: 29 Root token route stays available with qualified routes enabled
      When a token request is made with fixture Basic client ID "dms1606-401-object" at path "oauth/token"
      Then the token error is 401 "invalid_client" with description "Fixture rejected credentials"

    @DMS-1606 @TokenContractProxy @TokenContractQualified
    Scenario: 30 Qualified token route returns the same provider error contract
      When a token request is made with fixture Basic client ID "dms1606-401-object" at path "2025/oauth/token"
      Then the token error is 401 "invalid_client" with description "Fixture rejected credentials"

    @DMS-1606 @TokenContractProxy @TokenContractQualified
    Scenario: 32 Root and qualified error routes return equivalent OAuth responses
      When equivalent token errors are requested from the root and qualified routes using fixture client ID "dms1606-401-object"

    @DMS-1606 @TokenContractProxy @TokenContractQualified
    Scenario: 31 Qualified token route issues a real token
      When a token request is made with a Basic Authorization header at path "2025/oauth/token", content type "application/x-www-form-urlencoded" and body
        """
        grant_type=client_credentials
        """
      Then it should respond with 200
      And the token response carries an access token
