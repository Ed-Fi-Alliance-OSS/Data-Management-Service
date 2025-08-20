Feature: ClaimSets endpoints

        Background:
            Given valid credentials
              And token received

        Scenario: 01 Ensure clients can GET claim sets
             When a GET request is made to "/v2/claimSets" for first 3 items
             Then it should respond with 200
              And the response body is
                  """
                  [
                      {
                          "id": {claimSetId:E2E-NameSpaceBasedClaimSet},
                          "name": "E2E-NameSpaceBasedClaimSet",
                          "_isSystemReserved": true,
                          "_applications": {}
                      },
                      {
                          "id": {claimSetId:E2E-NoFurtherAuthRequiredClaimSet},
                          "name": "E2E-NoFurtherAuthRequiredClaimSet",
                          "_isSystemReserved": true,
                          "_applications": {}
                      },
                      {
                          "id": {claimSetId:E2E-RelationshipsWithEdOrgsOnlyClaimSet},
                          "name": "E2E-RelationshipsWithEdOrgsOnlyClaimSet",
                          "_isSystemReserved": true,
                          "_applications": {}
                      }
                  ]
                  """

        Scenario: 02 Ensure clients can GET claim sets with offset
             When a GET request is made to "/v2/claimSets" for next 1 items after skipping 1 items
             Then it should respond with 200
              And the response body is
                  """
                  [
                      {
                          "id": {claimSetId:E2E-NoFurtherAuthRequiredClaimSet},
                          "name": "E2E-NoFurtherAuthRequiredClaimSet",
                          "_isSystemReserved": true,
                          "_applications": {}
                      }
                  ]
                  """

        Scenario: 03 Ensure clients can GET a claim set by ID
             When a GET request is made to "/v2/claimSets/{claimSetId:E2E-NoFurtherAuthRequiredClaimSet}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                      "id": {claimSetId:E2E-NoFurtherAuthRequiredClaimSet},
                      "name": "E2E-NoFurtherAuthRequiredClaimSet",
                      "_isSystemReserved": true,
                      "_applications": {}
                  }
                  """

        Scenario: 04 Ensure clients can create, update, get and delete a new claim set
             When a POST request is made to "/v2/claimSets" with
                  """
                  {
                      "name": "NewClaimSet"
                  }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                  {
                      "location": "/v2/claimSets/{claimSetId}"
                  }
                  """
             When a PUT request is made to "/v2/claimSets/{claimSetId}" with
                  """
                    {
                        "id": {claimSetId},
                        "name": "UpdatedClaimSet"
                    }
                  """
             Then it should respond with 204
             When a GET request is made to "/v2/claimSets/{claimSetId}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                      "id": {claimSetId},
                      "name": "UpdatedClaimSet",
                      "_isSystemReserved": false,
                      "_applications": {}
                  }
                  """
             When a DELETE request is made to "/v2/claimSets/{claimSetId}"
             Then it should respond with 204

        Scenario: 05 Verify error handling when trying to GET a non-existent claim set
             When a GET request is made to "/v2/claimSets/999"
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
                  | name                  | isSystemReserved |
                  | DuplicateTestClaimSet | false            |
             When a POST request is made to "/v2/claimSets" with
                  """
                  {
                      "name": "DuplicateTestClaimSet"
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
                          "Name": [
                              "A claim set with this name already exists in the database. Please enter a unique name."
                          ]
                      },
                      "errors": []
                  }
                  """

        Scenario: 07 Verify error handling when trying to update a system-reserved claim set
             When a PUT request is made to "/v2/claimSets/8" with
                  """
                  {
                      "id": 8,
                      "name": "UpdatedSystemReservedClaimSet"
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
             When a DELETE request is made to "/v2/claimSets/8"
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
             When a POST request is made to "/v2/claimSets" with
                  """
                  {
                      "name": "TestClaimSetMismatchedIds"
                  }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                  {
                      "location": "/v2/claimSets/{claimSetId}"
                  }
                  """
             When a PUT request is made to "/v2/claimSets/{claimSetId}" with
                  """
                  {
                      "id": 999,
                      "name": "TestClaimSetMismatchedIdsUpdated"
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
        Scenario: 10 Ensure clients can successfully import a valid claim set
             When a POST request is made to "/v2/claimSets/import" with
                  """
                  {
                      "name": "AcademicHonorClaimSet",
                      "resourceClaims": [
                          {
                              "name": "http://ed-fi.org/identity/claims/domains/systemDescriptors",
                              "actions": [
                                 { "name": "Create", "enabled": true }
                              ],
                              "children": [
                                  {
                                      "name": "http://ed-fi.org/identity/claims/ed-fi/academicHonorCategoryDescriptor",
                                      "actions": [
                                          { "name": "Create", "enabled": true },
                                          { "name": "Read", "enabled": true },
                                          { "name": "Update", "enabled": true },
                                          { "name": "Delete", "enabled": true }
                                      ],
                                      "children": []
                                  }
                              ]
                          }
                      ]
                  }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                  {
                      "location": "/v2/claimSets/{claimSetId}"
                  }
                  """

        Scenario: 11 Ensure clients cannot import an invalid claim set with empty actions
             When a POST request is made to "/v2/claimSets/import" with
                  """
                  {
                      "name": "InvalidClaimSet",
                      "resourceClaims": [
                          {
                              "name": "http://ed-fi.org/identity/claims/domains/systemDescriptors",
                              "actions": [
                                 { "name": "Create", "enabled": true}
                              ],
                              "children": [
                                  {
                                      "name": "http://ed-fi.org/identity/claims/ed-fi/academicHonorCategoryDescriptor",
                                      "actions": [],
                                      "children": []
                                  }
                              ]
                          }
                      ]
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
                          "ResourceClaims": [
                              "Actions can not be empty. Resource name: 'http://ed-fi.org/identity/claims/ed-fi/academicHonorCategoryDescriptor'"
                          ]
                      },
                      "errors": []
                  }
                  """
