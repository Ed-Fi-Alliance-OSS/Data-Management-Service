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
                          "dmsInstanceIds": [{dmsInstanceId}],
                          "createdAt": "{*}",
                          "createdBy": "DmsConfigurationService",
                          "lastModifiedAt": null,
                          "modifiedBy": null
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
                    "dmsInstanceIds": [{dmsInstanceId}],
                    "createdAt": "{*}",
                    "createdBy": "DmsConfigurationService",
                    "lastModifiedAt": null,
                    "modifiedBy": null
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

        Scenario: 08 Ensure clients can PUT to update an apiClient successfully
            Given a POST request is made to "/v2/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 08",
                   "claimSetName": "TestClaim01",
                   "dmsInstanceIds": [{dmsInstanceId}]
                  }
                  """
              And a POST request is made to "/v2/dmsInstances" with
                  """
                    {
                        "instanceType": "Test",
                        "instanceName": "Test DMS Instance 3",
                        "connectionString": "Server=test3;Database=TestDb3;"
                    }
                  """
              And a POST request is made to "/v2/apiClients" with
                  """
                  {
                   "applicationId": {applicationId},
                   "name": "Original Client Name",
                   "isApproved": false,
                   "dmsInstanceIds": [{dmsInstanceId}]
                  }
                  """
             When a PUT request is made to "/v2/apiClients/{apiClientId}" with
                  """
                  {
                   "id": {apiClientId},
                   "applicationId": {applicationId},
                   "name": "Updated Client Name",
                   "isApproved": true,
                   "dmsInstanceIds": [{dmsInstanceId}]
                  }
                  """
             Then it should respond with 204

        Scenario: 09 Verify updated apiClient has correct values
            Given a POST request is made to "/v2/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 09",
                   "claimSetName": "TestClaim01",
                   "dmsInstanceIds": [{dmsInstanceId}]
                  }
                  """
              And a POST request is made to "/v2/dmsInstances" with
                  """
                    {
                        "instanceType": "Test",
                        "instanceName": "Test DMS Instance 4",
                        "connectionString": "Server=test4;Database=TestDb4;"
                    }
                  """
              And a POST request is made to "/v2/apiClients" with
                  """
                  {
                   "applicationId": {applicationId},
                   "name": "Original Name",
                   "isApproved": false,
                   "dmsInstanceIds": [{dmsInstanceId}]
                  }
                  """
              And a PUT request is made to "/v2/apiClients/{apiClientId}" with
                  """
                  {
                   "id": {apiClientId},
                   "applicationId": {applicationId},
                   "name": "New Name After Update",
                   "isApproved": true,
                   "dmsInstanceIds": [{dmsInstanceId}]
                  }
                  """
             When a GET request is made to "/v2/apiClients/{clientId}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                    "id": {apiClientId},
                    "applicationId": {applicationId},
                    "clientId": "{clientId}",
                    "clientUuid": "{clientUuid}",
                    "name": "New Name After Update",
                    "isApproved": true,
                    "dmsInstanceIds": [{dmsInstanceId}],
                    "createdAt": "{*}",
                    "createdBy": "DmsConfigurationService",
                    "lastModifiedAt": null,
                    "modifiedBy": null
                  }
                  """

        Scenario: 10 Verify error handling when updating non-existent apiClient
            Given a POST request is made to "/v2/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 10",
                   "claimSetName": "TestClaim01",
                   "dmsInstanceIds": [{dmsInstanceId}]
                  }
                  """
             When a PUT request is made to "/v2/apiClients/99999" with
                  """
                  {
                   "id": 99999,
                   "applicationId": {applicationId},
                   "name": "Test Client",
                   "isApproved": true,
                   "dmsInstanceIds": [{dmsInstanceId}]
                  }
                  """
             Then it should respond with 404

        Scenario: 11 Verify error handling when updating apiClient with non-existent application
            Given a POST request is made to "/v2/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 11",
                   "claimSetName": "TestClaim01",
                   "dmsInstanceIds": [{dmsInstanceId}]
                  }
                  """
              And a POST request is made to "/v2/apiClients" with
                  """
                  {
                   "applicationId": {applicationId},
                   "name": "Test Client 11",
                   "isApproved": true,
                   "dmsInstanceIds": [{dmsInstanceId}]
                  }
                  """
             When a PUT request is made to "/v2/apiClients/{apiClientId}" with
                  """
                  {
                   "id": {apiClientId},
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

        Scenario: 12 Verify error handling when updating apiClient with non-existent DmsInstanceIds
            Given a POST request is made to "/v2/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 12",
                   "claimSetName": "TestClaim01",
                   "dmsInstanceIds": [{dmsInstanceId}]
                  }
                  """
              And a POST request is made to "/v2/apiClients" with
                  """
                  {
                   "applicationId": {applicationId},
                   "name": "Test Client 12",
                   "isApproved": true,
                   "dmsInstanceIds": [{dmsInstanceId}]
                  }
                  """
             When a PUT request is made to "/v2/apiClients/{apiClientId}" with
                  """
                  {
                   "id": {apiClientId},
                   "applicationId": {applicationId},
                   "name": "Test Client",
                   "isApproved": true,
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

        Scenario: 13 Verify error handling when updating apiClient with empty DmsInstanceIds
            Given a POST request is made to "/v2/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 13",
                   "claimSetName": "TestClaim01",
                   "dmsInstanceIds": [{dmsInstanceId}]
                  }
                  """
              And a POST request is made to "/v2/apiClients" with
                  """
                  {
                   "applicationId": {applicationId},
                   "name": "Test Client 13",
                   "isApproved": true,
                   "dmsInstanceIds": [{dmsInstanceId}]
                  }
                  """
             When a PUT request is made to "/v2/apiClients/{apiClientId}" with
                  """
                  {
                   "id": {apiClientId},
                   "applicationId": {applicationId},
                   "name": "Test Client",
                   "isApproved": false,
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

        Scenario: 14 Ensure clients can DELETE an apiClient successfully
            Given a POST request is made to "/v2/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 14",
                   "claimSetName": "TestClaim01",
                   "dmsInstanceIds": [{dmsInstanceId}]
                  }
                  """
              And a POST request is made to "/v2/apiClients" with
                  """
                  {
                   "applicationId": {applicationId},
                   "name": "Client To Delete",
                   "isApproved": true,
                   "dmsInstanceIds": [{dmsInstanceId}]
                  }
                  """
             When a DELETE request is made to "/v2/apiClients/{apiClientId}"
             Then it should respond with 204

        Scenario: 15 Verify deleted apiClient no longer exists
            Given a POST request is made to "/v2/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 15",
                   "claimSetName": "TestClaim01",
                   "dmsInstanceIds": [{dmsInstanceId}]
                  }
                  """
              And a POST request is made to "/v2/apiClients" with
                  """
                  {
                   "applicationId": {applicationId},
                   "name": "Client To Delete 2",
                   "isApproved": true,
                   "dmsInstanceIds": [{dmsInstanceId}]
                  }
                  """
              And a DELETE request is made to "/v2/apiClients/{apiClientId}"
             When a GET request is made to "/v2/apiClients/{clientId}"
             Then it should respond with 404

        Scenario: 16 Verify error handling when deleting non-existent apiClient
            Given a POST request is made to "/v2/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 16",
                   "claimSetName": "TestClaim01",
                   "dmsInstanceIds": [{dmsInstanceId}]
                  }
                  """
             When a DELETE request is made to "/v2/apiClients/99999"
             Then it should respond with 404

        Scenario: 17 Ensure clients can reset credentials for an apiClient
            Given a POST request is made to "/v2/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 17",
                   "claimSetName": "TestClaim01",
                   "dmsInstanceIds": [{dmsInstanceId}]
                  }
                  """
              And a POST request is made to "/v2/apiClients" with
                  """
                  {
                   "applicationId": {applicationId},
                   "name": "Test Client for Reset",
                   "isApproved": true,
                   "dmsInstanceIds": [{dmsInstanceId}]
                  }
                  """
             When a PUT request is made to "/v2/apiClients/{apiClientId}/reset-credential" with
                  """
                  {}
                  """
             Then it should respond with 200
              And the response body has key and secret
              And the response body is
                  """
                  {
                    "id": {apiClientId},
                    "key": "{key}",
                    "secret": "{secret}"
                  }
                  """

        Scenario: 18 Verify error handling when resetting credentials for non-existent apiClient
            Given a POST request is made to "/v2/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 18",
                   "claimSetName": "TestClaim01",
                   "dmsInstanceIds": [{dmsInstanceId}]
                  }
                  """
             When a PUT request is made to "/v2/apiClients/99999/reset-credential" with
                  """
                  {}
                  """
             Then it should respond with 404
