Feature: Validating POST Actions

        @APIConventions @POST
        Scenario: 01 Verify user can send a POST using a valid data
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "54721642123",
                      "birthDate": "2007-08-13",
                      "firstName": "John",
                      "lastSurname": "Doe"
                  }
                  """
             Then it should respond with 201
              And the response headers includes
                  """
                    {
                        "location": "/ed-fi/students/{id}"
                    }
                  """

        @APIConventions @POST
        Scenario: 02 Verify user is able to execute upsert
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "54721642123",
                      "birthDate": "2007-08-13",
                      "firstName": "John",
                      "lastSurname": "Doe"
                  }
                  """
              And a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "54721642123",
                      "birthDate": "2007-08-13",
                      "firstName": "John",
                      "lastSurname": "Doe"
                  }
                  """
             Then it should respond with 200

        @APIConventions @POST
        Scenario: 03 Verify invalid json is handled correctly
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "".
                  }
                  """
             Then it should respond with 400
              And the response body is
                  """
                  {
                    "detail": "Data validation failed. See 'validationErrors' for details.",
                    "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                    "title": "Data Validation Failed",
                    "status": 400,
                    "correlationId": null,
                    "validationErrors": {
                        "$.": [
                        "'.' is invalid after a value. Expected either ',', '}', or ']'. LineNumber: 1 | BytePositionInLine: 25."
                        ]
                    },
                    "errors": []
                  }
                  """

        @APIConventions @POST
        Scenario: 04 Verify missing fields are handled correctly
             When a POST request is made to "/ed-fi/students" with
                  """
                   {
                        "studentUniqueId":"878787383",
                        "birthDate": "2016-08-07",
                        "lastSurname": "lastSurname"
                   }
                  """
             Then it should respond with 400
              And the response body is
                  """
                    {
                     "detail": "Data validation failed. See 'validationErrors' for details.",
                     "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                     "title": "Data Validation Failed",
                     "status": 400,
                     "correlationId": null,
                     "validationErrors": {
                         "$.firstName": [
                             "firstName is required."
                         ]
                     },
                     "errors": []
                    }
                  """

        @APIConventions @POST
        Scenario: 05 Verify user can send a POST using extra fields
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "54721642126",
                      "birthDate": "2007-08-13",
                      "firstName": "John",
                      "lastSurname": "Doe",
                      "newField": "Doe",
                      "newField2": "Doe"
                  }
                  """
             Then it should respond with 201
              And the response headers includes
                  """
                    {
                        "location": "/ed-fi/students/{id}"
                    }
                  """

        @ignore @APIConventions @POST
        Scenario: 06 Verify clients cannot post a resource being unauthenticated
            Given a unauthenticated user
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "54721642123",
                      "birthDate": "2007-08-13",
                      "firstName": "John",
                      "lastSurname": "Doe"
                  }
                  """
             Then it should respond with 401
              And the response body is
                  """
                  {
                      "detail": "The caller could not be authenticated.",
                      "type": "urn:ed-fi:api:security:authentication",
                      "title": "Authentication Failed",
                      "status": 401,
                      "correlationId": "4ebd1a6d-5ab2-40c8-a54b-fb8a5103c18b",
                      "errors": [
                          "Authorization header is missing."
                      ]
                  }
                  """
              And the response header contains
                  """
                  content-type: application/problem+json
                  """

        @ignore @APIConventions @POST
        Scenario: 07 Verify clients cannot POST a resource without permissions
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "54721642123",
                      "birthDate": "2007-08-13",
                      "firstName": "John",
                      "lastSurname": "Doe"
                  }
                  """
             Then it should respond with 401
              And the response body is
                  """
                  {
                      "detail": "The caller could not be authenticated.",
                      "type": "urn:ed-fi:api:security:authentication",
                      "title": "Authentication Failed",
                      "status": 401,
                      "correlationId": "4ebd1a6d-5ab2-40c8-a54b-fb8a5103c18b",
                      "errors": [
                          "Authorization header is missing."
                      ]
                  }
                  """
              And the response header contains
                  """
                  content-type: application/problem+json
                  """

        @ignore @APIConventions @POST
        Scenario: 08 Verify clients cannot POST a resource with invalid token
            Given the user is authenticated
              And the token is expired
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "54721642123",
                      "birthDate": "2007-08-13",
                      "firstName": "John",
                      "lastSurname": "Doe"
                  }
                  """
             Then it should respond with 401
              And the response body is
                  """
                  {
                      "detail": "The caller could not be authenticated.",
                      "type": "urn:ed-fi:api:security:authentication",
                      "title": "Authentication Failed",
                      "status": 401,
                      "correlationId": "4ebd1a6d-5ab2-40c8-a54b-fb8a5103c18b",
                      "errors": [
                          "Authorization header is missing."
                      ]
                  }
                  """
              And the response header contains
                  """
                  content-type: application/problem+json
                  """

        @APIConventions @POST
        Scenario: 09 Validate boundary values during POST action
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "54721642123",
                      "birthDate": "2007-08-13",
                      "firstName": "John",
                      "lastSurname": "Doeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"
                  }
                  """
             Then it should respond with 400
              And the response body is
                  """
                  {
                    "validationErrors": {
                        "$.lastSurname": [
                        "lastSurname Value should be at most 75 characters"
                        ]
                    },
                    "errors": [],
                    "detail": "Data validation failed. See 'validationErrors' for details.",
                    "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                    "title": "Data Validation Failed",
                    "status": 400,
                    "correlationId": null
                  }
                  """

        @APIConventions @POST
        Scenario: 10 Validate special characters values during POST action
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "54721642124",
                      "birthDate": "2007-08-13",
                      "firstName": "~!@:;?/.!{}@$:(_+#%^&*=+[>'])|FirstName\\",
                      "lastSurname": "~!@:;?/.!{}@$:(_+#%^&*=+[>'])|LastName\\"
                  }
                  """
             Then it should respond with 201
              And the response headers includes
                  """
                    {
                        "location": "/ed-fi/students/{id}"
                    }
                  """
