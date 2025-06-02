Feature: RelationshipsWithEdOrgsAndPeople Authorization with Big int IDs 

 Background:
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

Rule: Multi-school enrollment is properly authorized
       
       Background:
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "11559010011111, 11559020011111"
              And the resulting token is stored in the "EdFiSandbox_full_access" variable
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | categories                                                                                                          | localEducationAgencyCategoryDescriptor                       |
                  | 1155901                | Test LEA 1        | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#District" }] | "uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC" |
                  | 1155902                | Test LEA 2        | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#District" }] | "uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC" |
              And the system has these "schools"
                  | schoolId       | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                   | localEducationAgencyReference        |
                  | 11559010011111 | Test school 1     | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] | { "localEducationAgencyId": 1155901} |
                  | 11559020011111 | Test school 2     | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] | { "localEducationAgencyId": 1155902} |
                  | 5              | Test school 5     | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] | null                                 |
              And the system has these "students"
                  | studentUniqueId | firstName  | lastSurname | birthDate  |
                  | "111"           | student-fn | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | _storeResultingIdInVariable  | studentReference             | schoolReference                | entryGradeLevelDescriptor                          | entryDate  |
                  | StudentASchool1AssociationId | { "studentUniqueId": "111" } | { "schoolId": 11559010011111 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
                  | StudentASchool2AssociationId | { "studentUniqueId": "111" } | { "schoolId": 11559020011111 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |

        Scenario: 47 Ensure client can query a Student associated to a School with a long ID

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
