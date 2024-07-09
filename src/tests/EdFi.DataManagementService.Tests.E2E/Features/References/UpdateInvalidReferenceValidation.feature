Feature: Invalid Reference Validation
    PUT requests validation for invalid references

        Background:            
            Given the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | localEducationAgencyCategoryDescriptor                                          | categories                                                                                                                              |
                  | 10203040               | Institution Test  | "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Federal operated agency | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency" }]  |
              And the system has these "schools"
                  | schoolId     | nameOfInstitution | gradeLevels                                                                        | educationOrganizationCategories                                                                                        |
                  | 255901       | School Test       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School" }] |
              And the system has these "students"
                  | studentUniqueId   | birthDate   | firstName   | lastSurname  |
                  | "604834"          | 2000-01-01  | Thomas      | Johnson      |
              And the system has these "studentEducationOrganizationAssociations"
                  | educationOrganizationReference           | studentReference                  |
                  | {"educationOrganizationId":255901}       | {"studentUniqueId":"604834"}      |
              And the system has these "programs"
                  | programName                        | programTypeDescriptor                                                  | educationOrganizationReference        | programId |
                  | Career and Technical Education     | "uri://ed-fi.org/ProgramTypeDescriptor#Career and Technical Education" | {"educationOrganizationId":255901}    | "100"     |
              And the system has these "studentCTEProgramAssociations"
                  | beginDate  | educationOrganizationReference     | programReference                                                                                                                                                                     |  studentReference               |
                  | 2020-06-05 | {"educationOrganizationId":255901} | {"educationOrganizationId":255901, "programName":"Career and Technical Education", "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Career and Technical Education"}  | {"studentUniqueId":"604834"}    |
              And the system has these "schoolYearTypes"
                  | schoolYear | currentSchoolYear | schoolYearDescription |
                  | 2022       | true              | 2021-2022             |
              And the system has these "graduationPlans"
                  | graduationPlanTypeDescriptor   | educationOrganizationReference      | graduationSchoolYearTypeReference | totalRequiredCredits |
                  | Career and Technical Education | {"educationOrganizationId":255901}  | {"schoolYear":2022}               | 10.000               |
              And the system has these "courses"
                  | courseCode | identificationCodes                                                                                                                                  | educationOrganizationReference        | courseTitle | numberOfParts |
                  | ALG-1      | [{"identificationCode": "ALG-1", "courseIdentificationSystemDescriptor":"uri://ed-fi.org/CourseIdentificationSystemDescriptor#State course code"}]   | {"educationOrganizationId":255901}    | Algebra I   | 1             |
              And the system has these "sessions"
                  | sessionName               | schoolReference     | schoolYearTypeReference | beginDate  | endDate    | totalInstructionalDays | termDescriptor                                 |
                  | "2021-2022 Fall Semester" | {"schoolId":255901} | {"schoolYear":2022}     | 2021-08-23 | 2021-12-17 | 81                     | "uri://ed-fi.org/TermDescriptor#Fall Semester" |
           #   And the system has these "courseOfferings"
           #   And the system has these "sections"
           #   And the system has these "studentSectionAssociations"
           #       | beginDate  | sectionReference                                                                                                                                                  | studentReference               |
           #       | 2021-08-23 | {"localCourseCode":"ALG-1", "schoolId":255901, "schoolYear": 2022, "sectionIdentifier":"25590100102Trad220ALG112011", "sessionName":"2021-2022 Fall Semester"  }  | {"studentUniqueId":"604834"}   |


        
        Scenario: 01 Ensure clients cannot update a resource with a Descriptor that does not exist
             When a PUT request is made to "ed-fi/localEducationAgencies/{id}" with
                  """
                  {
                      "id": "{id}",
                      "localEducationAgencyId": 10203040,
                      "nameOfInstitution": "Institution Test",
                      "localEducationAgencyCategoryDescriptor": "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Federal operated agency",
                      "categories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Fake"
                          }
                      ]
                  }
                  """
             Then it should respond with 409
              And the response body is
                  """
                  {
                      "detail": "The referenced 'Categories' item does not exist.",
                      "type": "urn:ed-fi:api:data-conflict:unresolved-reference",
                      "title": "Unresolved Reference",
                      "status": 409,
                      "correlationId": null
                  }
                  """

        @ignore
        Scenario: 02 Ensure clients cannot update a resource missing a direct reference
             When a PUT request is made to "ed-fi/studentEducationOrganizationAssociations/{id}" with
                  """
                  {
                      "id": "{id}",
                      "educationOrganizationReference": {
                          "educationOrganizationId": 255901
                      }
                  }
                  """
             Then it should respond with 409
              And the response body is
                  """
                  {
                      "detail": "The referenced 'StudentReference' item does not exist.",
                      "type": "urn:ed-fi:api:data-conflict:unresolved-reference",
                      "title": "Unresolved Reference",
                      "status": 409,
                      "correlationId": null
                  }
                  """

        @ignore
        Scenario: 03 Ensure clients cannot update a resource using a correct reference but missing the other one
             When a PUT request is made to "ed-fi/studentEducationOrganizationAssociations/{id}" with
                  """
                  {
                      "id": "{id}",
                      "studentReference": {
                        "studentUniqueId":"604834"
                      }
                  }
                  """
             Then it should respond with 409
              And the response body is
                  """
                  {
                      "detail": "The referenced 'EducationOrganizationReference' item does not exist.",
                      "type": "urn:ed-fi:api:data-conflict:unresolved-reference",
                      "title": "Unresolved Reference",
                      "status": 409,
                      "correlationId": null
                  }
                  """

        @ignore
        Scenario: 04 Ensure clients cannot update a resource that uses a reference more than once
             When a PUT request is made to "ed-fi/studentCTEProgramAssociations/{id}" with
                  """
                  {
                      "id": "{id}",
                      "beginDate": "2020-06-05",
                      "programReference": {
                        "programName": "Career and Technical Education",
                        "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Career and Technical Education"
                      },
                      "studentReference": {
                          "studentUniqueId": "604825"
                      }
                  }
                  """
             Then it should respond with 409
              And the response body is
                  """
                  {
                      "detail": "The referenced 'EducationOrganizationReference' item does not exist.",
                      "type": "urn:ed-fi:api:data-conflict:unresolved-reference",
                      "title": "Unresolved Reference",
                      "status": 409,
                      "correlationId": null
                  }
                  """

        @ignore
        Scenario: 05 Ensure clients cannot update a resource that uses a reference more than once and misses another required reference
             When a PUT request is made to "ed-fi/studentCTEProgramAssociations/{id}" with
                  """
                  {
                      "id": "{id}",
                      "beginDate": "2020-06-05",
                      "educationOrganizationReference": {
                          "educationOrganizationId": 255901
                      },
                      "programReference": {
                        "programName": "Career and Technical Education",
                        "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Career and Technical Education"
                      }
                  }
                  """
             Then it should respond with 409
              And the response body is
                  """
                  {
                      "detail": "The referenced 'StudentReference' item does not exist.",
                      "type": "urn:ed-fi:api:data-conflict:unresolved-reference",
                      "title": "Unresolved Reference",
                      "status": 409,
                      "correlationId": null
                  }
                  """

        @ignore
        Scenario: 06 Ensure clients cannot update a resource that uses an invalid date from a reference
             When a PUT request is made to "ed-fi/graduationPlans/{id}" with
                  """
                  {
                      "id": "{id}",
                      "educationOrganizationReference": {
                          "educationOrganizationId": 255901
                      },
                      "graduationSchoolYearTypeReference": {
                          "schoolYear": 1970
                      },
                      "graduationPlanTypeDescriptor": "uri://ed-fi.org/GraduationPlanTypeDescriptor#Career and Technical Education",
                      "totalRequiredCredits": 10.000
                  }
                  """
             Then it should respond with 409
              And the response body is
                  """
                  {
                      "detail": "The referenced 'GraduationSchoolYearTypeReference' item does not exist.",
                      "type": "urn:ed-fi:api:data-conflict:unresolved-reference",
                      "title": "Unresolved Reference",
                      "status": 409,
                      "correlationId": null
                  }
                  """

        @ignore
        Scenario: 07 Ensure clients cannot update a resource that is incorrect from a deep reference
             When a PUT request is made to "ed-fi/studentSectionAssociations/{id}" with
                  """
                  {
                      "id": "{id}",
                      "sectionReference": {
                          "localCourseCode": "ALG-1",
                          "schoolYear": 2022,
                          "sectionIdentifier": "25590100102Trad220ALG112011",
                          "sessionName": "2021-2022 Fall Semester"
                      },
                      "studentReference": {
                          "studentUniqueId": "604874"
                      },
                      "beginDate": "2021-08-23"
                  }
                  """
             Then it should respond with 409
              And the response body is
                  """
                  {
                      "detail": "The referenced 'SectionReferenceSectionReference' item does not exist.",
                      "type": "urn:ed-fi:api:data-conflict:unresolved-reference",
                      "title": "Unresolved Reference",
                      "status": 409,
                      "correlationId": null
                  }
                  """
