Feature: DmsInstanceDerivatives endpoints

        Background:
            Given valid credentials
              And token received

        Scenario: 01 Ensure clients can GET dmsInstanceDerivatives list
            Given the system has these "dmsInstances"
                  | instanceType | instanceName    | connectionString                   |
                  | Production   | Parent Instance | Server=localhost;Database=TestDb;  |
              And the system has these "dmsInstanceDerivatives"
                  | instanceId | derivativeType | connectionString                       |
                  | {id}       | ReadReplica    | Server=replica1;Database=ReplicaDb1;   |
                  | {id}       | Snapshot       | Server=snapshot1;Database=SnapshotDb1; |
                  | {id}       | ReadReplica    | Server=replica2;Database=ReplicaDb2;   |
             When a GET request is made to "/v2/dmsInstanceDerivatives?offset=0&limit=2"
             Then it should respond with 200
              And the response body is
                  """
                      [{
                          "id": {id},
                          "instanceId": {id},
                          "derivativeType": "ReadReplica",
                          "connectionString": "Server=replica1;Database=ReplicaDb1;"
                      },
                      {
                          "id": {id},
                          "instanceId": {id},
                          "derivativeType": "Snapshot",
                          "connectionString": "Server=snapshot1;Database=SnapshotDb1;"
                      }]
                  """

        Scenario: 02 Ensure clients can create a dmsInstanceDerivative with ReadReplica type
            Given the system has these "dmsInstances"
                  | instanceType | instanceName    | connectionString                  |
                  | Production   | Parent Instance | Server=localhost;Database=TestDb; |
             When a POST request is made to "/v2/dmsInstanceDerivatives" with
                  """
                    {
                        "instanceId": {dmsInstanceId},
                        "derivativeType": "ReadReplica",
                        "connectionString": "Server=newreplica;Database=NewReplicaDb;"
                    }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                    {
                        "location": "/v2/dmsInstanceDerivatives/{dmsInstanceDerivativeId}"
                    }
                  """
              And the record can be retrieved with a GET request
                  """
                    {
                        "id": {id},
                        "instanceId": {id},
                        "derivativeType": "ReadReplica",
                        "connectionString": "Server=newreplica;Database=NewReplicaDb;"
                    }
                  """

        Scenario: 03 Ensure clients can create a dmsInstanceDerivative with Snapshot type
            Given the system has these "dmsInstances"
                  | instanceType | instanceName    | connectionString                  |
                  | Production   | Parent Instance | Server=localhost;Database=TestDb; |
             When a POST request is made to "/v2/dmsInstanceDerivatives" with
                  """
                    {
                        "instanceId": {dmsInstanceId},
                        "derivativeType": "Snapshot",
                        "connectionString": "Server=newsnapshot;Database=NewSnapshotDb;"
                    }
                  """
             Then it should respond with 201
              And the record can be retrieved with a GET request
                  """
                    {
                        "id": {id},
                        "instanceId": {id},
                        "derivativeType": "Snapshot",
                        "connectionString": "Server=newsnapshot;Database=NewSnapshotDb;"
                    }
                  """

        Scenario: 04 Verify retrieving a single dmsInstanceDerivative by ID
            Given the system has these "dmsInstances"
                  | instanceType | instanceName    | connectionString                  |
                  | Production   | Parent Instance | Server=localhost;Database=TestDb; |
             When a POST request is made to "/v2/dmsInstanceDerivatives" with
                  """
                    {
                        "instanceId": {dmsInstanceId},
                        "derivativeType": "ReadReplica",
                        "connectionString": "Server=retrieved;Database=RetrievedDb;"
                    }
                  """
             Then it should respond with 201
             When a GET request is made to "/v2/dmsInstanceDerivatives/{dmsInstanceDerivativeId}"
             Then it should respond with 200
              And the response body is
                  """
                      {
                          "id": {id},
                          "instanceId": {id},
                          "derivativeType": "ReadReplica",
                          "connectionString": "Server=retrieved;Database=RetrievedDb;"
                      }
                  """

        Scenario: 05 Put an existing dmsInstanceDerivative
            Given the system has these "dmsInstances"
                  | instanceType | instanceName    | connectionString                  |
                  | Production   | Parent Instance | Server=localhost;Database=TestDb; |
             When a POST request is made to "/v2/dmsInstanceDerivatives" with
                  """
                    {
                        "instanceId": {dmsInstanceId},
                        "derivativeType": "ReadReplica",
                        "connectionString": "Server=update;Database=UpdateDb;"
                    }
                  """
             Then it should respond with 201
             When a PUT request is made to "/v2/dmsInstanceDerivatives/{dmsInstanceDerivativeId}" with
                  """
                    {
                        "id": {dmsInstanceDerivativeId},
                        "instanceId": {dmsInstanceId},
                        "derivativeType": "Snapshot",
                        "connectionString": "Server=updated;Database=UpdatedDb;"
                    }
                  """
             Then it should respond with 204
              And the record can be retrieved with a GET request
                  """
                    {
                        "id": {id},
                        "instanceId": {id},
                        "derivativeType": "Snapshot",
                        "connectionString": "Server=updated;Database=UpdatedDb;"
                    }
                  """

        Scenario: 06 Verify deleting a specific dmsInstanceDerivative by ID
            Given the system has these "dmsInstances"
                  | instanceType | instanceName    | connectionString                  |
                  | Production   | Parent Instance | Server=localhost;Database=TestDb; |
             When a POST request is made to "/v2/dmsInstanceDerivatives" with
                  """
                    {
                        "instanceId": {dmsInstanceId},
                        "derivativeType": "ReadReplica",
                        "connectionString": "Server=delete;Database=DeleteDb;"
                    }
                  """
             Then it should respond with 201
             When a DELETE request is made to "/v2/dmsInstanceDerivatives/{dmsInstanceDerivativeId}"
             Then it should respond with 204

        Scenario: 07 Verify error handling when trying to get an item that has already been deleted
            Given the system has these "dmsInstances"
                  | instanceType | instanceName    | connectionString                  |
                  | Production   | Parent Instance | Server=localhost;Database=TestDb; |
             When a POST request is made to "/v2/dmsInstanceDerivatives" with
                  """
                    {
                        "instanceId": {dmsInstanceId},
                        "derivativeType": "Snapshot",
                        "connectionString": "Server=deletetest;Database=DeleteTestDb;"
                    }
                  """
             Then it should respond with 201
             When a DELETE request is made to "/v2/dmsInstanceDerivatives/{dmsInstanceDerivativeId}"
             Then it should respond with 204
             When a GET request is made to "/v2/dmsInstanceDerivatives/{dmsInstanceDerivativeId}"
             Then it should respond with 404

        Scenario: 08 Verify error handling when trying to update an item that has already been deleted
            Given the system has these "dmsInstances"
                  | instanceType | instanceName    | connectionString                  |
                  | Production   | Parent Instance | Server=localhost;Database=TestDb; |
             When a POST request is made to "/v2/dmsInstanceDerivatives" with
                  """
                    {
                        "instanceId": {dmsInstanceId},
                        "derivativeType": "ReadReplica",
                        "connectionString": "Server=updatedelete;Database=UpdateDeleteDb;"
                    }
                  """
             Then it should respond with 201
             When a DELETE request is made to "/v2/dmsInstanceDerivatives/{dmsInstanceDerivativeId}"
             Then it should respond with 204
             When a PUT request is made to "/v2/dmsInstanceDerivatives/{dmsInstanceDerivativeId}" with
                  """
                    {
                        "id": {dmsInstanceDerivativeId},
                        "instanceId": {dmsInstanceId},
                        "derivativeType": "Snapshot",
                        "connectionString": "Server=updateddelete;Database=UpdatedDeleteDb;"
                    }
                  """
             Then it should respond with 404

        Scenario: 09 Verify error handling when trying to delete an item that has already been deleted
            Given the system has these "dmsInstances"
                  | instanceType | instanceName    | connectionString                  |
                  | Production   | Parent Instance | Server=localhost;Database=TestDb; |
             When a POST request is made to "/v2/dmsInstanceDerivatives" with
                  """
                    {
                        "instanceId": {dmsInstanceId},
                        "derivativeType": "ReadReplica",
                        "connectionString": "Server=doubledelete;Database=DoubleDeleteDb;"
                    }
                  """
             Then it should respond with 201
             When a DELETE request is made to "/v2/dmsInstanceDerivatives/{dmsInstanceDerivativeId}"
             Then it should respond with 204
             When a DELETE request is made to "/v2/dmsInstanceDerivatives/{dmsInstanceDerivativeId}"
             Then it should respond with 404

        Scenario: 10 Verify error handling when trying to get a dmsInstanceDerivative using an invalid id
             When a GET request is made to "/v2/dmsInstanceDerivatives/a"
             Then it should respond with 400

        Scenario: 11 Verify error handling when trying to delete a dmsInstanceDerivative using an invalid id
             When a DELETE request is made to "/v2/dmsInstanceDerivatives/b"
             Then it should respond with 400

        Scenario: 12 Verify error handling when trying to update a dmsInstanceDerivative using an invalid id
             When a PUT request is made to "/v2/dmsInstanceDerivatives/c" with
                  """
                    {
                        "id": 1,
                        "instanceId": 1,
                        "derivativeType": "ReadReplica",
                        "connectionString": "Server=invalid;Database=InvalidDb;"
                    }
                  """
             Then it should respond with 400

        Scenario: 13 Verify validation for invalid instanceId (foreign key violation)
             When a POST request is made to "/v2/dmsInstanceDerivatives" with
                  """
                    {
                        "instanceId": 99999,
                        "derivativeType": "ReadReplica",
                        "connectionString": "Server=test;Database=TestDb;"
                    }
                  """
             Then it should respond with 400
              And the response body is
                  """
                    {
                        "detail": "The specified DmsInstance does not exist.",
                        "type": "urn:ed-fi:api:bad-request",
                        "title": "Bad Request",
                        "status": 400,
                        "errors": []
                    }
                  """

        Scenario: 14 Verify validation for invalid derivativeType
             When a POST request is made to "/v2/dmsInstanceDerivatives" with
                  """
                    {
                        "instanceId": 1,
                        "derivativeType": "InvalidType",
                        "connectionString": "Server=test;Database=TestDb;"
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
                            "DerivativeType": [
                                "DerivativeType must be either 'ReadReplica' or 'Snapshot'."
                            ]
                        },
                        "errors": []
                    }
                  """

        Scenario: 15 Verify validation for empty derivativeType
             When a POST request is made to "/v2/dmsInstanceDerivatives" with
                  """
                    {
                        "instanceId": 1,
                        "derivativeType": "",
                        "connectionString": "Server=test;Database=TestDb;"
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
                            "DerivativeType": [
                                "DerivativeType is required."
                            ]
                        },
                        "errors": []
                    }
                  """

        Scenario: 16 Verify validation for zero instanceId
             When a POST request is made to "/v2/dmsInstanceDerivatives" with
                  """
                    {
                        "instanceId": 0,
                        "derivativeType": "ReadReplica",
                        "connectionString": "Server=test;Database=TestDb;"
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
                            "InstanceId": [
                                "InstanceId must be greater than 0."
                            ]
                        },
                        "errors": []
                    }
                  """

        Scenario: 17 Verify validation connectionString too long
             When a POST request is made to "/v2/dmsInstanceDerivatives" with
                  """
                    {
                        "instanceId": 1,
                        "derivativeType": "ReadReplica",
                        "connectionString": "01234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789"
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
                            "ConnectionString": [
                                "The length of 'Connection String' must be 1000 characters or fewer. You entered 1010 characters."
                            ]
                        },
                        "errors": []
                    }
                  """

        Scenario: 18 Verify PUT request with mismatched IDs
            Given the system has these "dmsInstances"
                  | instanceType | instanceName    | connectionString                  |
                  | Production   | Parent Instance | Server=localhost;Database=TestDb; |
             When a POST request is made to "/v2/dmsInstanceDerivatives" with
                  """
                    {
                        "instanceId": {dmsInstanceId},
                        "derivativeType": "ReadReplica",
                        "connectionString": "Server=mismatch;Database=MismatchDb;"
                    }
                  """
             Then it should respond with 201
             When a PUT request is made to "/v2/dmsInstanceDerivatives/{dmsInstanceDerivativeId}" with
                  """
                    {
                        "id": 999,
                        "instanceId": {dmsInstanceId},
                        "derivativeType": "Snapshot",
                        "connectionString": "Server=mismatched;Database=MismatchedDb;"
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
                            "Id": [
                                "Request body id must match the id in the url."
                            ]
                        },
                        "errors": []
                    }
                  """

        Scenario: 19 Verify CASCADE DELETE when parent dmsInstance is deleted
            Given the system has these "dmsInstances"
                  | instanceType | instanceName    | connectionString                  |
                  | Production   | Parent Instance | Server=localhost;Database=TestDb; |
             When a POST request is made to "/v2/dmsInstanceDerivatives" with
                  """
                    {
                        "instanceId": {dmsInstanceId},
                        "derivativeType": "ReadReplica",
                        "connectionString": "Server=replica;Database=ReplicaDb;"
                    }
                  """
             Then it should respond with 201
             When a POST request is made to "/v2/dmsInstanceDerivatives" with
                  """
                    {
                        "instanceId": {dmsInstanceId},
                        "derivativeType": "Snapshot",
                        "connectionString": "Server=snapshot;Database=SnapshotDb;"
                    }
                  """
             Then it should respond with 201
             When a DELETE request is made to "/v2/dmsInstances/{dmsInstanceId}"
             Then it should respond with 204
             When a GET request is made to "/v2/dmsInstanceDerivatives/{dmsInstanceDerivativeId}"
             Then it should respond with 404

        Scenario: 20 Verify creating derivative without connectionString is valid
            Given the system has these "dmsInstances"
                  | instanceType | instanceName    | connectionString                  |
                  | Production   | Parent Instance | Server=localhost;Database=TestDb; |
             When a POST request is made to "/v2/dmsInstanceDerivatives" with
                  """
                    {
                        "instanceId": {dmsInstanceId},
                        "derivativeType": "ReadReplica",
                        "connectionString": null
                    }
                  """
             Then it should respond with 201
              And the record can be retrieved with a GET request
                  """
                    {
                        "id": {id},
                        "instanceId": {id},
                        "derivativeType": "ReadReplica",
                        "connectionString": null
                    }
                  """
