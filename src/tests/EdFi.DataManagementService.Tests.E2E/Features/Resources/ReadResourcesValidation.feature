Feature: Resources "Read" Operation validations

        Background:
            Given the Data Management Service must receive a token issued by "http://localhost"
              And user is already authorized
              And a POST request is made to "ed-fi/absenceEventCategoryDescriptors" with
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

        Scenario: 01 Verify existing resources can be retrieved successfully
             When a GET request is made to "ed-fi/absenceEventCategoryDescriptors"
             Then it should respond with 200
              And the response body is
                  """
                    [
                        {
                            "id": "{id}",
                            "codeValue": "Sick Leave",
                            "description": "Sick Leave",
                            "effectiveBeginDate": "2024-05-14",
                            "effectiveEndDate": "2024-05-14",
                            "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                            "shortDescription": "Sick Leave"
                        }
                    ]
                  """

        Scenario: 02 Verify retrieving a single resource by ID
             When a GET request is made to "ed-fi/absenceEventCategoryDescriptors/{id}"
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
             When a GET request is made to "ed-fi/absenceEventCategoryDescriptors/123123123123"
             Then it should respond with 404

        Scenario: 04 Verify array records content on GET All
             When a GET request is made to "ed-fi/absenceEventCategoryDescriptors"
             Then it should respond with 200
              And total of records should be 1
