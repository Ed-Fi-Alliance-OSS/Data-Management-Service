Feature: DataStores endpoints

        Background:
            Given valid credentials
              And token received

        Scenario: 01 Ensure clients can GET dataStores list
            Given the system has these "dataStores"
                  | dataStoreType | name           | connectionString                  |
                  | Production   | Test Instance  | Server=localhost;Database=TestDb; |
                  | Development  | Dev Instance   | Server=dev;Database=DevDb;        |
                  | Staging      | Stage Instance | Server=stage;Database=StageDb;    |
             When a GET request is made to "/v3/dataStores?offset=0&limit=2"
             Then it should respond with 200
              And the response body is
                  """
                      [{
                          "id": {id},
                          "dataStoreType": "Production",
                          "name": "Test Instance",
                          "connectionString": "{ignore}",
                          "dataStoreContexts": [],
                          "dataStoreDerivatives": []
                      },
                      {
                          "id": {id},
                          "dataStoreType": "Development",
                          "name": "Dev Instance",
                          "connectionString": "{ignore}",
                          "dataStoreContexts": [],
                          "dataStoreDerivatives": []
                      }]
                  """

        @MssqlRepresentative
        Scenario: 02 Ensure clients can create a dataStore
             When a POST request is made to "/v3/dataStores" with
                  """
                    {
                        "dataStoreType": "Production",
                        "name": "New Test Instance",
                        "connectionString": "Server=newtest;Database=NewTestDb;"
                    }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                    {
                        "location": "/v3/dataStores/{dataStoreId}"
                    }
                  """
              And the record can be retrieved with a GET request
                  """
                    {
                        "id": {id},
                        "dataStoreType": "Production",
                        "name": "New Test Instance",
                        "connectionString": "{ignore}",
                        "dataStoreContexts": [],
                        "dataStoreDerivatives": []
                    }
                  """

        Scenario: 03 Verify retrieving a single dataStore by ID
             When a POST request is made to "/v3/dataStores" with
                  """
                    {
                        "dataStoreType": "Development",
                        "name": "Retrieved Instance",
                        "connectionString": "Server=retrieved;Database=RetrievedDb;"
                    }
                  """
             Then it should respond with 201
             When a GET request is made to "/v3/dataStores/{dataStoreId}"
             Then it should respond with 200
              And the response body is
                  """
                      {
                          "id": {id},
                          "dataStoreType": "Development",
                          "name": "Retrieved Instance",
                          "connectionString": "{ignore}",
                          "dataStoreContexts": [],
                          "dataStoreDerivatives": []
                      }
                  """

        Scenario: 04 Put an existing dataStore
             When a POST request is made to "/v3/dataStores" with
                  """
                    {
                        "dataStoreType": "Staging",
                        "name": "Update Instance",
                        "connectionString": "Server=update;Database=UpdateDb;"
                    }
                  """
             Then it should respond with 201
             When a PUT request is made to "/v3/dataStores/{dataStoreId}" with
                  """
                    {
                        "id": {dataStoreId},
                        "dataStoreType": "Production",
                        "name": "Updated Instance",
                        "connectionString": "Server=updated;Database=UpdatedDb;"
                    }
                  """
             Then it should respond with 204
              And the record can be retrieved with a GET request
                  """
                    {
                        "id": {id},
                        "dataStoreType": "Production",
                        "name": "Updated Instance",
                        "connectionString": "{ignore}",
                        "dataStoreContexts": [],
                        "dataStoreDerivatives": []
                    }
                  """

        Scenario: 05 Verify deleting a specific dataStore by ID
             When a POST request is made to "/v3/dataStores" with
                  """
                    {
                        "dataStoreType": "Test",
                        "name": "Delete Instance",
                        "connectionString": "Server=delete;Database=DeleteDb;"
                    }
                  """
             Then it should respond with 201
             When a DELETE request is made to "/v3/dataStores/{dataStoreId}"
             Then it should respond with 204

        Scenario: 06 Verify error handling when trying to get an item that has already been deleted
             When a POST request is made to "/v3/dataStores" with
                  """
                    {
                        "dataStoreType": "Test",
                        "name": "Delete Test Instance",
                        "connectionString": "Server=deletetest;Database=DeleteTestDb;"
                    }
                  """
             Then it should respond with 201
             When a DELETE request is made to "/v3/dataStores/{dataStoreId}"
             Then it should respond with 204
             When a GET request is made to "/v3/dataStores/{dataStoreId}"
             Then it should respond with 404

        Scenario: 07 Verify error handling when trying to update an item that has already been deleted
             When a POST request is made to "/v3/dataStores" with
                  """
                    {
                        "dataStoreType": "Test",
                        "name": "Update Delete Instance",
                        "connectionString": "Server=updatedelete;Database=UpdateDeleteDb;"
                    }
                  """
             Then it should respond with 201
             When a DELETE request is made to "/v3/dataStores/{dataStoreId}"
             Then it should respond with 204
             When a PUT request is made to "/v3/dataStores/{dataStoreId}" with
                  """
                    {
                        "id": {dataStoreId},
                        "dataStoreType": "Production",
                        "name": "Updated Delete Instance",
                        "connectionString": "Server=updateddelete;Database=UpdatedDeleteDb;"
                    }
                  """
             Then it should respond with 404

        Scenario: 08 Verify error handling when trying to delete an item that has already been deleted
             When a POST request is made to "/v3/dataStores" with
                  """
                    {
                        "dataStoreType": "Test",
                        "name": "Double Delete Instance",
                        "connectionString": "Server=doubledelete;Database=DoubleDeleteDb;"
                    }
                  """
             Then it should respond with 201
             When a DELETE request is made to "/v3/dataStores/{dataStoreId}"
             Then it should respond with 204
             When a DELETE request is made to "/v3/dataStores/{dataStoreId}"
             Then it should respond with 404

        Scenario: 09 Verify error handling when trying to get a dataStore using an invalid id
             When a GET request is made to "/v3/dataStores/a"
             Then it should respond with 400

        Scenario: 10 Verify error handling when trying to delete a dataStore using an invalid id
             When a DELETE request is made to "/v3/dataStores/b"
             Then it should respond with 400

        Scenario: 11 Verify error handling when trying to update a dataStore using an invalid id
             When a PUT request is made to "/v3/dataStores/c" with
                  """
                    {
                        "id": 1,
                        "dataStoreType": "Production",
                        "name": "Invalid ID Instance",
                        "connectionString": "Server=invalid;Database=InvalidDb;"
                    }
                  """
             Then it should respond with 400

        Scenario: 12 Verify validation invalid dataStoreType
             When a POST request is made to "/v3/dataStores" with
                  """
                    {
                        "dataStoreType": "",
                        "name": "Test Instance",
                        "connectionString": "Server=test;Database=TestDb;"
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
                            "DataStoreType": [
                                "'Data Store Type' must not be empty."
                            ]
                        },
                        "errors": []
                    }
                  """

        Scenario: 13 Verify validation invalid name
             When a POST request is made to "/v3/dataStores" with
                  """
                    {
                        "dataStoreType": "Production",
                        "name": "",
                        "connectionString": "Server=test;Database=TestDb;"
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
                            "Name": [
                                "'Name' must not be empty."
                            ]
                        },
                        "errors": []
                    }
                  """

        Scenario: 14 Verify validation connectionString too long
             When a POST request is made to "/v3/dataStores" with
                  """
                    {
                        "dataStoreType": "Production",
                        "name": "Long Connection String Instance",
                        "connectionString": "01234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789"
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
                            "ConnectionString": [
                                "The length of 'Connection String' must be 1000 characters or fewer. You entered 1010 characters."
                            ]
                        },
                        "errors": []
                    }
                  """

        Scenario: 15 Verify PUT request with mismatched IDs
             When a POST request is made to "/v3/dataStores" with
                  """
                    {
                        "dataStoreType": "Production",
                        "name": "Mismatch Test Instance",
                        "connectionString": "Server=mismatch;Database=MismatchDb;"
                    }
                  """
             Then it should respond with 201
             When a PUT request is made to "/v3/dataStores/{dataStoreId}" with
                  """
                    {
                        "id": 999,
                        "dataStoreType": "Production",
                        "name": "Mismatched Instance",
                        "connectionString": "Server=mismatched;Database=MismatchedDb;"
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

        Scenario: 16 Verify retrieving a dataStore with derivatives
             When a POST request is made to "/v3/dataStores" with
                  """
                    {
                        "dataStoreType": "Production",
                        "name": "Instance with Derivatives",
                        "connectionString": "Server=main;Database=MainDb;"
                    }
                  """
             Then it should respond with 201
             When a POST request is made to "/v3/dataStoreDerivatives" with
                  """
                    {
                        "dataStoreId": {dataStoreId},
                        "derivativeType": "ReadReplica",
                        "connectionString": "Server=replica;Database=ReplicaDb;"
                    }
                  """
             Then it should respond with 201
             When a POST request is made to "/v3/dataStoreDerivatives" with
                  """
                    {
                        "dataStoreId": {dataStoreId},
                        "derivativeType": "Snapshot",
                        "connectionString": "Server=snapshot;Database=SnapshotDb;"
                    }
                  """
             Then it should respond with 201
             When a GET request is made to "/v3/dataStores/{dataStoreId}"
             Then it should respond with 200
              And the response body is
                  """
                    {
                        "id": {id},
                        "dataStoreType": "Production",
                        "name": "Instance with Derivatives",
                        "connectionString": "{ignore}",
                        "dataStoreContexts": [],
                        "dataStoreDerivatives": [
                            {
                                "id": "{*}",
                                "dataStoreId": {dataStoreId},
                                "derivativeType": "ReadReplica",
                                "connectionString": "{ignore}"
                            },
                            {
                                "id": "{*}",
                                "dataStoreId": {dataStoreId},
                                "derivativeType": "Snapshot",
                                "connectionString": "{ignore}"
                            }
                        ]
                    }
                  """

        Scenario: 17 Ensure clients can GET applications by dataStore
            Given a POST request is made to "/v3/vendors" with
                  """
                    {
                        "company": "Test Vendor 17",
                        "contactName": "Test Contact",
                        "contactEmailAddress": "test17@test.com",
                        "namespacePrefixes": "uri://test17.org"
                    }
                  """
              And a POST request is made to "/v3/dataStores" with
                  """
                    {
                        "dataStoreType": "Production",
                        "name": "Test Instance 17",
                        "connectionString": "Server=test17;Database=TestDb17;"
                    }
                  """
              And a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Application 17-1",
                   "claimSetName": "ClaimSet17-1",
                   "educationOrganizationIds": [1, 2],
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
              And a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Application 17-2",
                   "claimSetName": "ClaimSet17-2",
                   "educationOrganizationIds": [3, 4],
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
              And a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Application 17-3",
                   "claimSetName": "ClaimSet17-3",
                   "educationOrganizationIds": [],
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             When a GET request is made to "/v3/dataStores/{dataStoreId}/applications/?offset=1&limit=1"
             Then it should respond with 200
              And the response body is
                  """
                      [{
                          "id": {id},
                          "applicationName": "Application 17-2",
                          "claimSetName": "ClaimSet17-2",
                          "vendorId": {vendorId},
                          "educationOrganizationIds": [3, 4],
                          "dataStoreIds": [{dataStoreId}],
                          "profileIds": [],
                          "enabled": true
                      }]
                  """

        Scenario: 18 Verify error handling when getting applications for non-existent dataStore
             When a GET request is made to "/v3/dataStores/99999/applications/?offset=0&limit=25"
             Then it should respond with 404
              And the response body is
                  """
                    {
                        "detail": "DataStore 99999 not found. It may have been recently deleted.",
                        "type": "urn:ed-fi:api:not-found",
                        "title": "Not Found",
                        "status": 404,
                        "correlationId": "{*}",
                        "validationErrors": {},
                        "errors": []
                    }
                  """

        Scenario: 19 Verify getting applications for dataStore with no applications
            Given a POST request is made to "/v3/dataStores" with
                  """
                    {
                        "dataStoreType": "Production",
                        "name": "Instance No Apps",
                        "connectionString": "Server=test19;Database=TestDb19;"
                    }
                  """
             When a GET request is made to "/v3/dataStores/{dataStoreId}/applications/?offset=0&limit=25"
             Then it should respond with 200
              And the response body is
                  """
                    []
                  """

        Scenario: 20 Verify error handling when getting applications using invalid dataStore id
             When a GET request is made to "/v3/dataStores/invalid/applications/?offset=0&limit=25"
             Then it should respond with 400

        Scenario: 21 Ensure application with a disabled API client returns enabled false via dataStore sub-resource
            Given a POST request is made to "/v3/vendors" with
                  """
                    {
                        "company": "Test Vendor 21",
                        "contactName": "Test Contact",
                        "contactEmailAddress": "test21@test.com",
                        "namespacePrefixes": "uri://test21.org"
                    }
                  """
              And a POST request is made to "/v3/dataStores" with
                  """
                    {
                        "dataStoreType": "Production",
                        "name": "Test Instance 21",
                        "connectionString": "Server=test21;Database=TestDb21;"
                    }
                  """
              And a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Disabled Client App 21",
                   "claimSetName": "Claim21Inst",
                   "educationOrganizationIds": [],
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             Then it should respond with 201
              And the response body has key and secret
             When a POST request is made to "/v3/apiClients" with
                  """
                  {
                   "applicationId": {applicationId},
                   "name": "Disabled Client 21",
                   "isApproved": false,
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             Then it should respond with 201
             When a GET request is made to "/v3/dataStores/{dataStoreId}/applications/?offset=0&limit=25"
             Then it should respond with 200
              And the response body contains an object matching
                  """
                  {
                   "id": {applicationId},
                   "enabled": false
                  }
                  """
