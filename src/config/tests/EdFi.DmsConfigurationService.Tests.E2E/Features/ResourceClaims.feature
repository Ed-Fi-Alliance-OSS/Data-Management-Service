Feature: ResourceClaims endpoints

        Background:
            Given valid credentials
              And token received

        @MssqlRepresentative
        Scenario: 01 Ensure clients can GET resource claims list
             When a GET request is made to "/v3/resourceClaims"
             Then it should respond with 200
              And the response body is a non-empty array

        Scenario: 02 Ensure clients can GET resource claims with paging
             When a GET request is made to "/v3/resourceClaims?limit=2&offset=0"
             Then it should respond with 200
              And the response body is a non-empty array

        Scenario: 03 Ensure clients can GET resource claims with sorting
             When a GET request is made to "/v3/resourceClaims?orderBy=name&direction=asc"
             Then it should respond with 200
              And the response body is a non-empty array

        @MssqlRepresentative
        Scenario: 04 Ensure clients can GET a resource claim by ID
             When a GET request is made to "/v3/resourceClaims/1"
             Then it should respond with 200
              And the response body has property "id"
              And the response body has property "name"
              And the response body has property "children"

        Scenario: 05 Ensure clients get 404 for non-existent resource claim ID
             When a GET request is made to "/v3/resourceClaims/999999"
             Then it should respond with 404

        Scenario: 06 Ensure clients can filter resource claims by name
             When a GET request is made to "/v3/resourceClaims?name=assessmentMetadata"
             Then it should respond with 200
              And the response body is an array
              And the response body contains an item with property "name" having value "assessmentMetadata"

        Scenario: 07 Ensure validation fails for unsupported orderBy field
             When a GET request is made to "/v3/resourceClaims?orderBy=unsupportedField"
             Then it should respond with 400

        Scenario: 08 Ensure clients can GET resource claim actions
             When a GET request is made to "/v3/resourceClaimActions"
             Then it should respond with 200
              And the response body is a non-empty array

        Scenario: 09 Ensure clients can filter resource claim actions by resourceName
             When a GET request is made to "/v3/resourceClaimActions?resourceName=assessmentMetadata"
             Then it should respond with 200
              And the response body is an array
              And the response body contains an item with property "resourceName" having value "assessmentMetadata"

        Scenario: 10 Ensure clients can GET resource claim action auth strategies
             When a GET request is made to "/v3/resourceClaimActionAuthStrategies"
             Then it should respond with 200
              And the response body is a non-empty array

        Scenario: 11 Ensure clients can filter resource claim action auth strategies by resourceName
             When a GET request is made to "/v3/resourceClaimActionAuthStrategies?resourceName=assessmentMetadata"
             Then it should respond with 200
              And the response body is an array
              And the response body contains an item with property "resourceName" having value "assessmentMetadata"

        Scenario: 12 Ensure resource claims include recursive children structure
             When a GET request is made to "/v3/resourceClaims/1"
             Then it should respond with 200
              And the response body has property "children"
              And the response body property "children" is an array

        Scenario: 13 Ensure resource claim actions include action names
             When a GET request is made to "/v3/resourceClaimActions?limit=1"
             Then it should respond with 200
              And the response body is a non-empty array
              And the first response item has property "actions"
              And the first response item property "actions" is an array

        Scenario: 14 Ensure resource claim action auth strategies include auth strategies
             When a GET request is made to "/v3/resourceClaimActionAuthStrategies?limit=1"
             Then it should respond with 200
              And the response body is a non-empty array
              And the first response item has property "authorizationStrategiesForActions"
              And the first response item property "authorizationStrategiesForActions" is an array
