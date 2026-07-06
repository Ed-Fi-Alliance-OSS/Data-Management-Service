Feature: Authorization

        Background:
            Given valid credentials
              And token received

    Rule: Authorized with full_access scope. Should have access to all the endpoints
        Background:
            Given client "DmsConfigurationService" credentials with "edfi_admin_api/full_access" scope
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
                        "connectionString": "Server=localhost;Database=TestDb;"
                    }
                  """

        Scenario: 01 Ensure clients can GET applications
            Given the system has these "applications"
                  | vendorId  | applicationName | claimSetName | dataStoreId  |
                  | _vendorId | application01   | claim01      | _dataStoreId |
                  | _vendorId | application02   | claim01      | _dataStoreId |

             When a GET request is made to "/v3/applications"
             Then it should respond with 200

        @MssqlRepresentative
        Scenario: 02 Ensure clients can POST and GET application
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
                    "applicationName": "Demo application",
                    "vendorId": {vendorId},
                    "claimSetName": "Claim06",
                    "educationOrganizationIds": [1, 2, 3],
                    "dataStoreIds": [{dataStoreId}],
                    "profileIds": [],
                    "enabled": true
                  }
                  """

        Scenario: 03 Ensure clients can PUT and GET application
             When a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Demo application 03",
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
                      "applicationName": "Demo application 03 Update",
                      "claimSetName": "ClaimScenario03Update",
                      "dataStoreIds": [{dataStoreId}]
                      }
                  """
             Then it should respond with 204

        Scenario: 04 Ensure clients can DELETE an application
             When a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Delete application 04",
                   "claimSetName": "ClaimScenario05",
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             Then it should respond with 201
             When a DELETE request is made to "/v3/applications/{applicationId}"
             Then it should respond with 204

    Rule: Authorized with read only scope. Should have access to all the GET endpoints

        Background:
            Given client "DmsConfigurationService" credentials with "edfi_admin_api/full_access" scope
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
                        "connectionString": "Server=localhost;Database=TestDb;"
                    }
                  """

        Scenario: 05 Ensure clients can GET applications with read only scope
            Given client "CMSReadOnlyAccess" credentials with "edfi_admin_api/readonly_access" scope
              And the system has these "applications"
                  | vendorId  | applicationName | claimSetName | dataStoreId  |
                  | _vendorId | application03   | claim01      | _dataStoreId |
                  | _vendorId | application04   | claim01      | _dataStoreId |
              And token received
             When a GET request is made to "/v3/applications"
             Then it should respond with 200

        @MssqlRepresentative
        Scenario: 06 Ensure clients can not have access to POST,PUT,DELETE endpoints with read only access
            Given client "CMSReadOnlyAccess" credentials with "edfi_admin_api/readonly_access" scope
              And token received
             When a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Unauthorized application 06",
                   "claimSetName": "ClaimScenario08",
                   "dataStoreIds": [{dataStoreId}]
                  }
                  """
             Then it should respond with 403

    Rule: Authorized with authorization endpoints only scope. Should have read access to only v3/claimsets and /v3/authorizationMetadata endpoints

        Background:
            Given client "CMSAuthMetadataReadOnlyAccess" credentials with "edfi_admin_api/authMetadata_readonly_access" scope
              And token received

        Scenario: 07 Ensure clients can GETALL claim sets
             When a GET request is made to "/v3/claimSets"
             Then it should respond with 200

        Scenario: 08 Ensure clients can GET authorizationMetadata
             When a GET request is made to "/v3/authorizationMetadata?claimSetName=ClaimSet1"
             Then it should respond with 200

        Scenario: 09 Ensure clients can not GET vendors
             When a GET request is made to "/v3/vendors"
             Then it should respond with 403
