Feature: Applications endpoints

        Background:
            Given valid credentials
              And token received
              And a POST request is made to "/v3/vendors" with
                  """
                    {
                        "company": "Test Vendor 0",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "uri://ed-fi-e2e.org,uri://ed-fi-e2e2.org"
                    }
                  """
              And a POST request is made to "/v3/dataStores" with
                  """
                    {
                        "dataStoreType": "Test",
                        "name": "Test Data Store",
                        "connectionString": "Server=test;Database=TestDb;"
                    }
                  """
              And a POST request is made to "/v3/profiles" with
                  """
                    {
                        "name": "TestProfile_{scenarioRunId}",
                        "definition": "<Profile name=\"TestProfile_{scenarioRunId}\"><Resource name=\"School\"></Resource></Profile>"
                    }
                  """

        Scenario: 01 Ensure clients can GET applications
            Given a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "application01",
                   "claimSetName": "claim01",
                   "educationOrganizationIds": [],
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
              And a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "application02",
                   "claimSetName": "claim01",
                   "educationOrganizationIds": [],
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
              And a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "application03",
                   "claimSetName": "claim01",
                   "educationOrganizationIds": [],
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
              And a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "application04",
                   "claimSetName": "claim01",
                   "educationOrganizationIds": [],
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
              And a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "application05",
                   "claimSetName": "claim01",
                   "educationOrganizationIds": [],
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             When a GET request is made to "/v3/applications?offset=0&limit=2"
             Then it should respond with 200
              And the response body is
                  """
                      [{
                          "id": {id},
                          "applicationName": "application01",
                          "claimSetName": "claim01",
                          "vendorId": {vendorId},
                          "educationOrganizationIds": [],
                          "enabled": true,
                          "dataStoreIds": [{dataStoreId}],
                          "profileIds": []
                      },
                      {
                          "id": {id},
                          "applicationName": "application02",
                          "claimSetName": "claim01",
                          "vendorId": {vendorId},
                          "educationOrganizationIds": [],
                          "enabled": true,
                          "dataStoreIds": [{dataStoreId}],
                          "profileIds": []
                      }]
                  """
             When a GET request is made to "/v3/applications"
             Then it should respond with 200
              And the response body is
                  """
                      [{
                          "id": {id},
                          "applicationName": "application01",
                          "claimSetName": "claim01",
                          "vendorId": {vendorId},
                          "educationOrganizationIds": [],
                          "enabled": true,
                          "dataStoreIds": [{dataStoreId}],
                          "profileIds": []
                      },
                      {
                          "id": {id},
                          "applicationName": "application02",
                          "claimSetName": "claim01",
                          "vendorId": {vendorId},
                          "educationOrganizationIds": [],
                          "enabled": true,
                          "dataStoreIds": [{dataStoreId}],
                          "profileIds": []
                      },
                      {
                          "id": {id},
                          "applicationName": "application03",
                          "claimSetName": "claim01",
                          "vendorId": {vendorId},
                          "educationOrganizationIds": [],
                          "enabled": true,
                          "dataStoreIds": [{dataStoreId}],
                          "profileIds": []
                      },
                      {
                          "id": {id},
                          "applicationName": "application04",
                          "claimSetName": "claim01",
                          "vendorId": {vendorId},
                          "educationOrganizationIds": [],
                          "enabled": true,
                          "dataStoreIds": [{dataStoreId}],
                          "profileIds": []
                      },
                      {
                          "id": {id},
                          "applicationName": "application05",
                          "claimSetName": "claim01",
                          "vendorId": {vendorId},
                          "educationOrganizationIds": [],
                          "enabled": true,
                          "dataStoreIds": [{dataStoreId}],
                          "profileIds": []
                      }]
                  """

        @MssqlRepresentative
        Scenario: 02 Ensure clients can POST and GET application
             When a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Demo application 02",
                   "claimSetName": "Claim06",
                   "educationOrganizationIds": [1, 2, 3],
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                    {
                        "location": "/v3/applications/{applicationId}"
                    }
                  """
              And the response body has key and secret
              And the record can be retrieved with a GET request
                  """
                  {
                    "id": {applicationId},
                    "applicationName": "Demo application 02",
                    "vendorId": {vendorId},
                    "claimSetName": "Claim06",
                    "educationOrganizationIds": [1, 2, 3],
                    "dataStoreIds": [{dataStoreId}],
                    "profileIds": [],
                    "enabled": true
                  }
                  """

        Scenario: 03 Ensure clients can reset application credentials
             When a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Test Scenario 02",
                   "claimSetName": "TestScenario02",
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                    {
                        "location": "/v3/applications/{applicationId}"
                    }
                  """
              And the response body has key and secret
             When a PUT request is made to "/v3/applications/{applicationId}/reset-credential" with
                  """
                  {}
                  """
             Then it should respond with 200
              And the response body has key and secret

        Scenario: 04 Ensure clients can PUT and GET application
             When a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Scenario 04 Demo application",
                   "claimSetName": "ClaimScenario03",
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             Then it should respond with 201
             When a PUT request is made to "/v3/applications/{applicationId}" with
                  """
                      {
                      "id": {applicationId},
                      "vendorId": {vendorId},
                      "applicationName": "Demo application Update",
                      "claimSetName": "ClaimScenario03Update",
                      "dataStoreIds": [{dataStoreId}]
                      }
                  """
             Then it should respond with 204

        Scenario: 05 Ensure clients can DELETE an application
             When a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Delete application",
                   "claimSetName": "ClaimScenario05",
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             Then it should respond with 201
             When a DELETE request is made to "/v3/applications/{applicationId}"
             Then it should respond with 204

        Scenario: 06 Verify error handling when trying to get an item that has already been deleted
             When a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Delete application",
                   "claimSetName": "ClaimScenario06",
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             Then it should respond with 201
             When a DELETE request is made to "/v3/applications/{applicationId}"
             Then it should respond with 204
             When a GET request is made to "/v3/applications/{applicationId}"
             Then it should respond with 404

        Scenario: 07 Verify error handling when trying to update an item that has already been deleted
             When a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Delete application",
                   "claimSetName": "ClaimScenario07",
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             Then it should respond with 201
             When a DELETE request is made to "/v3/applications/{applicationId}"
             Then it should respond with 204
             When a PUT request is made to "/v3/applications/{applicationId}" with
                  """
                  {
                      "id": {applicationId},
                      "vendorId": {vendorId},
                      "applicationName": "Delete application update",
                      "claimSetName": "ClaimScenario07",
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             Then it should respond with 404

        Scenario: 08 Verify error handling when trying to delete an item that has already been deleted
             When a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Delete application",
                   "claimSetName": "ClaimScenario08",
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             Then it should respond with 201
             When a DELETE request is made to "/v3/applications/{applicationId}"
             Then it should respond with 204
             When a DELETE request is made to "/v3/applications/{applicationId}"
             Then it should respond with 404

        Scenario: 09 Verify error handling when trying to get an application using a invalid id
             When a GET request is made to "/v3/applications/a"
             Then it should respond with 400

        Scenario: 10 Verify error handling when trying to delete an application using a invalid id
             When a DELETE request is made to "/v3/applications/b"
             Then it should respond with 400

        Scenario: 11 Verify error handling when trying to update an application using a invalid id
             When a PUT request is made to "/v3/applications/c" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Delete application",
                   "claimSetName": "ClaimScenario04",
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             Then it should respond with 400

        Scenario: 12 Verify validation invalid vendor
             When a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": 9999,
                   "applicationName": "Demo application",
                   "claimSetName": "Claim999",
                   "educationOrganizationIds": [1, 2, 3],
                   "dataStoreIds": [{dataStoreId}]
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
             When a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": 9999,
                   "applicationName": "",
                   "claimSetName": "Claim999",
                   "educationOrganizationIds": [1, 2, 3],
                   "dataStoreIds": [{dataStoreId}]
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
             When a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": 9999,
                   "applicationName": "Test 1234",
                   "claimSetName": "",
                   "educationOrganizationIds": [1, 2, 3],
                   "dataStoreIds": [{dataStoreId}]
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
                    "correlationId": "0HN8RI9E3O45G:00000004",
                    "validationErrors": {
                    "ClaimSetName": [
                      "'Claim Set Name' must not be empty."
                    ]
                  },
                  "errors": []
                  }
                  """

        Scenario: 15 Verify validation invalid claim set name with white space
             When a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": 9999,
                   "applicationName": "Test 1234",
                   "claimSetName": "Claim set name with white space",
                   "educationOrganizationIds": [1, 2, 3],
                   "dataStoreIds": [{dataStoreId}]
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
                    "correlationId": "0HN8RI9E3O45G:00000004",
                    "validationErrors": {
                    "ClaimSetName": [
                      "Claim set name must not contain white spaces."
                    ]
                  },
                  "errors": []
                  }
                  """

        Scenario: 16 Verify validation invalid EducationOrganizationId
             When a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": 9999,
                   "applicationName": "Demo application",
                   "claimSetName": "Claim999",
                   "educationOrganizationIds": [0],
                   "dataStoreIds": []
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
                      "EducationOrganizationIds[0]": [
                      "'Education Organization Ids' must be greater than '0'."
                     ]
                    },
                   "errors": []
                  }
                  """

        Scenario: 16 Ensure the location header has correct path when a path base is provided
             When a POST request is made to "config/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Demo-application",
                   "claimSetName": "Claim06",
                   "educationOrganizationIds": [1, 2, 3],
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                    {
                        "location": "config/v3/applications/{applicationId}"
                    }
                  """
              And the response body has key and secret
              And the record can be retrieved with a GET request
                  """
                  {
                    "id": {applicationId},
                    "applicationName": "Demo-application",
                    "vendorId": {vendorId},
                    "claimSetName": "Claim06",
                    "educationOrganizationIds": [1, 2, 3],
                    "enabled": true,
                    "dataStoreIds": [{dataStoreId}],
                    "profileIds": []
                  }
                  """

        Scenario: 17 Ensure clients can update the claim set scope
             When a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Demo application 17",
                   "claimSetName": "ClaimScenario03",
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             Then it should respond with 201
             Then retrieve created key and secret
             When a PUT request is made to "/v3/applications/{applicationId}" with
                  """
                      {
                      "id": {applicationId},
                      "vendorId": {vendorId},
                      "applicationName": "Demo application 17 Update",
                      "claimSetName": "ClaimScenario03Update",
                      "dataStoreIds": [{dataStoreId}]
                      }
                  """
             Then it should respond with 204
             Then the token should have "ClaimScenario03Update" scope and "uri://ed-fi-e2e.org" namespacePrefix

        Scenario: 18 Ensure clients can update the namespacePrefix claim
             When a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Demo application 18",
                   "claimSetName": "ClaimScenario03",
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             Then it should respond with 201
             Then retrieve created key and secret
             When a PUT request is made to "/v3/vendors/{vendorId}" with
                  """
                    {
                        "id": {vendorId},
                        "company": "Test Vendor 0",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "uri://ed-fi-e2e.org, uri://ed-fi-e2e2.org,uri://new-namespace.org"
                    }
                  """
             Then it should respond with 204
             Then the token should have "ClaimScenario03" scope and "uri://new-namespace.org" namespacePrefix

        Scenario: 19 Ensure clients can update the claim set scope and education organization ids
             When a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Demo application 19",
                   "claimSetName": "ClaimScenario2559",
                   "educationOrganizationIds": [2559, 255901],
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             Then it should respond with 201
             Then retrieve created key and secret
             When a PUT request is made to "/v3/applications/{applicationId}" with
                  """
                      {
                      "id": {applicationId},
                      "vendorId": {vendorId},
                      "applicationName": "Demo application 19 Update",
                      "claimSetName": "ClaimScenario2559Update",
                      "educationOrganizationIds": [2559, 255902],
                      "dataStoreIds": [{dataStoreId}]
                      }
                  """
             Then it should respond with 204
             Then the token should have "ClaimScenario2559Update" scope and "255902" edOrgIds

        Scenario: 20 Ensure application names are unique per vendor
            Given a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Demo application 20",
                   "claimSetName": "Claim06",
                   "educationOrganizationIds": [1, 2, 3],
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             When a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Demo application 20",
                   "claimSetName": "Claim06",
                   "educationOrganizationIds": [1, 2, 3, 4],
                   "dataStoreIds": [{dataStoreId}]
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
                    "correlationId": "0HNEJBSQ1BUK4:00000004",
                    "validationErrors": {
                    "ApplicationName": [
                         "Application 'Demo application 20' already exists for vendor."
                    ]
                  },
                    "errors": []
                  }
                  """

        Scenario: 21 Ensure clients can POST and GET application with profileIds
             When a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Application with Profile 21",
                   "claimSetName": "Claim21",
                   "educationOrganizationIds": [1, 2, 3],
                   "dataStoreIds": [{dataStoreId}],
                   "profileIds": [{profileId}]
                  }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                    {
                        "location": "/v3/applications/{applicationId}"
                    }
                  """
              And the response body has key and secret
              And the record can be retrieved with a GET request
                  """
                  {
                    "id": {applicationId},
                    "applicationName": "Application with Profile 21",
                    "vendorId": {vendorId},
                    "claimSetName": "Claim21",
                    "educationOrganizationIds": [1, 2, 3],
                    "dataStoreIds": [{dataStoreId}],
                    "profileIds": [{profileId}],
                    "enabled": true
                  }
                  """

        Scenario: 22 Ensure clients can PUT application with profileIds
             When a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Application Update Profile 22",
                   "claimSetName": "Claim22",
                   "dataStoreIds": [{dataStoreId}],
                   "profileIds": []
                  }
                  """
             Then it should respond with 201
             When a PUT request is made to "/v3/applications/{applicationId}" with
                  """
                      {
                      "id": {applicationId},
                      "vendorId": {vendorId},
                      "applicationName": "Application Update Profile 22",
                      "claimSetName": "Claim22Update",
                      "dataStoreIds": [{dataStoreId}],
                      "profileIds": [{profileId}]
                      }
                  """
             Then it should respond with 204
              And the record can be retrieved with a GET request
                  """
                  {
                    "id": {applicationId},
                    "applicationName": "Application Update Profile 22",
                    "vendorId": {vendorId},
                    "claimSetName": "Claim22Update",
                    "educationOrganizationIds": [],
                    "dataStoreIds": [{dataStoreId}],
                    "profileIds": [{profileId}],
                    "enabled": true
                  }
                  """

        Scenario: 23 Verify validation invalid profile reference
             When a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Application Invalid Profile 23",
                   "claimSetName": "Claim23",
                   "educationOrganizationIds": [1, 2, 3],
                   "dataStoreIds": [{dataStoreId}],
                   "profileIds": [9999]
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
                    "correlationId": "0HN8RI9E3O45G:00000004",
                    "validationErrors": {
                    "ProfileId": [
                      "Profile does not exist."
                    ]
                  },
                  "errors": []
                  }
                  """

        Scenario: 24 Verify validation invalid profileId value
             When a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Application Invalid ProfileId Value 24",
                   "claimSetName": "Claim24",
                   "educationOrganizationIds": [1, 2, 3],
                   "dataStoreIds": [{dataStoreId}],
                   "profileIds": [0]
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
                      "ProfileIds[0]": [
                      "'Profile Ids' must be greater than '0'."
                     ]
                    },
                   "errors": []
                  }
                  """

        Scenario: 25 Ensure clients can POST application with duplicate profileIds
             When a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Application Duplicate Profile 25",
                   "claimSetName": "Claim25",
                   "educationOrganizationIds": [1, 2, 3],
                   "dataStoreIds": [{dataStoreId}],
                   "profileIds": [{profileId}, {profileId}]
                  }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                    {
                        "location": "/v3/applications/{applicationId}"
                    }
                  """
              And the response body has key and secret
              And the record can be retrieved with a GET request
                  """
                  {
                    "id": {applicationId},
                    "applicationName": "Application Duplicate Profile 25",
                    "vendorId": {vendorId},
                    "claimSetName": "Claim25",
                    "educationOrganizationIds": [1, 2, 3],
                    "dataStoreIds": [{dataStoreId}],
                    "profileIds": [{profileId}],
                    "enabled": true
                  }
                  """

        Scenario: 26 Ensure clients can PUT application with duplicate profileIds
             When a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Application Update Duplicate Profile 26",
                   "claimSetName": "Claim26",
                   "dataStoreIds": [{dataStoreId}],
                   "profileIds": []
                  }
                  """
             Then it should respond with 201
             When a PUT request is made to "/v3/applications/{applicationId}" with
                  """
                      {
                      "id": {applicationId},
                      "vendorId": {vendorId},
                      "applicationName": "Application Update Duplicate Profile 26",
                      "claimSetName": "Claim26Update",
                      "dataStoreIds": [{dataStoreId}],
                      "profileIds": [{profileId}, {profileId}]
                      }
                  """
             Then it should respond with 204
              And the record can be retrieved with a GET request
                  """
                  {
                    "id": {applicationId},
                    "applicationName": "Application Update Duplicate Profile 26",
                    "vendorId": {vendorId},
                    "claimSetName": "Claim26Update",
                    "educationOrganizationIds": [],
                    "dataStoreIds": [{dataStoreId}],
                    "profileIds": [{profileId}],
                    "enabled": true
                  }
                  """

        Scenario: 27 Ensure application with a disabled API client returns enabled false
             When a POST request is made to "/v3/applications" with
                  """
                  {
                  "vendorId": {vendorId},
                  "applicationName": "Disabled Client App",
                  "claimSetName": "Claim21",
                  "educationOrganizationIds": [],
                  "dataStoreIds": [{dataStoreId}]
                  }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                   {
                       "location": "/v3/applications/{applicationId}"
                   }
                  """
              And the response body has key and secret
             When a POST request is made to "/v3/apiClients" with
                  """
                  {
                   "applicationId": {applicationId},
                   "name": "Disabled Client",
                   "isApproved": false,
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             Then it should respond with 201
              And retrieve created key and secret
             When a GET request is made to "/v3/applications/{applicationId}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                   "id": {applicationId},
                   "applicationName": "Disabled Client App",
                   "vendorId": {vendorId},
                   "claimSetName": "Claim21",
                   "educationOrganizationIds": [],
                   "enabled": false,
                   "dataStoreIds": [{dataStoreId}],
                   "profileIds": []
                  }
                  """
             When a GET request is made to "/v3/applications?offset=0&limit=100"
             Then it should respond with 200
              And the response body contains an object matching
                  """
                  {
                   "id": {applicationId},
                   "enabled": false
                  }
                  """
             When a token request is attempted with the captured application credentials
             Then it should respond with 401
              And the response body is
                  """
                  {
                    "detail": "The request could not be processed. See 'errors' for details.",
                    "type":"urn:ed-fi:api:security:authentication",
                    "title":"Authentication Failed",
                    "status":401,
                    "validationErrors":{},
                     "errors": [
                        "invalid_client. Invalid client or Invalid client credentials"
                        ]
                  }
                  """
