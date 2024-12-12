Feature: Applications endpoints

        Background:
            Given valid credentials
              And token received

        Scenario: 00 Ensure clients can GET applications
            Given a POST request is made to "/v2/vendors" with
                  """
                    {
                        "company": "Test Vendor 0",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "Test"
                    }
                  """
              And the system has these "applications"
                  | vendorId | applicationName | claimSetName |
                  | _id      | application01   | claim01      |
                  | _id      | application02   | claim02      |
                  | _id      | application03   | claim03      |
                  | _id      | application04   | claim04      |
                  | _id      | application05   | claim05      |
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
                          "claimSetName": "claim02",
                          "vendorId": {vendorId},
                          "educationOrganizationIds": []
                      }]
                  """

        Scenario: 01 Ensure clients can POST application
             When a POST request is made to "/v2/vendors" with
                  """
                    {
                        "company": "Test Scenario 01",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "Test"
                    }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                    {
                        "location": "/v2/vendors/{id}"
                    }
                  """
             When a POST request is made to "/v2/applications" with
                  """
                  {
                   "vendorId": {id},
                   "applicationName": "Demo application",
                   "claimSetName": "Claim 06"
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

        Scenario: 02 Ensure clients can reset application credentials
             When a POST request is made to "/v2/vendors" with
                  """
                    {
                        "company": "Test Scenario 02",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "Test"
                    }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                    {
                        "location": "/v2/vendors/{id}"
                    }
                  """
             When a POST request is made to "/v2/applications" with
                  """
                  {
                   "vendorId": {id},
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


