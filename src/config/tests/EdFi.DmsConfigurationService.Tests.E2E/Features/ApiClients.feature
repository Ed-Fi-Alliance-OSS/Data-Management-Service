Feature: ApiClients endpoints

        Background:
            Given valid credentials
              And token received
              And a POST request is made to "/v2/vendors" with
                  """
                    {
                        "company": "Test Vendor",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "uri://ed-fi-e2e.org"
                    }
                  """
              And a POST request is made to "/v2/dmsInstances" with
                  """
                    {
                        "instanceType": "Test",
                        "instanceName": "Test DMS Instance",
                        "connectionString": "Server=test;Database=TestDb;"
                    }
                  """
              And a POST request is made to "/v2/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 01",
                   "claimSetName": "TestClaim01",
                   "dmsInstanceIds": [{dmsInstanceId}]
                  }
                  """

        Scenario: 01 Ensure clients can GET all apiClients
             When a GET request is made to "/v2/apiClients?offset=0&limit=25"
             Then it should respond with 200
              And the response body is
                  """
                      [{
                          "id": {id},
                          "applicationId": {applicationId},
                          "clientId": "{clientId}",
                          "clientUuid": "{clientUuid}",
                          "dmsInstanceIds": [{dmsInstanceId}]
                      }]
                  """

        Scenario: 02 Ensure clients can GET apiClient by clientId
            Given  a POST request is made to "/v2/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 02",
                   "claimSetName": "TestClaim01",
                   "dmsInstanceIds": [{dmsInstanceId}]
                  }
                  """
             When a GET request is made to "/v2/apiClients/{clientId}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                    "id": {id},
                    "applicationId": {applicationId},
                    "clientId": "{clientId}",
                    "clientUuid": "{clientUuid}",
                    "dmsInstanceIds": [{dmsInstanceId}]
                  }
                  """

        Scenario: 03 Verify error handling when trying to get a non-existent apiClient
             When a GET request is made to "/v2/apiClients/non-existent-client-id"
             Then it should respond with 404
