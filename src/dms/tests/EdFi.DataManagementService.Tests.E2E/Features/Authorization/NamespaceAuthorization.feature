Feature: Namespace Authorization

    Rule: Descriptors respect namespace authorization

        Background:
            # Note: the api client used in the background has two namespaces. For these tests we will
            # use the second namespace (ns2). Elsewhere in the test suite we will be using the first
            # and more common namespace "uri://ed-fi.org"
            Given the claimSet "E2E-NameSpaceBasedClaimSet" is authorized with namespacePrefixes "uri://ed-fi.org uri://ns2.org"
              And a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  """
                  {
                      "codeValue": "Sick Leave",
                      "description": "Sick Leave",
                      "effectiveBeginDate": "2024-05-14",
                      "effectiveEndDate": "2024-05-14",
                      "namespace": "uri://ns2.org/AbsenceEventCategoryDescriptor",
                      "shortDescription": "Sick Leave"
                  }
                  """

        Scenario: 01 Ensure client can create a descriptor in the ns2 namespace
             When a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  """
                  {
                      "codeValue": "Sick Leave",
                      "description": "Sick Leave",
                      "effectiveBeginDate": "2024-05-14",
                      "effectiveEndDate": "2024-05-14",
                      "namespace": "uri://ns2.org/AbsenceEventCategoryDescriptor",
                      "shortDescription": "Sick Leave"
                  }
                  """
             Then it should respond with 200

        Scenario: 02 Ensure client can get a descriptor in the ns2 namespace
             When a GET request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}"
             Then it should respond with 200

        Scenario: 03 Ensure client can update a descriptor in the ns2 namespace
             When a PUT request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}" with
                  """
                  {
                    "id": "{id}",
                    "codeValue": "Sick Leave",
                    "description": "Sick Leave Edited",
                    "namespace": "uri://ns2.org/AbsenceEventCategoryDescriptor",
                    "shortDescription": "Sick Leave Short Description",
                    "effectiveBeginDate": "2025-05-14",
                    "effectiveEndDate": "2027-05-14"
                  }
                  """
             Then it should respond with 204

        Scenario: 04 Ensure client can delete a descriptor in the ns2 namespace
             When a DELETE request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}"
             Then it should respond with 204

        Scenario: 05 Ensure claimSet with different namespace can not create a descriptor in the ns2 namespace
            Given the claimSet "E2E-NameSpaceBasedClaimSet" is authorized with namespacePrefixes "uri://ns3.org"
             When a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  """
                  {
                      "codeValue": "Sick Leave",
                      "description": "Sick Leave",
                      "effectiveBeginDate": "2024-05-14",
                      "effectiveEndDate": "2024-05-14",
                      "namespace": "uri://ns2.org/AbsenceEventCategoryDescriptor",
                      "shortDescription": "Sick Leave"
                  }
                  """
             Then it should respond with 403

        @ignore #DMS-503
        Scenario: 06 Ensure claimSet with different namespace can not get a descriptor in the ns2 namespace
            Given the SIS Vendor is authorized with namespacePrefixes "uri://ns3.org"
             When a GET request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}"
             Then it should respond with 403

        Scenario: 07 Ensure claimSet with different namespace can not update a descriptor in the ns2 namespace
            Given the claimSet "E2E-NameSpaceBasedClaimSet" is authorized with namespacePrefixes "uri://ns3.org"
             When a PUT request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}" with
                  """
                  {
                    "id": "{id}",
                    "codeValue": "Sick Leave",
                    "description": "Sick Leave Edited",
                    "namespace": "uri://ns2.org/AbsenceEventCategoryDescriptor",
                    "shortDescription": "Sick Leave Short Description",
                    "effectiveBeginDate": "2025-05-14",
                    "effectiveEndDate": "2027-05-14"
                  }
                  """
             Then it should respond with 403

        @ignore #DMS-503
        Scenario: 08 Ensure claimSet with different namespace can not delete a descriptor in the ns2 namespace
            Given the claimSet "E2E-NameSpaceBasedClaimSet" is authorized with namespacePrefixes "uri://ns3.org"
             When a DELETE request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}"
             Then it should respond with 403

    Rule: Resources respect namespace authorization

        Background:
            # Note: the api client used in the background has two namespaces. For these tests we will
            # use the second namespace (ns2). Elsewhere in the test suite we will be using the first
            # and more common namespace "uri://ed-fi.org"
            Given the claimSet "E2E-NameSpaceBasedClaimSet" is authorized with namespacePrefixes "uri://ed-fi.org uri://ns2.org"
            Given the system has these "schoolYearTypes"
                  | schoolYear | currentSchoolYear | schoolYearDescription |
                  | 2024       | true              | School Year 2024      |
              And a POST request is made to "/ed-fi/surveys" with
                  """
                  {
                      "namespace": "uri://ns2.org",
                      "surveyIdentifier": "CE_1",
                      "schoolYearTypeReference": {
                        "schoolYear": 2024
                      },
                    "surveyTitle": "Course Evaluation"
                  }
                  """

        Scenario: 09 Ensure client can create a resource in the ns2 namespace
             When a POST request is made to "/ed-fi/surveys" with
                  """
                  {
                      "namespace": "uri://ns2.org",
                      "surveyIdentifier": "CE_1",
                      "schoolYearTypeReference": {
                        "schoolYear": 2024
                      },
                    "surveyTitle": "Course Evaluation"
                  }
                  """
             Then it should respond with 200

        Scenario: 10 Ensure client can get a resource in the ns2 namespace
             When a GET request is made to "/ed-fi/surveys/{id}"
             Then it should respond with 200

        Scenario: 11 Ensure client can update a resource in the ns2 namespace
             When a PUT request is made to "/ed-fi/surveys/{id}" with
                  """
                  {
                    "id": "{id}",
                    "namespace": "uri://ns2.org",
                      "surveyIdentifier": "CE_1",
                      "schoolYearTypeReference": {
                        "schoolYear": 2024
                      },
                    "surveyTitle": "Course Evaluation Update"
                  }
                  """
             Then it should respond with 204

        Scenario: 12 Ensure client can delete a resource in the ns2 namespace
             When a DELETE request is made to "/ed-fi/surveys/{id}"
             Then it should respond with 204

        Scenario: 13 Ensure claimSet with different namespace can not create a resource in the ns2 namespace
            Given the claimSet "E2E-NameSpaceBasedClaimSet" is authorized with namespacePrefixes "uri://ns3.org"
             When a POST request is made to "/ed-fi/surveys" with
                  """
                  {
                      "namespace": "uri://ns2.org",
                      "surveyIdentifier": "CE_1",
                      "schoolYearTypeReference": {
                        "schoolYear": 2024
                      },
                    "surveyTitle": "Course Evaluation"
                  }
                  """
             Then it should respond with 403

        @ignore #DMS-503
        Scenario: 14 Ensure claimSet with different namespace can not get a resource in the ns2 namespace
            Given the claimSet "E2E-NameSpaceBasedClaimSet" is authorized with namespacePrefixes "uri://ns3.org"
             When a GET request is made to "/ed-fi/surveys/{id}"
             Then it should respond with 403

        Scenario: 15 Ensure claimSet with different namespace can not update a resource in the ns2 namespace
            Given the claimSet "E2E-NameSpaceBasedClaimSet" is authorized with namespacePrefixes "uri://ns3.org"
             When a PUT request is made to "/ed-fi/surveys/{id}" with
                  """
                  {
                    "id": "{id}",
                    "namespace": "uri://ns2.org",
                      "surveyIdentifier": "CE_1",
                      "schoolYearTypeReference": {
                        "schoolYear": 2024
                      },
                    "surveyTitle": "Course Evaluation Update"
                  }
                  """
             Then it should respond with 403

        @ignore #DMS-503
        Scenario: 16 Ensure claimSet with different namespace can not delete a resource in the ns2 namespace
            Given the claimSet "E2E-NameSpaceBasedClaimSet" is authorized with namespacePrefixes "uri://ns3.org"
             When a DELETE request is made to "/ed-fi/surveys/{id}"
             Then it should respond with 403
