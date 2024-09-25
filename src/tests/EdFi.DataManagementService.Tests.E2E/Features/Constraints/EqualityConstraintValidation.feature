Feature: Equality Constraint Validation
              Equality constraints on the resource describe values that must be equal when posting a resource. An example of an equalityConstraint on bellSchedule:
    "equalityConstraints": [
    {
    "sourceJsonPath": "$.classPeriods[*].classPeriodReference.schoolId",
    "targetJsonPath": "$.schoolReference.schoolId"
    }
    ]
        @API-001
        Scenario: 01 Post a valid bell schedule no equality constraint violations.
            Given the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 255901001 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "classPeriods"
                  | classPeriodName  | schoolReference           |
                  | 01 - Traditional | { "schoolId": 255901001 } |
                  | 02 - Traditional | { "schoolId": 255901001 } |
             When a POST request is made to "/ed-fi/bellschedules" with
                  """
                  {
                      "schoolReference": {
                          "schoolId": 255901001
                      },
                      "bellScheduleName": "Test Schedule",
                      "totalInstructionalTime": 325,
                      "classPeriods": [
                          {
                              "classPeriodReference": {
                                  "classPeriodName": "01 - Traditional",
                                  "schoolId": 255901001
                              }
                          },
                          {
                              "classPeriodReference": {
                                  "classPeriodName": "02 - Traditional",
                                  "schoolId": 255901001
                              }
                          }
                      ],
                      "dates": [],
                      "gradeLevels": []
                  }
                  """
             Then it should respond with 201 or 200

        @API-002
        Scenario: 02 Post an invalid bell schedule with equality constraint violations.
             When a POST request is made to "/ed-fi/bellschedules" with
                  """
                  {
                      "schoolReference": {
                          "schoolId": 255901001
                      },
                      "bellScheduleName": "Test Schedule",
                      "totalInstructionalTime": 325,
                      "classPeriods": [
                          {
                              "classPeriodReference": {
                                  "classPeriodName": "01 - Traditional",
                                  "schoolId": 1
                              }
                          },
                          {
                              "classPeriodReference": {
                                  "classPeriodName": "02 - Traditional",
                                  "schoolId": 1
                              }
                          }
                      ],
                      "dates": [],
                      "gradeLevels": []
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
                          "$.classPeriods[*].classPeriodReference.schoolId": [
                              "All values supplied for 'schoolId' must match. Review all references (including those higher up in the resource's data) and align the following conflicting values: '1', '255901001'"
                          ],
                          "$.schoolReference.schoolId": [
                              "All values supplied for 'schoolId' must match. Review all references (including those higher up in the resource's data) and align the following conflicting values: '1', '255901001'"
                          ]
                      },
                    "errors": []
                  }
                  """

        @API-003
        Scenario: 03 Making a Post request when value does not match the same value in an array
             When a POST request is made to "/ed-fi/sections" with
                  """
                  {
                      "sectionIdentifier": "25590100102Trad220ALG112011Test",
                      "courseOfferingReference": {
                          "localCourseCode": "ALG-1",
                          "schoolId": 255901001,
                          "schoolYear": 2022,
                          "sessionName": "2021-2022 Fall Semester"
                      },
                      "classPeriods": [
                          {
                              "classPeriodReference": {
                                  "classPeriodName": "02 - Traditional",
                                  "schoolId": 255901107
                              }
                          }
                      ]
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
                          "$.classPeriods[*].classPeriodReference.schoolId": [
                              "All values supplied for 'schoolId' must match. Review all references (including those higher up in the resource's data) and align the following conflicting values: '255901107', '255901001'"
                          ],
                          "$.courseOfferingReference.schoolId": [
                              "All values supplied for 'schoolId' must match. Review all references (including those higher up in the resource's data) and align the following conflicting values: '255901107', '255901001'"
                          ]
                      },
                    "errors": []
                  }
                  """

        @API-004
        Scenario: 04 Making a Post request when a value matches the first scenario in an array but not the second
             When a POST request is made to "/ed-fi/sections" with
                  """
                  {
                      "sectionIdentifier": "25590100102Trad220ALG112011Test",
                      "courseOfferingReference": {
                          "localCourseCode": "ALG-1",
                          "schoolId": 255901001,
                          "schoolYear": 2022,
                          "sessionName": "2021-2022 Fall Semester"
                      },
                      "classPeriods": [
                          {
                              "classPeriodReference": {
                                  "classPeriodName": "01 - Traditional",
                                  "schoolId": 1
                              }
                          },
                          {
                              "classPeriodReference": {
                                  "classPeriodName": "02 - Traditional",
                                  "schoolId": 1
                              }
                          }
                      ]
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
                          "$.classPeriods[*].classPeriodReference.schoolId": [
                              "All values supplied for 'schoolId' must match. Review all references (including those higher up in the resource's data) and align the following conflicting values: '1', '255901001'"
                          ],
                          "$.courseOfferingReference.schoolId": [
                              "All values supplied for 'schoolId' must match. Review all references (including those higher up in the resource's data) and align the following conflicting values: '1', '255901001'"
                          ]
                      },
                    "errors": []
                  }
                  """

        @API-005
        Scenario: 05 Making a Post request when value does not match the same value in a single other reference
             When a POST request is made to "/ed-fi/sections" with
                  """
                  {
                      "sectionIdentifier": "25590100102Trad220ALG112011Test",
                      "courseOfferingReference": {
                          "localCourseCode": "ALG-1",
                          "schoolId": 255901001,
                          "schoolYear": 2022,
                          "sessionName": "2021-2022 Fall Semester"
                      },
                      "locationReference": {
                          "classroomIdentificationCode": "106",
                          "schoolId": 1
                      }
                  }
                  """
             Then it should respond with 409
              And the response body is
                  """
                  {
                      "detail": "The referenced CourseOffering, Location item(s) do not exist.",
                      "type": "urn:ed-fi:api:data-conflict:unresolved-reference",
                      "title": "Unresolved Reference",
                      "status": 409,
                      "correlationId": null,
                      "validationErrors":{},
                      "errors":[]
                  }
                  """

