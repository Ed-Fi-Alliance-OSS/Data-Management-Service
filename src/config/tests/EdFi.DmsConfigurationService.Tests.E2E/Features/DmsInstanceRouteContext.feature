Feature: DMS Instance Route Context

        Background:
            Given valid credentials
              And token received
              And a POST request is made to "/v2/dmsInstances" with
                  """
                  {
                    "instanceType": "Production",
                    "instanceName": "Test Instance",
                    "connectionString": "Server=localhost;Database=TestDb;"
                  }
                  """

        @CleanupAfterScenario
        Scenario: 01 Ensure clients can create a new instance route context
             When a POST request is made to "/v2/dmsInstanceRouteContexts" with
                  """
                  {
                       "instanceId": {dmsInstanceId},
                       "contextKey": "schoolYear",
                       "contextValue": "2022"
                  }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                       {
                                 "location": "/v2/dmsInstanceRouteContexts/{dmsInstanceRouteContextId}"
                       }
                  """
              And the record can be retrieved with a GET request
                  """
                  {
                            "id": {id},
                            "instanceId": {dmsInstanceId},
                            "contextKey": "schoolYear",
                            "contextValue": "2022"
                  }
                  """

        @CleanupAfterScenario
        Scenario: 02 Ensure clients can GET dmsInstanceRouteContexts list
            Given a POST request is made to "/v2/dmsInstanceRouteContexts" with
                  """
                  {
                       "instanceId": {dmsInstanceId},
                       "contextKey": "schoolYear",
                       "contextValue": "2022"
                  }
                  """
             When a GET request is made to "/v2/dmsInstanceRouteContexts?offset=1&limit=1"
             Then it should respond with 200
              And the response body is
                  """
                      [{
                          "id": {id},
                          "instanceId": {dmsInstanceId},
                          "contextKey": "schoolYear",
                          "contextValue": "2022"
                      }]
                  """


        @CleanupAfterScenario
        Scenario: 03 Verify retrieving a single instance route context by ID
             When a POST request is made to "/v2/dmsInstanceRouteContexts" with
                  """
                  {
                       "instanceId": {dmsInstanceId},
                       "contextKey": "schoolYear",
                       "contextValue": "2022"
                  }
                  """
             Then it should respond with 201
             When a GET request is made to "/v2/dmsInstanceRouteContexts/{dmsInstanceRouteContextId}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                       "id": {id},
                       "instanceId": {dmsInstanceId},
                       "contextKey": "schoolYear",
                       "contextValue": "2022"
                  }
                  """

        @CleanupAfterScenario
        Scenario: 04 Put an existing instance route context
             When a POST request is made to "/v2/dmsInstanceRouteContexts" with
                  """
                  {
                       "instanceId": {dmsInstanceId},
                       "contextKey": "schoolYear",
                       "contextValue": "2022"
                  }
                  """
             Then it should respond with 201
             When a PUT request is made to "/v2/dmsInstanceRouteContexts/{dmsInstanceRouteContextId}" with
                  """
                  {
                    "id": {dmsInstanceRouteContextId},
                    "instanceId": {dmsInstanceId},
                    "contextKey": "schoolYear",
                    "contextValue": "2023"
                  }
                  """
             Then it should respond with 204
              And the record can be retrieved with a GET request
                  """
                  {
                       "id": {id},
                       "instanceId": {dmsInstanceId},
                       "contextKey": "schoolYear",
                       "contextValue": "2023"
                  }
                  """

        Scenario: 05 Verify deleting a specific instance route context by ID
             When a POST request is made to "/v2/dmsInstanceRouteContexts" with
                  """
                  {
                       "instanceId": {dmsInstanceId},
                       "contextKey": "schoolYear",
                       "contextValue": "2022"
                  }
                  """
             Then it should respond with 201
             When a DELETE request is made to "/v2/dmsInstanceRouteContexts/{dmsInstanceRouteContextId}"
             Then it should respond with 204

        Scenario: 06 Verify error handling when trying to get an item that has already been deleted
             When a POST request is made to "/v2/dmsInstanceRouteContexts" with
                  """
                  {
                       "instanceId": {dmsInstanceId},
                       "contextKey": "schoolYear",
                       "contextValue": "2022"
                  }
                  """
             Then it should respond with 201
             When a DELETE request is made to "/v2/dmsInstanceRouteContexts/{dmsInstanceRouteContextId}"
             Then it should respond with 204
             When a GET request is made to "/v2/dmsInstanceRouteContexts/{dmsInstanceRouteContextId}"
             Then it should respond with 404

        Scenario: 07 Verify error handling when using invalid ID
             When a GET request is made to "/v2/dmsInstanceRouteContexts/invalid"
             Then it should respond with 400

        Scenario: 08 Verify PUT request with mismatched IDs
             When a POST request is made to "/v2/dmsInstanceRouteContexts" with
                  """
                  {
                       "instanceId": {dmsInstanceId},
                       "contextKey": "schoolYear",
                       "contextValue": "2022"
                  }
                  """
             Then it should respond with 201
             When a PUT request is made to "/v2/dmsInstanceRouteContexts/{dmsInstanceRouteContextId}" with
                  """
                  {
                    "id": 999,
                    "instanceId": {dmsInstanceId},
                    "contextKey": "schoolYear",
                    "contextValue": "2023"
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
