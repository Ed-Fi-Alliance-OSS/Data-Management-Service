Feature: Vendors endpoints

        Background:
            Given valid credentials
            And token received

         Scenario: 01 Ensure clients can GET vendors list
           Given the system has these "vendors"
                  | company | contactName | contactEmailAddress | namespacePrefixes |
                  | Test 11 | Test        | test@gmail.com      | Test              |
                  | Test 12 | Test        | test@gmail.com      | Test              |
                  | Test 13 | Test        | test@gmail.com      | Test              |
                  | Test 14 | Test        | test@gmail.com      | Test              |
                  | Test 15 | Test        | test@gmail.com      | Test              |
             When a GET request is made to "/v2/vendors?offset=0&limit=2"
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

        Scenario: 02 Ensure clients can create a vendor
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

          Scenario: 03 Verify retrieving a single vendor by ID
           When a POST request is made to "/v2/vendors" with
                  """
                    {
                        "company": "Test 17",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "Test"
                    }
                    """
           Then it should respond with 201
           When a GET request is made to "/v2/vendors/{id}"
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
          When a POST request is made to "/v2/vendors" with
                  """
                    {
                        "company": "Test 18",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "Test"
                    }
                    """
           Then it should respond with 201
            When a PUT request is made to "/v2/vendors/{id}" with
                """
                {
                    "id": {id},
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
           When a POST request is made to "/v2/vendors" with
                  """
                    {
                        "company": "Test 19",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "Test"
                    }
                    """
           Then it should respond with 201
           When a DELETE request is made to "/v2/vendors/{id}"
           Then it should respond with 204

         Scenario: 06 Verify error handling when trying to get an item that has already been deleted
           When a POST request is made to "/v2/vendors" with
                  """
                    {
                        "company": "Test 20",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "Test"
                    }
                    """
           Then it should respond with 201
           When a DELETE request is made to "/v2/vendors/{id}"
           Then it should respond with 204
           When a GET request is made to "/v2/vendors/{id}"
           Then it should respond with 404

           Scenario: 07 Verify error handling when trying to update an item that has already been deleted
           When a POST request is made to "/v2/vendors" with
                  """
                    {
                        "company": "Test 21",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "Test"
                    }
                    """
           Then it should respond with 201
           When a DELETE request is made to "/v2/vendors/{id}"
           Then it should respond with 204
           When a PUT request is made to "/v2/vendors/{id}" with
                """
                {
                    "id": {id},
                    "company": "Test 21 updated",
                    "contactName": "Test",
                    "contactEmailAddress": "test@gmail.com",
                    "namespacePrefixes": "Test"
                }
                """
            Then it should respond with 404

          Scenario: 06 Verify error handling when trying to delete an item that has already been deleted
           When a POST request is made to "/v2/vendors" with
                  """
                    {
                        "company": "Test 22",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "Test"
                    }
                    """
           Then it should respond with 201
           When a DELETE request is made to "/v2/vendors/{id}"
           Then it should respond with 204
            When a DELETE request is made to "/v2/vendors/{id}"
           Then it should respond with 404

         Scenario: 07 Verify error handling when trying to get a vendor using a invalid id
           When a GET request is made to "/v2/vendors/a"
           Then it should respond with 400

        Scenario: 08 Verify error handling when trying to delete a vendor using a invalid id
           When a DELETE request is made to "/v2/vendors/b"
           Then it should respond with 400

        Scenario: 09 Verify error handling when trying to update a vendor using a invalid id
            When a PUT request is made to "/v2/vendors/c" with
                """
                {
                    "id": "c",
                    "company": "Test updated",
                    "contactName": "Test",
                    "contactEmailAddress": "test@gmail.com",
                    "namespacePrefixes": "Test"
                }
                """
           Then it should respond with 400


