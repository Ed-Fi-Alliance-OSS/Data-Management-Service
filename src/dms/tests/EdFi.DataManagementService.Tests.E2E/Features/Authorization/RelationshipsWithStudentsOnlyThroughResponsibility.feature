Feature: RelationshipsWithStudentsOnlyThroughResponsibility Authorization

        Background:
            Given the claimSet "EdFiAPIPublisherWriter" is authorized with educationOrganizationIds "1255901001"
              And the system has these "schoolYearTypes"
                  | schoolYear | currentSchoolYear | schoolYearDescription |
                  | 2023       | true              | "year 2023"           |
              And the system has these descriptors
                  | descriptorValue                                                      |
                  | uri://ed-fi.org/ResponsibilityDescriptor#Accountability              |
                  | uri://ed-fi.org/ResponsibilityDescriptor#Attendance                  |
                  | uri://ed-fi.org/ProgramTypeDescriptor#Career and Technical Education |
                  | uri://ed-fi.org/IdeaPartDescriptor#Eligible                          |

    Rule: StudentEducationOrganizationResponsibilityAssociation CRUD is properly authorized
        Background:
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1255901001, 1255901002"
              And the system has these "schools"
                  | schoolId   | nameOfInstitution   | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 1255901001 | Authorized school 1 | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
                  | 1255901002 | Authorized school 2 | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
                  | 1255901003 | Unauthorized school | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName            | lastSurname | birthDate  |
                  | "11"            | Authorized student   | student-ln  | 2008-01-01 |
                  | "12"            | Unauthorized student | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | entryDate  | schoolReference            | studentReference           | entryGradeLevelDescriptor                        |
                  | 2023-08-01 | { "schoolId": 1255901001 } | {"studentUniqueId": "11" } | uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade |
                  | 2023-08-01 | { "schoolId": 1255901002 } | {"studentUniqueId": "11" } | uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade |
              And the system has these "programs"
                  | programName                      | programTypeDescriptor                                                | educationOrganizationReference          |
                  | "Career and Technical Education" | uri://ed-fi.org/ProgramTypeDescriptor#Career and Technical Education | {"educationOrganizationId": 1255901002} |

        Scenario: 01 Ensure client can create a StudentEducationOrganizationResponsibilityAssociation
             When a POST request is made to "/ed-fi/studentEducationOrganizationResponsibilityAssociations" with
                  """
                  {
                      "beginDate": "2023-08-01",
                      "educationOrganizationReference": {
                          "educationOrganizationId": 1255901002
                      },
                      "studentReference": {
                          "studentUniqueId": "11"
                      },
                      "responsibilityDescriptor": "uri://ed-fi.org/ResponsibilityDescriptor#Accountability"
                  }
                  """
             Then it should respond with 201

        Scenario: 02 Ensure client can create a StudentSpecialEducationProgramEligibilityAssociation
            Given a POST request is made to "/ed-fi/studentEducationOrganizationResponsibilityAssociations" with
                  """
                  {
                      "beginDate": "2023-08-01",
                      "educationOrganizationReference": {
                          "educationOrganizationId": 1255901002
                      },
                      "studentReference": {
                          "studentUniqueId": "11"
                      },
                      "responsibilityDescriptor": "uri://ed-fi.org/ResponsibilityDescriptor#Accountability"
                  }
                  """
             When a POST request is made to "/ed-fi/studentSpecialEducationProgramEligibilityAssociations" with
                  """
                  {
                      "beginDate": "2023-08-01",
                      "educationOrganizationReference": {
                          "educationOrganizationId": 1255901002
                      },
                      "programReference": {
                          "educationOrganizationId": 1255901002,
                          "programName": "Career and Technical Education",
                          "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Career and Technical Education"
                      },
                      "studentReference": {
                          "studentUniqueId": "11"
                      },
                      "consentToEvaluationReceivedDate": "2023-08-01",
                      "ideaPartDescriptor": "uri://ed-fi.org/IdeaPartDescriptor#Eligible"
                  }
                  """
             Then it should respond with 201

        Scenario: 03 Ensure client can not create a StudentSpecialEducationProgramEligibilityAssociation without a StudentEducationOrganizationResponsibilityAssociation
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1255901001"
             When a POST request is made to "/ed-fi/studentSpecialEducationProgramEligibilityAssociations" with
                  """
                  {
                      "beginDate": "2023-08-01",
                      "educationOrganizationReference": {
                          "educationOrganizationId": 1255901002
                      },
                      "programReference": {
                          "educationOrganizationId": 1255901002,
                          "programName": "Career and Technical Education",
                          "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Career and Technical Education"
                      },
                      "studentReference": {
                          "studentUniqueId": "11"
                      },
                      "consentToEvaluationReceivedDate": "2023-08-01",
                      "ideaPartDescriptor": "uri://ed-fi.org/IdeaPartDescriptor#Eligible"
                  }
                  """
             Then it should respond with 403
              And the response body is
                  """
                  {
                    "detail": "Access to the resource could not be authorized. Hint: You may need to create a corresponding 'StudentEducationOrganizationResponsibilityAssociation' item.",
                    "type": "urn:ed-fi:api:security:authorization:",
                    "title": "Authorization Denied",
                    "status": 403,
                    "validationErrors": {},
                    "errors": [
                        "No relationships have been established between the caller's education organization id claims ('1255901001') and the resource item's EducationOrganizationId value.",
                        "No relationships have been established between the caller's education organization id claims ('1255901001') and the resource item's StudentUniqueId value."
                    ]
                  }
                  """
