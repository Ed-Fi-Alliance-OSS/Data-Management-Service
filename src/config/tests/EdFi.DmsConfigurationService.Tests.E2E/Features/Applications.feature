Feature: Applications endpoints

        Background:
            Given valid credentials
              And token received

        Scenario: 01 Ensure clients can POST application
             When a POST request is made to "/v2/vendors" with
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
                        "location": "/v2/vendors/{id}"
                    }
                  """
             When a POST request is made to "/v2/applications" with
                  """
                  {
                   "vendorId": {id},
                   "applicationName": "Demo application",
                   "claimSetName": "SIS Vendor"
                  }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                    {
                        "location": "/v2/applications/{id}"
                    }
                  """


