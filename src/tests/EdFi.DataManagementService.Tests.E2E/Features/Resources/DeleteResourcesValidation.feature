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

        Scenario: 01 Verify deleting a specific resource by ID
             When a DELETE request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}"
             Then it should respond with 200

        Scenario: 02 Verify error handling when deleting using a invalid id
             When a DELETE request is made to "/ed-fi/absenceEventCategoryDescriptors/00112233445566"
             Then it should respond with 404

        Scenario: 03 Verify error handling when deleting a non existing resource
            # The id value should be replaced with the resource created in the Background section
             When a DELETE request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}"
             Then it should respond with 200
             When a DELETE request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}"
             Then it should respond with 404

        Scenario: 04 Verify response code when GET a deleted resource
            # The id value should be replaced with the resource created in the Background section
             When a DELETE request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}"
             Then it should respond with 200
             When a GET request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}"
             Then it should respond with 404

        @ignore
        Scenario: 05 Verify response code when deleting a referenced descriptor
             When a POST request is made to "/ed-fi/gradingPeriodDescriptors" with
                  """
                   {
                     "codeValue": "First Six Weeks",
                     "description": "First Six Weeks",
                     "namespace": "uri://ed-fi.org/GradingPeriodDescriptor",
                     "shortDescription": "First Six Weeks"
                   }
                  """
             Then it should respond with 201 or 200
             When a POST request is made for dependent resource "/ed-fi/gradingPeriods" with
                  """
                   {
                     "schoolReference": {
                       "schoolId": 255901001
                     },
                     "schoolYearTypeReference": {
                       "schoolYear": 2022
                     },
                     "gradingPeriodDescriptor": "uri://ed-fi.org/GradingPeriodDescriptor#First Six Weeks",
                     "gradingPeriodName": "2021-2022 Fall Semester Exam 1",
                     "beginDate": "2021-08-23",
                     "endDate": "2021-10-03",
                     "periodSequence": 1,
                     "totalInstructionalDays": 29
                   }
                  """
             Then it should respond with 201 or 200
             When a DELETE request is made to "/ed-fi/gradingPeriodDescriptors/{id}"
             Then it should respond with 409

        @ignore
        Scenario: 06 Verify response code when deleting a referenced resource
             When a POST request is made to "/ed-fi/schools" with
                  """
                    {
                      "schoolId": 255901001,
                      "nameOfInstitution": "testschool",
                       "educationOrganizationCategories": [
                         {
                           "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Other"
                         }
                       ],
                       "schoolCategories": [
                         {
                           "schoolCategoryDescriptor": "uri://ed-fi.org/SchoolCategoryDescriptor#All Levels"
                         }
                       ],
                       "gradeLevels": [
                         {
                           "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#First Grade"
                         }
                       ]
                    }
                  """
             Then it should respond with 201 or 200
             When a POST request is made to "ed-fi/academicWeeks" with
                  """
                   {
                    "weekIdentifier": "abcdef",
                    "schoolReference": {
                        "schoolId": 255901001 },
                    "beginDate": "2024-04-04",
                    "endDate": "2024-04-04",
                    "totalInstructionalDays": 300
                   }
                  """
             Then it should respond with 201 or 200
             When a DELETE request is made to "/ed-fi/schools/{id}"
             Then it should respond with 409

