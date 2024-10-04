Feature: Query String handling for GET requests for Resource Queries

        @addwait
        Scenario: 00 Background
            Given the system has these "schools"
                  | schoolId | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 2        | School 2          | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
              And the system has these "academicweeks"
                  | weekIdentifier | beginDate  | endDate    | totalInstructionalDays | schoolReference |
                  | Week One       | 2024-05-15 | 2024-05-22 | 2                      | {"schoolId": 2} |

        @API-124
        Scenario: 01 Ensure clients can GET information when querying by valid date
             When a GET request is made to "/ed-fi/academicWeeks?beginDate=2024-05-15"
             Then it should respond with 200
              And the response body is
                  """
                  [{
                    "id": "{id}",
                    "schoolReference": {
                        "schoolId": 2
                    },
                    "weekIdentifier": "Week One",
                    "beginDate": "2024-05-15",
                    "endDate": "2024-05-22",
                    "totalInstructionalDays": 2
                  }]
                  """

        @API-125
        Scenario: 02 Ensure clients can't GET information when querying by invalid date
             When a GET request is made to "/ed-fi/academicWeeks?beginDate=024-04-09"
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
                           "$.beginDate": ["The value '024-04-09' is not valid for beginDate."]
                       },
                       "errors": []
                   }
                  """

        @API-126
        Scenario: 03 Ensure clients can't GET information when querying by a word
             When a GET request is made to "/ed-fi/academicWeeks?beginDate=word"
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
                        "$.beginDate": ["The value 'word' is not valid for beginDate."]
                    },
                    "errors": []
                  }
                  """

        @API-127
        Scenario: 04 Ensure clients can't GET information when querying by wrong begin date
             When a GET request is made to "/ed-fi/academicWeeks?beginDate=1970-04-09"
             Then it should respond with 200
              And the response body is
                  """
                  []
                  """

        @API-128
        Scenario: 05 Ensure clients can't GET information when querying by correct begin date and wrong end date
             When a GET request is made to "/ed-fi/academicWeeks?beginDate=2024-05-15&endDate=2025-06-23"
             Then it should respond with 200
              And the response body is
                  """
                  []
                  """

        @API-129
        Scenario: 06 Ensure clients can GET information when querying by string parameter
             When a GET request is made to "/ed-fi/academicWeeks?weekIdentifier=Week+One"
             Then it should respond with 200
              And the response body is
                  """
                  [{
                      "id": "{id}",
                      "schoolReference": {
                          "schoolId": 2
                      },
                      "weekIdentifier": "Week One",
                      "beginDate": "2024-05-15",
                      "endDate": "2024-05-22",
                      "totalInstructionalDays": 2
                  }]
                  """

        @API-130
        Scenario: 07 Ensure clients can GET information when querying by integer parameter
             When a GET request is made to "/ed-fi/academicWeeks?totalInstructionalDays=2"
             Then it should respond with 200
              And the response body is
                  """
                  [{
                      "id": "{id}",
                      "schoolReference": {
                          "schoolId": 2
                      },
                      "weekIdentifier": "Week One",
                      "beginDate": "2024-05-15",
                      "endDate": "2024-05-22",
                      "totalInstructionalDays": 2
                  }]
                  """

        @API-131
        Scenario: 08 Ensure clients can GET information when querying with mixed case parameter name
             When a GET request is made to "/ed-fi/academicWeeks?WEEKIdentifier=Week+One"
             Then it should respond with 200
              And the response body is
                  """
                  [{
                      "id": "{id}",
                      "schoolReference": {
                          "schoolId": 2
                      },
                      "weekIdentifier": "Week One",
                      "beginDate": "2024-05-15",
                      "endDate": "2024-05-22",
                      "totalInstructionalDays": 2
                  }]
                  """

        @API-132
        Scenario: 09 Ensure clients can GET information when querying with lower case parameter name
             When a GET request is made to "/ed-fi/academicWeeks?weekidentifier=Week+One"
             Then it should respond with 200
              And the response body is
                  """
                  [{
                      "id": "{id}",
                      "schoolReference": {
                          "schoolId": 2
                      },
                      "weekIdentifier": "Week One",
                      "beginDate": "2024-05-15",
                      "endDate": "2024-05-22",
                      "totalInstructionalDays": 2
                  }]
                  """

        @API-133
        Scenario: 10 Ensure clients can GET information when querying with upper case parameter name
             When a GET request is made to "/ed-fi/academicWeeks?WEEKIDENTIFIER=Week+One"
             Then it should respond with 200
              And the response body is
                  """
                  [{
                      "id": "{id}",
                      "schoolReference": {
                          "schoolId": 2
                      },
                      "weekIdentifier": "Week One",
                      "beginDate": "2024-05-15",
                      "endDate": "2024-05-22",
                      "totalInstructionalDays": 2
                  }]
                  """

        @API-134
        Scenario: 11 Ensure clients can GET information when querying with mixed case parameter name and value
             When a GET request is made to "/ed-fi/academicWeeks?WEEKIDENTIFier=week+ONE"
             Then it should respond with 200
              And the response body is
                  """
                  [{
                      "id": "{id}",
                      "schoolReference": {
                          "schoolId": 2
                      },
                      "weekIdentifier": "Week One",
                      "beginDate": "2024-05-15",
                      "endDate": "2024-05-22",
                      "totalInstructionalDays": 2
                  }]
                  """

        @API-135
        Scenario: 12 Ensure clients can GET information when querying with mixed case parameter name and upper case value
             When a GET request is made to "/ed-fi/academicWeeks?WEEKIDENTIFier=WEEK+ONE"
             Then it should respond with 200
              And the response body is
                  """
                  [{
                      "id": "{id}",
                      "schoolReference": {
                          "schoolId": 2
                      },
                      "weekIdentifier": "Week One",
                      "beginDate": "2024-05-15",
                      "endDate": "2024-05-22",
                      "totalInstructionalDays": 2
                  }]
                  """
