/*
                * SPDX-License-Identifier: Apache-2.0
                * Licensed to the Ed-Fi Alliance under one or more agreements.
                * The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
                * See the LICENSE and NOTICES files in the project root for more information.
                */

Feature: Check the least amount of friction in the data exchange while still ensuring data are valid

        Background:
            Given the system has these "schools" references
                  | educationOrganizationCategories | gradeLevels   | nameOfInstitution      | schoolId  |
                  | School                          | Twelfth grade | Grand Bend High School | 255901001 |

        @API-233 @ignore
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

        @API-234 @ignore
        Scenario: 02 Ensure attributes not defined are not included as part of the update of the resource
            Given the system has these "schools" references
                  | educationOrganizationCategories | gradeLevels | nameOfInstitution  | schoolId           |
                  | Post Secondary Institution      | Ninth grade | Middle School Test | 745672453832456000 |
             When a PUT request is made to "/ed-fi/schools/{id}" with
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
                      "schoolId": 745672453832456,
                      "nameOfInstitution": "Institution Test",
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
                      "schoolId": 745672453832456,
                      "nameOfInstitution": "Institution Test"
                  }
                  """

        @API-235 @ignore
        Scenario: 03 Ensure client can retrieve information through attribute using mixed changes
            Given the system has these "classPeriods" references
                  | classPeriodName   | schoolReference | officialAttendancePeriod |
                  | Class Period Test | 255901001       | 1                        |
             When a GET request is made to "/ed-fi/classPeriods?classPeriodName=CLASS+pERIOD+test"
             Then it should respond with 200
              And the response body is
                  """
                  [
                      {
                          "id": "bf1b531a8177472698e2c5f1e83ec4f7",
                          "schoolReference": {
                              "schoolId": 255901001
                          },
                          "classPeriodName": "Class Period Test",
                          "officialAttendancePeriod": true
                      }
                  ]
                  """

        @API-236 @ignore
        Scenario: 04 Ensure clients can create a resource using numeric values for booleans
             When a POST request is made to "/ed-fi/classPeriods" with
                  """
                  {
                      "classPeriodName": "Class Period Test 1",
                      "schoolReference": {
                          "schoolId": 255901001
                      },
                      "officialAttendancePeriod": 0
                  }
                  """
             Then it should respond with 201

        @API-237 @ignore
        Scenario: 05 Ensure clients can update a resource using numeric values for booleans
            Given the system has these "classPeriods" with
                  | classPeriodName     | schoolReference | officialAttendancePeriod |
                  | Class Period Test 1 | 255901001       | 0                        |
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
              And the record can be retrieved with a GET request

        @API-238 @ignore
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

        @API-239 @ignore
        Scenario: 07 Ensure clients cannot create a resource using incorrect values for booleans
            Given the system has these "classPeriods" with
                  | classPeriodName     | schoolReference | officialAttendancePeriod |
                  | Class Period Test 1 | 255901001       | 0                        |
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


        @API-240 @ignore
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

        @API-241 @ignore
        Scenario: 09 Ensure clients can update a resource using expected booleans
            Given the system has these "classPeriods" with
                  | classPeriodName     | schoolReference | officialAttendancePeriod |
                  | Class Period Test 2 | 255901001       | true                     |
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

        @API-242 @ignore
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

        @API-243 @ignore
        Scenario: 11 Ensure clients can update a resource using expected booleans as strings
            Given the system has these "classPeriods" with
                  | classPeriodName     | schoolReference | officialAttendancePeriod |
                  | Class Period Test 3 | 255901001       | true                     |
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

        @API-244 @ignore
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

        @API-245 @ignore
        Scenario: 13 Ensure clients can update a resource using numeric values as strings
            Given the system has these "classPeriods" with
                  | classPeriodName     | schoolReference | officialAttendancePeriod |
                  | Class Period Test 4 | 255901001       | true                     |
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

        @API-246 @ignore
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
            #  Pending response body

        @API-247 @ignore
        Scenario: 15 Ensure clients cannot update a resource that is using a different value typa than boolean
            Given the system has these "classPeriods" with
                  | classPeriodName     | schoolReference | officialAttendancePeriod |
                  | Class Period Test 4 | 255901001       | true                     |
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
             # Pending response

