Feature: Vendors endpoints

        Background:
            Given valid credentials
              And token received
              And a POST request is made to "/v3/dataStores" with
                  """
                    {
                        "dataStoreType": "Test",
                        "name": "Test Data Store",
                        "connectionString": "Server=localhost;Database=TestDb;"
                    }
                  """

        Scenario: 01 Ensure clients can GET vendors list
            Given the system has these "vendors"
                  | company | contactName | contactEmailAddress | namespacePrefixes |
                  | Test 11 | Test        | test@gmail.com      | Test              |
                  | Test 12 | Test        | test@gmail.com      | Test              |
                  | Test 13 | Test        | test@gmail.com      | Test              |
                  | Test 14 | Test        | test@gmail.com      | Test              |
                  | Test 15 | Test        | test@gmail.com      | Test              |
             When a GET request is made to "/v3/vendors?offset=0&limit=2"
             Then it should respond with 200
              And the response body is
                  """
                      [{
                          "id": {id},
                          "company": "Test 11",
                          "contactName": "Test",
                          "contactEmailAddress": "test@gmail.com",
                          "namespacePrefixes": "Test"
                      },
                      {
                          "id": {id},
                          "company": "Test 12",
                          "contactName": "Test",
                          "contactEmailAddress": "test@gmail.com",
                          "namespacePrefixes": "Test"
                      }]
                  """

        @MssqlRepresentative
        Scenario: 02 Ensure clients can create a vendor
             When a POST request is made to "/v3/vendors" with
                  """
                    {
                        "company": "Test 16",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "Test"
                    }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                    {
                        "location": "/v3/vendors/{vendorId}"
                    }
                  """
              And the response body is empty
              And the record can be retrieved with a GET request
                  """
                  {
                      "id": {id},
                      "company": "Test 16",
                      "contactName": "Test",
                      "contactEmailAddress": "test@gmail.com",
                      "namespacePrefixes": "Test"
                  }
                  """

        @MssqlRepresentative
        Scenario: 03 Verify retrieving a single vendor by ID
             When a POST request is made to "/v3/vendors" with
                  """
                    {
                        "company": "Test 17",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "Test"
                    }
                  """
             Then it should respond with 201
             When a GET request is made to "/v3/vendors/{vendorId}"
             Then it should respond with 200
              And the response body is
                  """
                      {
                        "id": {id},
                        "company": "Test 17",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "Test"
                      }
                  """

        Scenario: 04 Put an existing vendor
             When a POST request is made to "/v3/vendors" with
                  """
                    {
                        "company": "Test 18",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "Test"
                    }
                  """
             Then it should respond with 201
             When a PUT request is made to "/v3/vendors/{vendorId}" with
                  """
                  {
                      "id": {vendorId},
                      "company": "Test 18 updated",
                      "contactName": "Test",
                      "contactEmailAddress": "test@gmail.com",
                      "namespacePrefixes": "Test"
                  }
                  """
             Then it should respond with 204
              And the record can be retrieved with a GET request
                  """
                   {
                       "id": {id},
                       "company": "Test 18 updated",
                       "contactName": "Test",
                       "contactEmailAddress": "test@gmail.com",
                       "namespacePrefixes": "Test"
                   }
                  """

        Scenario: 05 Verify deleting a specific vendor by ID
             When a POST request is made to "/v3/vendors" with
                  """
                    {
                        "company": "Test 19",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "Test"
                    }
                  """
             Then it should respond with 201
             When a DELETE request is made to "/v3/vendors/{vendorId}"
             Then it should respond with 204

        Scenario: 06 Verify error handling when trying to get an item that has already been deleted
             When a POST request is made to "/v3/vendors" with
                  """
                    {
                        "company": "Test 20",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "Test"
                    }
                  """
             Then it should respond with 201
             When a DELETE request is made to "/v3/vendors/{vendorId}"
             Then it should respond with 204
             When a GET request is made to "/v3/vendors/{vendorId}"
             Then it should respond with 404

        Scenario: 07 Verify error handling when trying to update an item that has already been deleted
             When a POST request is made to "/v3/vendors" with
                  """
                    {
                        "company": "Test 21",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "Test"
                    }
                  """
             Then it should respond with 201
             When a DELETE request is made to "/v3/vendors/{vendorId}"
             Then it should respond with 204
             When a PUT request is made to "/v3/vendors/{vendorId}" with
                  """
                  {
                      "id": {vendorId},
                      "company": "Test 21 updated",
                      "contactName": "Test",
                      "contactEmailAddress": "test@gmail.com",
                      "namespacePrefixes": "Test"
                  }
                  """
             Then it should respond with 404

        Scenario: 08 Verify error handling when trying to delete an item that has already been deleted
             When a POST request is made to "/v3/vendors" with
                  """
                    {
                        "company": "Test 22",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "Test"
                    }
                  """
             Then it should respond with 201
             When a DELETE request is made to "/v3/vendors/{vendorId}"
             Then it should respond with 204
             When a DELETE request is made to "/v3/vendors/{vendorId}"
             Then it should respond with 404

        Scenario: 09 Verify error handling when trying to get a vendor using a invalid id
             When a GET request is made to "/v3/vendors/a"
             Then it should respond with 400

        Scenario: 10 Verify error handling when trying to delete a vendor using a invalid id
             When a DELETE request is made to "/v3/vendors/b"
             Then it should respond with 400

        Scenario: 11 Verify error handling when trying to update a vendor using a invalid id
        Scenario: 12 Verify validation invalid company
             When a POST request is made to "/v3/vendors" with
                  """
                    {
                        "company": "",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "Test"
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
                      "Company": [
                        "'Company' must not be empty."
                        ]
                    },
                   "errors": []
                  }
                  """
        Scenario: 13 Verify validation invalid contactName
             When a POST request is made to "/v3/vendors" with
                  """
                    {
                        "company": "Acme",
                        "contactName": "",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "Test"
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
                      "ContactName": [
                        "'Contact Name' must not be empty."
                        ]
                    },
                   "errors": []
                  }
                  """
        Scenario: 14 Verify validation invalid emailAddress
             When a POST request is made to "/v3/vendors" with
                  """
                    {
                        "company": "Acme",
                        "contactName": "George",
                        "contactEmailAddress": "notanemailaddress",
                        "namespacePrefixes": "Test"
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
                      "ContactEmailAddress": [
                        "'Contact Email Address' is not a valid email address."
                        ]
                    },
                   "errors": []
                  }
                  """
        Scenario: 15 Verify validation invalid namespacePrefix
             When a POST request is made to "/v3/vendors" with
                  """
                    {
                        "company": "Acme",
                        "contactName": "George",
                        "contactEmailAddress": "George@compuserv.net",
                        "namespacePrefixes": "0123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789"
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
                      "NamespacePrefixes": [
                        "Each NamespacePrefix length must be 128 characters or fewer."
                    ]
                    },
                   "errors": []
                  }
                  """

        Scenario: 16 Verify vendor applications endpoint
            Given a POST request is made to "/v3/vendors" with
                  """
                    {
                        "company": "Scenario 16",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "Test"
                    }
                  """
             When a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Demo application",
                   "claimSetName": "Claim06",
                   "educationOrganizationIds": [1, 2, 3],
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             Then it should respond with 201
             When a GET request is made to "/v3/vendors/{vendorId}/applications"
             Then it should respond with 200
              And the response body is
                  """
                  [
                     {
                            "id": {id},
                            "applicationName": "Demo application",
                            "vendorId": {vendorId},
                            "claimSetName": "Claim06",
                            "educationOrganizationIds": [
                                1,
                                2,
                                3
                            ],
                            "dataStoreIds": [{dataStoreId}],
                            "profileIds": [],
                            "enabled": true
                        }
                    ]
                  """

        Scenario: 17 Ensure the location header has correct path when a path base is provided
             When a POST request is made to "config/v3/vendors" with
                  """
                    {
                        "company": "Test 99",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "Test"
                    }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                    {
                        "location": "config/v3/vendors/{vendorId}"
                    }
                  """
              And the response body is empty
              And the record can be retrieved with a GET request
                  """
                  {
                      "id": {id},
                      "company": "Test 99",
                      "contactName": "Test",
                      "contactEmailAddress": "test@gmail.com",
                      "namespacePrefixes": "Test"
                  }
                  """


        Scenario: 18 POST with an existing company name returns 200 and updates the vendor
             When a POST request is made to "/v3/vendors" with
                  """
                   {
                       "company": "Upsert Co",
                       "contactName": "Initial Contact",
                       "contactEmailAddress": "initial@example.com",
                       "namespacePrefixes": "Test"
                   }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                    {
                        "location": "/v3/vendors/{vendorId}"
                    }
                  """
              And the response body is empty
             When a POST request is made to "/v3/vendors" with
                  """
                   {
                       "company": "Upsert Co",
                       "contactName": "Updated Contact",
                       "contactEmailAddress": "updated@example.com",
                       "namespacePrefixes": "Test"
                   }
                  """
             Then it should respond with 200
              And the response headers include
                  """
                   {
                       "location": "/v3/vendors/{vendorId}"
                   }
                  """
              And the response body is empty
              And the record can be retrieved with a GET request
                  """
                  {
                      "id": {id},
                      "company": "Upsert Co",
                      "contactName": "Updated Contact",
                      "contactEmailAddress": "updated@example.com",
                      "namespacePrefixes": "Test"
                  }
                  """

        # Every client the vendor owns must still be addressable by its stored identifier after a
        # namespace-prefix update. Before DMS-1356 the Keycloak provider replaced each client and
        # the workflow discarded the replacement's identifier, so the first operation below that
        # addresses a client by its stored identifier failed.
        @MssqlRepresentative
        Scenario: 19 Vendor namespace-prefix update keeps every affected client addressable
             When a POST request is made to "/v3/vendors" with
                  """
                    {
                        "company": "Scenario 19 {scenarioRunId}",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "uri://s19-old.org"
                    }
                  """
             Then it should respond with 201
              And the response location id is captured as "s19VendorId"
             When a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {s19VendorId},
                   "applicationName": "Scenario 19 Application A",
                   "claimSetName": "Claim06",
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             Then it should respond with 201
              And the response body credentials are captured as "a1"
              And the response body id is captured as "applicationA"
             When a GET request is made to "/v3/apiClients/{a1Key}"
             Then it should respond with 200
              And the response body id is captured as "a1Id"
              And the response body property "clientUuid" is captured as "a1UuidBefore"
             When a POST request is made to "/v3/apiClients" with
                  """
                  {
                   "applicationId": {applicationA},
                   "name": "Scenario 19 Client A2",
                   "isApproved": true,
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             Then it should respond with 201
              And the response body credentials are captured as "a2"
              And the response body id is captured as "a2Id"
             When a GET request is made to "/v3/apiClients/{a2Key}"
             Then it should respond with 200
              And the response body property "clientUuid" is captured as "a2UuidBefore"
             When a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {s19VendorId},
                   "applicationName": "Scenario 19 Application B",
                   "claimSetName": "Claim06",
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             Then it should respond with 201
              And the response body credentials are captured as "b1"
              And the response body id is captured as "applicationB"
             When a GET request is made to "/v3/apiClients/{b1Key}"
             Then it should respond with 200
              And the response body id is captured as "b1Id"
              And the response body property "clientUuid" is captured as "b1UuidBefore"

             When a PUT request is made to "/v3/vendors/{s19VendorId}" with
                  """
                    {
                        "id": {s19VendorId},
                        "company": "Scenario 19 {scenarioRunId}",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "uri://s19-old.org,uri://s19-new.org"
                    }
                  """
             Then it should respond with 204

             # Every stored identifier survived the update.
             When a GET request is made to "/v3/apiClients/{a1Key}"
             Then it should respond with 200
              And the response body property "clientUuid" equals the value captured as "a1UuidBefore"
             When a GET request is made to "/v3/apiClients/{a2Key}"
             Then it should respond with 200
              And the response body property "clientUuid" equals the value captured as "a2UuidBefore"
             When a GET request is made to "/v3/apiClients/{b1Key}"
             Then it should respond with 200
              And the response body property "clientUuid" equals the value captured as "b1UuidBefore"

             # Every client carries the new prefix at the real identity provider.
             When a token is requested with the credentials captured as "a1" and scope "Claim06"
             Then it should respond with 200
              And the token carries "uri://s19-new.org" in the namespacePrefixes claim
             When a token is requested with the credentials captured as "a2" and scope "Claim06"
             Then it should respond with 200
              And the token carries "uri://s19-new.org" in the namespacePrefixes claim
             When a token is requested with the credentials captured as "b1" and scope "Claim06"
             Then it should respond with 200
              And the token carries "uri://s19-new.org" in the namespacePrefixes claim

             # ApiClient update addresses each client by its stored identifier.
             When a PUT request is made to "/v3/apiClients/{a1Id}" with
                  """
                  {
                   "id": {a1Id},
                   "applicationId": {applicationA},
                   "name": "Scenario 19 Client A1 Renamed",
                   "isApproved": true,
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             Then it should respond with 204
             When a PUT request is made to "/v3/apiClients/{a2Id}" with
                  """
                  {
                   "id": {a2Id},
                   "applicationId": {applicationA},
                   "name": "Scenario 19 Client A2 Renamed",
                   "isApproved": true,
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             Then it should respond with 204
             When a PUT request is made to "/v3/apiClients/{b1Id}" with
                  """
                  {
                   "id": {b1Id},
                   "applicationId": {applicationB},
                   "name": "Scenario 19 Client B1 Renamed",
                   "isApproved": true,
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             Then it should respond with 204

             # Credential reset addresses each client by its stored identifier, and the reissued
             # secret still authenticates.
             When a PUT request is made to "/v3/apiClients/{a1Id}/reset-credential" with
                  """
                  {}
                  """
             Then it should respond with 200
              And the response body credentials are captured as "a1"
             When a token is requested with the credentials captured as "a1" and scope "Claim06"
             Then it should respond with 200
              And the response body has a non-empty access_token
             When a PUT request is made to "/v3/apiClients/{a2Id}/reset-credential" with
                  """
                  {}
                  """
             Then it should respond with 200
              And the response body credentials are captured as "a2"
             When a token is requested with the credentials captured as "a2" and scope "Claim06"
             Then it should respond with 200
              And the response body has a non-empty access_token
             When a PUT request is made to "/v3/apiClients/{b1Id}/reset-credential" with
                  """
                  {}
                  """
             Then it should respond with 200
              And the response body credentials are captured as "b1"
             When a token is requested with the credentials captured as "b1" and scope "Claim06"
             Then it should respond with 200
              And the response body has a non-empty access_token

             # Application update addresses its first client by the stored identifier, on both
             # affected applications.
             When a PUT request is made to "/v3/applications/{applicationA}" with
                  """
                  {
                   "id": {applicationA},
                   "vendorId": {s19VendorId},
                   "applicationName": "Scenario 19 Application A Renamed",
                   "claimSetName": "Claim06",
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             Then it should respond with 204
             When a token is requested with the credentials captured as "a1" and scope "Claim06"
             Then it should respond with 200
              And the response body has a non-empty access_token
             When a PUT request is made to "/v3/applications/{applicationB}" with
                  """
                  {
                   "id": {applicationB},
                   "vendorId": {s19VendorId},
                   "applicationName": "Scenario 19 Application B Renamed",
                   "claimSetName": "Claim06",
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             Then it should respond with 204
             When a token is requested with the credentials captured as "b1" and scope "Claim06"
             Then it should respond with 200
              And the response body has a non-empty access_token

             # Deletion addresses the stored identifier too, for one client and then for whole
             # applications.
             When a DELETE request is made to "/v3/apiClients/{a2Id}"
             Then it should respond with 204
             When a GET request is made to "/v3/apiClients/{a2Key}"
             Then it should respond with 404
             When a DELETE request is made to "/v3/applications/{applicationB}"
             Then it should respond with 204
             When a DELETE request is made to "/v3/applications/{applicationA}"
             Then it should respond with 204
             When a GET request is made to "/v3/apiClients/{a1Key}"
             Then it should respond with 404
