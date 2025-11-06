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
                          "name": "Test Application 01",
                          "isApproved": true,
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
                    "name": "Test Application 02",
                    "isApproved": true,
                    "dmsInstanceIds": [{dmsInstanceId}]
                  }
                  """

        Scenario: 03 Verify error handling when trying to get a non-existent apiClient
             When a GET request is made to "/v2/apiClients/non-existent-client-id"
             Then it should respond with 404

        Scenario: 04 Ensure clients can POST a new apiClient successfully
            Given a POST request is made to "/v2/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 04",
                   "claimSetName": "TestClaim01",
                   "dmsInstanceIds": [{dmsInstanceId}]
                  }
                  """
              And a POST request is made to "/v2/dmsInstances" with
                  """
                    {
                        "instanceType": "Test",
                        "instanceName": "Test DMS Instance 2",
                        "connectionString": "Server=test2;Database=TestDb2;"
                    }
                  """
             When a POST request is made to "/v2/apiClients" with
                  """
                  {
                   "applicationId": {applicationId},
                   "name": "My Custom API Client",
                   "isApproved": true,
                   "dmsInstanceIds": [{dmsInstanceId}]
                  }
                  """
             Then it should respond with 201
              And the response body is
                  """
                  {
                    "id": {id},
                    "key": "{key}",
                    "secret": "{secret}"
                  }
                  """

        Scenario: 05 Verify error handling when posting apiClient with non-existent application
            Given a POST request is made to "/v2/dmsInstances" with
                  """
                    {
                        "instanceType": "Test",
                        "instanceName": "Test DMS Instance 2",
                        "connectionString": "Server=test2;Database=TestDb2;"
                    }
                  """
             When a POST request is made to "/v2/apiClients" with
                  """
                  {
                   "applicationId": 99999,
                   "name": "Test Client",
                   "isApproved": true,
                   "dmsInstanceIds": [{dmsInstanceId}]
                  }
                  """
             Then it should respond with 400
              And the response body is
                  """
                  {
                    "detail": "Data validation failed. See 'validationErrors' for details.",
                    "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                    "title": "Data Validation Failed",
                    "status": 400,
                    "validationErrors": {
                        "ApplicationId": [
                            "Application with ID 99999 not found."
                        ]
                    },
                    "errors": []
                  }
                  """

        Scenario: 06 Verify error handling when posting apiClient with non-existent DmsInstanceIds
            Given a POST request is made to "/v2/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 06",
                   "claimSetName": "TestClaim01",
                   "dmsInstanceIds": [{dmsInstanceId}]
                  }
                  """
             When a POST request is made to "/v2/apiClients" with
                  """
                  {
                   "applicationId": {applicationId},
                   "name": "Test Client",
                   "isApproved": false,
                   "dmsInstanceIds": [99999, 88888]
                  }
                  """
             Then it should respond with 400
              And the response body is
                  """
                  {
                    "detail": "Data validation failed. See 'validationErrors' for details.",
                    "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                    "title": "Data Validation Failed",
                    "status": 400,
                    "validationErrors": {
                        "DmsInstanceIds": [
                            "The following DmsInstanceIds were not found in database: 99999, 88888"
                        ]
                    },
                    "errors": []
                  }
                  """

        Scenario: 07 Ensure clients can not POST apiClient with empty DmsInstanceIds
            Given a POST request is made to "/v2/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 07",
                   "claimSetName": "TestClaim01",
                   "dmsInstanceIds": [{dmsInstanceId}]
                  }
                  """
             When a POST request is made to "/v2/apiClients" with
                  """
                  {
                   "applicationId": {applicationId},
                   "name": "Another API Client",
                   "isApproved": true,
                   "dmsInstanceIds": []
                  }
                  """
             Then it should respond with 400
              And the response body is
                  """
                  {
                    "detail": "Data validation failed. See 'validationErrors' for details.",
                    "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                    "title": "Data Validation Failed",
                    "status": 400,
                    "validationErrors": {
                        "DmsInstanceIds": [
                            "DmsInstanceIds cannot be empty. At least one DMS Instance is required."
                        ]
                    },
                    "errors": []
                  }
                  """
