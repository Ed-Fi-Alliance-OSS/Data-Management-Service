@reset-data-before-scenario
Feature: RelationshipsWithPeople relational GET-many authorization

    Rule: People relationship GET-many scenarios use the relational backend authorization lane

        @relational-backend
        @relational-ci-shard-3
        Scenario: Student GET-many returns only students related to the caller
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with educationOrganizationIds "9155901001, 9155901002"
              And the system has these "schools"
                  | schoolId   | nameOfInstitution       | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 9155901001 | Authorized school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
                  | 9155901002 | Unrelated school        | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName          | lastSurname | birthDate  |
                  | "915501"        | Authorized student | student-ln  | 2008-01-01 |
                  | "915502"        | Unrelated student  | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | studentReference                 | schoolReference            | entryGradeLevelDescriptor                          | entryDate  |
                  | { "studentUniqueId": "915501" } | { "schoolId": 9155901001 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
                  | { "studentUniqueId": "915502" } | { "schoolId": 9155901002 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "9155901001"
             When a GET request is made to "/ed-fi/students?totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  {
                      "Total-Count": "1"
                  }
                  """
              And the response body is
                  """
                  [
                      {
                          "id": "{id}",
                          "studentUniqueId": "915501",
                          "birthDate": "2008-01-01",
                          "firstName": "Authorized student",
                          "lastSurname": "student-ln"
                      }
                  ]
                  """

        @relational-backend
        @relational-ci-shard-3
        Scenario: Staff GET-many returns only staff related to the caller through staff education organization associations
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "9255901001, 9255901002"
              And the system has these "schools"
                  | schoolId   | nameOfInstitution       | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 9255901001 | Authorized staff school | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
                  | 9255901002 | Unrelated staff school  | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "Staffs"
                  | staffUniqueId             | firstName        | lastSurname |
                  | staff-get-many-authorized | Authorized staff | staff-ln    |
                  | staff-get-many-unrelated  | Unrelated staff  | staff-ln    |
              And the system has these "staffEducationOrganizationAssignmentAssociations"
                  | beginDate  | staffClassificationDescriptor                         | educationOrganizationReference                | staffReference                                      |
                  | 2020-10-10 | uri://ed-fi.org/StaffClassificationDescriptor#Teacher | { "educationOrganizationId": 9255901001 }     | { "staffUniqueId": "staff-get-many-authorized" } |
                  | 2020-10-10 | uri://ed-fi.org/StaffClassificationDescriptor#Teacher | { "educationOrganizationId": 9255901002 }     | { "staffUniqueId": "staff-get-many-unrelated" }  |
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "9255901001"
             When a GET request is made to "/ed-fi/Staffs?totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  {
                      "Total-Count": "1"
                  }
                  """
              And the response body is
                  """
                  [
                      {
                          "id": "{id}",
                          "firstName": "Authorized staff",
                          "lastSurname": "staff-ln",
                          "staffUniqueId": "staff-get-many-authorized"
                      }
                  ]
                  """

        @relational-backend
        @relational-ci-shard-3
        Scenario: Empty education organization claims return an empty People page with total count zero
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with educationOrganizationIds "9355901001"
              And the system has these "schools"
                  | schoolId   | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 9355901001 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName    | lastSurname | birthDate  |
                  | "935501"        | Test student | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | studentReference                 | schoolReference            | entryGradeLevelDescriptor                          | entryDate  |
                  | { "studentUniqueId": "935501" } | { "schoolId": 9355901001 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds ""
             When a GET request is made to "/ed-fi/students?totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  {
                      "Total-Count": "0"
                  }
                  """
              And the response body is
                  """
                  []
                  """

        @relational-backend
        @relational-ci-shard-3
        @ResetClaimsetsAfterScenario
        Scenario: StudentsOnlyThroughResponsibility GET-many returns only students related by responsibility
            Given the claimSet "EdFiAPIPublisherWriter" is authorized with educationOrganizationIds "9455901001"
              And the system has these descriptors
                  | descriptorValue                                         |
                  | uri://ed-fi.org/ResponsibilityDescriptor#Accountability |
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with educationOrganizationIds "9455901001, 9455901002"
              And the system has these "schools"
                  | schoolId   | nameOfInstitution         | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 9455901001 | Responsible school        | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
                  | 9455901002 | Other responsibility org  | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName             | lastSurname | birthDate  |
                  | "945501"        | Responsible student   | student-ln  | 2008-01-01 |
                  | "945502"        | Other responsibility  | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | studentReference                 | schoolReference            | entryGradeLevelDescriptor                          | entryDate  |
                  | { "studentUniqueId": "945501" } | { "schoolId": 9455901001 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
                  | { "studentUniqueId": "945502" } | { "schoolId": 9455901002 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
            Given a claim set is uploaded to CMS that grants "studentEducationOrganizationResponsibilityAssociation" access to "PeopleGetMany-NoFurtherResponsibilitySetup"
              And the claim set upload to CMS should be successful
            Given the claimSet "PeopleGetMany-NoFurtherResponsibilitySetup" is authorized with educationOrganizationIds "9455901001, 9455901002"
              And a POST request is made to "/ed-fi/studentEducationOrganizationResponsibilityAssociations" with
                  """
                  {
                      "beginDate": "2023-08-01",
                      "educationOrganizationReference": {
                          "educationOrganizationId": 9455901001
                      },
                      "studentReference": {
                          "studentUniqueId": "945501"
                      },
                      "responsibilityDescriptor": "uri://ed-fi.org/ResponsibilityDescriptor#Accountability"
                  }
                  """
              And a POST request is made to "/ed-fi/studentEducationOrganizationResponsibilityAssociations" with
                  """
                  {
                      "beginDate": "2023-08-01",
                      "educationOrganizationReference": {
                          "educationOrganizationId": 9455901002
                      },
                      "studentReference": {
                          "studentUniqueId": "945502"
                      },
                      "responsibilityDescriptor": "uri://ed-fi.org/ResponsibilityDescriptor#Accountability"
                  }
                  """
            Given a claim set is uploaded to CMS that grants "student" access to "PeopleGetMany-StudentsOnlyThroughResponsibility" using authorization strategy "RelationshipsWithStudentsOnlyThroughResponsibility"
              And the claim set upload to CMS should be successful
            Given the claimSet "PeopleGetMany-StudentsOnlyThroughResponsibility" is authorized with educationOrganizationIds "9455901001"
             When a GET request is made to "/ed-fi/students?totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  {
                      "Total-Count": "1"
                  }
                  """
              And the response body is
                  """
                  [
                      {
                          "id": "{id}",
                          "studentUniqueId": "945501",
                          "birthDate": "2008-01-01",
                          "firstName": "Responsible student",
                          "lastSurname": "student-ln"
                      }
                  ]
                  """
