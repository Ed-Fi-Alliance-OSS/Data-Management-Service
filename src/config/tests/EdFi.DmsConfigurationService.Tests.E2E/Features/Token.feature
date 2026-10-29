Feature: Token validation

        Background:
            Given valid credentials
              And token received

        Scenario: 01 Ensure clients can create a vendor
             When a POST request is made to "/v3/vendors" with
                  """
                    {
                        "company": "Test 123",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "Test"
                    }
                  """
             Then it should respond with 201

        Scenario: 02 Ensure clients cannot create a vendor when the token signature is manipulated
            Given token signature manipulated
             When a POST request is made to "/v3/vendors" with
                  """
                    {
                        "company": "Test 456",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "Test"
                    }
                  """
             Then it should respond with 401

        Scenario: 03 Ensure a disabled API client cannot obtain a token
             When a POST request is made to "/v3/vendors" with
                  """
                    {
                      "company": "V-{scenarioRunId}",
                      "contactName": "Test",
                      "contactEmailAddress": "test@gmail.com",
                      "namespacePrefixes": "uri://token-test-{scenarioRunId}.org"
                    }
                  """
             Then it should respond with 201
             When a POST request is made to "/v3/dataStores" with
                  """
                    {
                      "dataStoreType": "Test",
                      "name": "I-{scenarioRunId}",
                      "connectionString": "Server=test;Database=TestDb;"
                    }
                  """
             Then it should respond with 201
             When a POST request is made to "/v3/applications" with
                  """
                    {
                      "vendorId": {vendorId},
                      "applicationName": "A-{scenarioRunId}",
                      "claimSetName": "ClaimSet03",
                      "educationOrganizationIds": [],
                      "dataStoreIds": [{dataStoreId}]
                    }
                  """
             Then it should respond with 201
             When a POST request is made to "/v3/apiClients" with
                  """
                    {
                      "applicationId": {applicationId},
                      "name": "DC-{scenarioRunId}",
                      "isApproved": false,
                      "dataStoreIds": [{dataStoreId}]
                    }
                  """
             Then it should respond with 201
              And retrieve created key and secret
             When a token request is attempted with the captured application credentials
             Then it should respond with 401
              And the response body is
                  """
                    {
                      "detail": "The request could not be processed. See 'errors' for details.",
                      "type": "urn:ed-fi:management-api:security:authentication",
                      "title": "Authentication Failed",
                      "status": 401,
                      "validationErrors": {},
                      "errors": [
                        "invalid_client. Invalid client or Invalid client credentials"
                      ]
                    }
                  """

        Scenario: 04 CMS-minted secret authenticates via raw HTTP Basic auth
             When a POST request is made to "/v3/vendors" with
                  """
                    {
                      "company": "V-basic-{scenarioRunId}",
                      "contactName": "Test",
                      "contactEmailAddress": "test@gmail.com",
                      "namespacePrefixes": "uri://basic-auth-test-{scenarioRunId}.org"
                    }
                  """
             Then it should respond with 201
             When a POST request is made to "/v3/dataStores" with
                  """
                    {
                      "dataStoreType": "Test",
                      "name": "DS-basic-{scenarioRunId}",
                      "connectionString": "Server=test;Database=TestDb;"
                    }
                  """
             Then it should respond with 201
             When a POST request is made to "/v3/applications" with
                  """
                    {
                      "vendorId": {vendorId},
                      "applicationName": "App-basic-{scenarioRunId}",
                      "claimSetName": "ClaimSet04Basic",
                      "educationOrganizationIds": [],
                      "dataStoreIds": [{dataStoreId}]
                    }
                  """
             Then it should respond with 201
              And retrieve created key and secret
             When a token is requested using raw HTTP Basic auth with the captured credentials
             Then it should respond with 200
              And the response body has a non-empty access_token

        # A client that authenticates but carries no role receives a token its API requests cannot
        # use, so the claim itself is the acceptance evidence. Both clients an application can
        # produce are checked: the one created with it, and one added afterwards.
        Scenario: 05 A newly created client's token carries the configured client role
             When a POST request is made to "/v3/vendors" with
                  """
                    {
                      "company": "V-role-{scenarioRunId}",
                      "contactName": "Test",
                      "contactEmailAddress": "test@gmail.com",
                      "namespacePrefixes": "uri://role-test-{scenarioRunId}.org"
                    }
                  """
             Then it should respond with 201
             When a POST request is made to "/v3/dataStores" with
                  """
                    {
                      "dataStoreType": "Test",
                      "name": "DS-role-{scenarioRunId}",
                      "connectionString": "Server=test;Database=TestDb;"
                    }
                  """
             Then it should respond with 201
             When a POST request is made to "/v3/applications" with
                  """
                    {
                      "vendorId": {vendorId},
                      "applicationName": "App-role-{scenarioRunId}",
                      "claimSetName": "ClaimSet05Role",
                      "educationOrganizationIds": [],
                      "dataStoreIds": [{dataStoreId}]
                    }
                  """
             Then it should respond with 201
              And the response body credentials are captured as "applicationClient"
             When a POST request is made to "/v3/apiClients" with
                  """
                    {
                      "applicationId": {applicationId},
                      "name": "Client-role-{scenarioRunId}",
                      "isApproved": true,
                      "dataStoreIds": [{dataStoreId}]
                    }
                  """
             Then it should respond with 201
              And the response body credentials are captured as "additionalClient"
             When a token is requested with the credentials captured as "applicationClient" and scope "ClaimSet05Role"
             Then it should respond with 200
              And the token carries the configured client role claim
             When a token is requested with the credentials captured as "additionalClient" and scope "ClaimSet05Role"
             Then it should respond with 200
              And the token carries the configured client role claim

        # Swagger UI sends the client credentials only in the Basic header and puts grant_type and
        # scope in the form. Both identity providers accept it.
        Scenario: 06 The system client authenticates via HTTP Basic auth with grant_type and scope in the form
             When a token is requested using HTTP Basic auth with the system credentials and scope "edfi_admin_api/full_access"
             Then it should respond with 200
              And the response body has a non-empty access_token

        # Keycloak rejects a scope the client does not hold; the identity provider's reason reaches
        # the caller as a 400 instead of a bad-gateway failure.
        @KeycloakOnly
        Scenario: 07 A scope the client does not hold is rejected as a bad request
            Given client "DmsConfigurationService" credentials with "edfi_admin_api/readonly_access" scope
             Then it should respond with 400
              And the response body is
                  """
                    {
                      "detail": "The request could not be processed. See 'errors' for details.",
                      "type": "urn:ed-fi:management-api:bad-request",
                      "title": "Bad Request",
                      "status": 400,
                      "validationErrors": {},
                      "errors": [
                        "invalid_scope. Invalid scopes: edfi_admin_api/readonly_access"
                      ]
                    }
                  """

        Scenario: 08 A token requested without a scope authorizes the system client
             When a token is requested without a scope using the system credentials
             Then it should respond with 200
              And the response body has a non-empty access_token
            Given token received
             When a GET request is made to "/v3/vendors"
             Then it should respond with 200

        # Keycloak 26.1.4 answers a form value over its limit with 413 without reading the rest of the
        # request and keeps the connection open, so the next request on that connection gets no answer;
        # the Configuration Service closes each connection to Keycloak after its answer.
        @KeycloakOnly
        Scenario: 09 A token request after one Keycloak refused as too large is answered as usual
             When a token is requested by the system client with a 400000-character client secret
             Then it should respond with 502
             When a token is requested without a scope using the system credentials
             Then it should respond with 200
              And the response body has a non-empty access_token
