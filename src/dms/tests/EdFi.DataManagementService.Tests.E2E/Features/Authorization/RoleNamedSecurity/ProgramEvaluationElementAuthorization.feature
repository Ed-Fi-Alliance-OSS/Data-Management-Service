Feature: ProgramEvaluationElement Authorization

        Background:
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901"
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | categories                                                                                                          | localEducationAgencyCategoryDescriptor                     |
                  | 255901                 | Test LEA          | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#District" }] | uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC |
              And the system has these "programs"
                  | programName | programTypeDescriptor                         | educationOrganizationReference     |
                  | 21st CCLC   | uri://ed-fi.org/ProgramTypeDescriptor#Support | {"educationOrganizationId":255901} |
              And the system has these "programEvaluations"
                  | _storeResultingIdInVariable | programEvaluationTitle | programEvaluationPeriodDescriptor                             | programEvaluationTypeDescriptor                                | programReference                                                                                                                          |
                  | programEvaluationId         | Test Evaluation        | uri://ed-fi.org/ProgramEvaluationPeriodDescriptor#End of Year | uri://ed-fi.org/ProgramEvaluationTypeDescriptor#Teacher survey | {"educationOrganizationId": 255901, "programName": "21st CCLC", "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Support"} |
              And the system has these "programEvaluationObjectives"
                  | _storeResultingIdInVariable  | programEvaluationObjectiveTitle | programEvaluationReference                                                                                                                                                                                                                                                                                                                                                                               |
                  | programEvaluationObjectiveId | Test Evaluation Objective       | {"programEducationOrganizationId": 255901, "programEvaluationPeriodDescriptor": "uri://ed-fi.org/ProgramEvaluationPeriodDescriptor#End of Year", "programEvaluationTitle": "Test Evaluation", "programEvaluationTypeDescriptor": "uri://ed-fi.org/ProgramEvaluationTypeDescriptor#Teacher survey", "programName": "21st CCLC", "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Support"} |
              And the system has these "programEvaluationElements"
                  | _storeResultingIdInVariable | programEvaluationElementTitle | programEvaluationReference                                                                                                                                                                                                                                                                                                                                                                               | programEvaluationObjectiveReference                                                                                                                                                                                                                                                                                                                                                                                                                                      |
                  | programEvaluationElementId  | Test Evaluation Element       | {"programEducationOrganizationId": 255901, "programEvaluationPeriodDescriptor": "uri://ed-fi.org/ProgramEvaluationPeriodDescriptor#End of Year", "programEvaluationTitle": "Test Evaluation", "programEvaluationTypeDescriptor": "uri://ed-fi.org/ProgramEvaluationTypeDescriptor#Teacher survey", "programName": "21st CCLC", "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Support"} | {"programEducationOrganizationId": 255901, "programEvaluationObjectiveTitle": "Test Evaluation Objective", "programEvaluationPeriodDescriptor": "uri://ed-fi.org/ProgramEvaluationPeriodDescriptor#End of Year", "programEvaluationTitle": "Test Evaluation", "programEvaluationTypeDescriptor": "uri://ed-fi.org/ProgramEvaluationTypeDescriptor#Teacher survey", "programName": "21st CCLC", "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Support"} |

    Rule: When the client is authorized
        Scenario: 01 Ensure authorized client can create a ProgramEvaluationElement
             When a POST request is made to "/ed-fi/programEvaluationElements" with
                  """
                  {
                    "programEvaluationObjectiveReference": {
                        "programEducationOrganizationId": 255901,
                        "programEvaluationObjectiveTitle": "Test Evaluation Objective",
                        "programEvaluationPeriodDescriptor": "uri://ed-fi.org/ProgramEvaluationPeriodDescriptor#End of Year",
                        "programEvaluationTitle": "Test Evaluation",
                        "programEvaluationTypeDescriptor": "uri://ed-fi.org/ProgramEvaluationTypeDescriptor#Teacher survey",
                        "programName": "21st CCLC",
                        "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Support"
                    },
                    "programEvaluationReference": {
                        "programEducationOrganizationId": 255901,
                        "programEvaluationPeriodDescriptor": "uri://ed-fi.org/ProgramEvaluationPeriodDescriptor#End of Year",
                        "programEvaluationTitle": "Test Evaluation",
                        "programEvaluationTypeDescriptor": "uri://ed-fi.org/ProgramEvaluationTypeDescriptor#Teacher survey",
                        "programName": "21st CCLC",
                        "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Support"
                    },
                    "programEvaluationElementTitle": "New Test Evaluation Element"
                  }
                  """
             Then it should respond with 201

        Scenario: 02.1 Ensure authorized client can get a ProgramEvaluationElement by id
             When a GET request is made to "/ed-fi/programEvaluationElements/{programEvaluationElementId}"
             Then it should respond with 200

        Scenario: 02.2 Ensure authorized client can get a ProgramEvaluationElement by query
            Given a POST request is made to "/ed-fi/programEvaluationElements" with
                  """
                  {
                    "programEvaluationObjectiveReference": {
                        "programEducationOrganizationId": 255901,
                        "programEvaluationObjectiveTitle": "Test Evaluation Objective",
                        "programEvaluationPeriodDescriptor": "uri://ed-fi.org/ProgramEvaluationPeriodDescriptor#End of Year",
                        "programEvaluationTitle": "Test Evaluation",
                        "programEvaluationTypeDescriptor": "uri://ed-fi.org/ProgramEvaluationTypeDescriptor#Teacher survey",
                        "programName": "21st CCLC",
                        "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Support"
                    },
                    "programEvaluationReference": {
                        "programEducationOrganizationId": 255901,
                        "programEvaluationPeriodDescriptor": "uri://ed-fi.org/ProgramEvaluationPeriodDescriptor#End of Year",
                        "programEvaluationTitle": "Test Evaluation",
                        "programEvaluationTypeDescriptor": "uri://ed-fi.org/ProgramEvaluationTypeDescriptor#Teacher survey",
                        "programName": "21st CCLC",
                        "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Support"
                    },
                    "programEvaluationElementTitle": "New Test Evaluation Element"
                  }
                  """
             When a GET request is made to "/ed-fi/programEvaluationElements?programEvaluationElementTitle=New Test Evaluation Element"
             Then it should respond with 200
              And the response body is
                  """
                  [
                    {
                        "id": "{id}",
                        "programEvaluationObjectiveReference": {
                            "programEducationOrganizationId": 255901,
                            "programEvaluationObjectiveTitle": "Test Evaluation Objective",
                            "programEvaluationPeriodDescriptor": "uri://ed-fi.org/ProgramEvaluationPeriodDescriptor#End of Year",
                            "programEvaluationTitle": "Test Evaluation",
                            "programEvaluationTypeDescriptor": "uri://ed-fi.org/ProgramEvaluationTypeDescriptor#Teacher survey",
                            "programName": "21st CCLC",
                            "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Support"
                        },
                        "programEvaluationReference": {
                            "programEducationOrganizationId": 255901,
                            "programEvaluationPeriodDescriptor": "uri://ed-fi.org/ProgramEvaluationPeriodDescriptor#End of Year",
                            "programEvaluationTitle": "Test Evaluation",
                            "programEvaluationTypeDescriptor": "uri://ed-fi.org/ProgramEvaluationTypeDescriptor#Teacher survey",
                            "programName": "21st CCLC",
                            "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Support"
                        },
                        "programEvaluationElementTitle": "New Test Evaluation Element"
                    }
                  ]
                  """

        Scenario: 03 Ensure authorized client can update a ProgramEvaluationElement
             When a PUT request is made to "/ed-fi/programEvaluationElements/{programEvaluationElementId}" with
                  """
                  {
                    "id": "{programEvaluationElementId}",
                    "programEvaluationObjectiveReference": {
                        "programEducationOrganizationId": 255901,
                        "programEvaluationObjectiveTitle": "Test Evaluation Objective",
                        "programEvaluationPeriodDescriptor": "uri://ed-fi.org/ProgramEvaluationPeriodDescriptor#End of Year",
                        "programEvaluationTitle": "Test Evaluation",
                        "programEvaluationTypeDescriptor": "uri://ed-fi.org/ProgramEvaluationTypeDescriptor#Teacher survey",
                        "programName": "21st CCLC",
                        "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Support"
                    },
                    "programEvaluationReference": {
                        "programEducationOrganizationId": 255901,
                        "programEvaluationPeriodDescriptor": "uri://ed-fi.org/ProgramEvaluationPeriodDescriptor#End of Year",
                        "programEvaluationTitle": "Test Evaluation",
                        "programEvaluationTypeDescriptor": "uri://ed-fi.org/ProgramEvaluationTypeDescriptor#Teacher survey",
                        "programName": "21st CCLC",
                        "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Support"
                    },
                    "programEvaluationElementTitle": "Test Evaluation Element"
                  }
                  """
             Then it should respond with 204

        Scenario: 04 Ensure authorized client can delete a ProgramEvaluationElement
             When a DELETE request is made to "/ed-fi/programEvaluationElements/{programEvaluationElementId}"
             Then it should respond with 204

    Rule: When the client is unauthorized
        Background:
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255902"

        Scenario: 05 Ensure unauthorized client can not create a ProgramEvaluationElement
             When a POST request is made to "/ed-fi/programEvaluationElements" with
                  """
                  {
                    "programEvaluationObjectiveReference": {
                        "programEducationOrganizationId": 255901,
                        "programEvaluationObjectiveTitle": "Test Evaluation Objective",
                        "programEvaluationPeriodDescriptor": "uri://ed-fi.org/ProgramEvaluationPeriodDescriptor#End of Year",
                        "programEvaluationTitle": "Test Evaluation",
                        "programEvaluationTypeDescriptor": "uri://ed-fi.org/ProgramEvaluationTypeDescriptor#Teacher survey",
                        "programName": "21st CCLC",
                        "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Support"
                    },
                    "programEvaluationReference": {
                        "programEducationOrganizationId": 255901,
                        "programEvaluationPeriodDescriptor": "uri://ed-fi.org/ProgramEvaluationPeriodDescriptor#End of Year",
                        "programEvaluationTitle": "Test Evaluation",
                        "programEvaluationTypeDescriptor": "uri://ed-fi.org/ProgramEvaluationTypeDescriptor#Teacher survey",
                        "programName": "21st CCLC",
                        "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Support"
                    },
                    "programEvaluationElementTitle": "New Test Evaluation Element"
                  }
                  """
             Then it should respond with 403
              And the response body is
                  """
                  {
                     "detail": "Access to the resource could not be authorized.",
                     "type": "urn:ed-fi:api:security:authorization:",
                     "title": "Authorization Denied",
                     "status": 403,
                     "validationErrors": {},
                     "errors": [
                        "No relationships have been established between the caller's education organization id claims ('255902') and the resource item's ProgramEvaluation value."
                     ]
                  }
                  """

        Scenario: 06.1 Ensure unauthorized client can not get a ProgramEvaluationElement by id
             When a GET request is made to "/ed-fi/programEvaluationElements/{programEvaluationElementId}"
             Then it should respond with 403
              And the response body is
                  """
                  {
                     "detail": "Access to the resource could not be authorized.",
                     "type": "urn:ed-fi:api:security:authorization:",
                     "title": "Authorization Denied",
                     "status": 403,
                     "validationErrors": {},
                     "errors": [
                        "No relationships have been established between the caller's education organization id claims ('255902') and the resource item's ProgramEvaluation value."
                     ]
                  }
                  """

        Scenario: 06.2 Ensure unauthorized client can not get a ProgramEvaluationElement by query
             When a GET request is made to "/ed-fi/programEvaluationElements?programEvaluationElementTitle=Test Evaluation Element"
             Then it should respond with 200
              And the response body is
                  """
                  []
                  """

        Scenario: 07 Ensure unauthorized client can not update a ProgramEvaluationElement
             When a PUT request is made to "/ed-fi/programEvaluationElements/{programEvaluationElementId}" with
                  """
                  {
                    "id": "{programEvaluationElementId}",
                    "programEvaluationObjectiveReference": {
                        "programEducationOrganizationId": 255901,
                        "programEvaluationObjectiveTitle": "Test Evaluation Objective",
                        "programEvaluationPeriodDescriptor": "uri://ed-fi.org/ProgramEvaluationPeriodDescriptor#End of Year",
                        "programEvaluationTitle": "Test Evaluation",
                        "programEvaluationTypeDescriptor": "uri://ed-fi.org/ProgramEvaluationTypeDescriptor#Teacher survey",
                        "programName": "21st CCLC",
                        "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Support"
                    },
                    "programEvaluationReference": {
                        "programEducationOrganizationId": 255901,
                        "programEvaluationPeriodDescriptor": "uri://ed-fi.org/ProgramEvaluationPeriodDescriptor#End of Year",
                        "programEvaluationTitle": "Test Evaluation",
                        "programEvaluationTypeDescriptor": "uri://ed-fi.org/ProgramEvaluationTypeDescriptor#Teacher survey",
                        "programName": "21st CCLC",
                        "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Support"
                    },
                    "programEvaluationElementTitle": "Test Evaluation Element"
                  }
                  """
             Then it should respond with 403
              And the response body is
                  """
                  {
                     "detail": "Access to the resource could not be authorized.",
                     "type": "urn:ed-fi:api:security:authorization:",
                     "title": "Authorization Denied",
                     "status": 403,
                     "validationErrors": {},
                     "errors": [
                        "No relationships have been established between the caller's education organization id claims ('255902') and the resource item's ProgramEvaluation value."
                     ]
                  }
                  """

        Scenario: 08 Ensure unauthorized client can not delete a ProgramEvaluationElement
             When a DELETE request is made to "/ed-fi/programEvaluationElements/{programEvaluationElementId}"
             Then it should respond with 403
              And the response body is
                  """
                  {
                     "detail": "Access to the resource could not be authorized.",
                     "type": "urn:ed-fi:api:security:authorization:",
                     "title": "Authorization Denied",
                     "status": 403,
                     "validationErrors": {},
                     "errors": [
                        "No relationships have been established between the caller's education organization id claims ('255902') and the resource item's ProgramEvaluation value."
                     ]
                  }
                  """
