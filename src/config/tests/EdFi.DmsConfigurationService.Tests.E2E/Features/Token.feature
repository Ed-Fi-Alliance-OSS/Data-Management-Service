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
                      "error": "invalid_client",
                      "error_description": "Client authentication failed."
                    }
                  """

        @SelfContainedOnly
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
