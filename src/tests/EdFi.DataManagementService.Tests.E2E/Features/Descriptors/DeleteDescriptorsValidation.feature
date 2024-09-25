Feature: Delete a Descriptor

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

        @API-022
        Scenario: 01 Verify deleting a specific descriptor by ID
             When a DELETE request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}"
             Then it should respond with 204

        @API-023
        Scenario: 02 Verify error handling when deleting a descriptor using a invalid id
             When a DELETE request is made to "/ed-fi/absenceEventCategoryDescriptors/00112233445566"
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
                      "errors" : []
                  }
                  """

        @API-024
        Scenario: 03 Verify error handling when trying to delete an item that has already been deleted
            Given a DELETE request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}"
             When a DELETE request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}"
             Then it should respond with 404

        @API-025
        Scenario: 04 Verify response code when trying to read a deleted resource
            Given a DELETE request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}"
             When a GET request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}"
             Then it should respond with 404

        @API-026
        Scenario: 05 Ensure clients cannot delete a descriptor that is being used by other Resources
            Given the system has these "schools"
                  | schoolId | nameOfInstitution             | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 5        | School with max edorgId value | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
              And a POST request is made to "/ed-fi/gradeLevelDescriptors" with
                  """
                    {
                        "codeValue": "Tenth grade",
                        "namespace": "uri://ed-fi.org/GradeLevelDescriptor",
                        "shortDescription": "Tenth grade"
                    }
                  """
             When a DELETE request is made to "/ed-fi/gradeLevelDescriptors/{id}"
             Then it should respond with 409
              And the response body is
                  """
                  {
                    "detail": "The requested action cannot be performed because this item is referenced by existing School item(s).",
                    "type": "urn:ed-fi:api:data-conflict:dependent-item-exists",
                    "title": "Dependent Item Exists",
                    "status": 409,
                    "correlationId": null,
                    "validationErrors": {},
                    "errors":[]
                  }
                  """




