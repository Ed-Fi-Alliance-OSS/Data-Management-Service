Feature: Data strictness
    Validate that the API meets data strictness / laxity requirements.

        Background:
            Given the system has these descriptors
                  | descriptorValue                                                 |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School  |
                  | uri://ed-fi.org/GradeLevelDescriptor#Twelfth grade              |
                  | uri://ed-fi.org/AcademicSubjectDescriptor#English Language Arts |
            Given the system has these "schools"
                  | schoolId  | nameOfInstitution        | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 255901044 | Grand Bend Middle School | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |

        @ignore @API-236
        Scenario: 04 Ensure clients can create a resource using numeric values for booleans
             When a POST request is made to "/ed-fi/classPeriods" with
                  """
                  {
                      "schoolReference": {
                          "schoolId": 255901044
                      },
                      "classPeriodName": "Class Period 1",
                      "officialAttendancePeriod": 0
                  }
                  """
             Then it should respond with 201

        @ignore @API-237
        Scenario: 05 Ensure clients can update a resource using numeric values for booleans
             When a PUT request is made to "/ed-fi/classPeriods/{id}" with
                  """
                  {
                      "classPeriodName": "Class Period Test 1",
                      "schoolReference": {
                          "schoolId": 255901044
                      },
                      "officialAttendancePeriod": 1
                  }
                  """
             Then it should respond with 204

        @ignore @API-238
        Scenario: 06 Ensure clients cannot create a resource using incorrect values for booleans
             When a POST request is made to "/ed-fi/classPeriods" with
                  """
                  {
                      "classPeriodName": "Class Period Test 1",
                      "schoolReference": {
                          "schoolId": 255901044
                      },
                      "officialAttendancePeriod": 2
                  }
                  """
             Then it should respond with 400
              And the response body is
              # Pending confirmation

        @ignore @API-239
        Scenario: 07 Ensure clients cannot create a resource using incorrect values for booleans
             When a POST request is made to "/ed-fi/classPeriods" with
                  """
                  {
                      "classPeriodName": "Class Period Test 1",
                      "schoolReference": {
                          "schoolId": 255901044
                      },
                      "officialAttendancePeriod": 2
                  }
                  """
             Then it should respond with 400
             # Pending confirmation


        @API-240
        Scenario: 08 Ensure clients can create a resource using expected booleans
             When a POST request is made to "/ed-fi/classPeriods" with
                  """
                  {
                      "classPeriodName": "Class Period Test 2",
                      "schoolReference": {
                          "schoolId": 255901044
                      },
                      "officialAttendancePeriod": true
                  }
                  """
             Then it should respond with 201

        @ignore @API-241
        Scenario: 09 Ensure clients can update a resource using expected booleans
             When a PUT request is made to "/ed-fi/classPeriods" with
                  """
                  {
                      "classPeriodName": "Class Period Test 2",
                      "schoolReference": {
                          "schoolId": 255901044
                      },
                      "officialAttendancePeriod": false
                  }
                  """
             Then it should respond with 204

        @ignore @API-242
        Scenario: 10 Ensure clients can create a resource using expected booleans as string
             When a POST request is made to "/ed-fi/classPeriods" with
                  """
                  {
                      "classPeriodName": "Class Period Test 3",
                      "schoolReference": {
                          "schoolId": 255901044
                      },
                      "officialAttendancePeriod": "true"
                  }
                  """
             Then it should respond with 201
              And the record can be retrieved with a GET request
                  """
                       {
                           "classPeriodName": "Class Period Test 3",
                           "schoolReference": {
                               "schoolId": 255901044
                           },
                           "officialAttendancePeriod": true
                       }
                  """

        @ignore @API-243
        Scenario: 11 Ensure clients can update a resource using expected booleans as strings
             When a PUT request is made to "/ed-fi/classPeriods" with
                  """
                  {
                      "classPeriodName": "Class Period Test 2",
                      "schoolReference": {
                          "schoolId": 255901044
                      },
                      "officialAttendancePeriod": "false"
                  }
                  """
             Then it should respond with 204
              And the record can be retrieved with a GET request
                  """
                       {
                           "classPeriodName": "Class Period Test 3",
                           "schoolReference": {
                               "schoolId": 255901044
                           },
                           "officialAttendancePeriod": false
                       }
                  """

        @ignore @API-244
        Scenario: 12 Ensure clients can create a resource using numeric values as strings
             When a POST request is made to "/ed-fi/classPeriods" with
                  """
                  {
                      "classPeriodName": "Class Period Test 4",
                      "schoolReference": {
                          "schoolId": 255901044
                      },
                      "officialAttendancePeriod": "1"
                  }
                  """
             Then it should respond with 201
              And the record can be retrieved with a GET request
                  """
                       {
                           "classPeriodName": "Class Period Test 4",
                           "schoolReference": {
                               "schoolId": 255901044
                           },
                           "officialAttendancePeriod": true
                       }
                  """

        @ignore @API-245
        Scenario: 13 Ensure clients can update a resource using numeric values as strings
             When a POST request is made to "/ed-fi/classPeriods" with
                  """
                  {
                      "classPeriodName": "Class Period Test 4",
                      "schoolReference": {
                          "schoolId": 255901044
                      },
                      "officialAttendancePeriod": "0"
                  }
                  """
             Then it should respond with 204
              And the record can be retrieved with a GET request
                  """
                       {
                           "classPeriodName": "Class Period Test 4",
                           "schoolReference": {
                               "schoolId": 255901044
                           },
                           "officialAttendancePeriod": true
                       }
                  """

        @ignore @API-246
        Scenario: 14 Ensure clients cannot update a resource that is using a different value typa than boolean
             When a POST request is made to "/ed-fi/classPeriods" with
                  """
                  {
                      "classPeriodName": "Class Period Test 4",
                      "schoolReference": {
                          "schoolId": 255901044
                      },
                      "officialAttendancePeriod": "string"
                  }
                  """
             Then it should respond with 400
                  """
                  {
                      "detail": "Data validation failed. See 'validationErrors' for details.",
                      "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                      "title": "Data Validation Failed",
                      "status": 400,
                      "correlationId": null,
                      "validationErrors": {
                          "$.officialAttendancePeriod": [
                          "Could not convert string to boolean: 1. Path 'officialAttendancePeriod'"
                          ]
                      }
                  }
                  """


        @ignore @API-247
        Scenario: 15 Ensure clients cannot update a resource that is using a different value type than boolean
             When a POST request is made to "/ed-fi/classPeriods" with
                  """
                  {
                      "classPeriodName": "Class Period Test 4",
                      "schoolReference": {
                          "schoolId": 255901044
                      },
                      "officialAttendancePeriod": "0"
                  }
                  """
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
                          "$.officialAttendancePeriod": [
                          "Could not convert string to boolean: 1. Path 'officialAttendancePeriod'"
                          ]
                      }
                  }
                  """

        @API-248
        Scenario: 16 Enforce case sensitivity of property names in POST request bodies.
             # Uppercase GRADELEVELS
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "GRADELEVELS": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                          }
                      ],
                      "schoolId": 745672453832456000,
                      "nameOfInstitution": "Middle School Test"
                  }
                  """
             Then it should respond with 400

        @API-249
        Scenario: 17 Enforce case sensitivity of property names in PUT request bodies.
             # Uppercase GRADELEVELS
             When a POST request is made to "/ed-fi/schools/{id}" with
                  """
                  {
                      "id": "{id}",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "GRADELEVELS": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                          }
                      ],
                      "schoolId": 745672453832456000,
                      "nameOfInstitution": "Middle School Test"
                  }
                  """
             Then it should respond with 400

        @API-255 @ignore
        # Currently the DMS is doing what an API _should_ do (require a time), but the ODS/API does NOT
        # require a time. Changing to require a time would be a breaking change, so we can't do that
        # in the DMS, at least not with Data Standard < 6.
        # DMS-396
        Scenario: 18 Enforce presence of time in a POST request with a datetime property
            Given a POST request is made to "/ed-fi/assessments" with
                  """
                  {
                      "assessmentIdentifier": "01774fa3-06f1-47fe-8801-c8b1e65057f2",
                      "namespace": "uri://ed-fi.org/Assessment/Assessment.xml", "academicSubjects": [
                          {
                              "academicSubjectDescriptor": "uri://ed-fi.org/AcademicSubjectDescriptor#English Language Arts"
                          }
                      ],
                      "assessmentTitle": "title"
                  }
                  """
            Given a POST request is made to "/ed-fi/schoolYearTypes" with
                  """
                  {
                    "schoolYear": 2022,
                    "schoolYearDescription": "2022",
                    "currentSchoolYear": true
                  }
                  """
            Given a POST request is made to "/ed-fi/students" with
                  """
                  {
                    "studentUniqueId": "604906",
                    "firstName": "first",
                    "lastSurname": "last",
                    "birthDate": "2001-01-01"
                  }
                  """
             When a POST request is made to "/ed-fi/studentAssessments" with
                # Adminstration Date is missing the time - THIS IS ACCEPTED BY THE ODS/API
                  """
                  {
                      "assessmentReference": {
                          "assessmentIdentifier": "01774fa3-06f1-47fe-8801-c8b1e65057f2",
                          "namespace": "uri://ed-fi.org/Assessment/Assessment.xml"
                      },
                      "schoolYearTypeReference": {
                          "schoolYear": 2022
                      },
                      "studentReference": {
                          "studentUniqueId": "604906"
                      },
                      "studentAssessmentIdentifier": "/Qhqqe/gI4p3RguP68ZEDArGHM64FKnCg/RLHG8c",
                      "administrationDate": "2021-09-28"
                  }
                  """
             Then it should respond with 201
