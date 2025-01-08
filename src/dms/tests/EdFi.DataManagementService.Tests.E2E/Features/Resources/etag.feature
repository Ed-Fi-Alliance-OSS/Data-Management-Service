Feature: Validates the functionality of the ETag

        @API-260
        Scenario: 01 Ensure that clients can retrieve an ETag in the response header
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mayers"
                  }
                  """
             Then it should respond with 201
              And the ETag is in the response header
              And the record can be retrieved with a GET request
                  """
                  {
                    "id": "{id}",
                    "studentUniqueId": "111111",
                    "birthDate": "2014-08-14",
                    "firstName": "Russella",
                    "lastSurname": "Mayers",
                    "_etag": "{etag}"
                  }
                  """

        @ignore @API-261
        Scenario: 02 Ensure that clients can pass an ETag in the request header
             When a PUT if-match "{etag}" request is made to "/ed-fi/students/{id}" with
                  """
                  {
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mayorga"
                  }
                  """
             Then it should respond with 204

        @ignore @API-262
        Scenario: 03 Ensure that clients cannot pass a different ETag in the If-Match header
             When a PUT if-match "0000000000" request is made to "/ed-fi/students/{id}" with
                  """
                  {
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mulligan"
                  }
                  """
             Then it should respond with 412
              And the response body is
                  """
                  {
                      "detail": "The item has been modified by another user.",
                      "type": "urn:ed-fi:api:optimistic-lock-failed",
                      "title": "Optimistic Lock Failed",
                      "status": 412,
                      "correlationId": null,
                      "errors": [
                          "The resource item's etag value does not match what was specified in the 'If-Match' request header indicating that it has been modified by another client since it was last retrieved."
                      ]
                  }
                  """

        @ignore @API-263
        Scenario: 04 Ensure that clients cannot pass a different ETag in the If-Match header to delete a resource
             When a DELETE if-match "0000000000" request is made to "/ed-fi/students/{id}"
             Then it should respond with 412
              And the response body is
                  """
                  {
                      "detail": "The item has been modified by another user.",
                      "type": "urn:ed-fi:api:optimistic-lock-failed",
                      "title": "Optimistic Lock Failed",
                      "status": 412,
                      "correlationId": null,
                      "errors": [
                          "The resource item's etag value does not match what was specified in the 'If-Match' request header indicating that it has been modified by another client since it was last retrieved."
                      ]
                  }
                  """

        @ignore @API-264
        Scenario: 05 Ensure that clients can pass an ETag to delete a resource
             When a DELETE if-match "{etag}" request is made to "/ed-fi/students/{id}"
             Then it should respond with 204
