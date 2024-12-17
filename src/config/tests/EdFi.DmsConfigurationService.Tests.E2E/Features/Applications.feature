Feature: Applications endpoints

        Background:
            Given valid credentials
              And token received
              And vendor created
                  """
                    {
                        "company": "Test Vendor 0",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "Test"
                    }
                  """

        Scenario: 01 Ensure clients can GET applications
            Given the system has these "applications"
                  | vendorId  | applicationName | claimSetName |
                  | _vendorId | application01   | claim01      |
                  | _vendorId | application02   | claim01      |
                  | _vendorId | application03   | claim01      |
                  | _vendorId | application04   | claim01      |
                  | _vendorId | application05   | claim01      |
             When a GET request is made to "/v2/applications?offset=0&limit=2"
             Then it should respond with 200
              And the response body is
                  """
                      [{
                          "id": {id},
                          "applicationName": "application01",
                          "claimSetName": "claim01",
                          "vendorId": {vendorId},
                          "educationOrganizationIds": []
                      },
                      {
                          "id": {id},
                          "applicationName": "application02",
                          "claimSetName": "claim01",
                          "vendorId": {vendorId},
                          "educationOrganizationIds": []
                      }]
                  """
             When a GET request is made to "/v2/applications"
             Then it should respond with 200
              And the response body is
                  """
                      [{
                          "id": {id},
                          "applicationName": "application01",
                          "claimSetName": "claim01",
                          "vendorId": {vendorId},
                          "educationOrganizationIds": []
                      },
                      {
                          "id": {id},
                          "applicationName": "application02",
                          "claimSetName": "claim01",
                          "vendorId": {vendorId},
                          "educationOrganizationIds": []
                      },
                      {
                          "id": {id},
                          "applicationName": "application03",
                          "claimSetName": "claim01",
                          "vendorId": {vendorId},
                          "educationOrganizationIds": []
                      },
                      {
                          "id": {id},
                          "applicationName": "application04",
                          "claimSetName": "claim01",
                          "vendorId": {vendorId},
                          "educationOrganizationIds": []
                      },
                      {
                          "id": {id},
                          "applicationName": "application05",
                          "claimSetName": "claim01",
                          "vendorId": {vendorId},
                          "educationOrganizationIds": []
                      }]
                  """

        Scenario: 02 Ensure clients can POST and GET application
             When a POST request is made to "/v2/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Demo application",
                   "claimSetName": "Claim 06",
                   "educationOrganizationIds": [1, 2, 3]
                  }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                    {
                        "location": "/v2/applications/{id}"
                    }
                  """
              And the response body has key and secret
              And the record can be retrieved with a GET request
                  """
                  {
                    "id": {id},
                    "applicationName": "Demo application",
                    "vendorId": {vendorId},
                    "claimSetName": "Claim 06",
                    "educationOrganizationIds": [1, 2, 3]
                  }
                  """

        Scenario: 03 Ensure clients can reset application credentials
             When a POST request is made to "/v2/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Scenario 02",
                   "claimSetName": "Test Scenario 02"
                  }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                    {
                        "location": "/v2/applications/{id}"
                    }
                  """
              And the response body has key and secret
             When a PUT request is made to "/v2/applications/{id}/reset-credential" with
                  """
                  {}
                  """
             Then it should respond with 200
              And the response body has key and secret

        Scenario: 04 Ensure clients can PUT and GET application
             When a POST request is made to "/v2/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Demo application",
                   "claimSetName": "Claim Scenario 03"
                  }
                  """
             Then it should respond with 201
             When a PUT request is made to "/v2/applications/{id}" with
                  """
                      {
                      "id": {id},
                      "vendorId": {vendorId},
                      "applicationName": "Demo application Update",
                      "claimSetName": "Claim Scenario 03 Update"
                      }
                  """
             Then it should respond with 204

        Scenario: 05 Ensure clients can DELETE an application
             When a POST request is made to "/v2/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Delete application",
                   "claimSetName": "Claim Scenario 05"
                  }
                  """
             Then it should respond with 201
             When a DELETE request is made to "/v2/applications/{id}"
             Then it should respond with 204

        Scenario: 06 Verify error handling when trying to get an item that has already been deleted
             When a POST request is made to "/v2/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Delete application",
                   "claimSetName": "Claim Scenario 06"
                  }
                  """
             Then it should respond with 201
             When a DELETE request is made to "/v2/applications/{id}"
             Then it should respond with 204
             When a GET request is made to "/v2/applications/{id}"
             Then it should respond with 404

        Scenario: 07 Verify error handling when trying to update an item that has already been deleted
             When a POST request is made to "/v2/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Delete application",
                   "claimSetName": "Claim Scenario 07"
                  }
                  """
             Then it should respond with 201
             When a DELETE request is made to "/v2/applications/{id}"
             Then it should respond with 204
             When a PUT request is made to "/v2/applications/{id}" with
                  """
                  {
                      "id": {id},
                      "vendorId": {vendorId},
                      "applicationName": "Delete application update",
                      "claimSetName": "Claim Scenario 07"
                  }
                  """
             Then it should respond with 404

        Scenario: 08 Verify error handling when trying to delete an item that has already been deleted
             When a POST request is made to "/v2/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Delete application",
                   "claimSetName": "Claim Scenario 08"
                  }
                  """
             Then it should respond with 201
             When a DELETE request is made to "/v2/applications/{id}"
             Then it should respond with 204
             When a DELETE request is made to "/v2/applications/{id}"
             Then it should respond with 404

        Scenario: 09 Verify error handling when trying to get an application using a invalid id
             When a GET request is made to "/v2/applications/a"
             Then it should respond with 400

        Scenario: 10 Verify error handling when trying to delete an application using a invalid id
             When a DELETE request is made to "/v2/applications/b"
             Then it should respond with 400

        Scenario: 11 Verify error handling when trying to update an application using a invalid id
             When a PUT request is made to "/v2/applications/c" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Delete application",
                   "claimSetName": "Claim Scenario 04"
                  }
                  """
             Then it should respond with 400

        Scenario: 12 Verify validation invalid vendor
             When a POST request is made to "/v2/applications" with
                  """
                  {
                   "vendorId": 9999,
                   "applicationName": "Demo application",
                   "claimSetName": "Claim 999",
                   "educationOrganizationIds": [1, 2, 3]
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
                    "correlationId": "0HN8RI9E3O45G:00000004",
                    "validationErrors": {
                    "VendorId": [
                      "Reference 'VendorId' does not exist."
                    ]
                  },
                  "errors": []
                  }
                  """

        Scenario: 13 Verify validation invalid applicationName
             When a POST request is made to "/v2/applications" with
                  """
                  {
                   "vendorId": 9999,
                   "applicationName": "",
                   "claimSetName": "Claim 999",
                   "educationOrganizationIds": [1, 2, 3]
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
                    "correlationId": "0HN8RI9E3O45G:00000004",
                    "validationErrors": {
                    "ApplicationName": [
                      "'Application Name' must not be empty."
                    ]
                  },
                  "errors": []
                  }
                  """

        Scenario: 14 Verify validation invalid claimsetName
             When a POST request is made to "/v2/applications" with
                  """
                  {
                   "vendorId": 9999,
                   "applicationName": "Test 1234",
                   "claimSetName": "",
                   "educationOrganizationIds": [1, 2, 3]
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
                    "correlationId": "0HN8RI9E3O45G:00000004",
                    "validationErrors": {
                    "ClaimSetName": [
                      "'Claim Set Name' must not be empty."
                    ]
                  },
                  "errors": []
                  }
                  """

        Scenario: 15 Verify validation invalid EducationOrganizationId
             When a POST request is made to "/v2/applications" with
                  """
                  {
                   "vendorId": 9999,
                   "applicationName": "Demo application",
                   "claimSetName": "Claim 999",
                   "educationOrganizationIds": [0]
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
                      "EducationOrganizationIds[0]": [
                      "'Education Organization Ids' must be greater than '0'."
                     ]
                    },
                   "errors": []
                  }
                  """
