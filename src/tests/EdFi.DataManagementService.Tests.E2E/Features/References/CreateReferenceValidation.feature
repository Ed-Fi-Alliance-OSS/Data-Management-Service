Feature: Create Reference Validation
    POST requests testing invalid references

        Background:
            Given the system has these descriptors
                  | descriptorValue                                                             |
                  | uri://ed-fi.org/GradeLevelDescriptor#TenthGrade                             |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School              |
                  | uri://ed-fi.org/ProgramTypeDescriptor#Career and Technical Education        |
                  | uri://ed-fi.org/GraduationPlanTypeDescriptor#Career and Technical Education |
                  | uri://ed-fi.org/TermDescriptor#Spring Semester                              |
              And the system has these "schoolYearTypes"
                  | schoolYear | currentSchoolYear | schoolYearDescription |
                  | 2025       | false             | School Year 2025      |
              And the system has these "Schools" references
                  | schoolId | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 123      | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |

        @API-079
        Scenario: 01 Ensure clients cannot create a resource with a non existing reference
             When a POST request is made to "/ed-fi/academicWeeks" with
                  """
                  {
                   "weekIdentifier": "WeekIdentifier1",
                   "schoolReference": {
                     "schoolId": 9999
                   },
                   "beginDate": "2023-09-11",
                   "endDate": "2023-09-11",
                   "totalInstructionalDays": 300
                  }
                  """

             Then the response body is
                  """
                  {
                      "detail": "The referenced School item(s) do not exist.",
                      "type": "urn:ed-fi:api:data-conflict:unresolved-reference",
                      "title": "Unresolved Reference",
                      "status": 409,
                      "correlationId": null,
                      "validationErrors": {},
                      "errors": []
                  }
                  """
              And it should respond with 409

        @API-080
        Scenario: 02 Ensure clients cannot create a resource with correct information but an invalid value belonging to the reference
             When a POST request is made to "/ed-fi/studentCTEProgramAssociations" with
                  """
                  {
                      "beginDate": "2020-06-05",
                      "educationOrganizationReference": {
                          "educationOrganizationId": 123
                      },
                      "programReference": {
                          "educationOrganizationId": 123,
                          "programName": "Fake",
                          "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Career and Technical Education"
                      },
                      "studentReference": {
                          "studentUniqueId": "604825"
                      }
                  }
                  """

             Then the response body is
                  """
                  {
                      "detail": "The referenced Program, Student item(s) do not exist.",
                      "type": "urn:ed-fi:api:data-conflict:unresolved-reference",
                      "title": "Unresolved Reference",
                      "status": 409,
                      "correlationId": null,
                      "validationErrors": {},
                      "errors": []
                  }
                  """
              And it should respond with 409

        @API-081
        Scenario: 03 Ensure clients cannot create a resource using a reference that is out of range of the existing values
             When a POST request is made to "/ed-fi/graduationPlans" with
                  """
                  {
                     "graduationPlanTypeDescriptor": "uri://ed-fi.org/GraduationPlanTypeDescriptor#Career and Technical Education",
                     "educationOrganizationReference": {
                         "educationOrganizationId": 123
                     },
                     "graduationSchoolYearTypeReference": {
                         "schoolYear": 1970
                     },
                     "totalRequiredCredits": 10
                  }
                  """
             Then it should respond with 409
              And the response body is
                  """
                  {
                    "detail": "The referenced SchoolYearType item(s) do not exist.",
                    "type": "urn:ed-fi:api:data-conflict:unresolved-reference",
                    "title": "Unresolved Reference",
                    "status": 409,
                    "correlationId": null,
                    "validationErrors": {},
                    "errors": []
                  }
                  """

        @API-082
        Scenario: 04 Ensure clients cannot create a resource using an invalid reference inside of another reference
            Given the system has these "students"
                  | studentUniqueId | firstName | lastSurname | birthDate    |
                  | "456"           | Firstname | Lastsurname | "2020-01-01" |
             When a POST request is made to "/ed-fi/studentSectionAssociations" with
                  """
                  {
                      "beginDate": "2023-08-23",
                      "sectionReference": {
                          "localCourseCode": "ALG-1",
                          "schoolId": 123,
                          "schoolYear": 2022,
                          "sectionIdentifier": "12300103Trad220ALG122011",
                          "sessionName": "2021-2022 Spring Semester"
                      },
                      "studentReference": {
                          "studentUniqueId": "456"
                      }
                  }
                  """
             Then it should respond with 409
              And the response body is
                  """
                  {
                      "detail": "The referenced Section item(s) do not exist.",
                      "type": "urn:ed-fi:api:data-conflict:unresolved-reference",
                      "title": "Unresolved Reference",
                      "status": 409,
                      "correlationId": null,
                      "validationErrors": {},
                      "errors": []
                  }
                  """

        @API-083
        Scenario: 05 Verify clients cannot update a resource with a bad academicWeeks reference
            Given the system has these "AcademicWeeks" references
                  | weekIdentifier | nameOfInstitution | schoolReference   | beginDate    | endDate      | totalInstructionalDays |
                  | WeekIdentifier | Test school       | {"schoolId": 123} | "2024-07-10" | "2024-07-10" | 365                    |

             When a POST request is made to "/ed-fi/sessions" with
                  """
                  {
                    "sessionName": "sessionName",
                    "schoolReference": {
                      "schoolId": 123
                    },
                    "schoolYearTypeReference": {
                      "schoolYear": 2025
                    },
                    "academicWeeks": [
                      {
                        "academicWeekReference": {
                          "schoolId": 123,
                          "weekIdentifier": "WeekIdentifier"
                        }
                      }
                    ],
                    "beginDate": "2024-07-10",
                    "endDate": "2024-07-10",
                    "termDescriptor": "uri://ed-fi.org/TermDescriptor#Spring Semester",
                    "totalInstructionalDays": 365
                  }
                  """

              And a POST request is made to "/ed-fi/sessions" with
                  """
                  {
                    "sessionName": "sessionName",
                    "schoolReference": {
                      "schoolId": 123
                    },
                    "schoolYearTypeReference": {
                      "schoolYear": 2025
                    },
                    "academicWeeks": [
                      {
                        "academicWeekReference": {
                          "schoolId": 123,
                          "weekIdentifier": "Invalid"
                        }
                      }
                    ],
                    "beginDate": "2024-07-10",
                    "endDate": "2024-07-10",
                    "termDescriptor": "uri://ed-fi.org/TermDescriptor#Spring Semester",
                    "totalInstructionalDays": 365
                  }
                  """

             Then the response body is
                  """
                  {
                      "detail": "The referenced AcademicWeek item(s) do not exist.",
                      "type": "urn:ed-fi:api:data-conflict:unresolved-reference",
                      "title": "Unresolved Reference",
                      "status": 409,
                      "correlationId": null,
                      "validationErrors": {},
                      "errors": []
                  }
                  """
              And it should respond with 409
