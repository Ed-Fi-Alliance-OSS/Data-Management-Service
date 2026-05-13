@reset-data-before-scenario
Feature: RelationshipsWithEdOrgsOnly relational GET-many authorization

    Rule: Query scenarios use the relational backend authorization lane

        @relational-backend
        Scenario: Inverted strategy allows school claims to query parent local education agencies
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "2, 201, 20101"
              And the system has these "stateEducationAgencies"
                  | stateEducationAgencyId | nameOfInstitution | categories                                                                                                            |
                  | 2                      | Test state        | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#State" }] |
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | stateEducationAgencyReference   | categories                                                                                                               | localEducationAgencyCategoryDescriptor                       |
                  | 201                    | Test LEA          | { "stateEducationAgencyId": 2 } | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#District" }] | "uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC" |
              And the system has these "schools"
                  | schoolId | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        | localEducationAgencyReference    |
                  | 20101    | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] | { "localEducationAgencyId": 201} |
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyInvertedClaimSet" is authorized with educationOrganizationIds "20101"
             When a GET request is made to "/ed-fi/localEducationAgencies"
             Then it should respond with 200
              And the response body is
                  """
                  [
                      {
                          "id": "{id}",
                          "localEducationAgencyId": 201,
                          "nameOfInstitution": "Test LEA",
                          "stateEducationAgencyReference": {
                              "stateEducationAgencyId": 2
                          },
                          "categories": [
                              {
                                  "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#District"
                              }
                          ],
                          "localEducationAgencyCategoryDescriptor": "uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC"
                      }
                  ]
                  """

        @relational-backend
        Scenario: Normal and inverted strategies are ORed for GET-many authorization
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "2, 201, 20101"
              And the system has these "stateEducationAgencies"
                  | stateEducationAgencyId | nameOfInstitution | categories                                                                                                            |
                  | 2                      | Test state        | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#State" }] |
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | stateEducationAgencyReference   | categories                                                                                                               | localEducationAgencyCategoryDescriptor                       |
                  | 201                    | Test LEA          | { "stateEducationAgencyId": 2 } | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#District" }] | "uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC" |
              And the system has these "schools"
                  | schoolId | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        | localEducationAgencyReference    |
                  | 20101    | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] | { "localEducationAgencyId": 201} |
              And the system has these "academicWeeks"
                  | weekIdentifier | schoolReference       | beginDate  | endDate    | totalInstructionalDays |
                  | week 1         | { "schoolId": 20101 } | 2023-08-01 | 2023-08-07 | 5                      |
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyOrInvertedClaimSet" is authorized with educationOrganizationIds "2"
             When a GET request is made to "/ed-fi/academicWeeks"
             Then it should respond with 200
              And the response body is
                  """
                  [
                      {
                          "id": "{id}",
                          "weekIdentifier": "week 1",
                          "beginDate": "2023-08-01",
                          "endDate": "2023-08-07",
                          "totalInstructionalDays": 5,
                          "schoolReference": {
                              "schoolId": 20101
                          }
                      }
                  ]
                  """

        @relational-backend
        Scenario: Empty education organization claims return an empty page with total count zero
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901001"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 255901001 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "academicWeeks"
                  | weekIdentifier | schoolReference           | beginDate  | endDate    | totalInstructionalDays |
                  | week 1         | { "schoolId": 255901001 } | 2023-08-01 | 2023-08-07 | 5                      |
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds ""
             When a GET request is made to "/ed-fi/academicWeeks?totalCount=true"
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
        Scenario: Paging and total count are applied after relational authorization filtering
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901001, 255901222"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution      | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 255901001 | Authorized school one  | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
                  | 255901222 | Unauthorized school    | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "academicWeeks"
                  | weekIdentifier | schoolReference             | beginDate  | endDate    | totalInstructionalDays |
                  | week unauth     | { "schoolId": 255901222 }   | 2023-08-01 | 2023-08-07 | 5                      |
                  | week auth 1     | { "schoolId": 255901001 }   | 2023-08-08 | 2023-08-14 | 5                      |
                  | week auth 2     | { "schoolId": 255901001 }   | 2023-08-15 | 2023-08-21 | 4                      |
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901001"
             When a GET request is made to "/ed-fi/academicWeeks?totalCount=true&limit=1"
             Then it should respond with 200
              And the response headers include
                  """
                  {
                      "Total-Count": "2"
                  }
                  """
              And the response body is
                  """
                  [
                      {
                          "id": "{id}",
                          "weekIdentifier": "week auth 1",
                          "beginDate": "2023-08-08",
                          "endDate": "2023-08-14",
                          "totalInstructionalDays": 5,
                          "schoolReference": {
                              "schoolId": 255901001
                          }
                      }
                  ]
                  """

        @relational-backend
        Scenario: Known unsupported mixed strategies return not implemented for GET-many
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901001"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 255901001 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "academicWeeks"
                  | weekIdentifier | schoolReference           | beginDate  | endDate    | totalInstructionalDays |
                  | week 1         | { "schoolId": 255901001 } | 2023-08-01 | 2023-08-07 | 5                      |
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyMixedStrategyClaimSet" is authorized with educationOrganizationIds "255901001"
             When a GET request is made to "/ed-fi/academicWeeks"
             Then it should respond with 501
              And the response body is
                  """
                  {
                      "error": "Relational query authorization is not implemented for resource 'Ed-Fi.AcademicWeek' when effective GET-many authorization includes strategies outside the current DMS-1055 EdOrg-only scope. Unsupported strategies: ['NamespaceBased']. Supported DMS-1055 strategies are 'RelationshipsWithEdOrgsOnly', 'RelationshipsWithEdOrgsOnlyInverted', and 'NoFurtherAuthorizationRequired' as a no-op."
                  }
                  """
