Feature: OwnershipTokens endpoints

        Background:
            Given valid credentials
              And token received

        Scenario: 01 Create and retrieve an ownership token
             When a POST request is made to "/v3/ownershipTokens" with
                  """
                  {
                    "description": "District Token"
                  }
                  """
             Then it should respond with 201
             When a GET request is made to "/v3/ownershipTokens/{ownershipTokenId}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                    "id": {ownershipTokenId},
                    "description": "District Token"
                  }
                  """

        Scenario: 02 Replace API-client ownership and read it from limited-access response
            Given a POST request is made to "/v3/vendors" with
                  """
                  {
                    "company": "Ownership Vendor",
                    "contactName": "Test",
                    "contactEmailAddress": "ownership@example.org",
                    "namespacePrefixes": "uri://ownership.example.org"
                  }
                  """
              And a POST request is made to "/v3/dataStores" with
                  """
                  {
                    "dataStoreType": "Test",
                    "name": "Ownership Data Store",
                    "connectionString": "Server=test;Database=OwnershipDb;"
                  }
                  """
              And a POST request is made to "/v3/applications" with
                  """
                  {
                    "vendorId": {vendorId},
                    "applicationName": "Ownership Application",
                    "claimSetName": "TestClaim01",
                    "dataStoreIds": [{dataStoreId}]
                  }
                  """
              And a POST request is made to "/v3/apiClients" with
                  """
                  {
                    "applicationId": {applicationId},
                    "name": "Ownership API Client",
                    "isApproved": true,
                    "dataStoreIds": [{dataStoreId}]
                  }
                  """
              And a POST request is made to "/v3/ownershipTokens" with
                  """
                  {
                    "description": "Creator Token"
                  }
                  """
              And the response location id is captured as "creatorOwnershipTokenId"
              And a POST request is made to "/v3/ownershipTokens" with
                  """
                  {
                    "description": "Read Token"
                  }
                  """
              And the response location id is captured as "readOwnershipTokenId"
             When a PUT request is made to "/v3/apiClients/{apiClientId}/ownership" with
                  """
                  {
                    "apiClientId": {apiClientId},
                    "creatorOwnershipTokenId": {creatorOwnershipTokenId},
                    "ownershipTokenIds": [{readOwnershipTokenId}]
                  }
                  """
             Then it should respond with 204
             When a GET request is made to "/v3/apiClients/{clientId}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                    "id": {apiClientId},
                    "applicationId": {applicationId},
                    "clientId": "{clientId}",
                    "clientUuid": "{clientUuid}",
                    "name": "Ownership API Client",
                    "isApproved": true,
                    "creatorOwnershipTokenId": {creatorOwnershipTokenId},
                    "ownershipTokenIds": [{readOwnershipTokenId}],
                    "dataStoreIds": [{dataStoreId}]
                  }
                  """
             When a GET request is made to "/v3/apiClients?applicationid={applicationId}&offset=0&limit=25"
             Then it should respond with 200
              And the response body is
                  """
                  [
                    {
                      "id": "{ignore}",
                      "applicationId": {applicationId},
                      "clientId": "{ignore}",
                      "clientUuid": "{ignore}",
                      "name": "Ownership Application",
                      "isApproved": true,
                      "creatorOwnershipTokenId": null,
                      "ownershipTokenIds": [],
                      "dataStoreIds": [{dataStoreId}]
                    },
                    {
                      "id": {apiClientId},
                      "applicationId": {applicationId},
                      "clientId": "{ignore}",
                      "clientUuid": "{ignore}",
                      "name": "Ownership API Client",
                      "isApproved": true,
                      "creatorOwnershipTokenId": {creatorOwnershipTokenId},
                      "ownershipTokenIds": [{readOwnershipTokenId}],
                      "dataStoreIds": [{dataStoreId}]
                    }
                  ]
                  """

        Scenario: 03 Reject API-client ownership replacement with an invalid creator token ID
            Given a POST request is made to "/v3/vendors" with
                  """
                  {
                    "company": "Ownership Validation Vendor",
                    "contactName": "Test",
                    "contactEmailAddress": "ownership-validation@example.org",
                    "namespacePrefixes": "uri://ownership-validation.example.org"
                  }
                  """
              And a POST request is made to "/v3/dataStores" with
                  """
                  {
                    "dataStoreType": "Test",
                    "name": "Ownership Validation Data Store",
                    "connectionString": "Server=test;Database=OwnershipValidationDb;"
                  }
                  """
              And a POST request is made to "/v3/applications" with
                  """
                  {
                    "vendorId": {vendorId},
                    "applicationName": "Ownership Validation Application",
                    "claimSetName": "TestClaim01",
                    "dataStoreIds": [{dataStoreId}]
                  }
                  """
              And a POST request is made to "/v3/apiClients" with
                  """
                  {
                    "applicationId": {applicationId},
                    "name": "Ownership Validation API Client",
                    "isApproved": true,
                    "dataStoreIds": [{dataStoreId}]
                  }
                  """
             When a PUT request is made to "/v3/apiClients/{apiClientId}/ownership" with
                  """
                  {
                    "apiClientId": {apiClientId},
                    "creatorOwnershipTokenId": 0,
                    "ownershipTokenIds": []
                  }
                  """
             Then it should respond with 400

        Scenario: 04 Retrieve ownership tokens with custom ordering and pagination
            Given a POST request is made to "/v3/ownershipTokens" with
                  """
                  {
                    "description": "District Token"
                  }
                  """
              And the response location id is captured as "zebraOwnershipTokenId"
              And a POST request is made to "/v3/ownershipTokens" with
                  """
                  {
                    "description": "School Token"
                  }
                  """
              And the response location id is captured as "appleOwnershipTokenId"
              And a POST request is made to "/v3/ownershipTokens" with
                  """
                  {
                    "description": "Staff Token"
                  }
                  """
              And the response location id is captured as "bananaOwnershipTokenId"
              And a POST request is made to "/v3/ownershipTokens" with
                  """
                  {
                    "description": "Read Token"
                  }
                  """
              And the response location id is captured as "readOwnershipTokenId"
             When a GET request is made to "/v3/ownershipTokens?orderBy=id&direction=desc&limit=2&offset=0"
             Then it should respond with 200
              And the response body is
                  """
                  [
                    {
                      "id": {readOwnershipTokenId},
                      "description": "Read Token"
                    },
                    {
                      "id": {bananaOwnershipTokenId},
                      "description": "Staff Token"
                    }
                  ]
                  """
             When a GET request is made to "/v3/ownershipTokens?orderBy=id&direction=desc&limit=2&offset=2"
             Then it should respond with 200
              And the response body is
                  """
                  [
                    {
                      "id": {appleOwnershipTokenId},
                      "description": "School Token"
                    },
                    {
                      "id": {zebraOwnershipTokenId},
                      "description": "District Token"
                    }
                  ]
                  """

        Scenario: 05 Repeat API-client ownership replacement with the same representation
            Given a POST request is made to "/v3/vendors" with
                  """
                  {
                    "company": "Ownership Idempotence Vendor",
                    "contactName": "Test",
                    "contactEmailAddress": "ownership-idempotence@example.org",
                    "namespacePrefixes": "uri://ownership-idempotence.example.org"
                  }
                  """
              And a POST request is made to "/v3/dataStores" with
                  """
                  {
                    "dataStoreType": "Test",
                    "name": "Ownership Idempotence Data Store",
                    "connectionString": "Server=test;Database=OwnershipIdempotenceDb;"
                  }
                  """
              And a POST request is made to "/v3/applications" with
                  """
                  {
                    "vendorId": {vendorId},
                    "applicationName": "Ownership Idempotence Application",
                    "claimSetName": "TestClaim01",
                    "dataStoreIds": [{dataStoreId}]
                  }
                  """
              And a POST request is made to "/v3/apiClients" with
                  """
                  {
                    "applicationId": {applicationId},
                    "name": "Ownership Idempotence API Client",
                    "isApproved": true,
                    "dataStoreIds": [{dataStoreId}]
                  }
                  """
              And a POST request is made to "/v3/ownershipTokens" with
                  """
                  {
                    "description": "Idempotent Creator Token"
                  }
                  """
              And the response location id is captured as "creatorOwnershipTokenId"
              And a POST request is made to "/v3/ownershipTokens" with
                  """
                  {
                    "description": "Idempotent Read Token"
                  }
                  """
              And the response location id is captured as "readOwnershipTokenId"
             When a PUT request is made to "/v3/apiClients/{apiClientId}/ownership" with
                  """
                  {
                    "apiClientId": {apiClientId},
                    "creatorOwnershipTokenId": {creatorOwnershipTokenId},
                    "ownershipTokenIds": [{readOwnershipTokenId}]
                  }
                  """
             Then it should respond with 204
             When a PUT request is made to "/v3/apiClients/{apiClientId}/ownership" with
                  """
                  {
                    "apiClientId": {apiClientId},
                    "creatorOwnershipTokenId": {creatorOwnershipTokenId},
                    "ownershipTokenIds": [{readOwnershipTokenId}]
                  }
                  """
             Then it should respond with 204
             When a GET request is made to "/v3/apiClients/{clientId}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                    "id": {apiClientId},
                    "applicationId": {applicationId},
                    "clientId": "{clientId}",
                    "clientUuid": "{clientUuid}",
                    "name": "Ownership Idempotence API Client",
                    "isApproved": true,
                    "creatorOwnershipTokenId": {creatorOwnershipTokenId},
                    "ownershipTokenIds": [{readOwnershipTokenId}],
                    "dataStoreIds": [{dataStoreId}]
                  }
                  """
