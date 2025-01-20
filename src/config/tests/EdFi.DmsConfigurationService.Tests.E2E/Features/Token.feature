Feature: Token validation

        Background:
            Given valid credentials
              And token received

        Scenario: 01 Ensure clients can create a vendor
             When a POST request is made to "/v2/vendors" with
                  """
                    {
                        "company": "Test 123",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "Test"
                    }
                  """
             Then it should respond with 201

        Scenario: 02 Ensure clients cannot create a vendor when the token signature is manipulated
            Given token signature manipulated
             When a POST request is made to "/v2/vendors" with
                  """
                    {
                        "company": "Test 456",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "Test"
                    }
                  """
             Then it should respond with 401
