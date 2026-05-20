Feature: Token validation

        Background:
            Given valid credentials
              And token received

        Scenario: 01 Ensure clients can create a vendor
             When a POST request is made to "/v2/vendors" with
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
             When a POST request is made to "/v2/vendors" with
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
             When a POST request is made to "/v2/vendors" with
                  """
                    {
                      "company": "V-{scenarioRunId}",
                      "contactName": "Test",
                      "contactEmailAddress": "test@gmail.com",
                      "namespacePrefixes": "uri://token-test-{scenarioRunId}.org"
                    }
                  """
             Then it should respond with 201
             When a POST request is made to "/v2/dmsInstances" with
                  """
                    {
                      "instanceType": "Test",
                      "instanceName": "I-{scenarioRunId}",
                      "connectionString": "Server=test;Database=TestDb;"
                    }
                  """
             Then it should respond with 201
             When a POST request is made to "/v2/applications" with
                  """
                    {
                      "vendorId": {vendorId},
                      "applicationName": "A-{scenarioRunId}",
                      "claimSetName": "ClaimSet03",
                      "educationOrganizationIds": [],
                      "dmsInstanceIds": [{dmsInstanceId}]
                    }
                  """
             Then it should respond with 201
             When a POST request is made to "/v2/apiClients" with
                  """
                    {
                      "applicationId": {applicationId},
                      "name": "DC-{scenarioRunId}",
                      "isApproved": false,
                      "dmsInstanceIds": [{dmsInstanceId}]
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
                      "type": "urn:ed-fi:api:security:authentication",
                      "title": "Authentication Failed",
                      "status": 401,
                      "validationErrors": {},
                      "errors": [
                        "invalid_client. Invalid client or invalid client credentials"
                      ]
                    }
                  """
