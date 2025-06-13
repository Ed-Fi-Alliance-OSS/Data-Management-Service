Feature: DisciplineAction Authorization

        Background:
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901"
              And the system has these descriptors
                  | descriptorValue                                           |
                  | uri://ed-fi.org/DisciplineDescriptor#in school suspension |
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | categories                                                                                                          | localEducationAgencyCategoryDescriptor                     |
                  | 255901                 | Test LEA          | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#District" }] | uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC |
              And the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                   | localEducationAgencyReference      |
                  | 255901001 | School 001        | [{ "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" }] | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school" }] | {"localEducationAgencyId": 255901} |
                  | 255901044 | School 044        | [{ "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" }] | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school" }] | {"localEducationAgencyId": 255901} |
              And the system has these "students"
                  | studentUniqueId | firstName  | lastSurname | birthDate  |
                  | "61"            | student-fn | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | studentReference          | schoolReference         | entryGradeLevelDescriptor                          | entryDate  |
                  | {"studentUniqueId": "61"} | {"schoolId": 255901001} | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
              And the system has these "disciplineIncidents"
                  | incidentIdentifier | incidentDate | schoolReference         |
                  | "1"                | 2024-01-25   | {"schoolId": 255901001} |
              And the system has these "disciplineActions"
                  | _storeResultingIdInVariable | disciplineActionIdentifier | disciplineDate | responsibilitySchoolReference | assignmentSchoolReference | studentReference          | studentDisciplineIncidentAssociations                                                                                              | disciplines                                                                               |
                  | disciplineActionId          | TEST-001                   | 2022-02-09     | {"schoolId": 255901001}       | {"schoolId": 255901044}   | {"studentUniqueId": "61"} | [{ "studentDisciplineIncidentAssociationReference": {"studentUniqueId": "61", "incidentIdentifier": "1", "schoolId": 255901001} }] | [{ "disciplineDescriptor": "uri://ed-fi.org/DisciplineDescriptor#In School Suspension" }] |

    Rule: When the client is authorized
        Background:
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901001"

        Scenario: 01 Ensure authorized client can create a DisciplineAction
             When a POST request is made to "/ed-fi/disciplineActions" with
                  """
                  {
                    "responsibilitySchoolReference": {
                        "schoolId": 255901001
                    },
                    "assignmentSchoolReference": {
                        "schoolId": 255901044
                    },
                    "studentReference": {
                        "studentUniqueId": "61"
                    },
                    "disciplineActionIdentifier": "New TEST-001",
                    "disciplineDate": "2022-02-09",
                    "disciplines": [
                        {
                            "disciplineDescriptor": "uri://ed-fi.org/DisciplineDescriptor#In School Suspension"
                        }
                    ],
                    "studentDisciplineIncidentAssociations": [
                        {
                            "studentDisciplineIncidentAssociationReference": {
                                "studentUniqueId": "61",
                                "incidentIdentifier": "1",
                                "schoolId": 255901001
                            }
                        }
                    ]
                  }
                  """
             Then it should respond with 201

        Scenario: 02 Ensure authorized client can get a DisciplineAction
             When a GET request is made to "/ed-fi/disciplineActions/{disciplineActionId}"
             Then it should respond with 200

        Scenario: 03 Ensure authorized client can update a DisciplineAction
             When a PUT request is made to "/ed-fi/disciplineActions/{disciplineActionId}" with
                  """
                  {
                    "id": "{disciplineActionId}",
                    "responsibilitySchoolReference": {
                        "schoolId": 255901001
                    },
                    "assignmentSchoolReference": {
                        "schoolId": 255901044
                    },
                    "studentReference": {
                        "studentUniqueId": "61"
                    },
                    "disciplineActionIdentifier": "TEST-001",
                    "disciplineDate": "2022-02-09",
                    "disciplines": [
                        {
                            "disciplineDescriptor": "uri://ed-fi.org/DisciplineDescriptor#In School Suspension"
                        }
                    ],
                    "studentDisciplineIncidentAssociations": [
                        {
                            "studentDisciplineIncidentAssociationReference": {
                                "studentUniqueId": "61",
                                "incidentIdentifier": "1",
                                "schoolId": 255901001
                            }
                        }
                    ]
                  }
                  """
             Then it should respond with 204

        Scenario: 04 Ensure authorized client can delete a DisciplineAction
             When a DELETE request is made to "/ed-fi/disciplineActions/{disciplineActionId}"
             Then it should respond with 204

    Rule: When the client is unauthorized
        Background:
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901044"

        Scenario: 05 Ensure unauthorized client can not create a DisciplineAction
             When a POST request is made to "/ed-fi/disciplineActions" with
                  """
                  {
                    "responsibilitySchoolReference": {
                        "schoolId": 255901001
                    },
                    "assignmentSchoolReference": {
                        "schoolId": 255901044
                    },
                    "studentReference": {
                        "studentUniqueId": "61"
                    },
                    "disciplineActionIdentifier": "New TEST-001",
                    "disciplineDate": "2022-02-09",
                    "disciplines": [
                        {
                            "disciplineDescriptor": "uri://ed-fi.org/DisciplineDescriptor#In School Suspension"
                        }
                    ],
                    "studentDisciplineIncidentAssociations": [
                        {
                            "studentDisciplineIncidentAssociationReference": {
                                "studentUniqueId": "61",
                                "incidentIdentifier": "1",
                                "schoolId": 255901001
                            }
                        }
                    ]
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
                        "No relationships have been established between the caller's education organization id claims ('255901044') and one or more of the following properties of the resource item: 'EducationOrganizationId', 'StudentUniqueId'."
                     ]
                  }
                  """

        Scenario: 06 Ensure unauthorized client can not get a DisciplineAction
             When a GET request is made to "/ed-fi/disciplineActions/{disciplineActionId}"
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
                        "No relationships have been established between the caller's education organization id claims ('255901044') and one or more of the following properties of the resource item: 'EducationOrganizationId', 'StudentUniqueId'."
                     ]
                  }
                  """

        Scenario: 07 Ensure unauthorized client can not update a DisciplineAction
             When a PUT request is made to "/ed-fi/disciplineActions/{disciplineActionId}" with
                  """
                  {
                    "id": "{disciplineActionId}",
                    "responsibilitySchoolReference": {
                        "schoolId": 255901001
                    },
                    "assignmentSchoolReference": {
                        "schoolId": 255901044
                    },
                    "studentReference": {
                        "studentUniqueId": "61"
                    },
                    "disciplineActionIdentifier": "TEST-001",
                    "disciplineDate": "2022-02-09",
                    "disciplines": [
                        {
                            "disciplineDescriptor": "uri://ed-fi.org/DisciplineDescriptor#In School Suspension"
                        }
                    ],
                    "studentDisciplineIncidentAssociations": [
                        {
                            "studentDisciplineIncidentAssociationReference": {
                                "studentUniqueId": "61",
                                "incidentIdentifier": "1",
                                "schoolId": 255901001
                            }
                        }
                    ]
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
                        "No relationships have been established between the caller's education organization id claims ('255901044') and one or more of the following properties of the resource item: 'EducationOrganizationId', 'StudentUniqueId'."
                     ]
                  }
                  """

        Scenario: 08 Ensure unauthorized client can not delete a DisciplineAction
             When a DELETE request is made to "/ed-fi/disciplineActions/{disciplineActionId}"
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
                        "No relationships have been established between the caller's education organization id claims ('255901044') and one or more of the following properties of the resource item: 'EducationOrganizationId', 'StudentUniqueId'."
                     ]
                  }
                  """
