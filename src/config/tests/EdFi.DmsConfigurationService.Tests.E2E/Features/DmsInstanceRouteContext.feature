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
              And the response "id" is saved as "instanceId"

        Scenario: Create a new instance route context
             When a POST request is made to "/v2/dmsInstanceRouteContexts" with
                  """
                  {
                    "instanceId": <instanceId>,
                    "contextKey": "schoolYear",
                    "contextValue": "2022"
                  }
                  """
             Then it should respond with 201
              And the response should contain "id"

        Scenario: List all instance route contexts
             When a GET request is made to "/v2/dmsInstanceRouteContexts"
             Then it should respond with 200
              And the response should contain the created route context

        Scenario: Get instance route context by ID
             When a GET request is made to "/v2/dmsInstanceRouteContexts/1"
             Then it should respond with 200
              And the response should contain "contextKey" as "schoolYear"
              And the response should contain "contextValue" as "2022"

        Scenario: Update instance route context by ID
             When a PUT request is made to "/v2/dmsInstanceRouteContexts/1" with
                  """
                  {
                    "id": 1,
                    "instanceId": <instanceId>,
                    "contextKey": "schoolYear",
                    "contextValue": "2023"
                  }
                  """
             Then it should respond with 204
              And the response should contain "contextValue" as "2023"

        Scenario: Delete instance route context by ID
             When a DELETE request is made to "/v2/dmsInstanceRouteContexts/1"
             Then it should respond with 204

