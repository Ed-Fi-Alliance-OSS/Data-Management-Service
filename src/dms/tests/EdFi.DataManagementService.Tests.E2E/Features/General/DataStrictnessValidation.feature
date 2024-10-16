Feature: Check the least amount of friction in the data exchange while still ensuring data are valid

        Background:
            Given the system has these descriptors
                  | descriptorValue                                                                    |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                                   |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School                     |
                  | uri://ed-fi.org/GradeLevelDescriptor#Twelfth grade                                 |
              And a POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Twelfth grade"
                          }
                      ],
                      "schoolId": 255901001,
                      "nameOfInstitution": "Grand Bend High School"
                  }
                  """
              And a POST request is made to "/ed-fi/classPeriods" with
                  """
                  {
                      "schoolReference": {
                          "schoolId": 255901001
                      },
                      "classPeriodName": "Class Period Test",
                      "officialAttendancePeriod": true
                  }
                  """

        @API-233
        Scenario: 01 Ensure attributes not defined are not included as part of the creation of a resource
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                          }
                      ],
                      "schoolId": 745672453832456000,
                      "nameOfInstitution": "Middle School Test",
                      "name": "Test"
                  }
                  """
             Then it should respond with 201
              And the record can be retrieved with a GET request
                  """
                  {
                      "id": "{id}",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                          }
                      ],
                      "nameOfInstitution": "Middle School Test",
                      "schoolId": 745672453832456000
                  }
                  """

        @API-234
        Scenario: 02 Ensure attributes not defined are not included as part of the update of the resource
            Given a POST request is made to "/ed-fi/schools/" with
                  """
                  {
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                          }
                      ],
                      "schoolId": 745672453832456000,
                      "nameOfInstitution": "Middle School Test"
                  }
                  """
             When a PUT request is made to "/ed-fi/schools/{id}" with
                  """
                  {
                      "id": "{id}",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                          }
                      ],
                      "schoolId": 745672453832456000,
                      "nameOfInstitution": "Middle School Test",
                      "name": "Test",
                      "lastName": "Last name"
                  }
                  """
             Then it should respond with 204
              And the record can be retrieved with a GET request
                  """
                  {
                      "id": "{id}",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                          }
                      ],
                      "schoolId": 745672453832456000,
                      "nameOfInstitution": "Middle School Test"
                  }
                  """

        @API-235
        Scenario: 03 Ensure client can retrieve information through attribute using mixed changes
             When a GET request is made to "/ed-fi/classPeriods?classPeriodName=CLASS+pERIOD+test"
             Then it should respond with 200
              And the response body is
                  """
                  [
                      {
                          "schoolReference": {
                              "schoolId": 255901001
                          },
                          "classPeriodName": "Class Period Test",
                          "officialAttendancePeriod": true
                      }
                  ]
                  """

        @ignore @API-236
        Scenario: 04 Ensure clients can create a resource using numeric values for booleans
             When a POST request is made to "/ed-fi/classPeriods" with
                  """
                  {
                      "schoolReference": {
                          "schoolId": 255901001
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
                          "schoolId": 255901001
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
                          "schoolId": 255901001
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
                          "schoolId": 255901001
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
                          "schoolId": 255901001
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
                          "schoolId": 255901001
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
                          "schoolId": 255901001
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
                               "schoolId": 255901001
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
                          "schoolId": 255901001
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
                               "schoolId": 255901001
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
                          "schoolId": 255901001
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
                               "schoolId": 255901001
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
                          "schoolId": 255901001
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
                               "schoolId": 255901001
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
                          "schoolId": 255901001
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
        Scenario: 15 Ensure clients cannot update a resource that is using a different value typa than boolean
             When a POST request is made to "/ed-fi/classPeriods" with
                  """
                  {
                      "classPeriodName": "Class Period Test 4",
                      "schoolReference": {
                          "schoolId": 255901001
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

