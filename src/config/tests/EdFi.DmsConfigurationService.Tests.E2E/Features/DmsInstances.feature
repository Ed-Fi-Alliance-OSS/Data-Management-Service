Feature: DmsInstances endpoints

        Background:
            Given valid credentials
              And token received

        Scenario: 01 Ensure clients can GET dmsInstances list
            Given the system has these "dmsInstances"
                  | instanceType | instanceName   | connectionString                  |
                  | Production   | Test Instance  | Server=localhost;Database=TestDb; |
                  | Development  | Dev Instance   | Server=dev;Database=DevDb;        |
                  | Staging      | Stage Instance | Server=stage;Database=StageDb;    |
             When a GET request is made to "/v2/dmsInstances?offset=0&limit=2"
             Then it should respond with 200
              And the response body is
                  """
                      [{
                          "id": {id},
                          "instanceType": "Production",
                          "instanceName": "Test Instance",
                          "connectionString": "Server=localhost;Database=TestDb;",
                          "dmsInstanceRouteContexts": []
                      },
                      {
                          "id": {id},
                          "instanceType": "Development",
                          "instanceName": "Dev Instance",
                          "connectionString": "Server=dev;Database=DevDb;",
                          "dmsInstanceRouteContexts": []
                      }]
                  """

        Scenario: 02 Ensure clients can create a dmsInstance
             When a POST request is made to "/v2/dmsInstances" with
                  """
                    {
                        "instanceType": "Production",
                        "instanceName": "New Test Instance",
                        "connectionString": "Server=newtest;Database=NewTestDb;"
                    }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                    {
                        "location": "/v2/dmsInstances/{dmsInstanceId}"
                    }
                  """
              And the record can be retrieved with a GET request
                  """
                    {
                        "id": {id},
                        "instanceType": "Production",
                        "instanceName": "New Test Instance",
                        "connectionString": "Server=newtest;Database=NewTestDb;",
                        "dmsInstanceRouteContexts": []
                    }
                  """

        Scenario: 03 Verify retrieving a single dmsInstance by ID
             When a POST request is made to "/v2/dmsInstances" with
                  """
                    {
                        "instanceType": "Development",
                        "instanceName": "Retrieved Instance",
                        "connectionString": "Server=retrieved;Database=RetrievedDb;"
                    }
                  """
             Then it should respond with 201
             When a GET request is made to "/v2/dmsInstances/{dmsInstanceId}"
             Then it should respond with 200
              And the response body is
                  """
                      {
                          "id": {id},
                          "instanceType": "Development",
                          "instanceName": "Retrieved Instance",
                          "connectionString": "Server=retrieved;Database=RetrievedDb;",
                          "dmsInstanceRouteContexts": []
                      }
                  """

        Scenario: 04 Put an existing dmsInstance
             When a POST request is made to "/v2/dmsInstances" with
                  """
                    {
                        "instanceType": "Staging",
                        "instanceName": "Update Instance",
                        "connectionString": "Server=update;Database=UpdateDb;"
                    }
                  """
             Then it should respond with 201
             When a PUT request is made to "/v2/dmsInstances/{dmsInstanceId}" with
                  """
                    {
                        "id": {dmsInstanceId},
                        "instanceType": "Production",
                        "instanceName": "Updated Instance",
                        "connectionString": "Server=updated;Database=UpdatedDb;"
                    }
                  """
             Then it should respond with 204
              And the record can be retrieved with a GET request
                  """
                    {
                        "id": {id},
                        "instanceType": "Production",
                        "instanceName": "Updated Instance",
                        "connectionString": "Server=updated;Database=UpdatedDb;",
                        "dmsInstanceRouteContexts": []
                    }
                  """

        Scenario: 05 Verify deleting a specific dmsInstance by ID
             When a POST request is made to "/v2/dmsInstances" with
                  """
                    {
                        "instanceType": "Test",
                        "instanceName": "Delete Instance",
                        "connectionString": "Server=delete;Database=DeleteDb;"
                    }
                  """
             Then it should respond with 201
             When a DELETE request is made to "/v2/dmsInstances/{dmsInstanceId}"
             Then it should respond with 204

        Scenario: 06 Verify error handling when trying to get an item that has already been deleted
             When a POST request is made to "/v2/dmsInstances" with
                  """
                    {
                        "instanceType": "Test",
                        "instanceName": "Delete Test Instance",
                        "connectionString": "Server=deletetest;Database=DeleteTestDb;"
                    }
                  """
             Then it should respond with 201
             When a DELETE request is made to "/v2/dmsInstances/{dmsInstanceId}"
             Then it should respond with 204
             When a GET request is made to "/v2/dmsInstances/{dmsInstanceId}"
             Then it should respond with 404

        Scenario: 07 Verify error handling when trying to update an item that has already been deleted
             When a POST request is made to "/v2/dmsInstances" with
                  """
                    {
                        "instanceType": "Test",
                        "instanceName": "Update Delete Instance",
                        "connectionString": "Server=updatedelete;Database=UpdateDeleteDb;"
                    }
                  """
             Then it should respond with 201
             When a DELETE request is made to "/v2/dmsInstances/{dmsInstanceId}"
             Then it should respond with 204
             When a PUT request is made to "/v2/dmsInstances/{dmsInstanceId}" with
                  """
                    {
                        "id": {dmsInstanceId},
                        "instanceType": "Production",
                        "instanceName": "Updated Delete Instance",
                        "connectionString": "Server=updateddelete;Database=UpdatedDeleteDb;"
                    }
                  """
             Then it should respond with 404

        Scenario: 08 Verify error handling when trying to delete an item that has already been deleted
             When a POST request is made to "/v2/dmsInstances" with
                  """
                    {
                        "instanceType": "Test",
                        "instanceName": "Double Delete Instance",
                        "connectionString": "Server=doubledelete;Database=DoubleDeleteDb;"
                    }
                  """
             Then it should respond with 201
             When a DELETE request is made to "/v2/dmsInstances/{dmsInstanceId}"
             Then it should respond with 204
             When a DELETE request is made to "/v2/dmsInstances/{dmsInstanceId}"
             Then it should respond with 404

        Scenario: 09 Verify error handling when trying to get a dmsInstance using an invalid id
             When a GET request is made to "/v2/dmsInstances/a"
             Then it should respond with 400

        Scenario: 10 Verify error handling when trying to delete a dmsInstance using an invalid id
             When a DELETE request is made to "/v2/dmsInstances/b"
             Then it should respond with 400

        Scenario: 11 Verify error handling when trying to update a dmsInstance using an invalid id
             When a PUT request is made to "/v2/dmsInstances/c" with
                  """
                    {
                        "id": 1,
                        "instanceType": "Production",
                        "instanceName": "Invalid ID Instance",
                        "connectionString": "Server=invalid;Database=InvalidDb;"
                    }
                  """
             Then it should respond with 400

        Scenario: 12 Verify validation invalid instanceType
             When a POST request is made to "/v2/dmsInstances" with
                  """
                    {
                        "instanceType": "",
                        "instanceName": "Test Instance",
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
                            "InstanceType": [
                                "'Instance Type' must not be empty."
                            ]
                        },
                        "errors": []
                    }
                  """

        Scenario: 13 Verify validation invalid instanceName
             When a POST request is made to "/v2/dmsInstances" with
                  """
                    {
                        "instanceType": "Production",
                        "instanceName": "",
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
                            "InstanceName": [
                                "'Instance Name' must not be empty."
                            ]
                        },
                        "errors": []
                    }
                  """

        Scenario: 14 Verify validation connectionString too long
             When a POST request is made to "/v2/dmsInstances" with
                  """
                    {
                        "instanceType": "Production",
                        "instanceName": "Long Connection String Instance",
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

        Scenario: 15 Verify PUT request with mismatched IDs
             When a POST request is made to "/v2/dmsInstances" with
                  """
                    {
                        "instanceType": "Production",
                        "instanceName": "Mismatch Test Instance",
                        "connectionString": "Server=mismatch;Database=MismatchDb;"
                    }
                  """
             Then it should respond with 201
             When a PUT request is made to "/v2/dmsInstances/{dmsInstanceId}" with
                  """
                    {
                        "id": 999,
                        "instanceType": "Production",
                        "instanceName": "Mismatched Instance",
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