Feature: RelationshipsWithEdOrgsAndPeople Authorization

        Background:
            Given the claimSet "EdFiAPIPublisherWriter" is authorized with educationOrganizationIds "255901001, 244901"
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
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901901"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution   | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 255901901 | Authorized school   | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
                  | 255901902 | Unauthorized school | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName            | lastSurname | birthDate  |
                  | "91111"         | Authorized student   | student-ln  | 2008-01-01 |
                  | "91112"         | Unauthorized student | student-ln  | 2008-01-01 |

        Scenario: 01 Ensure client can create a StudentSchoolAssociation
             When a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                          "schoolId": 255901901
                      },
                      "studentReference": {
                          "studentUniqueId": "91111"
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
                          "schoolId": 255901902
                      },
                      "studentReference": {
                          "studentUniqueId": "91112"
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
                          "schoolId": 255901901
                      },
                      "studentReference": {
                          "studentUniqueId": "91111"
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
                          "schoolId": 255901901
                      },
                      "studentReference": {
                          "studentUniqueId": "91111"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 201 or 200

            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901902"

             When a GET request is made to "/ed-fi/studentSchoolAssociations/{id}"
             Then it should respond with 403

        Scenario: 05 Ensure client can only query authorized StudentSchoolAssociation
            Given a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                          "schoolId": 255901901
                      },
                      "studentReference": {
                          "studentUniqueId": "91111"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
              And the resulting id is stored in the "StudentSchoolAssociationId" variable
             Then it should respond with 201 or 200

            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901902"
            Given a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                          "schoolId": 255901902
                      },
                      "studentReference": {
                          "studentUniqueId": "91112"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 201 or 200

            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901901"
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
                        "schoolId": 255901901
                      },
                      "studentReference": {
                        "studentUniqueId": "91111"
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
                          "schoolId": 255901901
                      },
                      "studentReference": {
                          "studentUniqueId": "91111"
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
                          "schoolId": 255901901
                      },
                      "studentReference": {
                          "studentUniqueId": "91111"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade",
                      "exitWithdrawDate": "2025-01-01"
                  }
                  """
             Then it should respond with 204

        Scenario: 07 Ensure client can not update a StudentSchoolAssociation belonging to an unauthorized education organization hierarchy
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901902"
            Given a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                          "schoolId": 255901902
                      },
                      "studentReference": {
                          "studentUniqueId": "91112"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 201 or 200

            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901901"
             When a PUT request is made to "/ed-fi/StudentSchoolAssociations/{id}" with
                  """
                  {
                      "id":"{id}",
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                          "schoolId": 255901902
                      },
                      "studentReference": {
                          "studentUniqueId": "91112"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade",
                      "exitWithdrawDate": "2025-01-01"
                  }
                  """
             Then it should respond with 403

        Scenario: 08 Ensure client can not delete a StudentSchoolAssociation belonging to an unauthorized education organization hierarchy
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901902"
            Given a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                          "schoolId": 255901902
                      },
                      "studentReference": {
                          "studentUniqueId": "91112"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 201 or 200

            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901901"
             When a DELETE request is made to "/ed-fi/StudentSchoolAssociations/{id}"
             Then it should respond with 403

        Scenario: 09 Ensure client can delete a StudentSchoolAssociation
            Given  a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                          "schoolId": 255901901
                      },
                      "studentReference": {
                          "studentUniqueId": "91111"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 201 or 200

             When a DELETE request is made to "/ed-fi/StudentSchoolAssociations/{id}"
             Then it should respond with 204

    Rule: Student CRUD is properly authorized
        Background:
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901911,255901912"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution   | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 255901911 | Authorized school   | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
                  | 255901912 | Unauthorized school | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "students"
                  | _storeResultingIdInVariable | studentUniqueId | firstName            | lastSurname | birthDate  |
                  | StudentId                   | "92111"         | Authorized student   | student-ln  | 2008-01-01 |
                  | UnauthorizedStudentId       | "92112"         | Unauthorized student | student-ln  | 2008-01-01 |
                  | UnassociatedStudentId       | "92113"         | Unassociated student | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | _storeResultingIdInVariable | studentReference               | schoolReference           | entryGradeLevelDescriptor                            | entryDate  | exitGradeLevel                                       | exitWithdrawTypeDescriptor                                    |
                  | StudentSchoolAssociationId  | { "studentUniqueId": "92111" } | { "schoolId": 255901911 } | "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary" | 2023-08-01 | "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary" | "uri://ed-fi.org/ExitWithdrawTypeDescriptor#Student withdrew" |
                  |                             | { "studentUniqueId": "92112" } | { "schoolId": 255901912 } | "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary" | 2023-08-01 | "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary" | "uri://ed-fi.org/ExitWithdrawTypeDescriptor#Student withdrew" |
              And the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901911"

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
                    "studentUniqueId": "92111"
                  }
                  """

        Scenario: 12 Ensure client can not retrieve an unassociated Student
             When a GET request is made to "/ed-fi/students/{UnassociatedStudentId}"
             Then it should respond with 403

        Scenario: 13 Ensure client can not retrieve a Student associated to an unauthorized education organization hierarchy
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901912"

            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901911"
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
                          "studentUniqueId": "92111",
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
                      "studentUniqueId": "92111",
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
                      "studentUniqueId": "92113",
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
                      "studentUniqueId": "92113",
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
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901921, 255901922"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution   | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 255901921 | Authorized school   | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
                  | 255901922 | Unauthorized school | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName            | lastSurname | birthDate  |
                  | "93111"         | Authorized student   | student-ln  | 2008-01-01 |
                  | "93112"         | Unauthorized student | student-ln  | 2008-01-01 |
                  | "93113"         | Unassociated student | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | studentReference               | schoolReference           | entryGradeLevelDescriptor                            | entryDate  | exitGradeLevel                                       | exitWithdrawTypeDescriptor                                    |
                  | { "studentUniqueId": "93111" } | { "schoolId": 255901921 } | "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary" | 2023-08-01 | "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary" | "uri://ed-fi.org/ExitWithdrawTypeDescriptor#Student withdrew" |
                  | { "studentUniqueId": "93112" } | { "schoolId": 255901922 } | "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary" | 2023-08-01 | "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary" | "uri://ed-fi.org/ExitWithdrawTypeDescriptor#Student withdrew" |
              And the system has these "postSecondaryInstitutions"
                  | postSecondaryInstitutionId | nameOfInstitution | categories                                                                                                                            |
                  | 255901923                  | Authorized PSI    | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution"} ] |
              And the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901921, 255901923"

        Scenario: 19 Ensure client can create a Student-securable
             When a POST request is made to "/ed-fi/PostSecondaryEvents" with
                  """
                  {
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "93111"
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
                          "studentUniqueId": "93113"
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
                          "studentUniqueId": "93112"
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
                          "studentUniqueId": "93111"
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
                          "studentUniqueId": "93111"
                      }
                  }
                  """

        Scenario: 23 Ensure client can not retrieve a Student-securable if the Student is unassociated

            Given a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                          "schoolId": 255901921
                      },
                      "studentReference": {
                          "studentUniqueId": "93113"
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
                          "studentUniqueId": "93113"
                      }
                  }
                  """
             Then it should respond with 201 or 200

            Given a DELETE request is made to "/ed-fi/studentSchoolAssociations/{StudentSchoolAssociationId}"
             Then it should respond with 204

             When a GET request is made to "/ed-fi/PostSecondaryEvents/{id}"
             Then it should respond with 403

        Scenario: 24 Ensure client can not retrieve a Student-securable if the Student is associated to an unauthorized education organization hierarchy
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901922"
            Given a POST request is made to "/ed-fi/PostSecondaryEvents" with
                  """
                  {
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "93112"
                      }
                  }
                  """
             Then it should respond with 201 or 200

            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901921"
             When a GET request is made to "/ed-fi/PostSecondaryEvents/{id}"
             Then it should respond with 403

        Scenario: 25 Ensure client can only query authorized Student-securables
            # Set up a Student-securable for an unassociated Student
            Given a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                          "schoolId": 255901921
                      },
                      "studentReference": {
                          "studentUniqueId": "93113"
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
                          "studentUniqueId": "93113"
                      }
                  }
                  """
             Then it should respond with 201 or 200

            Given a DELETE request is made to "/ed-fi/studentSchoolAssociations/{StudentSchoolAssociationId}"
             Then it should respond with 204

            # Set up a Student-securable in an unauthorized education organization hierarchy
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901922"
            Given a POST request is made to "/ed-fi/PostSecondaryEvents" with
                  """
                  {
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "93112"
                      }
                  }
                  """
             Then it should respond with 201 or 200

            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901921"

            # Set up a Student-securable
            Given a POST request is made to "/ed-fi/PostSecondaryEvents" with
                  """
                  {
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "93111"
                      }
                  }
                  """
              And the resulting id is stored in the "PostSecondaryEventId" variable
             Then it should respond with 201 or 200

            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901921"
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
                              "studentUniqueId": "93111"
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
                          "studentUniqueId": "93111"
                      }
                  }
                  """
             Then it should respond with 201 or 200

             When a PUT request is made to "/ed-fi/PostSecondaryEvents/{id}" with
                  """
                  {
                      "id":"{id}",
                      "postSecondaryInstitutionId": 255901923,
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "93111"
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
                          "schoolId": 255901921
                      },
                      "studentReference": {
                          "studentUniqueId": "93113"
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
                          "studentUniqueId": "93113"
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
                      "postSecondaryInstitutionId": 255901923,
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "93113"
                      }
                  }
                  """
             Then it should respond with 403

        Scenario: 28 Ensure client can not update a Student-securable if the Student is associated to an unauthorized education organization hierarchy
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901922"
            Given a POST request is made to "/ed-fi/PostSecondaryEvents" with
                  """
                  {
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "93112"
                      }
                  }
                  """
             Then it should respond with 201 or 200

            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901921"
             When a PUT request is made to "/ed-fi/PostSecondaryEvents/{id}" with
                  """
                  {
                      "id":"{id}",
                      "postSecondaryInstitutionId": 255901923,
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "93112"
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
                          "studentUniqueId": "93111"
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
                          "schoolId": 255901921
                      },
                      "studentReference": {
                          "studentUniqueId": "93113"
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
                          "studentUniqueId": "93113"
                      }
                  }
                  """
             Then it should respond with 201 or 200

            Given a DELETE request is made to "/ed-fi/studentSchoolAssociations/{StudentSchoolAssociationId}"
             Then it should respond with 204

             When a DELETE request is made to "/ed-fi/PostSecondaryEvents/{id}"
             Then it should respond with 403

        Scenario: 31 Ensure client can not delete a Student-securable if the Student is associated to an unauthorized education organization hierarchy
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901922"
            Given a POST request is made to "/ed-fi/PostSecondaryEvents" with
                  """
                  {
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "93112"
                      }
                  }
                  """
             Then it should respond with 201 or 200

            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901921"
             When a DELETE request is made to "/ed-fi/PostSecondaryEvents/{id}"
             Then it should respond with 403

    Rule: StudentSchoolAssociation mutations cascade down
        Background:
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901931, 255901932"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution   | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 255901931 | Authorized school   | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
                  | 255901932 | Unauthorized school | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "students"
                  | _storeResultingIdInVariable | studentUniqueId | firstName            | lastSurname | birthDate  |
                  | StudentId                   | "94111"         | Authorized student   | student-ln  | 2008-01-01 |
                  |                             | "94112"         | Unauthorized student | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | _storeResultingIdInVariable | studentReference               | schoolReference           | entryGradeLevelDescriptor                            | entryDate  | exitGradeLevel                                       | exitWithdrawTypeDescriptor                                    |
                  | StudentSchoolAssociationId  | { "studentUniqueId": "94111" } | { "schoolId": 255901931 } | "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary" | 2023-08-01 | "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary" | "uri://ed-fi.org/ExitWithdrawTypeDescriptor#Student withdrew" |
              And the system has these "PostSecondaryEvents"
                  | _storeResultingIdInVariable | studentReference               | eventDate  | postSecondaryEventCategoryDescriptor                                       |
                  | PostSecondaryEventId        | { "studentUniqueId": "94111" } | 2023-09-15 | "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application" |
              And the system has these "postSecondaryInstitutions"
                  | postSecondaryInstitutionId | nameOfInstitution | categories                                                                                                                            |
                  | 255901933                  | Authorized PSI    | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution"} ] |
              And the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901931, 255901933"

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
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901931, 255901932"
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
             Then it should respond with 204

            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901931"
             When a GET request is made to "/ed-fi/students/{StudentId}"
             Then it should respond with 403

             When a GET request is made to "/ed-fi/students?studentUniqueId=94111"
             Then it should respond with 200
              And the response body is
                  """
                  []
                  """

             When a PUT request is made to "/ed-fi/students/{StudentId}" with
                  """
                  {
                      "id": "{StudentId}",
                      "studentUniqueId": "94111",
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
                          "studentUniqueId": "94111"
                      }
                  }
                  """
             Then it should respond with 403

             When a GET request is made to "/ed-fi/postSecondaryEvents/{PostSecondaryEventId}"
             Then it should respond with 403

             When a GET request is made to "/ed-fi/PostSecondaryEvents?eventDate=2023-09-15&postSecondaryEventCategoryDescriptor=uri%3A%2F%2Fed-fi.org%2FPostSecondaryEventCategoryDescriptor%23College%20Application&studentUniqueId=94111"
             Then it should respond with 200
              And the response body is
                  """
                  []
                  """

             When a PUT request is made to "/ed-fi/postSecondaryEvents/{PostSecondaryEventId}" with
                  """
                  {
                      "id":"{PostSecondaryEventId}",
                      "postSecondaryInstitutionId": 255901933,
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "94111"
                      }
                  }
                  """
             Then it should respond with 403

             When a DELETE request is made to "/ed-fi/postSecondaryEvents/{PostSecondaryEventId}"
             Then it should respond with 403

            # Teardown
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901931, 255901932"
             When a DELETE request is made to "/ed-fi/studentSchoolAssociations/{StudentSchoolAssociationId}"
             Then it should respond with 204

        Scenario: 34 Ensure client can no longer CRUD a Student and a Student-securable after the StudentSchoolAssociation is deleted
             When a DELETE request is made to "/ed-fi/studentSchoolAssociations/{StudentSchoolAssociationId}"
             Then it should respond with 204

             When a GET request is made to "/ed-fi/students/{StudentId}"
             Then it should respond with 403

             When a GET request is made to "/ed-fi/students?studentUniqueId=94111"
             Then it should respond with 200
              And the response body is
                  """
                  []
                  """

             When a PUT request is made to "/ed-fi/students/{StudentId}" with
                  """
                  {
                      "id": "{StudentId}",
                      "studentUniqueId": "94111",
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
                          "studentUniqueId": "94111"
                      }
                  }
                  """
             Then it should respond with 403

             When a GET request is made to "/ed-fi/postSecondaryEvents/{PostSecondaryEventId}"
             Then it should respond with 403

             When a GET request is made to "/ed-fi/PostSecondaryEvents?eventDate=2023-09-15&postSecondaryEventCategoryDescriptor=uri%3A%2F%2Fed-fi.org%2FPostSecondaryEventCategoryDescriptor%23College%20Application&studentUniqueId=94111"
             Then it should respond with 200
              And the response body is
                  """
                  []
                  """

             When a PUT request is made to "/ed-fi/postSecondaryEvents/{PostSecondaryEventId}" with
                  """
                  {
                      "id":"{PostSecondaryEventId}",
                      "postSecondaryInstitutionId": 255901933,
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "94111"
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
                          "schoolId": 255901931
                      },
                      "studentReference": {
                          "studentUniqueId": "94111"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 201

             When a GET request is made to "/ed-fi/students/{StudentId}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/students?studentUniqueId=94111"
             Then it should respond with 200
              And the response body is
                  """
                  [
                      {
                          "id": "{StudentId}",
                          "studentUniqueId": "94111",
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
                      "studentUniqueId": "94111",
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
                          "studentUniqueId": "94111"
                      }
                  }
                  """
             Then it should respond with 201

             When a GET request is made to "/ed-fi/postSecondaryEvents/{PostSecondaryEventId}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/PostSecondaryEvents?eventDate=2023-09-15&postSecondaryEventCategoryDescriptor=uri%3A%2F%2Fed-fi.org%2FPostSecondaryEventCategoryDescriptor%23College%20Application&studentUniqueId=94111"
             Then it should respond with 200
              And the response body is
                  """
                  [
                      {
                          "id":"{PostSecondaryEventId}",
                          "eventDate": "2023-09-15",
                          "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                          "studentReference": {
                              "studentUniqueId": "94111"
                          }
                      }
                  ]
                  """

             When a PUT request is made to "/ed-fi/postSecondaryEvents/{PostSecondaryEventId}" with
                  """
                  {
                      "id":"{PostSecondaryEventId}",
                      "postSecondaryInstitutionId": 255901933,
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "94111"
                      }
                  }
                  """
             Then it should respond with 204

             When a DELETE request is made to "/ed-fi/postSecondaryEvents/{PostSecondaryEventId}"
             Then it should respond with 204

    Rule: A resource that is securable on both Student and Education Organization is properly authorized
        Background:
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901001, 244901"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                        | educationOrganizationCategories                                                                                   |
                  | 255901001 | ACC-test          | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName  | lastSurname | birthDate  |
                  | "98989898"      | student-fn | student-ln  | 2008-01-01 |
                  | "98989899"      | student-fn | student-ln  | 2008-01-01 |
              And the system has these "courses"
                  | courseCode       | identificationCodes                                                                                                                                   | educationOrganizationReference          | courseTitle        | numberOfParts |
                  | ACC-test-Course1 | [ {"courseIdentificationSystemDescriptor": "uri://ed-fi.org/CourseIdentificationSystemDescriptor#CSSC course code", "identificationCode": "Id-123"} ] | {"educationOrganizationId": 255901001 } | "ACC-test-Course1" | 8             |
                  | ACC-test-Course2 | [ {"courseIdentificationSystemDescriptor": "uri://ed-fi.org/CourseIdentificationSystemDescriptor#CSSC course code", "identificationCode": "Id-123"} ] | {"educationOrganizationId": 255901001 } | "ACC-test-Course2" | 8             |
                  | ACC-test-Course3 | [ {"courseIdentificationSystemDescriptor": "uri://ed-fi.org/CourseIdentificationSystemDescriptor#CSSC course code", "identificationCode": "Id-123"} ] | {"educationOrganizationId": 255901001 } | "ACC-test-Course3" | 8             |
            Given the system has these "studentschoolassociations"
                  | studentReference                  | schoolReference           | entryGradeLevelDescriptor                            | entryDate  | exitGradeLevel                                       | exitWithdrawTypeDescriptor                                    |
                  | { "studentUniqueId": "98989898" } | { "schoolId": 255901001 } | "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary" | 2023-08-01 | "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary" | "uri://ed-fi.org/ExitWithdrawTypeDescriptor#Student withdrew" |
              And the system has these "studentacademicrecords"
                  | studentReference                  | educationOrganizationReference           | schoolYearTypeReference | termDescriptor                            |
                  | { "studentUniqueId": "98989898" } | { "educationOrganizationId": 255901001 } | {"schoolYear": 2023}    | "uri://ed-fi.org/TermDescriptor#Semester" |

        Scenario: 36 Ensure client can create a courseTranscripts with edorg id:255901001 and student id:98989898
             When a POST request is made to "/ed-fi/courseTranscripts" with
                  """
                  {
                      "courseAttemptResultDescriptor": "uri://ed-fi.org/CourseAttemptResultDescriptor#Pass",
                      "courseReference": {
                        "courseCode": "ACC-test-Course1",
                        "educationOrganizationId": 255901001
                      },
                      "studentAcademicRecordReference": {
                        "educationOrganizationId": 255901001,
                        "schoolYear": 2023,
                        "studentUniqueId": "98989898",
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
                        "educationOrganizationId": 255901001
                      },
                      "studentAcademicRecordReference": {
                        "educationOrganizationId": 255901001,
                        "schoolYear": 2023,
                        "studentUniqueId": "98989898",
                        "termDescriptor": "uri://ed-fi.org/TermDescriptor#Semester"
                      }
                    }
                  """

        Scenario: 37 Ensure client can update a courseTranscripts with edorg id:255901001 and student id:98989898
             When a POST request is made to "/ed-fi/courseTranscripts" with
                  """
                  {
                      "courseAttemptResultDescriptor": "uri://ed-fi.org/CourseAttemptResultDescriptor#Pass",
                      "courseReference": {
                        "courseCode": "ACC-test-Course2",
                        "educationOrganizationId": 255901001
                      },
                      "studentAcademicRecordReference": {
                        "educationOrganizationId": 255901001,
                        "schoolYear": 2023,
                        "studentUniqueId": "98989898",
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
                        "educationOrganizationId": 255901001
                      },
                      "courseTitle":"ACC-test-Course2-title",
                      "studentAcademicRecordReference": {
                        "educationOrganizationId": 255901001,
                        "schoolYear": 2023,
                        "studentUniqueId": "98989898",
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
                        "educationOrganizationId": 255901001
                      },
                      "courseTitle":"ACC-test-Course2-title",
                      "studentAcademicRecordReference": {
                        "educationOrganizationId": 255901001,
                        "schoolYear": 2023,
                        "studentUniqueId": "98989898",
                        "termDescriptor": "uri://ed-fi.org/TermDescriptor#Semester"
                      }
                    }
                  """

        Scenario: 38 Ensure client can delete a courseTranscripts with edorg id:255901001 and student id:98989898
             When a POST request is made to "/ed-fi/courseTranscripts" with
                  """
                  {
                      "courseAttemptResultDescriptor": "uri://ed-fi.org/CourseAttemptResultDescriptor#Pass",
                      "courseReference": {
                        "courseCode": "ACC-test-Course3",
                        "educationOrganizationId": 255901001
                      },
                      "studentAcademicRecordReference": {
                        "educationOrganizationId": 255901001,
                        "schoolYear": 2023,
                        "studentUniqueId": "98989898",
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
                         "educationOrganizationId": 255901001
                       },
                       "courseAttemptResultDescriptor": "uri://ed-fi.org/CourseAttemptResultDescriptor#Pass",
                       "studentAcademicRecordReference": {
                         "schoolYear": 2023,
                         "termDescriptor": "uri://ed-fi.org/TermDescriptor#Semester",
                         "studentUniqueId": "98989898",
                         "educationOrganizationId": 255901001
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
                        "educationOrganizationId": 255901001
                      },
                      "studentAcademicRecordReference": {
                        "educationOrganizationId": 255901001,
                        "schoolYear": 2023,
                        "studentUniqueId": "98989899",
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
                    "No relationships have been established between the caller's education organization id claims ('255901001', '244901') and the resource item's StudentUniqueId value."
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
                        "educationOrganizationId": 255901001
                      },
                      "studentAcademicRecordReference": {
                        "educationOrganizationId": 255901001,
                        "schoolYear": 2023,
                        "studentUniqueId": "98989898",
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
                       "educationOrganizationId": 255901001
                     },
                    "studentAcademicRecordReference": {
                      "educationOrganizationId": 255901001,
                      "schoolYear": 2023,
                      "studentUniqueId": "98989899",
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
                      "No relationships have been established between the caller's education organization id claims ('255901001', '244901') and the resource item's StudentUniqueId value."
                      ]
                  }
                  """

    Rule: DELETE or GET resource fails with a 403 forbidden error with no student school association
        Background:
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "3, 301"
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
                  | 301                    | Test LEA          | { "stateEducationAgencyId": 3 } | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#District" }] | "uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC" |
              And the system has these "schools"
                  | schoolId    | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                   | localEducationAgencyReference    |
                  | 30101999999 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] | { "localEducationAgencyId": 301} |
              And the system has these "students"
                  | studentUniqueId | firstName  | lastSurname | birthDate  |
                  | "11111"         | student-fn | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | studentReference               | schoolReference             | entryGradeLevelDescriptor                          | entryDate  | exitGradeLevel                                     | exitWithdrawTypeDescriptor                                    |
                  | { "studentUniqueId": "11111" } | { "schoolId": 30101999999 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | "uri://ed-fi.org/ExitWithdrawTypeDescriptor#Student withdrew" |
        @addrelationships
        Scenario: 41 Ensure client can not delete or get a PostSecondaryEvent with out student school association

             When a POST request is made to "/ed-fi/PostSecondaryEvents" with
                  """
                  {
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                        "studentUniqueId": "11111"
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
                        "studentUniqueId": "11111"
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
                         "detail": "Access to the resource could not be authorized.",
                         "type": "urn:ed-fi:api:security:authorization:",
                         "title": "Authorization Denied",
                         "status": 403,
                         "validationErrors": {},
                         "errors": [
                              "No relationships have been established between the caller's education organization id claims ('3', '301') and the resource item's StudentUniqueId value."
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
                          "No relationships have been established between the caller's education organization id claims ('3', '301') and the resource item's StudentUniqueId value."
                        ]
                      }
                  """

    Rule: Edge cases are properly authorized
        Scenario: 42 Ensure client can CRUD a PostSecondaryEvent using the NoFurtherAuthorizationRequired strategy
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with educationOrganizationIds "255901101, 255901102"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 255901101 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName  | lastSurname | birthDate  |
                  | "21111"         | student-fn | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | studentReference               | schoolReference           | entryGradeLevelDescriptor                          | entryDate  |
                  | { "studentUniqueId": "21111" } | { "schoolId": 255901101 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
              And the system has these "postSecondaryInstitutions"
                  | postSecondaryInstitutionId | nameOfInstitution | categories                                                                                                                            |
                  | 255901102                  | Authorized PSI    | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution"} ] |

             When a POST request is made to "/ed-fi/PostSecondaryEvents" with
                  """
                  {
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "21111"
                      }
                  }
                  """
             Then it should respond with 201

             When a GET request is made to "/ed-fi/PostSecondaryEvents/{id}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/PostSecondaryEvents?eventDate=2023-09-15&postSecondaryEventCategoryDescriptor=uri%3A%2F%2Fed-fi.org%2FPostSecondaryEventCategoryDescriptor%23College%20Application&studentUniqueId=21111"
             Then it should respond with 200
              And the response body is
                  """
                  [
                    {
                      "id": "{id}",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                        "studentUniqueId": "21111"
                      },
                      "eventDate": "2023-09-15"
                    }
                  ]
                  """

             When a PUT request is made to "/ed-fi/PostSecondaryEvents/{id}" with
                  """
                  {
                      "id":"{id}",
                      "postSecondaryInstitutionId": 255901102,
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "21111"
                      }
                  }
                  """
             Then it should respond with 204

             When a DELETE request is made to "/ed-fi/PostSecondaryEvents/{id}"
             Then it should respond with 204

        Scenario: 43 Ensure client without education organization access can CRUD a PostSecondaryEvent using the NoFurtherAuthorizationRequired strategy
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with educationOrganizationIds "255901201, 255901202"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 255901201 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName  | lastSurname | birthDate  |
                  | "31111"         | student-fn | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | studentReference               | schoolReference           | entryGradeLevelDescriptor                          | entryDate  |
                  | { "studentUniqueId": "31111" } | { "schoolId": 255901201 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
              And the system has these "postSecondaryInstitutions"
                  | postSecondaryInstitutionId | nameOfInstitution | categories                                                                                                                            |
                  | 255901202                  | Authorized PSI    | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution"} ] |
              And the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with educationOrganizationIds "1"

             When a POST request is made to "/ed-fi/PostSecondaryEvents" with
                  """
                  {
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "31111"
                      }
                  }
                  """
             Then it should respond with 201

             When a GET request is made to "/ed-fi/PostSecondaryEvents/{id}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/PostSecondaryEvents?eventDate=2023-09-15&postSecondaryEventCategoryDescriptor=uri%3A%2F%2Fed-fi.org%2FPostSecondaryEventCategoryDescriptor%23College%20Application&studentUniqueId=31111"
             Then it should respond with 200
              And the response body is
                  """
                  [
                    {
                      "id": "{id}",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                        "studentUniqueId": "31111"
                      },
                      "eventDate": "2023-09-15"
                    }
                  ]
                  """

             When a PUT request is made to "/ed-fi/PostSecondaryEvents/{id}" with
                  """
                  {
                      "id":"{id}",
                      "postSecondaryInstitutionId": 255901202,
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "31111"
                      }
                  }
                  """
             Then it should respond with 204

             When a DELETE request is made to "/ed-fi/PostSecondaryEvents/{id}"
             Then it should respond with 204

        Scenario: 44 Ensure client with LEA access can CRUD a PostSecondaryEvent
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "25590"
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | categories                                                                                                          | localEducationAgencyCategoryDescriptor                       |
                  | 25590                  | Test LEA          | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#District" }] | "uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC" |
              And the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                   | localEducationAgencyReference      |
                  | 255901501 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] | { "localEducationAgencyId": 25590} |
              And the system has these "students"
                  | studentUniqueId | firstName  | lastSurname | birthDate  |
                  | "51111"         | student-fn | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | studentReference               | schoolReference           | entryGradeLevelDescriptor                          | entryDate  |
                  | { "studentUniqueId": "51111" } | { "schoolId": 255901501 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
              And the system has these "postSecondaryInstitutions"
                  | postSecondaryInstitutionId | nameOfInstitution | categories                                                                                                                            |
                  | 255901502                  | Authorized PSI    | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution"} ] |

              And a POST request is made to "/ed-fi/PostSecondaryEvents" with
                  """
                  {
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "51111"
                      }
                  }
                  """
             Then it should respond with 201

             When a GET request is made to "/ed-fi/PostSecondaryEvents/{id}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/PostSecondaryEvents?eventDate=2023-09-15&postSecondaryEventCategoryDescriptor=uri%3A%2F%2Fed-fi.org%2FPostSecondaryEventCategoryDescriptor%23College%20Application&studentUniqueId=51111"
             Then it should respond with 200
              And the response body is
                  """
                  [
                    {
                      "id": "{id}",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                        "studentUniqueId": "51111"
                      },
                      "eventDate": "2023-09-15"
                    }
                  ]
                  """

             When a PUT request is made to "/ed-fi/PostSecondaryEvents/{id}" with
                  """
                  {
                      "id":"{id}",
                      "postSecondaryInstitutionId": 255901502,
                      "eventDate": "2023-09-15",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "51111"
                      }
                  }
                  """
             Then it should respond with 204

             When a DELETE request is made to "/ed-fi/PostSecondaryEvents/{id}"
             Then it should respond with 204

        Scenario: 45 Ensure client can create a PostSecondaryEvent after transfering the Student to a School within the client's LEA
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901601, 255901002"
              And the resulting token is stored in the "EdFiSandbox_full_access" variable
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | categories                                                                                                          | localEducationAgencyCategoryDescriptor                       |
                  | 255901                 | Test LEA          | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#District" }] | "uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC" |
              And the system has these "schools"
                  | schoolId  | nameOfInstitution  | gradeLevels                                                                      | educationOrganizationCategories                                                                                   | localEducationAgencyReference       |
                  | 255901601 | Test school        | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] | { "localEducationAgencyId": 255901} |
                  | 255901002 | Test school no LEA | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] | null                                |
              And the system has these "students"
                  | studentUniqueId | firstName  | lastSurname | birthDate  |
                  | "61111"         | student-fn | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | _storeResultingIdInVariable | studentReference               | schoolReference           | entryGradeLevelDescriptor                          | entryDate  |
                  | StudentSchoolAssociationId  | { "studentUniqueId": "61111" } | { "schoolId": 255901002 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
              And the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901"
              And the resulting token is stored in the "EdFiSandbox_LEA_only" variable

             When a POST request is made to "/ed-fi/PostSecondaryEvents" with
                  """
                  {
                      "eventDate": "2025-01-01",
                      "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                      "studentReference": {
                          "studentUniqueId": "61111"
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
                          "schoolId": 255901601
                      },
                      "studentReference": {
                          "studentUniqueId": "61111"
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
                          "studentUniqueId": "61111"
                      }
                  }
                  """
             Then it should respond with 201 or 200

    Rule: Multiple relationships are properly authorized
        Background:
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1, 2"
              And the resulting token is stored in the "EdFiSandbox_full_access" variable
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | categories                                                                                                          | localEducationAgencyCategoryDescriptor                       |
                  | 11                     | Test LEA 11       | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#District" }] | "uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC" |
                  | 22                     | Test LEA 22       | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#District" }] | "uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC" |
              And the system has these "schools"
                  | schoolId | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                   | localEducationAgencyReference   |
                  | 1        | Test school 1     | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] | { "localEducationAgencyId": 11} |
                  | 2        | Test school 2     | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] | { "localEducationAgencyId": 22} |
                  | 5        | Test school 5     | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] | null                            |
              And the system has these "students"
                  | studentUniqueId | firstName  | lastSurname | birthDate  |
                  | "A"             | student-fn | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | _storeResultingIdInVariable  | studentReference           | schoolReference   | entryGradeLevelDescriptor                          | entryDate  |
                  | StudentASchool1AssociationId | { "studentUniqueId": "A" } | { "schoolId": 1 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
                  | StudentASchool2AssociationId | { "studentUniqueId": "A" } | { "schoolId": 2 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
        Scenario: 46 Ensure client with access to both schools can query multiple student school associations
             When a GET request is made to "/ed-fi/StudentSchoolAssociations?studentUniqueId=A&offset=0&limit=10"
             Then it should respond with 200
              And the response body is
                  """
                  [
                    {
                        "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade",
                        "entryDate": "2023-08-01",
                        "id": "{id}",
                        "schoolReference": {
                            "schoolId": 2
                        },
                        "studentReference": {
                            "studentUniqueId": "A"
                        }
                  },
                  {
                        "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade",
                        "entryDate": "2023-08-01",
                        "id": "{id}",
                        "schoolReference": {
                            "schoolId": 1
                        },
                        "studentReference": {
                            "studentUniqueId": "A"
                        }
                    }
                  ]
                  """
        Scenario: 47 Ensure client with access to one school can query one student school associations
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "11"
             When a GET request is made to "/ed-fi/StudentSchoolAssociations?studentUniqueId=A"
             Then it should respond with 200
              And the response body is
                  """
                  [
                    {
                      "id": "{id}",
                      "entryDate": "2023-08-01",
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade",
                      "studentReference": {
                        "studentUniqueId": "A"
                      },
                      "schoolReference": {
                        "schoolId": 1
                      }
                    }
                  ]
                  """
        Scenario: 48 Ensure search still works when the student association is updated and deleted
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1,2,5"
             When a PUT request is made to "/ed-fi/StudentSchoolAssociations/{StudentASchool1AssociationId}" with
                  """
                  {
                      "id":"{StudentASchool1AssociationId}",
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                          "schoolId": 5
                      },
                      "studentReference": {
                          "studentUniqueId": "A"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 204

             When a DELETE request is made to "/ed-fi/StudentSchoolAssociations/{StudentASchool2AssociationId}"
             Then it should respond with 204

             When a GET request is made to "/ed-fi/StudentSchoolAssociations?studentUniqueId=A&offset=0&limit=10"
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
                            "studentUniqueId": "A"
                        }
                    }
                  ]
                  """
