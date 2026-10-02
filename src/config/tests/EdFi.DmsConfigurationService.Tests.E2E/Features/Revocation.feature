@KeycloakRevocationObserver
Feature: Token revocation endpoint
    RFC 7009 token revocation at /connect/revoke (DMS-1327). Every error is an RFC 6749 §5.2
    JSON object. Token state is confirmed through the provider's validation path, never inferred
    from the revocation response: CMS introspection for the self-contained provider, Keycloak
    introspection by a dedicated observer client for keycloak, provisioned for this feature by the
    KeycloakRevocationObserver tag's hook and never used to authenticate a revocation.

        Background:
            Given valid credentials
              And token received

        Scenario: 01 A revocation request without a token is rejected as invalid_request
             When a revocation without a token is attempted with the current client's Basic credentials
             Then it should respond with 400
              And the response is the OAuth error "invalid_request" with description "The token parameter is missing."

        Scenario: 02 A revocation request mixing Basic and form client credentials is rejected
             When a revocation of the current token is attempted with Basic and form client credentials
             Then it should respond with 400
              And the response is the OAuth error "invalid_request" with description "Only one client authentication mechanism may be used."

        Scenario: 03 A revocation request without client credentials is rejected as invalid_client
             When a revocation of the current token is attempted with no client credentials
             Then it should respond with 400
              And the response is the OAuth error "invalid_client" with description "Client authentication is required."
              And the response header "WWW-Authenticate" is not present

        Scenario: 04 A wrong Basic client secret is rejected with a challenge and revokes nothing
             Then the current token is active at the identity provider
             When a revocation of the current token is attempted with a wrong Basic client secret
             Then it should respond with 401
              And the response is the OAuth error "invalid_client" with description "Invalid client or Invalid client credentials"
              And the response headers include
                  """
                  {
                    "WWW-Authenticate": "Basic realm=\"EdFi.DmsConfigurationService\""
                  }
                  """
              And the current token is active at the identity provider

        @MssqlRepresentative
        Scenario: 05 The owner revokes its token authenticating with HTTP Basic
             Then the current token is active at the identity provider
             When the current token is revoked
             Then it should respond with 200
              And the response body is empty
              And the current token is inactive at the identity provider

        @MssqlRepresentative
        Scenario: 06 The owner revokes its token authenticating with form credentials
             Then the current token is active at the identity provider
             When the current token is revoked using form credentials
             Then it should respond with 200
              And the response body is empty
              And the current token is inactive at the identity provider

        # Runs on both providers. The GET after the attempt is the self-contained provider's
        # protected-resource proof; Keycloak-issued tokens are validated locally until they
        # expire, so on keycloak the introspection that follows is the proof (D-16).
        @MssqlRepresentative
        Scenario: 07 Another client's revocation attempt answers 200 and leaves the token usable
             When a POST request is made to "/v3/vendors" with
                  """
                  {
                      "company": "Revocation Vendor",
                      "contactName": "Revocation",
                      "contactEmailAddress": "revocation@example.com",
                      "namespacePrefixes": "uri://ed-fi-e2e.org"
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Revocation Application",
                   "claimSetName": "TestClaim01",
                   "dataStoreIds": []
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/v3/apiClients" with
                  """
                  {
                   "applicationId": {applicationId},
                   "name": "Revocation Other Client",
                   "isApproved": true,
                   "dataStoreIds": []
                  }
                  """
             Then it should respond with 201
              And the response body credentials are captured as "other"
              And the current token is active at the identity provider
             When a revocation of the current token is attempted with the credentials captured as "other"
             Then it should respond with 200
              And the response body is empty
             When a GET request is made to "/v3/vendors"
             Then it should respond with 200
              And the current token is active at the identity provider

        # Public (secretless) clients exist only in Keycloak (D-11). Keycloak itself would accept
        # either request and revoke the token (§9.1, K-18 and K-19), so the token staying active
        # shows the Configuration Service rejected the request before delegating it.
        @KeycloakOnly
        Scenario: 08 A Keycloak public client presenting no secret is rejected and its token stays active
            Given a Keycloak public client holds a user-flow access token
             Then the public client's token is active at the identity provider
             When the public client attempts to revoke its token with no client secret
             Then it should respond with 400
              And the response is the OAuth error "invalid_client" with description "Client authentication is required."
              And the response header "WWW-Authenticate" is not present
              And the public client's token is active at the identity provider

        @KeycloakOnly
        Scenario: 09 A Keycloak public client presenting an arbitrary form secret is rejected and its token stays active
            Given a Keycloak public client holds a user-flow access token
             Then the public client's token is active at the identity provider
             When the public client attempts to revoke its token with an arbitrary form client secret
             Then it should respond with 400
              And the response is the OAuth error "invalid_client" with description "Invalid client or Invalid client credentials"
              And the response header "WWW-Authenticate" is not present
              And the public client's token is active at the identity provider

        @KeycloakOnly
        Scenario: 10 A Keycloak public client presenting an arbitrary Basic secret is rejected with a challenge and its token stays active
            Given a Keycloak public client holds a user-flow access token
             Then the public client's token is active at the identity provider
             When the public client attempts to revoke its token with an arbitrary Basic client secret
             Then it should respond with 401
              And the response is the OAuth error "invalid_client" with description "Invalid client or Invalid client credentials"
              And the response headers include
                  """
                  {
                    "WWW-Authenticate": "Basic realm=\"EdFi.DmsConfigurationService\""
                  }
                  """
              And the public client's token is active at the identity provider
