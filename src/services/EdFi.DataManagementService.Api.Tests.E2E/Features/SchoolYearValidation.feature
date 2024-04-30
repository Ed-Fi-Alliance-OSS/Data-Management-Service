@SchoolYear @Ignore
Feature: School Year Reference Validation
  Reference validation on School Years

        Background:
                  """
                  @StudentEducationOrganizationAssociation Hook that will create the following data for the required tests
                  createStudent(studentUniqueId)
                  createSchool(schoolId)
                  createCourse(courseCode, schoolId)
                  createSession(schoolId, schoolYear, sessionName)
                  createCourseOffering(localCourseCode, courseCode, schoolId, schoolYear, sessionName)
                  createSection(localCourseCode, schoolId, schoolYear, sessionName, sectionIdentifier)
                  """
            Given the following Student should exist
                  | studentUniqueId | firstName  | lastName | birthDate  |
                  | 604822          | Automation | Student  | 2010-01-01 |
              And School with this information should exist
                  | schoolId  | nameInstitution      | educationOrganizationCategories                               | schoolCategories                                    | gradeLevels                                      |
                  | 255901001 | New School 255901001 | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Other | uri://ed-fi.org/SchoolCategoryDescriptor#All Levels | uri://ed-fi.org/GradeLevelDescriptor#First Grade |
              And Course with this information should exist
                  | courseCode | codeValue        | shortDescription | description     | namespace                                             |
                  | 03100500   | 'LEA course code | LEA course code  | LEA course code | 'uri://ed-fi.org/CourseIdentificationSystemDescriptor |
              And Session with this information should exist
                  | sessionName             | schoolId  | schoolYear | termDescription                                |
                  | 2021-2022 Fall Semester | 255901001 | 2024       | uri://ed-fi.org/termDescriptor#Spring Semester |
              And CourseOffering information should exist
                  | localCourseCode | courseCode | schoolId  | schoolYear | sessionName               |
                  | ART-1           | 03100500   | 255901001 | 2024       | 2023-2024 Spring Semester |
              And Section with this information should exist
                  | sectionIdentifier | localCourseCode | schoolId  | schoolYear | sessionName             |
                  | L0CA1             | ART-1           | 255901001 | 2024       | 2021-2022 Fall Semester |
              And Student Section Association should exist
                  | localCourseCode | schoolId  | schoolYear | sessionName             | sectionIdentifier | studentUniqueId |
                  | ART-1           | 255901001 | 2024       | 2021-2022 Fall Semester | L0CA1             | 604822          |

        @Ignore
        Scenario: Try creating a resource using a valid school year
             When sending a POST request to "/ed-fi/studentSectionAssociation" with body
                  """
                      {
                          "beginDate": "2024-04-25",
                          "sectionReference": {
                              "localCourseCode": "ART-1",
                              "schoolId": 255901001,
                              "schoolYear": 2024,
                              "sectionIdentifier": "25590100107Trad322ART112011",
                              "sessionName": "2021-2022 Fall Semester",
                          },
                          "studentReference": {
                              "studentUniqueId": "604822",
                          }
                      }
                  """
             Then the response code is 201

        @Ignore
        Scenario: Try creating a resource using an invalid school year
             When sending a POST request to "/ed-fi/studentSectionAssociation" with body
                  """
                      {
                          "beginDate": "2024-04-25",
                          "sectionReference": {
                              "localCourseCode": "ART-1",
                              "schoolId": 255901001,
                              "schoolYear": 2029,
                              "sectionIdentifier": "25590100107Trad322ART112011",
                              "sessionName": "2021-2022 Fall Semester",
                          },
                          "studentReference": {
                              "studentUniqueId": "604822",
                          }
                      }
                  """
             Then the response code is 409
              And the response body is
                  """
                      {
                          "message": "The value supplied for the related 'section' resource does not exist.",
                      }
                  """

  #Course Offering / School Year
        @Ignore
        Scenario: Post a valid request using an existing CourseOffering
            Given a existing CourseOffering
             When sending a POST request to "/ed-fi/courseOfferings" with body
                  """
                      {
                          "localCourseCode": "ALG-1",
                          "courseReference": {
                              "courseCode": "ALG-1",
                              "educationOrganizationId": 255901001
                          },
                          "schoolReference": {
                              "schoolId": 255901001
                          },
                          "sessionReference": {
                              "schoolId": 255901001,
                              "schoolYear": 2022,
                              "sessionName": "2021-2022 Spring Semester"
                          }
                      }
                  """
             Then the response code is 201

        @Ignore
        Scenario: Post a valid request using a non existing CourseOffering and API will handle this correctly
             When sending a POST request to "/ed-fi/courseOfferings" with body
                  """
                      {
                      "localCourseCode": "ALG-1",
                      "courseReference": {
                          "courseCode": "ALG-1",
                          "educationOrganizationId": 255901001
                      },
                      "schoolReference": {
                          "schoolId": 255901001
                      },
                      "sessionReference": {
                          "schoolId": 255901001,
                          "schoolYear": 2022,
                          "sessionName": "2021-2022 Spring Semester"
                      }
                      }
                  """
             Then the response code is 409
              And the response body is
                  """
                      {
                          "message": "The value supplied for the related 'section' resource does not exist.",
                      }
                  """

                  """
                  The cohort year scenario is interesting because it is testing a situation where a resource has a collection of SchoolYear references.
                  The StudentEducationOrganizationAssociation has this collection, via CohortYears.
                  """
        @Ignore
        Scenario: Handling the array with two valid cohorts
             When sending a POST request to "/ed-fi/studentEducationOrganizationAssociations" with body
                  """
                  {
                      "educationOrganizationReference": {
                          "educationOrganizationId": 255901
                      },
                      "studentReference": {
                          "studentUniqueId": "604824"
                      },
                      "cohortYears": [
                          {
                          "cohortYearTypeDescriptor" : "uri://ed-fi.org/CohortYearTypeDescriptor#First grade",
                          "termDescriptor": "uri://ed-fi.org/termDescriptor#Spring Semester",
                          "schoolYearTypeReference": {
                              "schoolYear": 2022
                          }
                          },
                          {
                          "cohortYearTypeDescriptor" : "uri://ed-fi.org/CohortYearTypeDescriptor#First grade",
                          "termDescriptor": "uri://ed-fi.org/termDescriptor#Spring Semester",
                          "schoolYearTypeReference": {
                              "schoolYear": 2023
                          }
                          }
                      ]
                  }
                  """
             Then the response code is 200

        @Ignore
        Scenario: Handling the array with 2 cohorts (1st valid / 2nd invalid)
             When sending a POST request to "/ed-fi/studentEducationOrganizationAssociations" with body
                  """
                  {
                      "educationOrganizationReference": {
                          "educationOrganizationId": 255901
                      },
                      "studentReference": {
                          "studentUniqueId": "604824"
                      },
                      "cohortYears": [
                          {
                          "cohortYearTypeDescriptor" : "uri://ed-fi.org/CohortYearTypeDescriptor#First grade",
                          "termDescriptor": "uri://ed-fi.org/termDescriptor#Spring Semester",
                          "schoolYearTypeReference": {
                              "schoolYear": 2022
                          }
                          },
                          {
                          "cohortYearTypeDescriptor" : "uri://ed-fi.org/CohortYearTypeDescriptor#First grade",
                          "termDescriptor": "uri://ed-fi.org/termDescriptor#Spring Semester",
                          "schoolYearTypeReference": {
                              "schoolYear": 2099
                          }
                          }
                      ]
                  }
                  """
             Then the response code is 409
              And the response body is
                  """
                  {
                      "message": "The value supplied for the related 'schoolYearType' resource does not exist.",
                  }
                  """

        @Ignore
        Scenario: Handling the array with 2 cohorts (1st invalid / 2nd valid)
             When sending a POST request to "/ed-fi/studentEducationOrganizationAssociations" with body
                  """
                  {
                      "educationOrganizationReference": {
                          "educationOrganizationId": 255901
                      },
                      "studentReference": {
                          "studentUniqueId": "604824"
                      },
                      "cohortYears": [
                          {
                          "cohortYearTypeDescriptor" : "uri://ed-fi.org/CohortYearTypeDescriptor#First grade",
                          "termDescriptor": "uri://ed-fi.org/termDescriptor#Spring Semester",
                          "schoolYearTypeReference": {
                              "schoolYear": 2099
                          }
                          },
                          {
                          "cohortYearTypeDescriptor" : "uri://ed-fi.org/CohortYearTypeDescriptor#First grade",
                          "termDescriptor": "uri://ed-fi.org/termDescriptor#Spring Semester",
                          "schoolYearTypeReference": {
                              "schoolYear": 2022
                          }
                          }
                      ]
                  }
                  """
             Then the response code is 409
              And the response body is
                  """
                  {
                      "message": "The value supplied for the related 'schoolYearType' resource does not exist.",
                  }
                  """

        @Ignore
        Scenario: Handling the array with 2 duplicate cohorts
             When sending a POST request to "/ed-fi/studentEducationOrganizationAssociations" with body
                  """
                  {
                      "educationOrganizationReference": {
                          "educationOrganizationId": 255901
                      },
                      "studentReference": {
                          "studentUniqueId": "604824"
                      },
                      "cohortYears": [
                          {
                          "cohortYearTypeDescriptor" : "uri://ed-fi.org/CohortYearTypeDescriptor#First grade",
                          "termDescriptor": "uri://ed-fi.org/termDescriptor#Spring Semester",
                          "schoolYearTypeReference": {
                              "schoolYear": 2022
                          }
                          },
                          {
                          "cohortYearTypeDescriptor" : "uri://ed-fi.org/CohortYearTypeDescriptor#First grade",
                          "termDescriptor": "uri://ed-fi.org/termDescriptor#Spring Semester",
                          "schoolYearTypeReference": {
                              "schoolYear": 2022
                          }
                          }
                      ]
                  }
                  """
             Then the response code is 400
              And the response body is
                  """
                      {
                      "message": "The request is invalid.",
                      "correlationId": "bf73f2df-d19c-40c1-b6e4-d7ba294f9fd6",
                      "modelState": {
                          "request.StudentEducationOrganizationAssociationCohortYears": [
                          "The 2nd item of the StudentEducationOrganizationAssociationCohortYears collection is a duplicate."
                          ]
                      }
                      }
                  """
