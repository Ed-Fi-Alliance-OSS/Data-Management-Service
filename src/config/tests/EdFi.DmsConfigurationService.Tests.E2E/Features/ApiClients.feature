Feature: ApiClients endpoints

        Background:
            Given valid credentials
              And token received
              And a POST request is made to "/v3/vendors" with
                  """
                    {
                        "company": "Test Vendor",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "uri://ed-fi-e2e.org"
                    }
                  """
              And a POST request is made to "/v3/dataStores" with
                  """
                    {
                        "dataStoreType": "Test",
                        "name": "Test Data Store",
                        "connectionString": "Server=test;Database=TestDb;"
                    }
                  """
              And a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 01",
                   "claimSetName": "TestClaim01",
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """

        Scenario: 01 Ensure clients can GET all apiClients
             When a GET request is made to "/v3/apiClients?offset=0&limit=25"
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
                          "creatorOwnershipTokenId": null,
                          "ownershipTokenIds": [],
                          "dataStoreIds": [{dataStoreId}]
                      }]
                  """

        Scenario: 02 Ensure clients can GET apiClient by clientId
            Given  a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 02",
                   "claimSetName": "TestClaim01",
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             When a GET request is made to "/v3/apiClients/{clientId}"
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
                    "creatorOwnershipTokenId": null,
                    "ownershipTokenIds": [],
                    "dataStoreIds": [{dataStoreId}]
                  }
                  """

        Scenario: 03 Verify error handling when trying to get a non-existent apiClient
             When a GET request is made to "/v3/apiClients/non-existent-client-id"
             Then it should respond with 404

        @MssqlRepresentative
        Scenario: 04 Ensure clients can POST a new apiClient successfully
            Given a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 04",
                   "claimSetName": "TestClaim01",
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
              And a POST request is made to "/v3/dataStores" with
                  """
                    {
                        "dataStoreType": "Test",
                        "name": "Test Data Store 2",
                        "connectionString": "Server=test2;Database=TestDb2;"
                    }
                  """
             When a POST request is made to "/v3/apiClients" with
                  """
                  {
                   "applicationId": {applicationId},
                   "name": "My Custom API Client",
                   "isApproved": true,
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             Then it should respond with 201
              And the response body is
                  """
                  {
                    "id": {apiClientId},
                    "applicationId": {applicationId},
                    "name": "My Custom API Client",
                    "key": "{key}",
                    "secret": "{secret}"
                  }
                  """

        Scenario: 05 Verify error handling when posting apiClient with non-existent application
            Given a POST request is made to "/v3/dataStores" with
                  """
                    {
                        "dataStoreType": "Test",
                        "name": "Test Data Store 2",
                        "connectionString": "Server=test2;Database=TestDb2;"
                    }
                  """
             When a POST request is made to "/v3/apiClients" with
                  """
                  {
                   "applicationId": 99999,
                   "name": "Test Client",
                   "isApproved": true,
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             Then it should respond with 409
              And the response body is
                  """
                  {
                    "detail": "Application with ID 99999 not found.",
                    "type": "urn:ed-fi:api:conflict:unresolved-reference",
                    "title": "Unresolved Reference",
                    "status": 409,
                    "validationErrors": {},
                    "errors": []
                  }
                  """

        Scenario: 06 Verify error handling when posting apiClient with non-existent DataStoreIds
            Given a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 06",
                   "claimSetName": "TestClaim01",
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             When a POST request is made to "/v3/apiClients" with
                  """
                  {
                   "applicationId": {applicationId},
                   "name": "Test Client",
                   "isApproved": false,
                   "dataStoreIds": [99999, 88888]
                  }
                  """
             Then it should respond with 409
              And the response body is
                  """
                  {
                    "detail": "The following DataStoreIds were not found in database: 99999, 88888",
                    "type": "urn:ed-fi:api:conflict:unresolved-reference",
                    "title": "Unresolved Reference",
                    "status": 409,
                    "validationErrors": {},
                    "errors": []
                  }
                  """

        Scenario: 07 Ensure clients can POST apiClient with empty DataStoreIds
            Given a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 07",
                   "claimSetName": "TestClaim01",
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             When a POST request is made to "/v3/apiClients" with
                  """
                  {
                   "applicationId": {applicationId},
                   "name": "Another API Client",
                   "isApproved": true,
                   "dataStoreIds": []
                  }
                  """
             Then it should respond with 201
              And the response body has key and secret
             When a GET request is made to "/v3/apiClients/{clientId}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                    "id": {id},
                    "applicationId": {applicationId},
                    "clientId": "{clientId}",
                    "clientUuid": "{clientUuid}",
                    "name": "Another API Client",
                    "isApproved": true,
                    "creatorOwnershipTokenId": null,
                    "ownershipTokenIds": [],
                    "dataStoreIds": []
                  }
                  """

        Scenario: 08 Ensure clients can PUT to update an apiClient successfully
            Given a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 08",
                   "claimSetName": "TestClaim01",
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
              And a POST request is made to "/v3/dataStores" with
                  """
                    {
                        "dataStoreType": "Test",
                        "name": "Test Data Store 3",
                        "connectionString": "Server=test3;Database=TestDb3;"
                    }
                  """
              And a POST request is made to "/v3/apiClients" with
                  """
                  {
                   "applicationId": {applicationId},
                   "name": "Original Client Name",
                   "isApproved": false,
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             When a PUT request is made to "/v3/apiClients/{apiClientId}" with
                  """
                  {
                   "id": {apiClientId},
                   "applicationId": {applicationId},
                   "name": "Updated Client Name",
                   "isApproved": true,
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             Then it should respond with 204

        Scenario: 09 Verify updated apiClient has correct values
            Given a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 09",
                   "claimSetName": "TestClaim01",
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
              And a POST request is made to "/v3/dataStores" with
                  """
                    {
                        "dataStoreType": "Test",
                        "name": "Test Data Store 4",
                        "connectionString": "Server=test4;Database=TestDb4;"
                    }
                  """
              And a POST request is made to "/v3/apiClients" with
                  """
                  {
                   "applicationId": {applicationId},
                   "name": "Original Name",
                   "isApproved": false,
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
              And a PUT request is made to "/v3/apiClients/{apiClientId}" with
                  """
                  {
                   "id": {apiClientId},
                   "applicationId": {applicationId},
                   "name": "New Name After Update",
                   "isApproved": true,
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             When a GET request is made to "/v3/apiClients/{clientId}"
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
                    "creatorOwnershipTokenId": null,
                    "ownershipTokenIds": [],
                    "dataStoreIds": [{dataStoreId}]
                  }
                  """

        Scenario: 10 Verify error handling when updating non-existent apiClient
            Given a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 10",
                   "claimSetName": "TestClaim01",
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             When a PUT request is made to "/v3/apiClients/99999" with
                  """
                  {
                   "id": 99999,
                   "applicationId": {applicationId},
                   "name": "Test Client",
                   "isApproved": true,
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             Then it should respond with 404

        Scenario: 11 Verify error handling when updating apiClient with non-existent application
            Given a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 11",
                   "claimSetName": "TestClaim01",
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
              And a POST request is made to "/v3/apiClients" with
                  """
                  {
                   "applicationId": {applicationId},
                   "name": "Test Client 11",
                   "isApproved": true,
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             When a PUT request is made to "/v3/apiClients/{apiClientId}" with
                  """
                  {
                   "id": {apiClientId},
                   "applicationId": 99999,
                   "name": "Test Client",
                   "isApproved": true,
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             Then it should respond with 409
              And the response body is
                  """
                  {
                    "detail": "Application with ID 99999 not found.",
                    "type": "urn:ed-fi:api:conflict:unresolved-reference",
                    "title": "Unresolved Reference",
                    "status": 409,
                    "validationErrors": {},
                    "errors": []
                  }
                  """

        Scenario: 12 Verify error handling when updating apiClient with non-existent DataStoreIds
            Given a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 12",
                   "claimSetName": "TestClaim01",
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
              And a POST request is made to "/v3/apiClients" with
                  """
                  {
                   "applicationId": {applicationId},
                   "name": "Test Client 12",
                   "isApproved": true,
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             When a PUT request is made to "/v3/apiClients/{apiClientId}" with
                  """
                  {
                   "id": {apiClientId},
                   "applicationId": {applicationId},
                   "name": "Test Client",
                   "isApproved": true,
                   "dataStoreIds": [99999, 88888]
                  }
                  """
             Then it should respond with 409
              And the response body is
                  """
                  {
                    "detail": "The following DataStoreIds were not found in database: 99999, 88888",
                    "type": "urn:ed-fi:api:conflict:unresolved-reference",
                    "title": "Unresolved Reference",
                    "status": 409,
                    "validationErrors": {},
                    "errors": []
                  }
                  """

        Scenario: 13 Ensure clients can PUT an apiClient clearing its last DataStoreIds
            Given a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 13",
                   "claimSetName": "TestClaim01",
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
              And a POST request is made to "/v3/apiClients" with
                  """
                  {
                   "applicationId": {applicationId},
                   "name": "Test Client 13",
                   "isApproved": true,
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             When a PUT request is made to "/v3/apiClients/{apiClientId}" with
                  """
                  {
                   "id": {apiClientId},
                   "applicationId": {applicationId},
                   "name": "Test Client",
                   "isApproved": false,
                   "dataStoreIds": []
                  }
                  """
             Then it should respond with 204
             When a GET request is made to "/v3/apiClients/{clientId}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                    "id": {apiClientId},
                    "applicationId": {applicationId},
                    "clientId": "{clientId}",
                    "clientUuid": "{clientUuid}",
                    "name": "Test Client",
                    "isApproved": false,
                    "creatorOwnershipTokenId": null,
                    "ownershipTokenIds": [],
                    "dataStoreIds": []
                  }
                  """

        Scenario: 14 Ensure clients can DELETE an apiClient successfully
            Given a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 14",
                   "claimSetName": "TestClaim01",
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
              And a POST request is made to "/v3/apiClients" with
                  """
                  {
                   "applicationId": {applicationId},
                   "name": "Client To Delete",
                   "isApproved": true,
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             When a DELETE request is made to "/v3/apiClients/{apiClientId}"
             Then it should respond with 204

        Scenario: 15 Verify deleted apiClient no longer exists
            Given a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 15",
                   "claimSetName": "TestClaim01",
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
              And a POST request is made to "/v3/apiClients" with
                  """
                  {
                   "applicationId": {applicationId},
                   "name": "Client To Delete 2",
                   "isApproved": true,
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
              And a DELETE request is made to "/v3/apiClients/{apiClientId}"
             When a GET request is made to "/v3/apiClients/{clientId}"
             Then it should respond with 404

        Scenario: 16 Verify error handling when deleting non-existent apiClient
            Given a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 16",
                   "claimSetName": "TestClaim01",
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             When a DELETE request is made to "/v3/apiClients/99999"
             Then it should respond with 404

        Scenario: 17 Ensure clients can reset credentials for an apiClient
            Given a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 17",
                   "claimSetName": "TestClaim01",
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
              And a POST request is made to "/v3/apiClients" with
                  """
                  {
                   "applicationId": {applicationId},
                   "name": "Test Client for Reset",
                   "isApproved": true,
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             When a PUT request is made to "/v3/apiClients/{apiClientId}/reset-credential" with
                  """
                  {}
                  """
             Then it should respond with 200
              And the response body has key and secret
              And the response body is
                  """
                  {
                    "id": {apiClientId},
                    "applicationId": {applicationId},
                    "name": "Test Client for Reset",
                    "key": "{key}",
                    "secret": "{secret}"
                  }
                  """

        Scenario: 18 Verify error handling when resetting credentials for non-existent apiClient
            Given a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 18",
                   "claimSetName": "TestClaim01",
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             When a PUT request is made to "/v3/apiClients/99999/reset-credential" with
                  """
                  {}
                  """
             Then it should respond with 404

        Scenario: 19 Verify PUT request with mismatched IDs
            Given a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 19",
                   "claimSetName": "TestClaim01",
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
              And a POST request is made to "/v3/apiClients" with
                  """
                  {
                   "applicationId": {applicationId},
                   "name": "Mismatch Test Client",
                   "isApproved": true,
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             When a PUT request is made to "/v3/apiClients/{apiClientId}" with
                  """
                  {
                   "id": 999999,
                   "applicationId": {applicationId},
                   "name": "Mismatch Test Client",
                   "isApproved": true,
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             Then it should respond with 400
              And the response body is
                  """
                    {
                        "detail": "Data validation failed. See 'validationErrors' for details.",
                        "type": "urn:ed-fi:api:bad-request:data",
                        "title": "Data Validation Failed",
                        "status": 400,
                        "validationErrors": {
                            "Id": [
                                "Request body id must match the id in the url."
                            ]
                        },
                        "errors": []
                    }
                  """

        # Scenarios 20 onward cover clients with no datastore assignment. They are appended rather
        # than inserted because scenario 01 asserts the complete apiClients collection and test
        # data is only cleaned between features, not between scenarios.
        @MssqlRepresentative
        Scenario: 20 Ensure an application and an additional client with no DataStoreIds complete the full lifecycle
             When a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 20",
                   "claimSetName": "TestClaim01",
                   "dataStoreIds": []
                  }
                  """
             Then it should respond with 201
              And the response body has key and secret
              And the response body credentials are captured as "initial"
             When a token is requested with the credentials captured as "initial"
             Then it should respond with 200
              And the response body has a non-empty access_token
             When a GET request is made to "/v3/apiClients/{initialKey}"
             Then it should respond with 200
              And the response body id is captured as "initialApiClientId"
              And the response body is
                  """
                  {
                    "id": {initialApiClientId},
                    "applicationId": {applicationId},
                    "clientId": "{initialKey}",
                    "clientUuid": "{clientUuid}",
                    "name": "Test Application 20",
                    "isApproved": true,
                    "creatorOwnershipTokenId": null,
                    "ownershipTokenIds": [],
                    "dataStoreIds": []
                  }
                  """
             When a POST request is made to "/v3/apiClients" with
                  """
                  {
                   "applicationId": {applicationId},
                   "name": "Additional Client 20",
                   "isApproved": true,
                   "dataStoreIds": []
                  }
                  """
             Then it should respond with 201
              And the response body has key and secret
              And the response body credentials are captured as "additional"
              And the response body id is captured as "additionalApiClientId"
             When a token is requested with the credentials captured as "additional"
             Then it should respond with 200
              And the response body has a non-empty access_token
             When a GET request is made to "/v3/apiClients/{additionalKey}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                    "id": {additionalApiClientId},
                    "applicationId": {applicationId},
                    "clientId": "{additionalKey}",
                    "clientUuid": "{clientUuid}",
                    "name": "Additional Client 20",
                    "isApproved": true,
                    "creatorOwnershipTokenId": null,
                    "ownershipTokenIds": [],
                    "dataStoreIds": []
                  }
                  """
             When a PUT request is made to "/v3/apiClients/{initialApiClientId}" with
                  """
                  {
                   "id": {initialApiClientId},
                   "applicationId": {applicationId},
                   "name": "Initial Client 20 Renamed",
                   "isApproved": true,
                   "dataStoreIds": []
                  }
                  """
             Then it should respond with 204
             When a PUT request is made to "/v3/apiClients/{additionalApiClientId}" with
                  """
                  {
                   "id": {additionalApiClientId},
                   "applicationId": {applicationId},
                   "name": "Additional Client 20 Renamed",
                   "isApproved": true,
                   "dataStoreIds": []
                  }
                  """
             Then it should respond with 204
             When a GET request is made to "/v3/apiClients/{initialKey}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                    "id": {initialApiClientId},
                    "applicationId": {applicationId},
                    "clientId": "{initialKey}",
                    "clientUuid": "{clientUuid}",
                    "name": "Initial Client 20 Renamed",
                    "isApproved": true,
                    "creatorOwnershipTokenId": null,
                    "ownershipTokenIds": [],
                    "dataStoreIds": []
                  }
                  """
             When a GET request is made to "/v3/apiClients/{additionalKey}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                    "id": {additionalApiClientId},
                    "applicationId": {applicationId},
                    "clientId": "{additionalKey}",
                    "clientUuid": "{clientUuid}",
                    "name": "Additional Client 20 Renamed",
                    "isApproved": true,
                    "creatorOwnershipTokenId": null,
                    "ownershipTokenIds": [],
                    "dataStoreIds": []
                  }
                  """
             When a token is requested with the credentials captured as "initial"
             Then it should respond with 200
              And the response body has a non-empty access_token
             When a token is requested with the credentials captured as "additional"
             Then it should respond with 200
              And the response body has a non-empty access_token
             When a PUT request is made to "/v3/apiClients/{initialApiClientId}/reset-credential" with
                  """
                  {}
                  """
             Then it should respond with 200
              And the response body has key and secret
              And the response body credentials are captured as "initial"
             When a token is requested with the credentials captured as "initial"
             Then it should respond with 200
              And the response body has a non-empty access_token
             When a PUT request is made to "/v3/apiClients/{additionalApiClientId}/reset-credential" with
                  """
                  {}
                  """
             Then it should respond with 200
              And the response body has key and secret
              And the response body credentials are captured as "additional"
             When a token is requested with the credentials captured as "additional"
             Then it should respond with 200
              And the response body has a non-empty access_token
             When a GET request is made to "/v3/apiClients/{initialKey}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                    "id": {initialApiClientId},
                    "applicationId": {applicationId},
                    "clientId": "{initialKey}",
                    "clientUuid": "{clientUuid}",
                    "name": "Initial Client 20 Renamed",
                    "isApproved": true,
                    "creatorOwnershipTokenId": null,
                    "ownershipTokenIds": [],
                    "dataStoreIds": []
                  }
                  """
             When a GET request is made to "/v3/apiClients/{additionalKey}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                    "id": {additionalApiClientId},
                    "applicationId": {applicationId},
                    "clientId": "{additionalKey}",
                    "clientUuid": "{clientUuid}",
                    "name": "Additional Client 20 Renamed",
                    "isApproved": true,
                    "creatorOwnershipTokenId": null,
                    "ownershipTokenIds": [],
                    "dataStoreIds": []
                  }
                  """
             When a DELETE request is made to "/v3/apiClients/{additionalApiClientId}"
             Then it should respond with 204
             When a GET request is made to "/v3/apiClients/{additionalKey}"
             Then it should respond with 404
             When a GET request is made to "/v3/apiClients/{initialKey}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                    "id": {initialApiClientId},
                    "applicationId": {applicationId},
                    "clientId": "{initialKey}",
                    "clientUuid": "{clientUuid}",
                    "name": "Initial Client 20 Renamed",
                    "isApproved": true,
                    "creatorOwnershipTokenId": null,
                    "ownershipTokenIds": [],
                    "dataStoreIds": []
                  }
                  """

        # Keycloak omits the dataStoreIds protocol mapper entirely when the assignment is empty, so
        # the claim shape is only assertable on the self-contained provider. Scenario 20 proves
        # token issuance itself on both providers.
        @SelfContainedOnly
        Scenario: 21 Verify a client with no DataStoreIds receives a token with an empty dataStoreIds claim
             When a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 21",
                   "claimSetName": "TestClaim01",
                   "dataStoreIds": []
                  }
                  """
             Then it should respond with 201
              And the response body credentials are captured as "identityOnly"
             When a token is requested with the credentials captured as "identityOnly"
             Then it should respond with 200
              And the response body has a non-empty access_token
              And the token has an empty dataStoreIds claim

        Scenario: 22 Verify error handling when DataStoreIds is explicitly null
            Given a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 22",
                   "claimSetName": "TestClaim01",
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             When a POST request is made to "/v3/apiClients" with
                  """
                  {
                   "applicationId": {applicationId},
                   "name": "Null Data Stores Client",
                   "isApproved": true,
                   "dataStoreIds": null
                  }
                  """
             Then it should respond with 400
              And the response body should not contain "DataStoreIds cannot be empty"
              And the response body is
                  """
                  {
                    "detail": "Data validation failed. See 'validationErrors' for details.",
                    "type": "urn:ed-fi:api:bad-request:data",
                    "title": "Data Validation Failed",
                    "status": 400,
                    "validationErrors": {
                        "DataStoreIds": [
                            "DataStoreIds cannot be null. Supply an array of Data Store ids, or an empty array for a client with no Data Store assignment."
                        ]
                    },
                    "errors": []
                  }
                  """
             When a POST request is made to "/v3/apiClients" with
                  """
                  {
                   "applicationId": {applicationId},
                   "name": "Valid Client 22",
                   "isApproved": true,
                   "dataStoreIds": []
                  }
                  """
             Then it should respond with 201
              And the response body id is captured as "client22Id"
             When a PUT request is made to "/v3/apiClients/{client22Id}" with
                  """
                  {
                   "id": {client22Id},
                   "applicationId": {applicationId},
                   "name": "Valid Client 22",
                   "isApproved": true,
                   "dataStoreIds": null
                  }
                  """
             Then it should respond with 400
              And the response body is
                  """
                  {
                    "detail": "Data validation failed. See 'validationErrors' for details.",
                    "type": "urn:ed-fi:api:bad-request:data",
                    "title": "Data Validation Failed",
                    "status": 400,
                    "validationErrors": {
                        "DataStoreIds": [
                            "DataStoreIds cannot be null. Supply an array of Data Store ids, or an empty array for a client with no Data Store assignment."
                        ]
                    },
                    "errors": []
                  }
                  """

        Scenario: 23 Ensure an omitted DataStoreIds list creates and replaces with no assignment
            Given a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 23",
                   "claimSetName": "TestClaim01",
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
              And a POST request is made to "/v3/apiClients" with
                  """
                  {
                   "applicationId": {applicationId},
                   "name": "Populated Client 23",
                   "isApproved": true,
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             Then it should respond with 201
              And the response body credentials are captured as "populated"
              And the response body id is captured as "client23Id"
             When a GET request is made to "/v3/apiClients/{populatedKey}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                    "id": {client23Id},
                    "applicationId": {applicationId},
                    "clientId": "{populatedKey}",
                    "clientUuid": "{clientUuid}",
                    "name": "Populated Client 23",
                    "isApproved": true,
                    "creatorOwnershipTokenId": null,
                    "ownershipTokenIds": [],
                    "dataStoreIds": [{dataStoreId}]
                  }
                  """
             When a PUT request is made to "/v3/apiClients/{client23Id}" with
                  """
                  {
                   "id": {client23Id},
                   "applicationId": {applicationId},
                   "name": "Populated Client 23",
                   "isApproved": true
                  }
                  """
             Then it should respond with 204
             When a GET request is made to "/v3/apiClients/{populatedKey}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                    "id": {client23Id},
                    "applicationId": {applicationId},
                    "clientId": "{populatedKey}",
                    "clientUuid": "{clientUuid}",
                    "name": "Populated Client 23",
                    "isApproved": true,
                    "creatorOwnershipTokenId": null,
                    "ownershipTokenIds": [],
                    "dataStoreIds": []
                  }
                  """
              # An omitted list on create must fall back to the command's own empty default, the
              # same end state an explicit empty array produces.
             When a POST request is made to "/v3/apiClients" with
                  """
                  {
                   "applicationId": {applicationId},
                   "name": "Omitted List Client 23",
                   "isApproved": true
                  }
                  """
             Then it should respond with 201
              And the response body has key and secret
              And the response body credentials are captured as "omittedCreate"
              And the response body id is captured as "omittedCreateId"
             When a GET request is made to "/v3/apiClients/{omittedCreateKey}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                    "id": {omittedCreateId},
                    "applicationId": {applicationId},
                    "clientId": "{omittedCreateKey}",
                    "clientUuid": "{clientUuid}",
                    "name": "Omitted List Client 23",
                    "isApproved": true,
                    "creatorOwnershipTokenId": null,
                    "ownershipTokenIds": [],
                    "dataStoreIds": []
                  }
                  """

        Scenario: 24 Verify supplied DataStoreIds are still validated after the minimum count rule was removed
            Given a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Application 24",
                   "claimSetName": "TestClaim01",
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
              And a POST request is made to "/v3/apiClients" with
                  """
                  {
                   "applicationId": {applicationId},
                   "name": "Client 24",
                   "isApproved": true,
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             Then it should respond with 201
              And the response body credentials are captured as "client24"
              And the response body id is captured as "client24Id"
             When a POST request is made to "/v3/apiClients" with
                  """
                  {
                   "applicationId": {applicationId},
                   "name": "Bad Data Store Client 24",
                   "isApproved": true,
                   "dataStoreIds": [999999]
                  }
                  """
             Then it should respond with 409
             When a PUT request is made to "/v3/apiClients/{client24Id}" with
                  """
                  {
                   "id": {client24Id},
                   "applicationId": {applicationId},
                   "name": "Client 24",
                   "isApproved": true,
                   "dataStoreIds": [999999]
                  }
                  """
             Then it should respond with 409
             When a GET request is made to "/v3/apiClients/{client24Key}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                    "id": {client24Id},
                    "applicationId": {applicationId},
                    "clientId": "{client24Key}",
                    "clientUuid": "{clientUuid}",
                    "name": "Client 24",
                    "isApproved": true,
                    "creatorOwnershipTokenId": null,
                    "ownershipTokenIds": [],
                    "dataStoreIds": [{dataStoreId}]
                  }
                  """
