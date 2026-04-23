# This is a rough draft feature for future use.

Feature: Resources "Delete" Operation validations

        Background:
            Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"

        Scenario: 00 Background
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

        # FK-violation scenarios (formerly 01-04) were extracted into
        # DeleteResourcesReferenceValidation.feature so they can carry the relational-backend
        # tag without conflicting with the "Scenario: 00 Background" seed guardrail in this file.
        # See DMS-1011.

        @API-180
        Scenario: 05 Verify response when deleting
            Given a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  """
                    {
                        "codeValue": "abc",
                        "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                        "shortDescription": "abc"
                    }
                  """
             When a GET request is made to "/ed-fi/absenceEventCategoryDescriptors?codeValue=abc"
             Then it should respond with 200
              And the response body is
                  """
                  [
                  {
                    "id": "{id}",
                    "codeValue": "abc",
                    "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                    "shortDescription": "abc"
                    }
                  ]
                  """
             When a DELETE request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}"
             Then it should respond with 204
             When a GET request is made to "/ed-fi/absenceEventCategoryDescriptors?codeValue=abc"
             Then it should respond with 200
              And the response body is
                  """
                  []
                  """
