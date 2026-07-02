Feature: DataStoreDerivatives endpoints

        Background:
            Given valid credentials
              And token received
              And a POST request is made to "/v3/dataStores" with
                  """
                  {
                    "dataStoreType": "Production",
                    "name": "Parent Instance",
                    "connectionString": "Server=localhost;Database=TestDb;"
                  }
                  """

        @MssqlRepresentative
        Scenario: 02 Ensure clients can create a dataStoreDerivative with ReadReplica type
             When a POST request is made to "/v3/dataStoreDerivatives" with
                  """
                    {
                        "dataStoreId": {dataStoreId},
                        "derivativeType": "ReadReplica",
                        "connectionString": "Server=newreplica;Database=NewReplicaDb;"
                    }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                    {
                        "location": "/v3/dataStoreDerivatives/{dataStoreDerivativeId}"
                    }
                  """
              And the record can be retrieved with a GET request
                  """
                    {
                        "id": {id},
                        "dataStoreId": {dataStoreId},
                        "derivativeType": "ReadReplica",
                        "connectionString": "{ignore}"
                    }
                  """

        Scenario: 03 Ensure clients can create a dataStoreDerivative with Snapshot type
             When a POST request is made to "/v3/dataStoreDerivatives" with
                  """
                    {
                        "dataStoreId": {dataStoreId},
                        "derivativeType": "Snapshot",
                        "connectionString": "Server=newsnapshot;Database=NewSnapshotDb;"
                    }
                  """
             Then it should respond with 201
              And the record can be retrieved with a GET request
                  """
                    {
                        "id": {id},
                        "dataStoreId": {dataStoreId},
                        "derivativeType": "Snapshot",
                        "connectionString": "{ignore}"
                    }
                  """

        Scenario: 04 Verify retrieving a single dataStoreDerivative by ID
             When a POST request is made to "/v3/dataStoreDerivatives" with
                  """
                    {
                        "dataStoreId": {dataStoreId},
                        "derivativeType": "ReadReplica",
                        "connectionString": "Server=retrieved;Database=RetrievedDb;"
                    }
                  """
             Then it should respond with 201
             When a GET request is made to "/v3/dataStoreDerivatives/{dataStoreDerivativeId}"
             Then it should respond with 200
              And the response body is
                  """
                      {
                          "id": {id},
                          "dataStoreId": {dataStoreId},
                          "derivativeType": "ReadReplica",
                          "connectionString": "{ignore}"
                      }
                  """

        Scenario: 05 Put an existing dataStoreDerivative
             When a POST request is made to "/v3/dataStoreDerivatives" with
                  """
                    {
                        "dataStoreId": {dataStoreId},
                        "derivativeType": "ReadReplica",
                        "connectionString": "Server=update;Database=UpdateDb;"
                    }
                  """
             Then it should respond with 201
             When a PUT request is made to "/v3/dataStoreDerivatives/{dataStoreDerivativeId}" with
                  """
                    {
                        "id": {dataStoreDerivativeId},
                        "dataStoreId": {dataStoreId},
                        "derivativeType": "Snapshot",
                        "connectionString": "Server=updated;Database=UpdatedDb;"
                    }
                  """
             Then it should respond with 204
              And the record can be retrieved with a GET request
                  """
                    {
                        "id": {id},
                        "dataStoreId": {dataStoreId},
                        "derivativeType": "Snapshot",
                        "connectionString": "{ignore}"
                    }
                  """

        Scenario: 06 Verify deleting a specific dataStoreDerivative by ID
             When a POST request is made to "/v3/dataStoreDerivatives" with
                  """
                    {
                        "dataStoreId": {dataStoreId},
                        "derivativeType": "ReadReplica",
                        "connectionString": "Server=delete;Database=DeleteDb;"
                    }
                  """
             Then it should respond with 201
             When a DELETE request is made to "/v3/dataStoreDerivatives/{dataStoreDerivativeId}"
             Then it should respond with 204

        Scenario: 07 Verify error handling when trying to get an item that has already been deleted
             When a POST request is made to "/v3/dataStoreDerivatives" with
                  """
                    {
                        "dataStoreId": {dataStoreId},
                        "derivativeType": "Snapshot",
                        "connectionString": "Server=deletetest;Database=DeleteTestDb;"
                    }
                  """
             Then it should respond with 201
             When a DELETE request is made to "/v3/dataStoreDerivatives/{dataStoreDerivativeId}"
             Then it should respond with 204
             When a GET request is made to "/v3/dataStoreDerivatives/{dataStoreDerivativeId}"
             Then it should respond with 404

        Scenario: 08 Verify error handling when trying to update an item that has already been deleted
             When a POST request is made to "/v3/dataStoreDerivatives" with
                  """
                    {
                        "dataStoreId": {dataStoreId},
                        "derivativeType": "ReadReplica",
                        "connectionString": "Server=updatedelete;Database=UpdateDeleteDb;"
                    }
                  """
             Then it should respond with 201
             When a DELETE request is made to "/v3/dataStoreDerivatives/{dataStoreDerivativeId}"
             Then it should respond with 204
             When a PUT request is made to "/v3/dataStoreDerivatives/{dataStoreDerivativeId}" with
                  """
                    {
                        "id": {dataStoreDerivativeId},
                        "dataStoreId": {dataStoreId},
                        "derivativeType": "Snapshot",
                        "connectionString": "Server=updateddelete;Database=UpdatedDeleteDb;"
                    }
                  """
             Then it should respond with 404

        Scenario: 09 Verify error handling when trying to delete an item that has already been deleted
             When a POST request is made to "/v3/dataStoreDerivatives" with
                  """
                    {
                        "dataStoreId": {dataStoreId},
                        "derivativeType": "ReadReplica",
                        "connectionString": "Server=doubledelete;Database=DoubleDeleteDb;"
                    }
                  """
             Then it should respond with 201
             When a DELETE request is made to "/v3/dataStoreDerivatives/{dataStoreDerivativeId}"
             Then it should respond with 204
             When a DELETE request is made to "/v3/dataStoreDerivatives/{dataStoreDerivativeId}"
             Then it should respond with 404

        Scenario: 10 Verify error handling when trying to get a dataStoreDerivative using an invalid id
             When a GET request is made to "/v3/dataStoreDerivatives/a"
             Then it should respond with 400

        Scenario: 11 Verify error handling when trying to delete a dataStoreDerivative using an invalid id
             When a DELETE request is made to "/v3/dataStoreDerivatives/b"
             Then it should respond with 400

        Scenario: 12 Verify error handling when trying to update a dataStoreDerivative using an invalid id
             When a PUT request is made to "/v3/dataStoreDerivatives/c" with
                  """
                    {
                        "id": 1,
                        "dataStoreId": 1,
                        "derivativeType": "ReadReplica",
                        "connectionString": "Server=invalid;Database=InvalidDb;"
                    }
                  """
             Then it should respond with 400

        Scenario: 13 Verify validation for invalid dataStoreId (foreign key violation)
             When a POST request is made to "/v3/dataStoreDerivatives" with
                  """
                    {
                        "dataStoreId": 99999,
                        "derivativeType": "ReadReplica",
                        "connectionString": "Server=test;Database=TestDb;"
                    }
                  """
             Then it should respond with 400
              And the response body is
                  """
                    {
                        "detail": "The specified DataStore does not exist.",
                        "type": "urn:ed-fi:api:bad-request",
                        "title": "Bad Request",
                        "status": 400,
                        "validationErrors": {},
                        "errors": []
                    }
                  """

        Scenario: 14 Verify validation for invalid derivativeType
             When a POST request is made to "/v3/dataStoreDerivatives" with
                  """
                    {
                        "dataStoreId": 1,
                        "derivativeType": "InvalidType",
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
                            "DerivativeType": [
                                "DerivativeType must be either 'ReadReplica' or 'Snapshot'."
                            ]
                        },
                        "errors": []
                    }
                  """

        Scenario: 15 Verify validation for empty derivativeType
             When a POST request is made to "/v3/dataStoreDerivatives" with
                  """
                    {
                        "dataStoreId": 1,
                        "derivativeType": "",
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
                            "DerivativeType": [
                                "DerivativeType is required.",
                                "DerivativeType must be either 'ReadReplica' or 'Snapshot'."
                            ]
                        },
                        "errors": []
                    }
                  """

        Scenario: 16 Verify validation for zero dataStoreId
             When a POST request is made to "/v3/dataStoreDerivatives" with
                  """
                    {
                        "dataStoreId": 0,
                        "derivativeType": "ReadReplica",
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
                            "DataStoreId": [
                                "DataStoreId must be greater than 0."
                            ]
                        },
                        "errors": []
                    }
                  """

        Scenario: 17 Verify validation connectionString too long
             When a POST request is made to "/v3/dataStoreDerivatives" with
                  """
                    {
                        "dataStoreId": 1,
                        "derivativeType": "ReadReplica",
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
                                "ConnectionString must be 1000 characters or fewer."
                            ]
                        },
                        "errors": []
                    }
                  """

        Scenario: 18 Verify PUT request with mismatched IDs
             When a POST request is made to "/v3/dataStoreDerivatives" with
                  """
                    {
                        "dataStoreId": {dataStoreId},
                        "derivativeType": "ReadReplica",
                        "connectionString": "Server=mismatch;Database=MismatchDb;"
                    }
                  """
             Then it should respond with 201
             When a PUT request is made to "/v3/dataStoreDerivatives/{dataStoreDerivativeId}" with
                  """
                    {
                        "id": 999,
                        "dataStoreId": {dataStoreId},
                        "derivativeType": "Snapshot",
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

        Scenario: 19 Verify CASCADE DELETE when parent dataStore is deleted
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
             When a DELETE request is made to "/v3/dataStores/{dataStoreId}"
             Then it should respond with 204
             When a GET request is made to "/v3/dataStoreDerivatives/{dataStoreDerivativeId}"
             Then it should respond with 404

        Scenario: 20 Ensure clients can GET dataStoreDerivatives list with paging
             When a POST request is made to "/v3/dataStoreDerivatives" with
                  """
                    {
                        "dataStoreId": {dataStoreId},
                        "derivativeType": "ReadReplica",
                        "connectionString": "Server=list-a;Database=ListADb;"
                    }
                  """
             Then it should respond with 201
             When a POST request is made to "/v3/dataStoreDerivatives" with
                  """
                    {
                        "dataStoreId": {dataStoreId},
                        "derivativeType": "Snapshot",
                        "connectionString": "Server=list-b;Database=ListBDb;"
                    }
                  """
             Then it should respond with 201
             When a GET request is made to "/v3/dataStoreDerivatives?offset=0&limit=1"
             Then it should respond with 200
