Feature: Resources "Delete" Operation standalone validations

        Background:
            Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"

        @API-180 @relational-backend
        @relational-ci-shard-1
        Scenario: 01 Verify response when deleting
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
