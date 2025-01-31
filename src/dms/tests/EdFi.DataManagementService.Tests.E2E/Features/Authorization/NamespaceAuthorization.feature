Feature Namespace Authorization

        Background:
            Given the SIS Vendor is authorized

        Scenario: 01 Ensure SIS Vendor can create a descriptor in the ed-fi namespace
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
             Then it should respond with 201

        Scenario: 02 Ensure SIS Vendor can not create a descriptor in the non existent namespace
             When a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  """
                  {
                      "codeValue": "Sick Leave",
                      "description": "Sick Leave",
                      "effectiveBeginDate": "2024-05-14",
                      "effectiveEndDate": "2024-05-14",
                      "namespace": "uri://nonexistent.org/AbsenceEventCategoryDescriptor",
                      "shortDescription": "Sick Leave"
                  }
                  """
             Then it should respond with 403
             

