@KeycloakRevocationObserver
Feature: Token introspection endpoint
    RFC 7662 token introspection at /connect/introspect. The caller must authenticate as a client
    with HTTP Basic or form credentials; under keycloak the Configuration Service passes them to
    Keycloak's own introspection endpoint. A client introspecting its own token receives the token's
    claims; any other client receives only {"active":false}. Every error is an RFC 6749 §5.2 JSON
    object; a request Keycloak refuses as too large is answered as invalid_request. The
    KeycloakRevocationObserver tag provides the Keycloak public client of scenarios 09 and 10; it
    provisions nothing under the self-contained provider.

        Background:
            Given valid credentials
              And token received

        Scenario: 01 An introspection request without client credentials is rejected as invalid_client
             When an introspection of the current token is attempted with no client credentials
             Then it should respond with 400
              And the response is the OAuth error "invalid_client" with description "Client authentication is required."
              And the response header "WWW-Authenticate" is not present

        Scenario: 02 A wrong Basic client secret is rejected with a challenge
             When an introspection of the current token is attempted with a wrong Basic client secret
             Then it should respond with 401
              And the response is the OAuth error "invalid_client" with description "Invalid client or Invalid client credentials"
              And the response headers include
                  """
                  {
                    "WWW-Authenticate": "Basic realm=\"EdFi.DmsConfigurationService\""
                  }
                  """

        Scenario: 03 A Bearer authorization header alone is not client authentication
             When an introspection of the current token is attempted with only a Bearer authorization header
             Then it should respond with 400
              And the response is the OAuth error "invalid_client" with description "Client authentication is required."

        Scenario: 04 An introspection request mixing Basic and form client credentials is rejected
             When an introspection of the current token is attempted with Basic and form client credentials
             Then it should respond with 400
              And the response is the OAuth error "invalid_request" with description "Only one client authentication mechanism may be used."

        Scenario: 05 An introspection request without a token is rejected as invalid_request
             When an introspection without a token is attempted with the current client's Basic credentials
             Then it should respond with 400
              And the response is the OAuth error "invalid_request" with description "The token parameter is missing."

        @MssqlRepresentative
        Scenario: 06 The owner introspects its token authenticating with HTTP Basic
             When the current token is introspected by its owner using Basic credentials
             Then it should respond with 200
              And the introspection response reports the current client as active with numeric exp and iat

        @MssqlRepresentative
        Scenario: 07 The owner introspects its token authenticating with form credentials
             When the current token is introspected by its owner using form credentials
             Then it should respond with 200
              And the introspection response reports the current client as active with numeric exp and iat

        @MssqlRepresentative
        Scenario: 08 Another client introspecting the token learns only that it is not active
             When a POST request is made to "/v3/vendors" with
                  """
                  {
                      "company": "Introspection Vendor",
                      "contactName": "Introspection",
                      "contactEmailAddress": "introspection@example.com",
                      "namespacePrefixes": "uri://ed-fi-e2e.org"
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Introspection Application",
                   "claimSetName": "TestClaim01",
                   "dataStoreIds": []
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/v3/apiClients" with
                  """
                  {
                   "applicationId": {applicationId},
                   "name": "Introspection Other Client",
                   "isApproved": true,
                   "dataStoreIds": []
                  }
                  """
             Then it should respond with 201
              And the response body credentials are captured as "other"
             When the current token is introspected with the credentials captured as "other"
             Then it should respond with 200
              And the response body is
                  """
                  {
                    "active": false
                  }
                  """

        @KeycloakOnly
        Scenario: 09 A Keycloak public client presenting an arbitrary form secret is rejected as invalid_client
            Given a Keycloak public client holds a user-flow access token
             When the public client attempts to introspect its token with an arbitrary form client secret
             Then it should respond with 400
              And the response is the OAuth error "invalid_client" with description "Invalid client or Invalid client credentials"
              And the response header "WWW-Authenticate" is not present

        @KeycloakOnly
        Scenario: 10 A Keycloak public client presenting an arbitrary Basic secret is rejected with a challenge
            Given a Keycloak public client holds a user-flow access token
             When the public client attempts to introspect its token with an arbitrary Basic client secret
             Then it should respond with 401
              And the response is the OAuth error "invalid_client" with description "Invalid client or Invalid client credentials"
              And the response headers include
                  """
                  {
                    "WWW-Authenticate": "Basic realm=\"EdFi.DmsConfigurationService\""
                  }
                  """

        # The captured client is the current client once it obtains the current token, so the owner
        # steps that follow authenticate as it. From Keycloak 26.6.2 this answers active only because
        # the Configuration Service puts each client it creates into its own tokens' aud.
        Scenario: 11 A client created through the Configuration Service introspects its own token
             When a POST request is made to "/v3/vendors" with
                  """
                  {
                      "company": "Introspection Owner Vendor {scenarioRunId}",
                      "contactName": "Introspection Owner",
                      "contactEmailAddress": "introspection-owner@example.com",
                      "namespacePrefixes": "uri://ed-fi-e2e.org"
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Owner App {scenarioRunId}",
                   "claimSetName": "TestClaim01",
                   "dataStoreIds": []
                  }
                  """
             Then it should respond with 201
              And the response body credentials are captured as "owner"
             When the credentials captured as "owner" obtain the current token with scope "TestClaim01"
             Then it should respond with 200
             When the current token is introspected by its owner using Basic credentials
             Then it should respond with 200
              And the introspection response reports the current client as active with numeric exp and iat

        # Keycloak refuses any form value over 131072 characters with 413 before it authenticates the
        # caller; the Configuration Service answers that as the caller's own invalid request.
        @KeycloakOnly
        Scenario: 12 A token over Keycloak's form attribute limit is rejected as invalid_request
             When an introspection of a 131073-character token is attempted with the current client's Basic credentials
             Then it should respond with 400
              And the response is the OAuth error "invalid_request" with description "The identity provider rejected the introspection request."
              And the response header "WWW-Authenticate" is not present

        @KeycloakOnly
        Scenario: 13 A token at Keycloak's form attribute limit is answered as not active
             When an introspection of a 131072-character token is attempted with the current client's Basic credentials
             Then it should respond with 200
              And the response body is
                  """
                  {
                    "active": false
                  }
                  """
