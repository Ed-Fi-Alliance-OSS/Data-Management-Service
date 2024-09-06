Feature: Read a Descriptor

        Background:
            Given the Data Management Service must receive a token issued by "http://localhost"
              And user is already authorized
              And a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  """
                    {
                        "codeValue": "Sick Leave",
                        "description": "Sick Leave",
                        "effectiveBeginDate": "2024-05-14",
                        "effectiveEndDate": "2024-05-14",
                        "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                        "shortDescription": "Sick Leave"
                    }
                  """
             Then it should respond with 201 or 200

        Scenario: 02 Verify retrieving a single descriptor by ID
             When a GET request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}"
             Then it should respond with 200
              And the response body is
                  """
                    {
                      "id": "{id}",
                      "codeValue": "Sick Leave",
                      "description": "Sick Leave",
                      "effectiveBeginDate": "2024-05-14",
                      "effectiveEndDate": "2024-05-14",
                      "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                      "shortDescription": "Sick Leave"
                    }
                  """

        Scenario: 03 Verify response code 404 when ID does not exist
             When a GET request is made to "/ed-fi/absenceEventCategoryDescriptors/124c8513-fade-4ce2-ab71-0e40e148de5b"
             Then it should respond with 404

        Scenario: 04 Read a descriptor that only contains required attributes
            Given a POST request is made to "/ed-fi/disabilityDescriptors" with
                  """
                    {
                      "namespace": "uri://ed-fi.org/DisabilityDescriptor",
                      "codeValue": "Testing Deaf-Blindness",
                      "shortDescription": "Testing Deaf-Blindness"
                    }
                  """
             When a GET request is made to "/ed-fi/disabilityDescriptors/{id}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                      "id": "{id}",
                      "namespace": "uri://ed-fi.org/DisabilityDescriptor",
                      "codeValue": "Testing Deaf-Blindness",
                      "shortDescription": "Testing Deaf-Blindness"
                  }
                  """

        # DMS-89
        @ignore
        Scenario: 06 Ensure clients cannot retrieve a descriptor by requesting through a non existing codeValue
             When a GET request is made to "/ed-fi/absenceEventCategoryDescriptors?codeValue=Test"
             Then it should respond with 200
              And the response body is
                  """
                  []
                  """

        # DMS-89
        @ignore
        Scenario: 08 Ensure clients cannot retrieve a descriptor by requesting through a non existing namespace
             When a GET request is made to "/ed-fi/disabilityDescriptors?namespace=uri://ed-fi.org/DisabilityDescriptor#Fake"
             Then it should respond with 200
              And the response body is
                  """
                  []
                  """

        # DMS-309
        @ignore
        Scenario: 03 Verify response code 404 when ID is not valid
             When a GET request is made to "/ed-fi/absenceEventCategoryDescriptors/00112233445566"
             Then it should respond with 404
              And the response body is
                  """
                  {
                      "detail": "Data validation failed. See 'validationErrors' for details.",
                      "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                      "title": "Data Validation Failed",
                      "status": 404,
                      "correlationId": null,
                      "validationErrors": {
                          "$.id": [
                              "The value '00112233445566' is not valid."
                          ]
                      }
                  }
                  """
