Feature: Invalid Reference Validation
    PUT requests validation for invalid references

        Background:            
            Given the system has these "schools"
                  | schoolId     | nameOfInstitution | gradeLevels                                                                        | educationOrganizationCategories                                                                                        |
                  | 255901       | School Test       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School" }] |
              And the system has these "students"
                  | studentUniqueId   | birthDate   | firstName   | lastSurname  |
                  | "604834"          | 2000-01-01  | Thomas      | Johnson      |
              And the system has these "programs"
                  | programName                        | programTypeDescriptor                                                  | educationOrganizationReference        | programId |
                  | Career and Technical Education     | "uri://ed-fi.org/ProgramTypeDescriptor#Career and Technical Education" | {"educationOrganizationId":255901}    | "100"     |
              And the system has these "schoolYearTypes"
                  | schoolYear | currentSchoolYear | schoolYearDescription |
                  | 2022       | true              | 2021-2022             |

        
        Scenario: 01 Ensure clients cannot update a resource with a Descriptor that does not exist
            Given the system has these "localEducationAgencies" references
                  | localEducationAgencyId | nameOfInstitution | localEducationAgencyCategoryDescriptor                                          | categories                                                                                                                              |
                  | 10203040               | Institution Test  | "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Federal operated agency | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency" }]  |
             When a PUT request is made to referenced resource "ed-fi/localEducationAgencies/{id}" with
                  """
                  {
                      "id": "{id}",
                      "localEducationAgencyId": 102030401,
                      "nameOfInstitution": "Institution Test",
                      "localEducationAgencyCategoryDescriptor": "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Federal operated agency",
                      "categories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Fake"
                          }
                      ]
                  }
                  """
             Then it should respond with 400
              And the response body is
                  """
                  {
                      "detail": "Identifying values for the LocalEducationAgency resource cannot be changed. Delete and recreate the resource item instead.",
                      "type": "urn:ed-fi:api:bad-request:data-validation-failed:key-change-not-supported",
                      "title": "Key Change Not Supported",
                      "status": 400,
                      "correlationId": null,
                      "validationErrors": null,
                      "errors": null
                  }
                  """
                          
        Scenario: 02 Ensure clients cannot update a resource missing a direct reference
            Given the system has these "studentEducationOrganizationAssociations" references
                  | educationOrganizationReference           | studentReference                  |
                  | {"educationOrganizationId":255901}       | {"studentUniqueId":"604834"}      |
             When a PUT request is made to referenced resource "ed-fi/studentEducationOrganizationAssociations/{id}" with
                  """
                  {
                      "id": "{id}",
                      "educationOrganizationReference": {
                          "educationOrganizationId": 255901
                      }
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
                            "$.studentReference": [
                                "studentReference is required."
                            ]
                        },
                      "errors": []
                  }
                  """
                          
        Scenario: 03 Ensure clients cannot update a resource using a wrong reference
            Given the system has these "studentEducationOrganizationAssociations" references
                  | educationOrganizationReference           | studentReference                  |
                  | {"educationOrganizationId":255901}       | {"studentUniqueId":"604834"}      |
             When a PUT request is made to referenced resource "ed-fi/studentEducationOrganizationAssociations/{id}" with
                  """
                  {
                      "id": "{id}",
                      "educationOrganizationReference": {
                          "educationOrganizationId": 25590111
                      },
                      "studentReference": {
                        "studentUniqueId":"604834"
                      }
                  }
                  """
             Then it should respond with 409
              And the response body is
                  """
                  {
                      "detail": "The referenced EducationOrganization item(s) do not exist.",
                      "type": "urn:ed-fi:api:data-conflict:unresolved-reference",
                      "title": "Unresolved Reference",
                      "status": 409,
                      "correlationId": null,
                      "validationErrors": null,
                      "errors": null
                  }
                  """

        
        Scenario: 04 Ensure clients cannot update a resource that uses a reference that not exist
            Given the system has these "studentCTEProgramAssociations" references
                  | beginDate  | educationOrganizationReference     | programReference                                                                                                                                                                     |  studentReference               |
                  | 2020-06-05 | {"educationOrganizationId":255901} | {"educationOrganizationId":255901, "programName":"Career and Technical Education", "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Career and Technical Education"}  | {"studentUniqueId":"604834"}    |
             When a PUT request is made to referenced resource "ed-fi/studentCTEProgramAssociations/{id}" with
                  """
                  {
                      "id": "{id}",
                      "beginDate": "2020-06-05",
                      "educationOrganizationReference":{
		                    "educationOrganizationId":255901
                      },
                      "programReference": {
                        "educationOrganizationId":255901,
                        "programName": "Program Error",
                        "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Program Error"
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
                      "detail": "The referenced Program,Student item(s) do not exist.",
                      "type": "urn:ed-fi:api:data-conflict:unresolved-reference",
                      "title": "Unresolved Reference",
                      "status": 409,
                      "correlationId": null,
                      "validationErrors": null,
                      "errors": null
                  }
                  """
                        

        
        Scenario: 05 Ensure clients cannot update a resource that uses an invalid school year reference
            Given the system has these "graduationPlans" references
                  | graduationPlanTypeDescriptor   | educationOrganizationReference      | graduationSchoolYearTypeReference | totalRequiredCredits |
                  | Career and Technical Education | {"educationOrganizationId":255901}  | {"schoolYear":2022}               | 10.000               |
             When a PUT request is made to referenced resource "ed-fi/graduationPlans/{id}" with
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
                      "detail": "The referenced SchoolYearType item(s) do not exist.",
                      "type": "urn:ed-fi:api:data-conflict:unresolved-reference",
                      "title": "Unresolved Reference",
                      "status": 409,
                      "correlationId": null,
                      "validationErrors": null,
                      "errors": null
                  }
                  """
        # There is a problem when trying to save a section It appears that the reference to CourseOffering is not being assembled properly.
        @ignore
        Scenario: 06 Ensure clients cannot update a resource that is incorrect from a deep reference
            Given the system has these "courses"
                  | courseCode | identificationCodes                                                                                                                                  | educationOrganizationReference        | courseTitle | numberOfParts |
                  | ALG-1      | [{"identificationCode": "ALG-1", "courseIdentificationSystemDescriptor":"uri://ed-fi.org/CourseIdentificationSystemDescriptor#State course code"}]   | {"educationOrganizationId":255901}    | Algebra I   | 1             |
              And the system has these "sessions"
                  | sessionName               | schoolReference     | schoolYearTypeReference | beginDate  | endDate    | totalInstructionalDays | termDescriptor                                 |
                  | "2021-2022 Fall Semester" | {"schoolId":255901} | {"schoolYear":2022}     | 2021-08-23 | 2021-12-17 | 81                     | "uri://ed-fi.org/TermDescriptor#Fall Semester" |
              And the system has these "courseOfferings"
                  | localCourseCode | courseReference                                          | schoolReference     | sessionReference                                                                  |
                  | ALG-1           | {"courseCode":"ALG-1", "educationOrganizationId":255901} | {"schoolId":255901} | {"schoolId":255901, "schoolYear": 2022, "sessionName":"2021-2022 Fall Semester" } |
              And the system has these "sections"
                  | sectionIdentifier               | courseOfferingReference                                                                                        |
                  | 25590100102Trad220ALG112011     | {"localCourseCode":"ALG-1", "schoolId":255901, "schoolYear":2022, "sessionName":"2021-2022 Fall Semester"}     |
              And the system has these "studentSectionAssociations" references
                  | beginDate  | sectionReference                                                                                                                                                  | studentReference               |
                  | 2021-08-23 | {"localCourseCode":"ALG-1", "schoolId":255901, "schoolYear": 2022, "sectionIdentifier":"25590100102Trad220ALG112011", "sessionName":"2021-2022 Fall Semester"  }  | {"studentUniqueId":"604834"}   |
             When a PUT request is made to referenced resource "ed-fi/studentSectionAssociations/{id}" with
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
