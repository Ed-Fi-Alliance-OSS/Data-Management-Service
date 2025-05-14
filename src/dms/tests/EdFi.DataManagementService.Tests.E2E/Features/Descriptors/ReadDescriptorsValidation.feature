Feature: Read a Descriptor

        Background:
            Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"
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

        @API-027
        Scenario: 01 Verify retrieving a single descriptor by ID
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

        @API-028
        Scenario: 02 Verify response code 404 when ID does not exist
             When a GET request is made to "/ed-fi/absenceEventCategoryDescriptors/124c8513-fade-4ce2-ab71-0e40e148de5b"
             Then it should respond with 404

        @API-029
        Scenario: 03 Read a descriptor that only contains required attributes
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

        @API-030
        Scenario: 04 Ensure clients cannot retrieve a descriptor by requesting through a non existing codeValue
             When a GET request is made to "/ed-fi/absenceEventCategoryDescriptors?codeValue=Test"
             Then it should respond with 200
              And the response body is
                  """
                  []
                  """

        @API-031
        Scenario: 05 Ensure clients cannot retrieve a descriptor by requesting through a non existing namespace
             When a GET request is made to "/ed-fi/disabilityDescriptors?namespace=uri://ed-fi.org/DoesNotExistDescriptor"
             Then it should respond with 200
              And the response body is
                  """
                  []
                  """

        Scenario: 06 Ensure clients cannot retrieve a descriptor by requesting a valid namespace with valid codeValue attached
        # Descriptors are referenced in resources by attaching the namespace and codeValue like so: uri://ed-fi.org/DisabilityDescriptor#Blindness
        # but you cannot search for a descriptor by using this combination.
            Given a POST request is made to "/ed-fi/disabilityDescriptors" with
                  """
                    {
                      "namespace": "uri://ed-fi.org/DisabilityDescriptor",
                      "codeValue": "Blindness",
                      "shortDescription": "Blindness"
                    }
                  """
            # %23 is the url encoded value for #.
             When a GET request is made to "/ed-fi/disabilityDescriptors?namespace=uri://ed-fi.org/DisabilityDescriptor%23Blindness"
             Then it should respond with 200
              And the response body is
                  """
                  []
                  """


        @API-032
        Scenario: 07 Verify response code 404 when ID is not valid
             When a GET request is made to "/ed-fi/absenceEventCategoryDescriptors/00112233445566"
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
                          "$.id": [
                              "The value '00112233445566' is not valid."
                          ]
                      },
                      "errors": []
                  }
                  """

        Scenario: 08 Get a Descriptor using a resource not configured in claims
             When a GET request is made to "/ed-fi/academicHonorCategoryDescriptors"
             Then it should respond with 403
              And the response body is
                  """
                  {
                      "detail": "Access to the resource could not be authorized.",
                      "type": "urn:ed-fi:api:security:authorization:",
                      "title": "Authorization Denied",
                      "status": 403,
                      "correlationId": null,
                      "validationErrors": {},
                      "errors": []
                  }
                  """
