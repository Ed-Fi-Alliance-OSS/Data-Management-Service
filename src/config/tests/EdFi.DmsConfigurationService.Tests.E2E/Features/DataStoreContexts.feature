Feature: Data Store Context

        Background:
            Given valid credentials
              And token received
              And a POST request is made to "/v3/dataStores" with
                  """
                  {
                    "dataStoreType": "Production",
                    "name": "Test Instance",
                    "connectionString": "Server=localhost;Database=TestDb;"
                  }
                  """


        @MssqlRepresentative
        Scenario: 01 Ensure clients can create a new data store context
             When a POST request is made to "/v3/dataStoreContexts" with
                  """
                  {
                       "dataStoreId": {dataStoreId},
                       "contextKey": "schoolYear",
                       "contextValue": "2022"
                  }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                       {
                                 "location": "/v3/dataStoreContexts/{dataStoreContextId}"
                       }
                  """
              And the record can be retrieved with a GET request
                  """
                  {
                            "id": {id},
                            "dataStoreId": {dataStoreId},
                            "contextKey": "schoolYear",
                            "contextValue": "2022"
                  }
                  """
             When a DELETE request is made to "/v3/dataStoreContexts/{dataStoreContextId}"
             Then it should respond with 204
             When a DELETE request is made to "/v3/dataStores/{dataStoreId}"
             Then it should respond with 204


        Scenario: 02 Ensure clients can GET dataStoreContexts list
            Given a POST request is made to "/v3/dataStoreContexts" with
                  """
                  {
                       "dataStoreId": {dataStoreId},
                       "contextKey": "schoolYear",
                       "contextValue": "2022"
                  }
                  """
             When a GET request is made to "/v3/dataStoreContexts?offset=0&limit=1"
             Then it should respond with 200
              And the response body is
                  """
                      [{
                          "id": {id},
                          "dataStoreId": {dataStoreId},
                          "contextKey": "schoolYear",
                          "contextValue": "2022"
                      }]
                  """
             When a DELETE request is made to "/v3/dataStoreContexts/{dataStoreContextId}"
             Then it should respond with 204
             When a DELETE request is made to "/v3/dataStores/{dataStoreId}"
             Then it should respond with 204



        Scenario: 03 Verify retrieving a single data store context by ID
             When a POST request is made to "/v3/dataStoreContexts" with
                  """
                  {
                       "dataStoreId": {dataStoreId},
                       "contextKey": "schoolYear",
                       "contextValue": "2022"
                  }
                  """
             Then it should respond with 201
             When a GET request is made to "/v3/dataStoreContexts/{dataStoreContextId}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                       "id": {id},
                       "dataStoreId": {dataStoreId},
                       "contextKey": "schoolYear",
                       "contextValue": "2022"
                  }
                  """
             When a DELETE request is made to "/v3/dataStoreContexts/{dataStoreContextId}"
             Then it should respond with 204
             When a DELETE request is made to "/v3/dataStores/{dataStoreId}"
             Then it should respond with 204


        Scenario: 04 Put an existing data store context
             When a POST request is made to "/v3/dataStoreContexts" with
                  """
                  {
                       "dataStoreId": {dataStoreId},
                       "contextKey": "schoolYear",
                       "contextValue": "2022"
                  }
                  """
             Then it should respond with 201
             When a PUT request is made to "/v3/dataStoreContexts/{dataStoreContextId}" with
                  """
                  {
                    "id": {dataStoreContextId},
                    "dataStoreId": {dataStoreId},
                    "contextKey": "schoolYear",
                    "contextValue": "2023"
                  }
                  """
             Then it should respond with 204
              And the record can be retrieved with a GET request
                  """
                  {
                       "id": {id},
                       "dataStoreId": {dataStoreId},
                       "contextKey": "schoolYear",
                       "contextValue": "2023"
                  }
                  """
             When a DELETE request is made to "/v3/dataStoreContexts/{dataStoreContextId}"
             Then it should respond with 204
             When a DELETE request is made to "/v3/dataStores/{dataStoreId}"
             Then it should respond with 204

        Scenario: 05 Verify deleting a specific data store context by ID
             When a POST request is made to "/v3/dataStoreContexts" with
                  """
                  {
                       "dataStoreId": {dataStoreId},
                       "contextKey": "schoolYear",
                       "contextValue": "2022"
                  }
                  """
             Then it should respond with 201
             When a DELETE request is made to "/v3/dataStoreContexts/{dataStoreContextId}"
             Then it should respond with 204
             When a DELETE request is made to "/v3/dataStores/{dataStoreId}"
             Then it should respond with 204

        Scenario: 06 Verify error handling when trying to get an item that has already been deleted
             When a POST request is made to "/v3/dataStoreContexts" with
                  """
                  {
                       "dataStoreId": {dataStoreId},
                       "contextKey": "schoolYear",
                       "contextValue": "2022"
                  }
                  """
             Then it should respond with 201
             When a DELETE request is made to "/v3/dataStoreContexts/{dataStoreContextId}"
             Then it should respond with 204
             When a GET request is made to "/v3/dataStoreContexts/{dataStoreContextId}"
             Then it should respond with 404
             When a DELETE request is made to "/v3/dataStores/{dataStoreId}"
             Then it should respond with 204

        Scenario: 07 Verify error handling when using invalid ID
             When a GET request is made to "/v3/dataStoreContexts/invalid"
             Then it should respond with 400
             When a DELETE request is made to "/v3/dataStores/{dataStoreId}"
             Then it should respond with 204

        Scenario: 08 Verify PUT request with mismatched IDs
             When a POST request is made to "/v3/dataStoreContexts" with
                  """
                  {
                       "dataStoreId": {dataStoreId},
                       "contextKey": "schoolYear",
                       "contextValue": "2022"
                  }
                  """
             Then it should respond with 201
             When a PUT request is made to "/v3/dataStoreContexts/{dataStoreContextId}" with
                  """
                  {
                    "id": 999,
                    "dataStoreId": {dataStoreId},
                    "contextKey": "schoolYear",
                    "contextValue": "2023"
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
             When a DELETE request is made to "/v3/dataStoreContexts/{dataStoreContextId}"
             Then it should respond with 204
             When a DELETE request is made to "/v3/dataStores/{dataStoreId}"
             Then it should respond with 204

        Scenario: 09 Verify contexts appear in data store GET response
             When a POST request is made to "/v3/dataStoreContexts" with
                  """
                  {
                       "dataStoreId": {dataStoreId},
                       "contextKey": "schoolYear",
                       "contextValue": "2024"
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/v3/dataStoreContexts" with
                  """
                  {
                       "dataStoreId": {dataStoreId},
                       "contextKey": "environment",
                       "contextValue": "production"
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
                       "name": "Test Instance",
                       "connectionString": "{ignore}",
                       "dataStoreContexts": [
                           {
                               "id": "{*}",
                               "dataStoreId": {id},
                               "contextKey": "environment",
                               "contextValue": "production"
                           },
                           {
                               "id": "{*}",
                               "dataStoreId": {id},
                               "contextKey": "schoolYear",
                               "contextValue": "2024"
                           }
                       ],
                       "dataStoreDerivatives": []
                  }
                  """
             When a DELETE request is made to "/v3/dataStores/{dataStoreId}"
             Then it should respond with 204
