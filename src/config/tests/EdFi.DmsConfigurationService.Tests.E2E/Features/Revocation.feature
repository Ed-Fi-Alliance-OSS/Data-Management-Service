Feature: Token revocation endpoint
    RFC 7009 token revocation at /connect/revoke (DMS-1327). Every error is an RFC 6749 §5.2
    JSON object. Token state is confirmed through the provider's validation path, never inferred
    from the revocation response: CMS introspection for the self-contained provider, Keycloak
    introspection by a dedicated observer client for keycloak (provisioned by DMS-1327 P3.2).

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

        Scenario: 05 The owner revokes its token authenticating with HTTP Basic
             Then the current token is active at the identity provider
             When the current token is revoked
             Then it should respond with 200
              And the response body is empty
              And the current token is inactive at the identity provider

        Scenario: 06 The owner revokes its token authenticating with form credentials
             Then the current token is active at the identity provider
             When the current token is revoked using form credentials
             Then it should respond with 200
              And the response body is empty
              And the current token is inactive at the identity provider

        # The Keycloak cross-client scenario observes the token through the Keycloak observer
        # client and is added with it in DMS-1327 P3.2.
        @SelfContainedOnly
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
