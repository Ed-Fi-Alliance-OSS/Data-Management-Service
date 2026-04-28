Feature: ETag validations

        Background:
            Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"

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

        Scenario: 02 Ensure that clients can pass an IfMatch in the request header
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mayers"
                  }
                  """
             Then it should respond with 201 or 200
              And the ETag is in the response header
             When a PUT if-match "{IfMatch}" request is made to "/ed-fi/students/{id}" with
                  """
                  {
                      "id": "{id}",
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mayorga"
                  }
                  """
             Then it should respond with 204

        Scenario: 03 Ensure that clients can pass an IfMatch in the request header and ignore _etag in request body
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mayers"
                  }
                  """
             Then it should respond with 201 or 200
              And the ETag is in the response header
             When a PUT if-match "{IfMatch}" request is made to "/ed-fi/students/{id}" with
                  """
                  {
                      "id": "{id}",
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mayorga",
                      "_etag": "{etag}"
                  }
                  """
             Then it should respond with 204

        Scenario: 04 Ensure that clients cannot pass a different If-Match in the request header
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mayers"
                  }
                  """
             Then it should respond with 201 or 200
              And the ETag is in the response header
             When a PUT if-match "0000000000" request is made to "/ed-fi/students/{id}" with
                  """
                  {
                      "id": "{id}",
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
                      "validationErrors": {},
                      "errors": [
                        "The resource item's etag value does not match what was specified in the 'If-Match' request header indicating that it has been modified by another client since it was last retrieved."
                      ]
                  }
                  """

        Scenario: 05 Ensure that POST-as-update succeeds when If-Match matches the current ETag
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mayers"
                  }
                  """
             Then it should respond with 201 or 200
              And the ETag is in the response header
             When a POST if-match "{IfMatch}" request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mayorga"
                  }
                  """
             Then it should respond with 200

        Scenario: 06 Ensure that POST-as-update fails with 412 when If-Match does not match the current ETag
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mayers"
                  }
                  """
             Then it should respond with 201 or 200
              And the ETag is in the response header
             When a POST if-match "0000000000" request is made to "/ed-fi/students" with
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
                      "validationErrors": {},
                      "errors": [
                        "The resource item's etag value does not match what was specified in the 'If-Match' request header indicating that it has been modified by another client since it was last retrieved."
                      ]
                  }
                  """

        # DEVIATION FROM RFC 7232 §3.1:
        # The RFC mandates that If-Match on a request with no prior entity state returns 412
        # when no current representation exists ("none-match → 412" for conditional PUT/POST).
        # DMS intentionally ignores If-Match on a first-time POST (CreateNew path) because
        # there is no prior entity state to compare against; the document is simply inserted.
        Scenario: 07 Ensure that If-Match on a first-time POST is silently ignored and the insert proceeds
             When a POST if-match "any-etag-value" request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "999009",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mayers"
                  }
                  """
             Then it should respond with 201
