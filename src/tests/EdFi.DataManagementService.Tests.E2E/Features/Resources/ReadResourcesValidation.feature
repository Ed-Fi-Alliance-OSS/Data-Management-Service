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

        Scenario: 05 Verify response code 404 when trying to get a school with an ID that corresponds to Course
            Given the system has these "Schools" 
                  | schoolId | nameOfInstitution | educationOrganizationCategories                                                                                         | gradeLevels                                                                          |
                  | 100      | School Test       | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"}]   | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"}]      |
            And a POST request is made to "courses" with
                   """
                   {
                        "courseCode": "ALG-2",
                        "identificationCodes": [
                          {
                            "courseIdentificationSystemDescriptor": "uri://ed-fi.org/CourseIdentificationSystemDescriptor#LEA course code",
                            "identificationCode": "ALG-2"
                          }
                        ],
                        "educationOrganizationReference": {
                          "educationOrganizationId": 100
                        },
                        "courseTitle": "Algebra 2",
                        "numberOfParts": 2
                    }
                   """
             When a GET request is made to "ed-fi/schools/{id}"
             Then it should respond with 404

        Scenario: 06 Verify response code 200 when trying to get a school with a correct ID
            Given a POST request is made to "Schools" with
                """
                {
                   "schoolId":101,
                   "nameOfInstitution":"School Test",
                   "gradeLevels":[    
                      {
                         "gradeLevelDescriptor":"Postsecondary"
                      }
                   ],
                   "educationOrganizationCategories":[
                      {
                         "educationOrganizationCategoryDescriptor":"School"
                      }
                   ]
                }
                """
             When a GET request is made to "ed-fi/schools/{id}"
             Then it should respond with 200
