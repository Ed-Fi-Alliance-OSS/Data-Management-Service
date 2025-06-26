Feature: RelationshipsWithEdOrgsAndPeople Authorization

        Background:
            Given the claimSet "EdFiAPIPublisherWriter" is authorized with educationOrganizationIds "1255901001"
              And the system has these "schoolYearTypes"
                  | schoolYear | currentSchoolYear | schoolYearDescription |
                  | 2023       | true              | "year 2023"           |
              And the system has these descriptors
                  | descriptorValue                                                          |
                  | uri://ed-fi.org/CourseAttemptResultDescriptor#Pass                       |
                  | uri://ed-fi.org/TermDescriptor#Semester                                  |
                  | uri://ed-fi.org/CourseIdentificationSystemDescriptor#CSSC course code    |
                  | uri://ed-fi.org/ExitWithdrawTypeDescriptor#Student withdrew              |
                  | uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application |

    Rule: StudentSchoolAssociation CRUD is properly authorized
        Background:
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1255901001"
              And the system has these "schools"
                  | schoolId   | nameOfInstitution   | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 1255901001 | Authorized school   | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
                  | 1255901002 | Unauthorized school | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName            | lastSurname | birthDate  |
                  | "11"            | Authorized student   | student-ln  | 2008-01-01 |
                  | "12"            | Unauthorized student | student-ln  | 2008-01-01 |

        Scenario: 01 Ensure client can create a StudentSchoolAssociation
             When a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                          "schoolId": 1255901001
                      },
                      "studentReference": {
                          "studentUniqueId": "11"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 201

        Scenario: 02 Ensure client can not create a StudentSchoolAssociation belonging to an unauthorized education organization hierarchy
             When a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                          "schoolId": 1255901002
                      },
                      "studentReference": {
                          "studentUniqueId": "12"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 403

        Scenario: 03 Ensure client can retrieve a StudentSchoolAssociation
            Given a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                          "schoolId": 1255901001
                      },
                      "studentReference": {
                          "studentUniqueId": "11"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 201 or 200

             When a GET request is made to "/ed-fi/studentSchoolAssociations/{id}"
             Then it should respond with 200

        Scenario: 04 Ensure client can not retrieve a StudentSchoolAssociation belonging to an unauthorized education organization hierarchy
            Given a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                          "schoolId": 1255901001
                      },
                      "studentReference": {
                          "studentUniqueId": "11"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 201 or 200

            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1255901002"

             When a GET request is made to "/ed-fi/studentSchoolAssociations/{id}"
             Then it should respond with 403

        Scenario: 05 Ensure client can only query authorized StudentSchoolAssociation
            Given a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                          "schoolId": 1255901001
                      },
                      "studentReference": {
                          "studentUniqueId": "11"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
              And the resulting id is stored in the "StudentSchoolAssociationId" variable
             Then it should respond with 201 or 200

            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1255901002"
            Given a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                          "schoolId": 1255901002
                      },
                      "studentReference": {
                          "studentUniqueId": "12"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 201 or 200

            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1255901001"
             When a GET request is made to "/ed-fi/studentSchoolAssociations"
             Then it should respond with 200
              And the response body is
                  """
                  [
                    {
                      "id":"{StudentSchoolAssociationId}",
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade",
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                        "schoolId": 1255901001
                      },
                      "studentReference": {
                        "studentUniqueId": "11"
                      }
                    }
                  ]
                  """

        Scenario: 06 Ensure client can update a StudentSchoolAssociation
            Given a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                          "schoolId": 1255901001
                      },
                      "studentReference": {
                          "studentUniqueId": "11"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 201 or 200

             When a PUT request is made to "/ed-fi/StudentSchoolAssociations/{id}" with
                  """
                  {
                      "id":"{id}",
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                          "schoolId": 1255901001
                      },
                      "studentReference": {
                          "studentUniqueId": "11"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade",
                      "exitWithdrawDate": "2025-01-01"
                  }
                  """
             Then it should respond with 204

        Scenario: 07 Ensure client can not update a StudentSchoolAssociation belonging to an unauthorized education organization hierarchy
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1255901002"
            Given a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                          "schoolId": 1255901002
                      },
                      "studentReference": {
                          "studentUniqueId": "12"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 201 or 200

            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1255901001"
             When a PUT request is made to "/ed-fi/StudentSchoolAssociations/{id}" with
                  """
                  {
                      "id":"{id}",
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                          "schoolId": 1255901002
                      },
                      "studentReference": {
                          "studentUniqueId": "12"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade",
                      "exitWithdrawDate": "2025-01-01"
                  }
                  """
             Then it should respond with 403

        Scenario: 08 Ensure client can not delete a StudentSchoolAssociation belonging to an unauthorized education organization hierarchy
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1255901002"
            Given a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                          "schoolId": 1255901002
                      },
                      "studentReference": {
                          "studentUniqueId": "12"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 201 or 200

            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1255901001"
             When a DELETE request is made to "/ed-fi/StudentSchoolAssociations/{id}"
             Then it should respond with 403

        Scenario: 09 Ensure client can delete a StudentSchoolAssociation
            Given  a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                          "schoolId": 1255901001
                      },
                      "studentReference": {
                          "studentUniqueId": "11"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 201 or 200

             When a DELETE request is made to "/ed-fi/StudentSchoolAssociations/{id}"
             Then it should respond with 204

    Rule: Student CRUD is properly authorized
        Background:
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "2255901001,2255901002"
              And the system has these "schools"
                  | schoolId   | nameOfInstitution   | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 2255901001 | Authorized school   | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
                  | 2255901002 | Unauthorized school | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "students"
                  | _storeResultingIdInVariable | studentUniqueId | firstName            | lastSurname | birthDate  |
                  | StudentId                   | "21"            | Authorized student   | student-ln  | 2008-01-01 |
                  | UnauthorizedStudentId       | "22"            | Unauthorized student | student-ln  | 2008-01-01 |
                  | UnassociatedStudentId       | "23"            | Unassociated student | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | _storeResultingIdInVariable | studentReference            | schoolReference            | entryGradeLevelDescriptor                            | entryDate  | exitGradeLevel                                       | exitWithdrawTypeDescriptor                                    |
                  | StudentSchoolAssociationId  | { "studentUniqueId": "21" } | { "schoolId": 2255901001 } | "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary" | 2023-08-01 | "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary" | "uri://ed-fi.org/ExitWithdrawTypeDescriptor#Student withdrew" |
                  |                             | { "studentUniqueId": "22" } | { "schoolId": 2255901002 } | "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary" | 2023-08-01 | "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary" | "uri://ed-fi.org/ExitWithdrawTypeDescriptor#Student withdrew" |
              And the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "2255901001"

        Scenario: 10 Ensure client can create a Student
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "36515582",
                      "birthDate": "1994-05-24",
                      "firstName": "John",
                      "lastSurname": "Doe"
                  }
                  """
             Then it should respond with 201

        Scenario: 11 Ensure client can retrieve a Student
             When a GET request is made to "/ed-fi/students/{StudentId}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                    "id": "{StudentId}",
                    "birthDate": "2008-01-01",
                    "firstName": "Authorized student",
                    "lastSurname": "student-ln",
                    "studentUniqueId": "21"
                  }
                  """

        Scenario: 12 Ensure client can not retrieve an unassociated Student
             When a GET request is made to "/ed-fi/students/{UnassociatedStudentId}"
             Then it should respond with 403

        Scenario: 13 Ensure client can not retrieve a Student associated to an unauthorized education organization hierarchy
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "2255901002"

            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "2255901001"
             When a GET request is made to "/ed-fi/students/{UnassociatedStudentId}"
             Then it should respond with 403

        Scenario: 14 Ensure client can only query authorized Students
             When a GET request is made to "/ed-fi/students"
             Then it should respond with 200
              And the response body is
                  """
                  [
                      {
                          "id":"{StudentId}",
                          "studentUniqueId": "21",
                          "birthDate": "2008-01-01",
                          "firstName": "Authorized student",
                          "lastSurname": "student-ln"
                      }
                  ]
                  """

        Scenario: 15 Ensure client can update a Student
             When a PUT request is made to "/ed-fi/students/{StudentId}" with
                  """
                  {
                      "id": "{StudentId}",
                      "studentUniqueId": "21",
                      "birthDate": "2008-01-01",
                      "firstName": "Jane",
                      "lastSurname": "Doe"
                  }
                  """
             Then it should respond with 204

        Scenario: 16 Ensure client can not update an unassociated Student
             When a PUT request is made to "/ed-fi/students/{UnassociatedStudentId}" with
                  """
                  {
                      "id": "{UnassociatedStudentId}",
                      "studentUniqueId": "23",
                      "birthDate": "2008-01-01",
                      "firstName": "Mike",
                      "lastSurname": "Doe"
                  }
                  """
             Then it should respond with 403

        Scenario: 17 Ensure client can not update a Student associated to an unauthorized education organization hierarchy
             When a PUT request is made to "/ed-fi/students/{UnauthorizedStudentId}" with
                  """
                  {
                      "id": "{UnauthorizedStudentId}",
                      "studentUniqueId": "23",
                      "birthDate": "2008-01-01",
                      "firstName": "Axel",
                      "lastSurname": "Marquez"
                  }
                  """
             Then it should respond with 403

        Scenario: 18 Ensure client can delete a Student
            Given a DELETE request is made to "/ed-fi/studentSchoolAssociations/{StudentSchoolAssociationId}"

             When a DELETE request is made to "/ed-fi/students/{StudentId}"
             Then it should respond with 204

    Rule: Student-securable CRUD is properly authorized
        Background:
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "3255901001, 3255901002"
              And the system has these "schools"
                  | schoolId   | nameOfInstitution   | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 3255901001 | Authorized school   | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
                  | 3255901002 | Unauthorized school | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName            | lastSurname | birthDate  |
                  | "31"            | Authorized student   | student-ln  | 2008-01-01 |
                  | "32"            | Unauthorized student | student-ln  | 2008-01-01 |
                  | "33"            | Unassociated student | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | studentReference            | schoolReference            | entryGradeLevelDescriptor                            | entryDate  | exitGradeLevel                                       | exitWithdrawTypeDescriptor                                    |
                  | { "studentUniqueId": "31" } | { "schoolId": 3255901001 } | "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary" | 2023-08-01 | "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary" | "uri://ed-fi.org/ExitWithdrawTypeDescriptor#Student withdrew" |
                  | { "studentUniqueId": "32" } | { "schoolId": 3255901002 } | "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary" | 2023-08-01 | "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary" | "uri://ed-fi.org/ExitWithdrawTypeDescriptor#Student withdrew" |
              And the system has these "postSecondaryInstitutions"
                  | postSecondaryInstitutionId | nameOfInstitution | categories                                                                                                                            |
                  | 3255902001                 | Authorized PSI    | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution"} ] |
              And the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "3255901001, 3255902001"

        Scenario: 19 Ensure client can create a Student-securable
             When a POST request is made to "/ed-fi/PostSecondaryEvents" with
                  """
                  {
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "31"
                      }
                  }
                  """
             Then it should respond with 201

        Scenario: 20 Ensure client can not create a Student-securable if the Student is unassociated
             When a POST request is made to "/ed-fi/PostSecondaryEvents" with
                  """
                  {
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "33"
                      }
                  }
                  """
             Then it should respond with 403

        Scenario: 21 Ensure client can not create a Student-securable if the Student is associated to an unauthorized education organization hierarchy
             When a POST request is made to "/ed-fi/PostSecondaryEvents" with
                  """
                  {
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "32"
                      }
                  }
                  """
             Then it should respond with 403

        Scenario: 22 Ensure client can retrieve a Student-securable
            Given a POST request is made to "/ed-fi/PostSecondaryEvents" with
                  """
                  {
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "31"
                      }
                  }
                  """
             Then it should respond with 201 or 200

             When a GET request is made to "/ed-fi/PostSecondaryEvents/{id}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                      "id": "{id}",
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "31"
                      }
                  }
                  """

        Scenario: 23 Ensure client can not retrieve a Student-securable if the Student is unassociated

            Given a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                          "schoolId": 3255901001
                      },
                      "studentReference": {
                          "studentUniqueId": "33"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
              And the resulting id is stored in the "StudentSchoolAssociationId" variable
             Then it should respond with 201 or 200

            Given a POST request is made to "/ed-fi/PostSecondaryEvents" with
                  """
                  {
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "33"
                      }
                  }
                  """
             Then it should respond with 201 or 200

            Given a DELETE request is made to "/ed-fi/studentSchoolAssociations/{StudentSchoolAssociationId}"
             Then it should respond with 204

             When a GET request is made to "/ed-fi/PostSecondaryEvents/{id}"
             Then it should respond with 403

        Scenario: 24 Ensure client can not retrieve a Student-securable if the Student is associated to an unauthorized education organization hierarchy
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "3255901002"
            Given a POST request is made to "/ed-fi/PostSecondaryEvents" with
                  """
                  {
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "32"
                      }
                  }
                  """
             Then it should respond with 201 or 200

            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "3255901001"
             When a GET request is made to "/ed-fi/PostSecondaryEvents/{id}"
             Then it should respond with 403

        Scenario: 25 Ensure client can only query authorized Student-securables
            # Set up a Student-securable for an unassociated Student
            Given a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                          "schoolId": 3255901001
                      },
                      "studentReference": {
                          "studentUniqueId": "33"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
              And the resulting id is stored in the "StudentSchoolAssociationId" variable
             Then it should respond with 201 or 200

            Given a POST request is made to "/ed-fi/PostSecondaryEvents" with
                  """
                  {
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "33"
                      }
                  }
                  """
             Then it should respond with 201 or 200

            Given a DELETE request is made to "/ed-fi/studentSchoolAssociations/{StudentSchoolAssociationId}"
             Then it should respond with 204

            # Set up a Student-securable in an unauthorized education organization hierarchy
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "3255901002"
            Given a POST request is made to "/ed-fi/PostSecondaryEvents" with
                  """
                  {
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "32"
                      }
                  }
                  """
             Then it should respond with 201 or 200

            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "3255901001"

            # Set up a Student-securable
            Given a POST request is made to "/ed-fi/PostSecondaryEvents" with
                  """
                  {
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "31"
                      }
                  }
                  """
              And the resulting id is stored in the "PostSecondaryEventId" variable
             Then it should respond with 201 or 200

            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "3255901001"
             When a GET request is made to "/ed-fi/PostSecondaryEvents"
             Then it should respond with 200
              And the response body is
                  """
                  [
                      {
                          "id":"{PostSecondaryEventId}",
                          "eventDate": "2023-09-15",
                          "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                          "studentReference": {
                              "studentUniqueId": "31"
                          }
                      }
                  ]
                  """

        Scenario: 26 Ensure client can update a Student-securable
            Given a POST request is made to "/ed-fi/PostSecondaryEvents" with
                  """
                  {
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "31"
                      }
                  }
                  """
             Then it should respond with 201 or 200

             When a PUT request is made to "/ed-fi/PostSecondaryEvents/{id}" with
                  """
                  {
                      "id":"{id}",
                      "postSecondaryInstitutionId": 3255902001,
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "31"
                      }
                  }
                  """
             Then it should respond with 204

        Scenario: 27 Ensure client can not update a Student-securable if the Student is unassociated
            Given a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                          "schoolId": 3255901001
                      },
                      "studentReference": {
                          "studentUniqueId": "33"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
              And the resulting id is stored in the "StudentSchoolAssociationId" variable
             Then it should respond with 201 or 200

            Given a POST request is made to "/ed-fi/PostSecondaryEvents" with
                  """
                  {
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "33"
                      }
                  }
                  """
             Then it should respond with 201 or 200

            Given a DELETE request is made to "/ed-fi/studentSchoolAssociations/{StudentSchoolAssociationId}"
             Then it should respond with 204

             When a PUT request is made to "/ed-fi/PostSecondaryEvents/{id}" with
                  """
                  {
                      "id":"{id}",
                      "postSecondaryInstitutionId": 3255902001,
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "33"
                      }
                  }
                  """
             Then it should respond with 403

        Scenario: 28 Ensure client can not update a Student-securable if the Student is associated to an unauthorized education organization hierarchy
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "3255901002"
            Given a POST request is made to "/ed-fi/PostSecondaryEvents" with
                  """
                  {
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "32"
                      }
                  }
                  """
             Then it should respond with 201 or 200

            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "3255901001"
             When a PUT request is made to "/ed-fi/PostSecondaryEvents/{id}" with
                  """
                  {
                      "id":"{id}",
                      "postSecondaryInstitutionId": 3255902001,
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "32"
                      }
                  }
                  """
             Then it should respond with 403

        Scenario: 29 Ensure client can delete a Student-securable
            Given a POST request is made to "/ed-fi/PostSecondaryEvents" with
                  """
                  {
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "31"
                      }
                  }
                  """
             Then it should respond with 201 or 200

             When a DELETE request is made to "/ed-fi/PostSecondaryEvents/{id}"
             Then it should respond with 204

        Scenario: 30 Ensure client can not delete a Student-securable if the Student is unassociated
            Given a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                          "schoolId": 3255901001
                      },
                      "studentReference": {
                          "studentUniqueId": "33"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
              And the resulting id is stored in the "StudentSchoolAssociationId" variable
             Then it should respond with 201 or 200

            Given a POST request is made to "/ed-fi/PostSecondaryEvents" with
                  """
                  {
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "33"
                      }
                  }
                  """
             Then it should respond with 201 or 200

            Given a DELETE request is made to "/ed-fi/studentSchoolAssociations/{StudentSchoolAssociationId}"
             Then it should respond with 204

             When a DELETE request is made to "/ed-fi/PostSecondaryEvents/{id}"
             Then it should respond with 403

        Scenario: 31 Ensure client can not delete a Student-securable if the Student is associated to an unauthorized education organization hierarchy
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "3255901002"
            Given a POST request is made to "/ed-fi/PostSecondaryEvents" with
                  """
                  {
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "32"
                      }
                  }
                  """
             Then it should respond with 201 or 200

            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "3255901001"
             When a DELETE request is made to "/ed-fi/PostSecondaryEvents/{id}"
             Then it should respond with 403

    Rule: StudentSchoolAssociation mutations cascade down
        Background:
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "4255901001, 4255901002"
              And the system has these "schools"
                  | schoolId   | nameOfInstitution   | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 4255901001 | Authorized school   | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
                  | 4255901002 | Unauthorized school | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "students"
                  | _storeResultingIdInVariable | studentUniqueId | firstName            | lastSurname | birthDate  |
                  | StudentId                   | "41"            | Authorized student   | student-ln  | 2008-01-01 |
                  |                             | "42"            | Unauthorized student | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | _storeResultingIdInVariable | studentReference            | schoolReference            | entryGradeLevelDescriptor                            | entryDate  | exitGradeLevel                                       | exitWithdrawTypeDescriptor                                    |
                  | StudentSchoolAssociationId  | { "studentUniqueId": "41" } | { "schoolId": 4255901001 } | "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary" | 2023-08-01 | "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary" | "uri://ed-fi.org/ExitWithdrawTypeDescriptor#Student withdrew" |
              And the system has these "PostSecondaryEvents"
                  | _storeResultingIdInVariable | studentReference            | eventDate  | postSecondaryEventCategoryDescriptor                                       |
                  | PostSecondaryEventId        | { "studentUniqueId": "41" } | 2023-09-15 | "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application" |
              And the system has these "postSecondaryInstitutions"
                  | postSecondaryInstitutionId | nameOfInstitution | categories                                                                                                                            |
                  | 4255902001                 | Authorized PSI    | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution"} ] |
              And the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "4255901001, 4255902001"

        Scenario: 32 Ensure client can not update a StudentSchoolAssociation to an unauthorized education organization hierarchy
             When a PUT request is made to "/ed-fi/studentSchoolAssociations/{StudentSchoolAssociationId}" with
                  """
                  {
                      "id": "{StudentSchoolAssociationId}",
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                          "schoolId": 255901932
                      },
                      "studentReference": {
                          "studentUniqueId": "94111"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 403

        Scenario: 33 Ensure client can no longer CRUD a Student and a Student-securable after the StudentSchoolAssociation's SchoolId changed
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "4255901001, 4255901002"
             When a PUT request is made to "/ed-fi/studentSchoolAssociations/{StudentSchoolAssociationId}" with
                  """
                  {
                      "id": "{StudentSchoolAssociationId}",
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                          "schoolId": 4255901002
                      },
                      "studentReference": {
                          "studentUniqueId": "41"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 204

            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "4255901001"
             When a GET request is made to "/ed-fi/students/{StudentId}"
             Then it should respond with 403

             When a GET request is made to "/ed-fi/students?studentUniqueId=41"
             Then it should respond with 200
              And the response body is
                  """
                  []
                  """

             When a PUT request is made to "/ed-fi/students/{StudentId}" with
                  """
                  {
                      "id": "{StudentId}",
                      "studentUniqueId": "41",
                      "birthDate": "2008-01-01",
                      "firstName": "Authorized student",
                      "lastSurname": "student-ln"
                  }
                  """
             Then it should respond with 403

             When a POST request is made to "/ed-fi/postSecondaryEvents" with
                  """
                  {
                      "eventDate": "2025-01-01",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "41"
                      }
                  }
                  """
             Then it should respond with 403

             When a GET request is made to "/ed-fi/postSecondaryEvents/{PostSecondaryEventId}"
             Then it should respond with 403

             When a GET request is made to "/ed-fi/PostSecondaryEvents?eventDate=2023-09-15&postSecondaryEventCategoryDescriptor=uri%3A%2F%2Fed-fi.org%2FPostSecondaryEventCategoryDescriptor%23College%20Application&studentUniqueId=41"
             Then it should respond with 200
              And the response body is
                  """
                  []
                  """

             When a PUT request is made to "/ed-fi/postSecondaryEvents/{PostSecondaryEventId}" with
                  """
                  {
                      "id":"{PostSecondaryEventId}",
                      "postSecondaryInstitutionId": 4255902001,
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "41"
                      }
                  }
                  """
             Then it should respond with 403

             When a DELETE request is made to "/ed-fi/postSecondaryEvents/{PostSecondaryEventId}"
             Then it should respond with 403

            # Teardown
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "4255901001, 4255901002"
             When a DELETE request is made to "/ed-fi/studentSchoolAssociations/{StudentSchoolAssociationId}"
             Then it should respond with 204

        Scenario: 34 Ensure client can no longer CRUD a Student and a Student-securable after the StudentSchoolAssociation is deleted
             When a DELETE request is made to "/ed-fi/studentSchoolAssociations/{StudentSchoolAssociationId}"
             Then it should respond with 204

             When a GET request is made to "/ed-fi/students/{StudentId}"
             Then it should respond with 403

             When a GET request is made to "/ed-fi/students?studentUniqueId=41"
             Then it should respond with 200
              And the response body is
                  """
                  []
                  """

             When a PUT request is made to "/ed-fi/students/{StudentId}" with
                  """
                  {
                      "id": "{StudentId}",
                      "studentUniqueId": "41",
                      "birthDate": "2008-01-01",
                      "firstName": "Authorized student",
                      "lastSurname": "student-ln"
                  }
                  """
             Then it should respond with 403

             When a POST request is made to "/ed-fi/postSecondaryEvents" with
                  """
                  {
                      "eventDate": "2025-01-01",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "41"
                      }
                  }
                  """
             Then it should respond with 403

             When a GET request is made to "/ed-fi/postSecondaryEvents/{PostSecondaryEventId}"
             Then it should respond with 403

             When a GET request is made to "/ed-fi/PostSecondaryEvents?eventDate=2023-09-15&postSecondaryEventCategoryDescriptor=uri%3A%2F%2Fed-fi.org%2FPostSecondaryEventCategoryDescriptor%23College%20Application&studentUniqueId=41"
             Then it should respond with 200
              And the response body is
                  """
                  []
                  """

             When a PUT request is made to "/ed-fi/postSecondaryEvents/{PostSecondaryEventId}" with
                  """
                  {
                      "id":"{PostSecondaryEventId}",
                      "postSecondaryInstitutionId": 4255902001,
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "41"
                      }
                  }
                  """
             Then it should respond with 403

             When a DELETE request is made to "/ed-fi/postSecondaryEvents/{PostSecondaryEventId}"
             Then it should respond with 403

        Scenario: 35 Ensure client can CRUD a Student and a Student-securable after the StudentSchoolAssociation is re-created
             When a DELETE request is made to "/ed-fi/studentSchoolAssociations/{StudentSchoolAssociationId}"
             Then it should respond with 204

             When a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                          "schoolId": 4255901001
                      },
                      "studentReference": {
                          "studentUniqueId": "41"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 201

             When a GET request is made to "/ed-fi/students/{StudentId}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/students?studentUniqueId=41"
             Then it should respond with 200
              And the response body is
                  """
                  [
                      {
                          "id": "{StudentId}",
                          "studentUniqueId": "41",
                          "birthDate": "2008-01-01",
                          "firstName": "Authorized student",
                          "lastSurname": "student-ln"
                      }
                  ]
                  """

             When a PUT request is made to "/ed-fi/students/{StudentId}" with
                  """
                  {
                      "id": "{StudentId}",
                      "studentUniqueId": "41",
                      "birthDate": "2008-01-01",
                      "firstName": "Authorized student",
                      "lastSurname": "student-ln"
                  }
                  """
             Then it should respond with 204

             When a POST request is made to "/ed-fi/postSecondaryEvents" with
                  """
                  {
                      "eventDate": "2025-01-01",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "41"
                      }
                  }
                  """
             Then it should respond with 201

             When a GET request is made to "/ed-fi/postSecondaryEvents/{PostSecondaryEventId}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/PostSecondaryEvents?eventDate=2023-09-15&postSecondaryEventCategoryDescriptor=uri%3A%2F%2Fed-fi.org%2FPostSecondaryEventCategoryDescriptor%23College%20Application&studentUniqueId=41"
             Then it should respond with 200
              And the response body is
                  """
                  [
                      {
                          "id":"{PostSecondaryEventId}",
                          "eventDate": "2023-09-15",
                          "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                          "studentReference": {
                              "studentUniqueId": "41"
                          }
                      }
                  ]
                  """

             When a PUT request is made to "/ed-fi/postSecondaryEvents/{PostSecondaryEventId}" with
                  """
                  {
                      "id":"{PostSecondaryEventId}",
                      "postSecondaryInstitutionId": 4255902001,
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "41"
                      }
                  }
                  """
             Then it should respond with 204

             When a DELETE request is made to "/ed-fi/postSecondaryEvents/{PostSecondaryEventId}"
             Then it should respond with 204

    Rule: A resource that is securable on both Student and Education Organization is properly authorized
        Background:
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "5255901001"
              And the system has these "schools"
                  | schoolId   | nameOfInstitution | gradeLevels                                                                        | educationOrganizationCategories                                                                                   |
                  | 5255901001 | ACC-test          | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName  | lastSurname | birthDate  |
                  | "51"            | student-fn | student-ln  | 2008-01-01 |
                  | "52"            | student-fn | student-ln  | 2008-01-01 |
              And the system has these "courses"
                  | courseCode       | identificationCodes                                                                                                                                   | educationOrganizationReference           | courseTitle        | numberOfParts |
                  | ACC-test-Course1 | [ {"courseIdentificationSystemDescriptor": "uri://ed-fi.org/CourseIdentificationSystemDescriptor#CSSC course code", "identificationCode": "Id-123"} ] | {"educationOrganizationId": 5255901001 } | "ACC-test-Course1" | 8             |
                  | ACC-test-Course2 | [ {"courseIdentificationSystemDescriptor": "uri://ed-fi.org/CourseIdentificationSystemDescriptor#CSSC course code", "identificationCode": "Id-123"} ] | {"educationOrganizationId": 5255901001 } | "ACC-test-Course2" | 8             |
                  | ACC-test-Course3 | [ {"courseIdentificationSystemDescriptor": "uri://ed-fi.org/CourseIdentificationSystemDescriptor#CSSC course code", "identificationCode": "Id-123"} ] | {"educationOrganizationId": 5255901001 } | "ACC-test-Course3" | 8             |
            Given the system has these "studentschoolassociations"
                  | studentReference            | schoolReference            | entryGradeLevelDescriptor                            | entryDate  | exitGradeLevel                                       | exitWithdrawTypeDescriptor                                    |
                  | { "studentUniqueId": "51" } | { "schoolId": 5255901001 } | "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary" | 2023-08-01 | "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary" | "uri://ed-fi.org/ExitWithdrawTypeDescriptor#Student withdrew" |
              And the system has these "studentacademicrecords"
                  | studentReference            | educationOrganizationReference            | schoolYearTypeReference | termDescriptor                            |
                  | { "studentUniqueId": "51" } | { "educationOrganizationId": 5255901001 } | {"schoolYear": 2023}    | "uri://ed-fi.org/TermDescriptor#Semester" |

        Scenario: 36 Ensure client can create a courseTranscripts with edorg id:5255901001 and student id:51
             When a POST request is made to "/ed-fi/courseTranscripts" with
                  """
                  {
                      "courseAttemptResultDescriptor": "uri://ed-fi.org/CourseAttemptResultDescriptor#Pass",
                      "courseReference": {
                        "courseCode": "ACC-test-Course1",
                        "educationOrganizationId": 5255901001
                      },
                      "studentAcademicRecordReference": {
                        "educationOrganizationId": 5255901001,
                        "schoolYear": 2023,
                        "studentUniqueId": "51",
                        "termDescriptor": "uri://ed-fi.org/TermDescriptor#Semester"
                      }
                    }
                  """
             Then it should respond with 201 or 200
             When a GET request is made to "/ed-fi/courseTranscripts/{id}"
             Then it should respond with 200
              And the response body is
                  """
                      {
                      "id":"{id}",
                      "courseAttemptResultDescriptor": "uri://ed-fi.org/CourseAttemptResultDescriptor#Pass",
                      "courseReference": {
                        "courseCode": "ACC-test-Course1",
                        "educationOrganizationId": 5255901001
                      },
                      "studentAcademicRecordReference": {
                        "educationOrganizationId": 5255901001,
                        "schoolYear": 2023,
                        "studentUniqueId": "51",
                        "termDescriptor": "uri://ed-fi.org/TermDescriptor#Semester"
                      }
                    }
                  """

        Scenario: 37 Ensure client can update a courseTranscripts with edorg id:5255901001 and student id:51
             When a POST request is made to "/ed-fi/courseTranscripts" with
                  """
                  {
                      "courseAttemptResultDescriptor": "uri://ed-fi.org/CourseAttemptResultDescriptor#Pass",
                      "courseReference": {
                        "courseCode": "ACC-test-Course2",
                        "educationOrganizationId": 5255901001
                      },
                      "studentAcademicRecordReference": {
                        "educationOrganizationId": 5255901001,
                        "schoolYear": 2023,
                        "studentUniqueId": "51",
                        "termDescriptor": "uri://ed-fi.org/TermDescriptor#Semester"
                      }
                    }
                  """
             Then it should respond with 201 or 200
             When a PUT request is made to "/ed-fi/courseTranscripts/{id}" with
                  """
                  {
                      "id":"{id}",
                      "courseAttemptResultDescriptor": "uri://ed-fi.org/CourseAttemptResultDescriptor#Pass",
                      "courseReference": {
                        "courseCode": "ACC-test-Course2",
                        "educationOrganizationId": 5255901001
                      },
                      "courseTitle":"ACC-test-Course2-title",
                      "studentAcademicRecordReference": {
                        "educationOrganizationId": 5255901001,
                        "schoolYear": 2023,
                        "studentUniqueId": "51",
                        "termDescriptor": "uri://ed-fi.org/TermDescriptor#Semester"
                      }
                    }
                  """
             Then it should respond with 204
             When a GET request is made to "/ed-fi/courseTranscripts/{id}"
             Then it should respond with 200
              And the response body is
                  """
                      {
                      "id":"{id}",
                      "courseAttemptResultDescriptor": "uri://ed-fi.org/CourseAttemptResultDescriptor#Pass",
                      "courseReference": {
                        "courseCode": "ACC-test-Course2",
                        "educationOrganizationId": 5255901001
                      },
                      "courseTitle":"ACC-test-Course2-title",
                      "studentAcademicRecordReference": {
                        "educationOrganizationId": 5255901001,
                        "schoolYear": 2023,
                        "studentUniqueId": "51",
                        "termDescriptor": "uri://ed-fi.org/TermDescriptor#Semester"
                      }
                    }
                  """

        Scenario: 38 Ensure client can delete a courseTranscripts with edorg id:5255901001 and student id:51
             When a POST request is made to "/ed-fi/courseTranscripts" with
                  """
                  {
                      "courseAttemptResultDescriptor": "uri://ed-fi.org/CourseAttemptResultDescriptor#Pass",
                      "courseReference": {
                        "courseCode": "ACC-test-Course3",
                        "educationOrganizationId": 5255901001
                      },
                      "studentAcademicRecordReference": {
                        "educationOrganizationId": 5255901001,
                        "schoolYear": 2023,
                        "studentUniqueId": "51",
                        "termDescriptor": "uri://ed-fi.org/TermDescriptor#Semester"
                      }
                    }
                  """
             Then it should respond with 201 or 200
             When a GET request is made to "/ed-fi/courseTranscripts/{id}"
             Then it should respond with 200
              And the response body is
                  """
                    {
                       "id": "{id}",
                       "courseReference": {
                         "courseCode": "ACC-test-Course3",
                         "educationOrganizationId": 5255901001
                       },
                       "courseAttemptResultDescriptor": "uri://ed-fi.org/CourseAttemptResultDescriptor#Pass",
                       "studentAcademicRecordReference": {
                         "schoolYear": 2023,
                         "termDescriptor": "uri://ed-fi.org/TermDescriptor#Semester",
                         "studentUniqueId": "51",
                         "educationOrganizationId": 5255901001
                       }
                     }
                  """
             When a DELETE request is made to "/ed-fi/courseTranscripts/{id}"
             Then it should respond with 204

        Scenario: 39 Ensure client can not create a courseTranscripts with out student school association
             When a POST request is made to "/ed-fi/courseTranscripts" with
                  """
                  {
                      "courseAttemptResultDescriptor": "uri://ed-fi.org/CourseAttemptResultDescriptor#Pass",
                      "courseReference": {
                        "courseCode": "ACC-test-Course1",
                        "educationOrganizationId": 5255901001
                      },
                      "studentAcademicRecordReference": {
                        "educationOrganizationId": 5255901001,
                        "schoolYear": 2023,
                        "studentUniqueId": "52",
                        "termDescriptor": "uri://ed-fi.org/TermDescriptor#Semester"
                      }
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
                    "No relationships have been established between the caller's education organization id claims ('5255901001') and the resource item's StudentUniqueId value."
                  ]
                  }
                  """

        Scenario: 40 Ensure client can not update a courseTranscripts with out student school association
             When a POST request is made to "/ed-fi/courseTranscripts" with
                  """
                  {
                      "courseAttemptResultDescriptor": "uri://ed-fi.org/CourseAttemptResultDescriptor#Pass",
                      "courseReference": {
                        "courseCode": "ACC-test-Course1",
                        "educationOrganizationId": 5255901001
                      },
                      "studentAcademicRecordReference": {
                        "educationOrganizationId": 5255901001,
                        "schoolYear": 2023,
                        "studentUniqueId": "51",
                        "termDescriptor": "uri://ed-fi.org/TermDescriptor#Semester"
                      }
                    }
                  """
             Then it should respond with 201 or 200
             When a PUT request is made to "/ed-fi/courseTranscripts/{id}" with
                  """
                  {
                      "id":"{id}",
                      "courseAttemptResultDescriptor": "uri://ed-fi.org/CourseAttemptResultDescriptor#Pass",
                       "courseReference": {
                       "courseCode": "ACC-test-Course1",
                       "educationOrganizationId": 5255901001
                     },
                    "studentAcademicRecordReference": {
                      "educationOrganizationId": 5255901001,
                      "schoolYear": 2023,
                      "studentUniqueId": "52",
                      "termDescriptor": "uri://ed-fi.org/TermDescriptor#Semester"
                    }
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
                      "No relationships have been established between the caller's education organization id claims ('5255901001') and the resource item's StudentUniqueId value."
                      ]
                  }
                  """

    Rule: DELETE or GET resource fails with a 403 forbidden error with no student school association
        Background:
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "3, 6255901"
              And the system has these descriptors
                  | descriptorValue                                                  |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#State    |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#District |
                  | uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade                 |
                  | uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC       |
              And the system has these "stateEducationAgencies"
                  | stateEducationAgencyId | nameOfInstitution | categories                                                                                                       |
                  | 3                      | Test state        | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#State" }] |
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | stateEducationAgencyReference   | categories                                                                                                          | localEducationAgencyCategoryDescriptor                       |
                  | 6255901                | Test LEA          | { "stateEducationAgencyId": 3 } | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#District" }] | "uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC" |
              And the system has these "schools"
                  | schoolId   | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                   | localEducationAgencyReference        |
                  | 6255901001 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] | { "localEducationAgencyId": 6255901} |
              And the system has these "students"
                  | studentUniqueId | firstName  | lastSurname | birthDate  |
                  | "61"            | student-fn | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | studentReference            | schoolReference            | entryGradeLevelDescriptor                          | entryDate  | exitGradeLevel                                     | exitWithdrawTypeDescriptor                                    |
                  | { "studentUniqueId": "61" } | { "schoolId": 6255901001 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | "uri://ed-fi.org/ExitWithdrawTypeDescriptor#Student withdrew" |
        @addrelationships
        Scenario: 41 Ensure client can not delete or get a PostSecondaryEvent with out student school association

             When a POST request is made to "/ed-fi/PostSecondaryEvents" with
                  """
                  {
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                        "studentUniqueId": "61"
                      }
                    }
                  """
             Then it should respond with 201 or 200
             When a GET request is made to "/ed-fi/PostSecondaryEvents/{id}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                      "id": "{id}",
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                        "studentUniqueId": "61"
                      }
                    }
                  """
             When a relationship with "studentSchoolAssociations" is deleted
             Then it should respond with 204
             When a GET request is made to "/ed-fi/PostSecondaryEvents/{id}"
             Then it should respond with 403
              And the response body is
                  """
                        {
                         "detail": "Access to the resource could not be authorized. Hint: You may need to create a corresponding 'StudentSchoolAssociation' item.",
                         "type": "urn:ed-fi:api:security:authorization:",
                         "title": "Authorization Denied",
                         "status": 403,
                         "validationErrors": {},
                         "errors": [
                              "No relationships have been established between the caller's education organization id claims ('3', '6255901') and the resource item's StudentUniqueId value."
                          ]
                        }
                  """
             When a DELETE request is made to "/ed-fi/PostSecondaryEvents/{id}"
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
                          "No relationships have been established between the caller's education organization id claims ('3', '6255901') and the resource item's StudentUniqueId value."
                        ]
                      }
                  """

    Rule: Edge cases are properly authorized
        Scenario: 42 Ensure client can CRUD a PostSecondaryEvent using the NoFurtherAuthorizationRequired strategy
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with educationOrganizationIds "7255901001, 7255902001"
              And the system has these "schools"
                  | schoolId   | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 7255901001 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName  | lastSurname | birthDate  |
                  | "71"            | student-fn | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | studentReference            | schoolReference            | entryGradeLevelDescriptor                          | entryDate  |
                  | { "studentUniqueId": "71" } | { "schoolId": 7255901001 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
              And the system has these "postSecondaryInstitutions"
                  | postSecondaryInstitutionId | nameOfInstitution | categories                                                                                                                            |
                  | 7255902001                 | Authorized PSI    | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution"} ] |

             When a POST request is made to "/ed-fi/PostSecondaryEvents" with
                  """
                  {
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "71"
                      }
                  }
                  """
             Then it should respond with 201

             When a GET request is made to "/ed-fi/PostSecondaryEvents/{id}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/PostSecondaryEvents?eventDate=2023-09-15&postSecondaryEventCategoryDescriptor=uri%3A%2F%2Fed-fi.org%2FPostSecondaryEventCategoryDescriptor%23College%20Application&studentUniqueId=71"
             Then it should respond with 200
              And the response body is
                  """
                  [
                    {
                      "id": "{id}",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                        "studentUniqueId": "71"
                      },
                      "eventDate": "2023-09-15"
                    }
                  ]
                  """

             When a PUT request is made to "/ed-fi/PostSecondaryEvents/{id}" with
                  """
                  {
                      "id":"{id}",
                      "postSecondaryInstitutionId": 7255902001,
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "71"
                      }
                  }
                  """
             Then it should respond with 204

             When a DELETE request is made to "/ed-fi/PostSecondaryEvents/{id}"
             Then it should respond with 204

        Scenario: 43 Ensure client without education organization access can CRUD a PostSecondaryEvent using the NoFurtherAuthorizationRequired strategy
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with educationOrganizationIds "8255901001, 8255902001"
              And the system has these "schools"
                  | schoolId   | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 8255901001 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName  | lastSurname | birthDate  |
                  | "81"            | student-fn | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | studentReference            | schoolReference            | entryGradeLevelDescriptor                          | entryDate  |
                  | { "studentUniqueId": "81" } | { "schoolId": 8255901001 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
              And the system has these "postSecondaryInstitutions"
                  | postSecondaryInstitutionId | nameOfInstitution | categories                                                                                                                            |
                  | 8255902001                 | Authorized PSI    | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution"} ] |
              And the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with educationOrganizationIds "1"

             When a POST request is made to "/ed-fi/PostSecondaryEvents" with
                  """
                  {
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "81"
                      }
                  }
                  """
             Then it should respond with 201

             When a GET request is made to "/ed-fi/PostSecondaryEvents/{id}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/PostSecondaryEvents?eventDate=2023-09-15&postSecondaryEventCategoryDescriptor=uri%3A%2F%2Fed-fi.org%2FPostSecondaryEventCategoryDescriptor%23College%20Application&studentUniqueId=81"
             Then it should respond with 200
              And the response body is
                  """
                  [
                    {
                      "id": "{id}",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                        "studentUniqueId": "81"
                      },
                      "eventDate": "2023-09-15"
                    }
                  ]
                  """

             When a PUT request is made to "/ed-fi/PostSecondaryEvents/{id}" with
                  """
                  {
                      "id":"{id}",
                      "postSecondaryInstitutionId": 8255902001,
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "81"
                      }
                  }
                  """
             Then it should respond with 204

             When a DELETE request is made to "/ed-fi/PostSecondaryEvents/{id}"
             Then it should respond with 204

        # Change to use long EdOrgIds when DMS-706 is done
        Scenario: 44 Ensure client with LEA access can CRUD a PostSecondaryEvent
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "955901"
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | categories                                                                                                          | localEducationAgencyCategoryDescriptor                       |
                  | 955901                 | Test LEA          | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#District" }] | "uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC" |
              And the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                   | localEducationAgencyReference       |
                  | 955901001 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] | { "localEducationAgencyId": 955901} |
              And the system has these "students"
                  | studentUniqueId | firstName  | lastSurname | birthDate  |
                  | "91"            | student-fn | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | studentReference            | schoolReference           | entryGradeLevelDescriptor                          | entryDate  |
                  | { "studentUniqueId": "91" } | { "schoolId": 955901001 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
              And the system has these "postSecondaryInstitutions"
                  | postSecondaryInstitutionId | nameOfInstitution | categories                                                                                                                            |
                  | 955902001                  | Authorized PSI    | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution"} ] |

              And a POST request is made to "/ed-fi/PostSecondaryEvents" with
                  """
                  {
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "91"
                      }
                  }
                  """
             Then it should respond with 201

             When a GET request is made to "/ed-fi/PostSecondaryEvents/{id}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/PostSecondaryEvents?eventDate=2023-09-15&postSecondaryEventCategoryDescriptor=uri%3A%2F%2Fed-fi.org%2FPostSecondaryEventCategoryDescriptor%23College%20Application&studentUniqueId=91"
             Then it should respond with 200
              And the response body is
                  """
                  [
                    {
                      "id": "{id}",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                        "studentUniqueId": "91"
                      },
                      "eventDate": "2023-09-15"
                    }
                  ]
                  """

             When a PUT request is made to "/ed-fi/PostSecondaryEvents/{id}" with
                  """
                  {
                      "id":"{id}",
                      "postSecondaryInstitutionId": 955902001,
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "91"
                      }
                  }
                  """
             Then it should respond with 204

             When a DELETE request is made to "/ed-fi/PostSecondaryEvents/{id}"
             Then it should respond with 204

        Scenario: 45 Ensure client can create a PostSecondaryEvent after transfering the Student to a School within the client's LEA
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "10255901001, 10255901002"
              And the resulting token is stored in the "EdFiSandbox_full_access" variable
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | categories                                                                                                          | localEducationAgencyCategoryDescriptor                       |
                  | 10255901               | Test LEA          | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#District" }] | "uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC" |
              And the system has these "schools"
                  | schoolId    | nameOfInstitution  | gradeLevels                                                                      | educationOrganizationCategories                                                                                   | localEducationAgencyReference         |
                  | 10255901001 | Test school        | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] | { "localEducationAgencyId": 10255901} |
                  | 10255901002 | Test school no LEA | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] | null                                  |
              And the system has these "students"
                  | studentUniqueId | firstName  | lastSurname | birthDate  |
                  | "101"           | student-fn | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | _storeResultingIdInVariable | studentReference             | schoolReference             | entryGradeLevelDescriptor                          | entryDate  |
                  | StudentSchoolAssociationId  | { "studentUniqueId": "101" } | { "schoolId": 10255901002 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
              And the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "10255901"
              And the resulting token is stored in the "EdFiSandbox_LEA_only" variable

             When a POST request is made to "/ed-fi/PostSecondaryEvents" with
                  """
                  {
                      "eventDate": "2025-01-01",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "101"
                      }
                  }
                  """
             Then it should respond with 403

            Given the token gets switched to the one in the "EdFiSandbox_full_access" variable
             When a DELETE request is made to "/ed-fi/StudentSchoolAssociations/{StudentSchoolAssociationId}"
             Then it should respond with 204

             When a POST request is made to "/ed-fi/StudentSchoolAssociations" with
                  """
                  {
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                          "schoolId": 10255901001
                      },
                      "studentReference": {
                          "studentUniqueId": "101"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """

            Given the token gets switched to the one in the "EdFiSandbox_LEA_only" variable
             When a POST request is made to "/ed-fi/PostSecondaryEvents" with
                  """
                  {
                      "eventDate": "2025-01-01",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "101"
                      }
                  }
                  """
             Then it should respond with 201 or 200

        Scenario: 46 Ensure securable elements are combined with 'and' and not with 'or'
            # Change to use long EdOrgIds when DMS-706 is done
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1555901001, 1555901002"
              And the system has these "schools"
                  | schoolId   | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 1555901001 | Test school 1     | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
                  | 1555901002 | Test school 2     | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName  | lastSurname | birthDate  |
                  | "151"           | student-fn | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | studentReference             | schoolReference            | entryGradeLevelDescriptor                          | entryDate  |
                  | { "studentUniqueId": "151" } | { "schoolId": 1555901001 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
              And the system has these "StudentAcademicRecords"
                  | _storeResultingIdInVariable | studentReference             | educationOrganizationReference            | termDescriptor                                 | schoolYearTypeReference |
                  | StudentAcademicRecordId     | { "studentUniqueId": "151" } | { "educationOrganizationId": 1555901002 } | "uri://ed-fi.org/TermDescriptor#Fall Semester" | { "schoolYear": 2023 }  |

             When a GET request is made to "/ed-fi/StudentAcademicRecords/{StudentAcademicRecordId}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/StudentAcademicRecords"
             Then it should respond with 200
              And the response body is
                  """
                  [
                    {
                        "id": "{StudentAcademicRecordId}",
                        "educationOrganizationReference": {
                          "educationOrganizationId": 1555901002
                        },
                        "schoolYearTypeReference": {
                          "schoolYear": 2023
                        },
                        "studentReference": {
                          "studentUniqueId": "151"
                        },
                        "termDescriptor": "uri://ed-fi.org/TermDescriptor#Fall Semester"
                      }
                  ]
                  """

            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1555901001"

             When a GET request is made to "/ed-fi/StudentAcademicRecords/{StudentAcademicRecordId}"
             Then it should respond with 403

             When a GET request is made to "/ed-fi/StudentAcademicRecords"
             Then it should respond with 200
              And the response body is
                  """
                  [
                  ]
                  """

            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1555901002"

             When a GET request is made to "/ed-fi/StudentAcademicRecords/{StudentAcademicRecordId}"
             Then it should respond with 403

             When a GET request is made to "/ed-fi/StudentAcademicRecords"
             Then it should respond with 200
              And the response body is
                  """
                  [
                  ]
                  """


    Rule: Multi-school enrollment is properly authorized
        Background:
        # Change to use long EdOrgIds when DMS-706 is done
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1155901001, 1155902001"
              And the resulting token is stored in the "EdFiSandbox_full_access" variable
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | categories                                                                                                          | localEducationAgencyCategoryDescriptor                       |
                  | 1155901                | Test LEA 1        | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#District" }] | "uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC" |
                  | 1155902                | Test LEA 2        | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#District" }] | "uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC" |
              And the system has these "schools"
                  | schoolId   | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                   | localEducationAgencyReference        |
                  | 1155901001 | Test school 1     | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] | { "localEducationAgencyId": 1155901} |
                  | 1155902001 | Test school 2     | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] | { "localEducationAgencyId": 1155902} |
                  | 5          | Test school 5     | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] | null                                 |
              And the system has these "students"
                  | studentUniqueId | firstName  | lastSurname | birthDate  |
                  | "111"           | student-fn | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | _storeResultingIdInVariable  | studentReference             | schoolReference            | entryGradeLevelDescriptor                          | entryDate  |
                  | StudentASchool1AssociationId | { "studentUniqueId": "111" } | { "schoolId": 1155901001 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
                  | StudentASchool2AssociationId | { "studentUniqueId": "111" } | { "schoolId": 1155902001 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |

        Scenario: 46 Ensure client with access to both schools can query multiple student school associations
             When a GET request is made to "/ed-fi/StudentSchoolAssociations?studentUniqueId=111&offset=0&limit=10"
             Then it should respond with 200
              And the response body is
                  """
                  [
                   {
                        "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade",
                        "entryDate": "2023-08-01",
                        "id": "{StudentASchool1AssociationId}",
                        "schoolReference": {
                            "schoolId": 1155901001
                        },
                        "studentReference": {
                            "studentUniqueId": "111"
                        }
                    },
                    {
                        "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade",
                        "entryDate": "2023-08-01",
                        "id": "{StudentASchool2AssociationId}",
                        "schoolReference": {
                            "schoolId": 1155902001
                        },
                        "studentReference": {
                            "studentUniqueId": "111"
                        }
                     }
                  ]
                  """
        Scenario: 47 Ensure client with access to one school can query one student school associations
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1155901"
             When a GET request is made to "/ed-fi/StudentSchoolAssociations?studentUniqueId=111"
             Then it should respond with 200
              And the response body is
                  """
                  [
                    {
                      "id": "{id}",
                      "entryDate": "2023-08-01",
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade",
                      "studentReference": {
                        "studentUniqueId": "111"
                      },
                      "schoolReference": {
                        "schoolId": 1155901001
                      }
                    }
                  ]
                  """
        Scenario: 48 Ensure search still works when the student association is updated and deleted
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1155901001,1155902001,5"
             When a PUT request is made to "/ed-fi/StudentSchoolAssociations/{StudentASchool1AssociationId}" with
                  """
                  {
                      "id":"{StudentASchool1AssociationId}",
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                          "schoolId": 5
                      },
                      "studentReference": {
                          "studentUniqueId": "111"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 204

             When a DELETE request is made to "/ed-fi/StudentSchoolAssociations/{StudentASchool2AssociationId}"
             Then it should respond with 204

             When a GET request is made to "/ed-fi/StudentSchoolAssociations?studentUniqueId=111&offset=0&limit=10"
             Then it should respond with 200
              And the response body is
                  """
                  [
                    {
                        "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade",
                        "entryDate": "2023-08-01",
                        "id": "{id}",
                        "schoolReference": {
                            "schoolId": 5
                        },
                        "studentReference": {
                            "studentUniqueId": "111"
                        }
                    }
                  ]
                  """

    Rule: Multi-school enrollment is properly authorized - Edge cases

        Scenario: 51 Ensure client can retrieve a Student-securable after the SSA has been recreated
            # Change to use long EdOrgIds when DMS-706 is done
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1255901001, 1255901002, 1255901003"
              And the resulting token is stored in the "EdFiSandbox_full_access" variable
              And the system has these "schools"
                  | schoolId   | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 1255901001 | Test school 1     | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
                  | 1255901002 | Test school 2     | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
                  | 1255901003 | Test school 3     | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName  | lastSurname | birthDate  |
                  | "121"           | student-fn | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | _storeResultingIdInVariable | studentReference             | schoolReference            | entryGradeLevelDescriptor                          | entryDate  |
                  |                             | { "studentUniqueId": "121" } | { "schoolId": 1255901001 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
                  | StudentSchoolAssociationId  | { "studentUniqueId": "121" } | { "schoolId": 1255901002 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
              And the system has these "PostSecondaryEvents"
                  | _storeResultingIdInVariable | studentReference             | eventDate  | postSecondaryEventCategoryDescriptor                                       |
                  | PostSecondaryEventId        | { "studentUniqueId": "121" } | 2023-09-15 | "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application" |

            # Assert that token with '1255901001' access can retrieve a Student-securable resource
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1255901001"
             When a GET request is made to "/ed-fi/postSecondaryEvents/{PostSecondaryEventId}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/PostSecondaryEvents?eventDate=2023-09-15&postSecondaryEventCategoryDescriptor=uri%3A%2F%2Fed-fi.org%2FPostSecondaryEventCategoryDescriptor%23College%20Application&studentUniqueId=121"
             Then it should respond with 200
              And the response body is
                  """
                  [
                    {
                      "id": "{PostSecondaryEventId}",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                        "studentUniqueId": "121"
                      },
                      "eventDate": "2023-09-15"
                    }
                  ]
                  """

            # Assert that token with '1255901002' access can retrieve a Student-securable resource
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1255901002"
             When a GET request is made to "/ed-fi/postSecondaryEvents/{PostSecondaryEventId}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/PostSecondaryEvents?eventDate=2023-09-15&postSecondaryEventCategoryDescriptor=uri%3A%2F%2Fed-fi.org%2FPostSecondaryEventCategoryDescriptor%23College%20Application&studentUniqueId=121"
             Then it should respond with 200
              And the response body is
                  """
                  [
                    {
                      "id": "{PostSecondaryEventId}",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                        "studentUniqueId": "121"
                      },
                      "eventDate": "2023-09-15"
                    }
                  ]
                  """

            # Assert that token with '1255901003' access can not retrieve a Student-securable resource
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1255901003"
             When a GET request is made to "/ed-fi/postSecondaryEvents/{PostSecondaryEventId}"
             Then it should respond with 403

             When a GET request is made to "/ed-fi/PostSecondaryEvents?eventDate=2023-09-15&postSecondaryEventCategoryDescriptor=uri%3A%2F%2Fed-fi.org%2FPostSecondaryEventCategoryDescriptor%23College%20Application&studentUniqueId=121"
             Then it should respond with 200
              And the response body is
                  """
                  [
                  ]
                  """

            # Delete SSA for School '1255901002'
            Given the token gets switched to the one in the "EdFiSandbox_full_access" variable
             When a DELETE request is made to "/ed-fi/studentSchoolAssociations/{StudentSchoolAssociationId}"
             Then it should respond with 204

            # Assert that token with '1255901001' access continues to be able to retrieve a Student-securable resource
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1255901001"
             When a GET request is made to "/ed-fi/postSecondaryEvents/{PostSecondaryEventId}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/PostSecondaryEvents?eventDate=2023-09-15&postSecondaryEventCategoryDescriptor=uri%3A%2F%2Fed-fi.org%2FPostSecondaryEventCategoryDescriptor%23College%20Application&studentUniqueId=121"
             Then it should respond with 200
              And the response body is
                  """
                  [
                    {
                      "id": "{PostSecondaryEventId}",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                        "studentUniqueId": "121"
                      },
                      "eventDate": "2023-09-15"
                    }
                  ]
                  """

            # Assert that token with '1255901002' access can no longer retrieve a Student-securable resource
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1255901002"
             When a GET request is made to "/ed-fi/postSecondaryEvents/{PostSecondaryEventId}"
             Then it should respond with 403

             When a GET request is made to "/ed-fi/PostSecondaryEvents?eventDate=2023-09-15&postSecondaryEventCategoryDescriptor=uri%3A%2F%2Fed-fi.org%2FPostSecondaryEventCategoryDescriptor%23College%20Application&studentUniqueId=121"
             Then it should respond with 200
              And the response body is
                  """
                  [
                  ]
                  """

            # Assert that token with '1255901003' access continues to not being able to retrieve a Student-securable resource
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1255901003"
             When a GET request is made to "/ed-fi/postSecondaryEvents/{PostSecondaryEventId}"
             Then it should respond with 403

             When a GET request is made to "/ed-fi/PostSecondaryEvents?eventDate=2023-09-15&postSecondaryEventCategoryDescriptor=uri%3A%2F%2Fed-fi.org%2FPostSecondaryEventCategoryDescriptor%23College%20Application&studentUniqueId=121"
             Then it should respond with 200
              And the response body is
                  """
                  [
                  ]
                  """

            # Recreate SSA for School '1255901002'
            Given the token gets switched to the one in the "EdFiSandbox_full_access" variable

             When a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                          "schoolId": 1255901002
                      },
                      "studentReference": {
                          "studentUniqueId": "121"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 201

            # Assert that token with '1255901001' access continues to be able to retrieve a Student-securable resource
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1255901001"
             When a GET request is made to "/ed-fi/postSecondaryEvents/{PostSecondaryEventId}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/PostSecondaryEvents?eventDate=2023-09-15&postSecondaryEventCategoryDescriptor=uri%3A%2F%2Fed-fi.org%2FPostSecondaryEventCategoryDescriptor%23College%20Application&studentUniqueId=121"
             Then it should respond with 200
              And the response body is
                  """
                  [
                    {
                      "id": "{PostSecondaryEventId}",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                        "studentUniqueId": "121"
                      },
                      "eventDate": "2023-09-15"
                    }
                  ]
                  """

            # Assert that token with '1255901002' access can retrieve a Student-securable resource
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1255901002"
             When a GET request is made to "/ed-fi/postSecondaryEvents/{PostSecondaryEventId}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/PostSecondaryEvents?eventDate=2023-09-15&postSecondaryEventCategoryDescriptor=uri%3A%2F%2Fed-fi.org%2FPostSecondaryEventCategoryDescriptor%23College%20Application&studentUniqueId=121"
             Then it should respond with 200
              And the response body is
                  """
                  [
                    {
                      "id": "{PostSecondaryEventId}",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                        "studentUniqueId": "121"
                      },
                      "eventDate": "2023-09-15"
                    }
                  ]
                  """

            # Assert that token with '1255901003' access continues to not being able to retrieve a Student-securable resource
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1255901003"
             When a GET request is made to "/ed-fi/postSecondaryEvents/{PostSecondaryEventId}"
             Then it should respond with 403

             When a GET request is made to "/ed-fi/PostSecondaryEvents?eventDate=2023-09-15&postSecondaryEventCategoryDescriptor=uri%3A%2F%2Fed-fi.org%2FPostSecondaryEventCategoryDescriptor%23College%20Application&studentUniqueId=121"
             Then it should respond with 200
              And the response body is
                  """
                  [
                  ]
                  """

        Scenario: 52 Ensure client can retrieve a Student-securable after the SSA has been updated to a new School
            # Change to use long EdOrgIds when DMS-706 is done
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1355901001, 1355901002, 1355901003"
              And the resulting token is stored in the "EdFiSandbox_full_access" variable
              And the system has these "schools"
                  | schoolId   | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 1355901001 | Test school 1     | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
                  | 1355901002 | Test school 2     | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
                  | 1355901003 | Test school 3     | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName  | lastSurname | birthDate  |
                  | "131"           | student-fn | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | _storeResultingIdInVariable | studentReference             | schoolReference            | entryGradeLevelDescriptor                          | entryDate  |
                  |                             | { "studentUniqueId": "131" } | { "schoolId": 1355901001 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
                  | StudentSchoolAssociationId  | { "studentUniqueId": "131" } | { "schoolId": 1355901002 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
              And the system has these "PostSecondaryEvents"
                  | _storeResultingIdInVariable | studentReference             | eventDate  | postSecondaryEventCategoryDescriptor                                       |
                  | PostSecondaryEventId        | { "studentUniqueId": "131" } | 2023-09-15 | "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application" |

            # Assert that token with '1355901001' access can retrieve a Student-securable resource
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1355901001"
             When a GET request is made to "/ed-fi/postSecondaryEvents/{PostSecondaryEventId}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/PostSecondaryEvents?eventDate=2023-09-15&postSecondaryEventCategoryDescriptor=uri%3A%2F%2Fed-fi.org%2FPostSecondaryEventCategoryDescriptor%23College%20Application&studentUniqueId=131"
             Then it should respond with 200
              And the response body is
                  """
                  [
                    {
                      "id": "{PostSecondaryEventId}",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                        "studentUniqueId": "131"
                      },
                      "eventDate": "2023-09-15"
                    }
                  ]
                  """

            # Assert that token with '1355901002' access can retrieve a Student-securable resource
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1355901002"
             When a GET request is made to "/ed-fi/postSecondaryEvents/{PostSecondaryEventId}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/PostSecondaryEvents?eventDate=2023-09-15&postSecondaryEventCategoryDescriptor=uri%3A%2F%2Fed-fi.org%2FPostSecondaryEventCategoryDescriptor%23College%20Application&studentUniqueId=131"
             Then it should respond with 200
              And the response body is
                  """
                  [
                    {
                      "id": "{PostSecondaryEventId}",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                        "studentUniqueId": "131"
                      },
                      "eventDate": "2023-09-15"
                    }
                  ]
                  """

            # Assert that token with '1355901003' access can not retrieve a Student-securable resource
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1355901003"
             When a GET request is made to "/ed-fi/postSecondaryEvents/{PostSecondaryEventId}"
             Then it should respond with 403

             When a GET request is made to "/ed-fi/PostSecondaryEvents?eventDate=2023-09-15&postSecondaryEventCategoryDescriptor=uri%3A%2F%2Fed-fi.org%2FPostSecondaryEventCategoryDescriptor%23College%20Application&studentUniqueId=131"
             Then it should respond with 200
              And the response body is
                  """
                  [
                  ]
                  """

            # Update SSA's School from '1355901002' to '1355901003'
            Given the token gets switched to the one in the "EdFiSandbox_full_access" variable

             When a PUT request is made to "/ed-fi/StudentSchoolAssociations/{StudentSchoolAssociationId}" with
                  """
                  {
                      "id":"{StudentSchoolAssociationId}",
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                          "schoolId": 1355901003
                      },
                      "studentReference": {
                          "studentUniqueId": "131"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade",
                      "exitWithdrawDate": "2025-01-01"
                  }
                  """
             Then it should respond with 204

            # Assert that token with '1355901001' access continues to be able to retrieve a Student-securable resource
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1355901001"
             When a GET request is made to "/ed-fi/postSecondaryEvents/{PostSecondaryEventId}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/PostSecondaryEvents?eventDate=2023-09-15&postSecondaryEventCategoryDescriptor=uri%3A%2F%2Fed-fi.org%2FPostSecondaryEventCategoryDescriptor%23College%20Application&studentUniqueId=131"
             Then it should respond with 200
              And the response body is
                  """
                  [
                    {
                      "id": "{PostSecondaryEventId}",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                        "studentUniqueId": "131"
                      },
                      "eventDate": "2023-09-15"
                    }
                  ]
                  """

            # Assert that token with '1355901002' access can no longer retrieve a Student-securable resource
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1355901002"
             When a GET request is made to "/ed-fi/postSecondaryEvents/{PostSecondaryEventId}"
             Then it should respond with 403

             When a GET request is made to "/ed-fi/PostSecondaryEvents?eventDate=2023-09-15&postSecondaryEventCategoryDescriptor=uri%3A%2F%2Fed-fi.org%2FPostSecondaryEventCategoryDescriptor%23College%20Application&studentUniqueId=131"
             Then it should respond with 200
              And the response body is
                  """
                  [
                  ]
                  """

            # Assert that token with '1355901003' access now can retrieve a Student-securable resource
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1355901003"
             When a GET request is made to "/ed-fi/postSecondaryEvents/{PostSecondaryEventId}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/PostSecondaryEvents?eventDate=2023-09-15&postSecondaryEventCategoryDescriptor=uri%3A%2F%2Fed-fi.org%2FPostSecondaryEventCategoryDescriptor%23College%20Application&studentUniqueId=131"
             Then it should respond with 200
              And the response body is
                  """
                  [
                    {
                      "id": "{PostSecondaryEventId}",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                        "studentUniqueId": "131"
                      },
                      "eventDate": "2023-09-15"
                    }
                  ]
                  """

        Scenario: 53 Ensure client can retrieve a Student-securable after the SSA has been updated to a new Student
            # Change to use long EdOrgIds when DMS-706 is done
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1455901001"
              And the resulting token is stored in the "EdFiSandbox_full_access" variable
              And the system has these "schools"
                  | schoolId   | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 1455901001 | Test school 1     | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName  | lastSurname | birthDate  |
                  | "141"           | student-fn | student-ln  | 2008-01-01 |
                  | "142"           | student-fn | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | _storeResultingIdInVariable | studentReference             | schoolReference            | entryGradeLevelDescriptor                          | entryDate  |
                  | StudentSchoolAssociationId1 | { "studentUniqueId": "141" } | { "schoolId": 1455901001 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
                  | StudentSchoolAssociationId2 | { "studentUniqueId": "142" } | { "schoolId": 1455901001 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
              And the system has these "PostSecondaryEvents"
                  | _storeResultingIdInVariable | studentReference             | eventDate  | postSecondaryEventCategoryDescriptor                                       |
                  | PostSecondaryEventId1       | { "studentUniqueId": "141" } | 2023-09-15 | "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application" |
                  | PostSecondaryEventId2       | { "studentUniqueId": "142" } | 2023-09-15 | "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application" |

             # Assert that token can retrieve the Student-securable resource A
             When a GET request is made to "/ed-fi/PostSecondaryEvents/{PostSecondaryEventId1}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/PostSecondaryEvents?eventDate=2023-09-15&postSecondaryEventCategoryDescriptor=uri%3A%2F%2Fed-fi.org%2FPostSecondaryEventCategoryDescriptor%23College%20Application&studentUniqueId=141"
             Then it should respond with 200
              And the response body is
                  """
                  [
                    {
                      "id": "{PostSecondaryEventId1}",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                        "studentUniqueId": "141"
                      },
                      "eventDate": "2023-09-15"
                    }
                  ]
                  """

             # Assert that token can retrieve the Student-securable resource B
             When a GET request is made to "/ed-fi/postSecondaryEvents/{PostSecondaryEventId2}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/PostSecondaryEvents?eventDate=2023-09-15&postSecondaryEventCategoryDescriptor=uri%3A%2F%2Fed-fi.org%2FPostSecondaryEventCategoryDescriptor%23College%20Application&studentUniqueId=142"
             Then it should respond with 200
              And the response body is
                  """
                  [
                    {
                      "id": "{PostSecondaryEventId2}",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                        "studentUniqueId": "142"
                      },
                      "eventDate": "2023-09-15"
                    }
                  ]
                  """

             # Update SSA's Student from A to B
             When a PUT request is made to "/ed-fi/StudentSchoolAssociations/{StudentSchoolAssociationId1}" with
                  """
                  {
                      "id":"{StudentSchoolAssociationId1}",
                      "entryDate": "2024-08-01",
                      "schoolReference": {
                          "schoolId": 1455901001
                      },
                      "studentReference": {
                          "studentUniqueId": "142"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 204

             # Assert that token can no longer retrieve the Student-securable resource A
             When a GET request is made to "/ed-fi/postSecondaryEvents/{PostSecondaryEventId1}"
             Then it should respond with 403

             When a GET request is made to "/ed-fi/PostSecondaryEvents?eventDate=2023-09-15&postSecondaryEventCategoryDescriptor=uri%3A%2F%2Fed-fi.org%2FPostSecondaryEventCategoryDescriptor%23College%20Application&studentUniqueId=141"
             Then it should respond with 200
              And the response body is
                  """
                  [
                  ]
                  """

             # Assert that token continues to be able to retrieve the Student-securable resource B
             When a GET request is made to "/ed-fi/postSecondaryEvents/{PostSecondaryEventId2}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/PostSecondaryEvents?eventDate=2023-09-15&postSecondaryEventCategoryDescriptor=uri%3A%2F%2Fed-fi.org%2FPostSecondaryEventCategoryDescriptor%23College%20Application&studentUniqueId=142"
             Then it should respond with 200
              And the response body is
                  """
                  [
                    {
                      "id": "{PostSecondaryEventId2}",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                        "studentUniqueId": "142"
                      },
                      "eventDate": "2023-09-15"
                    }
                  ]
                  """

             # Delete one of the SSAs
             When a DELETE request is made to "/ed-fi/StudentSchoolAssociations/{StudentSchoolAssociationId2}"
             Then it should respond with 204

             # Assert that token continues to not being able to retrieve the Student-securable resource A
             When a GET request is made to "/ed-fi/postSecondaryEvents/{PostSecondaryEventId1}"
             Then it should respond with 403

             When a GET request is made to "/ed-fi/PostSecondaryEvents?eventDate=2023-09-15&postSecondaryEventCategoryDescriptor=uri%3A%2F%2Fed-fi.org%2FPostSecondaryEventCategoryDescriptor%23College%20Application&studentUniqueId=141"
             Then it should respond with 200
              And the response body is
                  """
                  [
                  ]
                  """

             # Assert that token continues to be able to retrieve the Student-securable resource B
             When a GET request is made to "/ed-fi/postSecondaryEvents/{PostSecondaryEventId2}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/PostSecondaryEvents?eventDate=2023-09-15&postSecondaryEventCategoryDescriptor=uri%3A%2F%2Fed-fi.org%2FPostSecondaryEventCategoryDescriptor%23College%20Application&studentUniqueId=142"
             Then it should respond with 200
              And the response body is
                  """
                  [
                    {
                      "id": "{PostSecondaryEventId2}",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                        "studentUniqueId": "142"
                      },
                      "eventDate": "2023-09-15"
                    }
                  ]
                  """
        Scenario: 54 Ensure client can query a Student associated to a School with a long ID
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "3, 301, 30101999999"
              And the system has these "stateEducationAgencies"
                  | stateEducationAgencyId | nameOfInstitution | categories                                                                                                       |
                  | 3                      | Test state        | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#State" }] |
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | stateEducationAgencyReference   | categories                                                                                                          | localEducationAgencyCategoryDescriptor                       |
                  | 301                    | Test LEA          | { "stateEducationAgencyId": 3 } | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#District" }] | "uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC" |
              And the system has these "schools"
                  | schoolId    | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                   | localEducationAgencyReference    |
                  | 30101999999 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] | { "localEducationAgencyId": 301} |
              And the system has these "students"
                  | studentUniqueId | firstName  | lastSurname | birthDate  |
                  | "91"            | student-fn | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | studentReference            | schoolReference             | entryGradeLevelDescriptor                          | entryDate  |
                  | { "studentUniqueId": "91" } | { "schoolId": 30101999999 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |

            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "3"
             When a GET request is made to "/ed-fi/students?studentUniqueId=91"
             Then it should respond with 200
              And the response body is
                  """
                    [
                      {
                        "id": "{id}",
                        "firstName": "student-fn",
                        "studentUniqueId": "91",
                        "birthDate": "2008-01-01",
                        "lastSurname": "student-ln"
                      }
                    ]
                  """
