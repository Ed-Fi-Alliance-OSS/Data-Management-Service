Feature: ClaimSets endpoints

        Background:
            Given valid credentials
              And token received

        @MssqlRepresentative
        Scenario: 01 Ensure clients can GET claim sets
             When a GET request is made to "/v3/claimSets" for first 3 items
             Then it should respond with 200
              And the response body is
                  """
                  [
                      {
                          "id": {claimSetId:SISVendor},
                          "claimSetName": "SISVendor",
                          "_isSystemReserved": true,
                          "_applications": []
                      },
                      {
                          "id": {claimSetId:EdFiSandbox},
                          "claimSetName": "EdFiSandbox",
                          "_isSystemReserved": true,
                          "_applications": []
                      },
                      {
                          "id": {claimSetId:RosterVendor},
                          "claimSetName": "RosterVendor",
                          "_isSystemReserved": true,
                          "_applications": []
                      }
                  ]
                  """

        Scenario: 02 Ensure clients can GET claim sets with offset
             When a GET request is made to "/v3/claimSets" for next 1 items after skipping 1 items
             Then it should respond with 200
              And the response body is
                  """
                  [
                      {
                          "id": {claimSetId:EdFiSandbox},
                          "claimSetName": "EdFiSandbox",
                          "_isSystemReserved": true,
                          "_applications": []
                      }
                  ]
                  """

        Scenario: 03 Ensure clients can GET a claim set by ID
             When a GET request is made to "/v3/claimSets/{claimSetId:E2E-NoFurtherAuthRequiredClaimSet}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                      "id": {claimSetId:E2E-NoFurtherAuthRequiredClaimSet},
                      "claimSetName": "E2E-NoFurtherAuthRequiredClaimSet",
                      "_isSystemReserved": true,
                      "_applications": [],
                      "resourceClaims": "{*}"
                  }
                  """

        Scenario: 04 Ensure clients can create, update, get and delete a new claim set
             When a POST request is made to "/v3/claimSets" with
                  """
                  {
                      "claimSetName": "NewClaimSet"
                  }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                  {
                      "location": "/v3/claimSets/{claimSetId}"
                  }
                  """
             When a PUT request is made to "/v3/claimSets/{claimSetId}" with
                  """
                    {
                        "id": {claimSetId},
                        "claimSetName": "UpdatedClaimSet"
                    }
                  """
             Then it should respond with 204
             When a GET request is made to "/v3/claimSets/{claimSetId}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                      "id": {claimSetId},
                      "claimSetName": "UpdatedClaimSet",
                      "_isSystemReserved": false,
                      "_applications": [],
                      "resourceClaims": "{*}"
                  }
                  """
             When a DELETE request is made to "/v3/claimSets/{claimSetId}"
             Then it should respond with 204

        Scenario: 05 Verify error handling when trying to GET a non-existent claim set
             When a GET request is made to "/v3/claimSets/999"
             Then it should respond with 404
              And the response body is
                  """
                  {
                      "detail": "ClaimSet 999 not found. It may have been recently deleted.",
                      "type": "urn:ed-fi:api:not-found",
                      "title": "Not Found",
                      "status": 404,
                      "validationErrors": {},
                      "errors": []
                  }
                  """

        Scenario: 06 Verify error handling when trying to POST a duplicate claim set name
            Given the system has these "claimSets"
                  | claimSetName          | isSystemReserved |
                  | DuplicateTestClaimSet | false            |
             When a POST request is made to "/v3/claimSets" with
                  """
                  {
                      "claimSetName": "DuplicateTestClaimSet"
                  }
                  """
             Then it should respond with 409
              And the response body is
                  """
                  {
                      "detail": "The identifying value(s) of the item are the same as another item that already exists.",
                      "type": "urn:ed-fi:api:conflict:non-unique-identity",
                      "title": "Identifying Values Are Not Unique",
                      "status": 409,
                      "validationErrors": {},
                      "errors": [
                          "A claim set with this name already exists."
                      ]
                  }
                  """

        Scenario: 07 Verify error handling when trying to update a system-reserved claim set
             When a PUT request is made to "/v3/claimSets/8" with
                  """
                  {
                      "id": 8,
                      "claimSetName": "UpdatedSystemReservedClaimSet"
                  }
                  """
             Then it should respond with 400
              And the response body is
                  """
                  {
                      "detail": "The specified claim set is system-reserved and cannot be updated.",
                      "type": "urn:ed-fi:api:bad-request",
                      "title": "Bad Request",
                      "status": 400,
                      "validationErrors": {},
                      "errors": []
                  }
                  """

        Scenario: 08 Verify error handling when trying to delete a system-reserved claim set
             When a DELETE request is made to "/v3/claimSets/8"
             Then it should respond with 400
              And the response body is
                  """
                  {
                      "detail": "The specified claim set is system-reserved and cannot be deleted.",
                      "type": "urn:ed-fi:api:bad-request",
                      "title": "Bad Request",
                      "status": 400,
                      "validationErrors": {},
                      "errors": []
                  }
                  """

        Scenario: 09 Verify error handling when trying to update a claim set with mismatched IDs
             When a POST request is made to "/v3/claimSets" with
                  """
                  {
                      "claimSetName": "TestClaimSetMismatchedIds"
                  }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                  {
                      "location": "/v3/claimSets/{claimSetId}"
                  }
                  """
             When a PUT request is made to "/v3/claimSets/{claimSetId}" with
                  """
                  {
                      "id": 999,
                      "claimSetName": "TestClaimSetMismatchedIdsUpdated"
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
        @MssqlRepresentative
        Scenario: 10 Ensure clients can successfully import a valid claim set
             When a POST request is made to "/v3/claimSets/import" with
                  """
                  {
                      "claimSetName": "AcademicHonorClaimSet",
                      "resourceClaims": [
                          {
                              "name": "systemDescriptors",
                              "claimName": "http://ed-fi.org/identity/claims/domains/systemDescriptors",
                              "actions": [
                                 { "name": "Create", "enabled": true }
                              ]
                          },
                          {
                              "name": "academicHonorCategoryDescriptor",
                              "claimName": "http://ed-fi.org/identity/claims/ed-fi/academicHonorCategoryDescriptor",
                              "parentClaimName": "http://ed-fi.org/identity/claims/domains/systemDescriptors",
                              "actions": [
                                  { "name": "Create", "enabled": true },
                                  { "name": "Read", "enabled": true },
                                  { "name": "Update", "enabled": true },
                                  { "name": "Delete", "enabled": true }
                              ]
                          }
                      ]
                  }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                  {
                      "location": "/v3/claimSets/{claimSetId}"
                  }
                  """

        Scenario: 11 Ensure clients cannot import an invalid claim set with empty actions
             When a POST request is made to "/v3/claimSets/import" with
                  """
                  {
                      "claimSetName": "InvalidClaimSet",
                      "resourceClaims": [
                          {
                              "name": "systemDescriptors",
                              "claimName": "http://ed-fi.org/identity/claims/domains/systemDescriptors",
                              "actions": [
                                 { "name": "Create", "enabled": true}
                              ]
                          },
                          {
                              "name": "academicHonorCategoryDescriptor",
                              "claimName": "http://ed-fi.org/identity/claims/ed-fi/academicHonorCategoryDescriptor",
                              "parentClaimName": "http://ed-fi.org/identity/claims/domains/systemDescriptors",
                              "actions": []
                          }
                      ]
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
                          "ResourceClaims": [
                              "Actions can not be empty. Resource name: 'http://ed-fi.org/identity/claims/ed-fi/academicHonorCategoryDescriptor'"
                          ]
                      },
                      "errors": []
                  }
                  """
        Scenario: 12 Verify copy rejects a claim set name with white space the same way insert does
             When a POST request is made to "/v3/claimSets/copy" with
                  """
                  {
                      "originalId": {claimSetId:E2E-NoFurtherAuthRequiredClaimSet},
                      "claimSetName": "DistrictHostedSISVendor (copy)"
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
                              "Claim set name must not contain white spaces."
                          ]
                      },
                      "errors": []
                  }
                  """
             When a POST request is made to "/v3/claimSets" with
                  """
                  {
                      "claimSetName": "DistrictHostedSISVendor (copy)"
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
                              "Claim set name must not contain white spaces."
                          ]
                      },
                      "errors": []
                  }
                  """

        Scenario: 13 Ensure clients can copy a claim set with a valid name and keep its resource claims
             When a POST request is made to "/v3/claimSets/import" with
                  """
                  {
                      "claimSetName": "CopySourceClaimSet",
                      "resourceClaims": [
                          {
                              "name": "systemDescriptors",
                              "claimName": "http://ed-fi.org/identity/claims/domains/systemDescriptors",
                              "actions": [
                                 { "name": "Create", "enabled": true }
                              ]
                          },
                          {
                              "name": "academicHonorCategoryDescriptor",
                              "claimName": "http://ed-fi.org/identity/claims/ed-fi/academicHonorCategoryDescriptor",
                              "parentClaimName": "http://ed-fi.org/identity/claims/domains/systemDescriptors",
                              "actions": [
                                  { "name": "Create", "enabled": true },
                                  { "name": "Read", "enabled": true }
                              ]
                          }
                      ]
                  }
                  """
             Then it should respond with 201
              And the response location id is captured as "sourceClaimSetId"
             When a GET request is made to "/v3/claimSets/{sourceClaimSetId}"
             Then it should respond with 200
              And the response body property "resourceClaims" is captured as "sourceResourceClaims"
             When a POST request is made to "/v3/claimSets/copy" with
                  """
                  {
                      "originalId": {sourceClaimSetId},
                      "claimSetName": "CopySourceClaimSet-Copy"
                  }
                  """
             Then it should respond with 201
              And the response location id is captured as "copiedClaimSetId"
             When a GET request is made to "/v3/claimSets/{copiedClaimSetId}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                      "id": {copiedClaimSetId},
                      "claimSetName": "CopySourceClaimSet-Copy",
                      "_isSystemReserved": false,
                      "_applications": [],
                      "resourceClaims": "{*}"
                  }
              """
              And the response body property "resourceClaims" equals the value captured as "sourceResourceClaims"

        Scenario: 14 Ensure clients can grant selected resource claim actions
             When a POST request is made to "/v3/claimSets" with
                  """
                  {
                      "claimSetName": "DMS-853ResourceActionGrant{scenarioRunId}"
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/v3/claimSets/{claimSetId}/resourceClaimActions" with
                  """
                  {
                      "claimSetId": {claimSetId},
                      "resourceClaimId": 233,
                      "resourceClaimActions": [
                          { "name": "Read", "enabled": true },
                          { "name": "Update", "enabled": false }
                      ]
                  }
                  """
             Then it should respond with 201

        Scenario: 15 Ensure clients can override a resource claim action authorization strategy
             When a POST request is made to "/v3/claimSets" with
                  """
                  {
                      "claimSetName": "DMS-853AuthorizationStrategyOverride{scenarioRunId}"
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/v3/claimSets/{claimSetId}/resourceClaimActions" with
                  """
                  {
                      "claimSetId": {claimSetId},
                      "resourceClaimId": 233,
                      "resourceClaimActions": [
                          { "name": "Read", "enabled": true }
                      ]
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/v3/claimSets/{claimSetId}/resourceClaimActions/233/overrideAuthorizationStrategy" with
                  """
                  {
                      "claimSetId": {claimSetId},
                      "resourceClaimId": 233,
                      "actionName": "Read",
                      "authStrategyIds": [1],
                      "authorizationStrategies": ["NoFurtherAuthorizationRequired"]
                  }
                  """
             Then it should respond with 200

        Scenario: 16 Ensure clients can reset resource claim action authorization strategies
             When a POST request is made to "/v3/claimSets" with
                  """
                  {
                      "claimSetName": "DMS-853AuthorizationStrategyReset{scenarioRunId}"
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/v3/claimSets/{claimSetId}/resourceClaimActions" with
                  """
                  {
                      "claimSetId": {claimSetId},
                      "resourceClaimId": 233,
                      "resourceClaimActions": [
                          { "name": "Read", "enabled": true }
                      ]
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/v3/claimSets/{claimSetId}/resourceClaimActions/233/overrideAuthorizationStrategy" with
                  """
                  {
                      "claimSetId": {claimSetId},
                      "resourceClaimId": 233,
                      "actionName": "Read",
                      "authStrategyIds": [1],
                      "authorizationStrategies": ["NoFurtherAuthorizationRequired"]
                  }
                  """
             Then it should respond with 200
             When a GET request is made to "/v3/claimSets/{claimSetId}"
             Then it should respond with 200
              And the response body contains a non-empty authorization strategy override
             When a POST request is made to "/v3/claimSets/{claimSetId}/resourceClaimActions/233/resetAuthorizationStrategies" with no body
             Then it should respond with 200
             When a GET request is made to "/v3/claimSets/{claimSetId}"
             Then it should respond with 200
              And the response body contains no non-empty authorization strategy overrides
