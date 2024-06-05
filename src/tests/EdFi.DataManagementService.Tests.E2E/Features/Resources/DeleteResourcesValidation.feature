# This is a rough draft feature for future use.

Feature: Resources "Delete" Operation validations

        Background:
            Given the Data Management Service must receive a token issued by "http://localhost"
              And user is already authorized
             When a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
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

        Scenario: Verify deleting a specific resource by ID
             When a DELETE request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}"
             Then it should respond with 200

        Scenario: Verify error handling when deleting using a invalid id
             When a DELETE request is made to "/ed-fi/absenceEventCategoryDescriptors/00112233445566"
             Then it should respond with 404

        Scenario: Verify error handling when deleting a non existing resource
            # The id value should be replaced with the resource created in the Background section
             When a DELETE request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}"
             Then it should respond with 200
             When a DELETE request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}"
             Then it should respond with 404

        Scenario: Verify response code when GET a deleted resource
            # The id value should be replaced with the resource created in the Background section
             When a DELETE request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}"
             Then it should respond with 200
             When a GET request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}"
             Then it should respond with 404

