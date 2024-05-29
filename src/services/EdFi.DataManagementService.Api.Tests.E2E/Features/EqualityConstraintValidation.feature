Feature: Equality Constraint Validation
              Equality constraints on the resource describe values that must be equal when posting a resource. An example of an equalityConstraint on bellSchedule:
    "equalityConstraints": [
        {
            "sourceJsonPath": "$.classPeriods[*].classPeriodReference.schoolId",
            "targetJsonPath": "$.schoolReference.schoolId"
        }
    ]

        Scenario: Post a valid bell schedule no equality constraint violations.
             When a POST request is made to "ed-fi/bellschedules" with
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
             Then it should respond with 201

        Scenario: Post an invalid bell schedule with equality constraint violations.
             When a POST request is made to "ed-fi/bellschedules" with
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
                  {"detail":"The request could not be processed. See 'errors' for details.","type":"urn:ed-fi:api:bad-request","title":"Bad Request","status":400,"correlationId":null,"validationErrors":null,"errors":["Constraint failure: document paths $.classPeriods[*].classPeriodReference.schoolId and $.schoolReference.schoolId must have the same values"]}
                  """

        @ignore
        Scenario: Making a Post request when value does not match the same value in an array
             When a POST request is made to "ed-fi/sections" with
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
                      "type": "urn:ed-fi:api:bad-request:data",
                      "title": "Data Validation Failed",
                      "status": 400,
                      "correlationId": null,
                      "validationErrors": {
                          "$.classPeriods[0].classPeriodReference.schoolId": [
                              "All values supplied for 'schoolId' must match. Review all references (including those higher up in the resource's data) and align the following conflicting values: '255901001', '255901107'"
                          ]
                      }
                  }
                  """

        @ignore
        Scenario: Making a Post request when a value matches the first scenario in an array but not the second
             When a POST request is made to "ed-fi/sections" with
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
                      "type": "urn:ed-fi:api:bad-request:data",
                      "detail": "Data validation failed. See 'validationErrors' for details.",
                      "title": "Data Validation Failed",
                      "status": 400,
                      "correlationId": null,
                      "validationErrors": {
                          "$.classPeriods[0].classPeriodReference.schoolId": [
                              "All values supplied for 'schoolId' must match. Review all references (including those higher up in the resource's data) and align the following conflicting values: '255901001', '1'"
                          ],
                          "$.classPeriods[1].classPeriodReference.schoolId": [
                              "All values supplied for 'schoolId' must match. Review all references (including those higher up in the resource's data) and align the following conflicting values: '255901001', '1'"
                          ]
                      }
                  }
                  """

        @ignore
        Scenario: Making a Post request when value does not match the same value in a single other reference
             When a POST request is made to "ed-fi/sections" with
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
                      "detail": "The referenced 'Location' resource does not exist.",
                      "type": "urn:ed-fi:api:conflict:invalid-reference",
                      "title": "Resource Not Unique Conflict due to invalid-reference",
                      "status": 409,
                      "correlationId": null
                  }
                  """

