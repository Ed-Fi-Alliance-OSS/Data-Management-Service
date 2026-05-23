@reset-data-before-scenario
Feature: RelationshipsWithEdOrgsOnly relational authorization

    Rule: Query scenarios use the relational backend authorization lane

        @relational-backend
        @relational-ci-shard-3
        Scenario: Inverted strategy allows school claims to query parent local education agencies
            # Use broader setup access only to seed the state/LEA/school hierarchy for this scenario.
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "2, 201, 20101"
              And the system has these "stateEducationAgencies"
                  | stateEducationAgencyId | nameOfInstitution | categories                                                                                                            |
                  | 2                      | Test state        | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#State" }] |
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | stateEducationAgencyReference   | categories                                                                                                               | localEducationAgencyCategoryDescriptor                       |
                  | 201                    | Test LEA          | { "stateEducationAgencyId": 2 } | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#District" }] | "uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC" |
              And the system has these "schools"
                  | schoolId | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        | localEducationAgencyReference    |
                  | 20101    | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] | { "localEducationAgencyId": 201} |
            # Switch to the narrower inverted claim set under test before issuing the query.
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
        @relational-ci-shard-3
        Scenario: Normal and inverted strategies are ORed for GET-many authorization
            # Use broader setup access only to seed two independent state/LEA/school hierarchies.
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "2, 201, 20101, 3, 301, 30101"
              And the system has these "stateEducationAgencies"
                  | stateEducationAgencyId | nameOfInstitution | categories                                                                                                            |
                  | 2                      | Test state 2      | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#State" }] |
                  | 3                      | Test state 3      | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#State" }] |
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | stateEducationAgencyReference   | categories                                                                                                               | localEducationAgencyCategoryDescriptor                       |
                  | 201                    | Test LEA 201      | { "stateEducationAgencyId": 2 } | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#District" }] | "uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC" |
                  | 301                    | Test LEA 301      | { "stateEducationAgencyId": 3 } | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#District" }] | "uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC" |
              And the system has these "schools"
                  | schoolId | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        | localEducationAgencyReference    |
                  | 20101    | Test school 20101 | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] | { "localEducationAgencyId": 201} |
                  | 30101    | Test school 30101 | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] | { "localEducationAgencyId": 301} |
            # Switch to the query token under test.
            # State claim 2 authorizes LEA 201 only through normal top-down access.
            # School claim 30101 authorizes LEA 301 only through inverted bottom-up access.
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyOrInvertedClaimSet" is authorized with educationOrganizationIds "2, 30101"
             When a GET request is made to "/ed-fi/localEducationAgencies"
             Then it should respond with 200
              And the response body is
                  """
                  [
                      {
                          "id": "{id}",
                          "localEducationAgencyId": 201,
                          "nameOfInstitution": "Test LEA 201",
                          "stateEducationAgencyReference": {
                              "stateEducationAgencyId": 2
                          },
                          "categories": [
                              {
                                  "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#District"
                              }
                          ],
                          "localEducationAgencyCategoryDescriptor": "uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC"
                      },
                      {
                          "id": "{id}",
                          "localEducationAgencyId": 301,
                          "nameOfInstitution": "Test LEA 301",
                          "stateEducationAgencyReference": {
                              "stateEducationAgencyId": 3
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
        @relational-ci-shard-3
        Scenario: Empty education organization claims return an empty page with total count zero
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901001"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 255901001 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
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
        @relational-ci-shard-3
        Scenario: Paging and total count are applied after relational authorization filtering
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901001, 255901222"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution      | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 255901001 | Authorized school one  | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 255901222 | Unauthorized school    | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
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
        @relational-ci-shard-3
        Scenario: Known unsupported mixed strategies return not implemented for GET-many
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901001"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 255901001 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
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

    Rule: POST create-new scenarios use proposed-value relationship authorization

        @relational-backend
        @relational-ci-shard-3
        Scenario: POST create-new succeeds when the caller has a relationship to the proposed school
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901001"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 255901001 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
             When a POST request is made to "/ed-fi/academicWeeks" with
                  """
                  {
                      "weekIdentifier": "post create authorized",
                      "schoolReference": {
                          "schoolId": 255901001
                      },
                      "beginDate": "2023-08-01",
                      "endDate": "2023-08-07",
                      "totalInstructionalDays": 5
                  }
                  """
             Then it should respond with 201 or 200
              And the record can be retrieved with a GET request
                  """
                  {
                      "id": "{id}",
                      "weekIdentifier": "post create authorized",
                      "beginDate": "2023-08-01",
                      "endDate": "2023-08-07",
                      "totalInstructionalDays": 5,
                      "schoolReference": {
                          "schoolId": 255901001
                      }
                  }
                  """

        @relational-backend
        @relational-ci-shard-3
        Scenario: POST create-new succeeds through the inverted strategy lane
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901001"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 255901001 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyInvertedClaimSet" is authorized with educationOrganizationIds "255901001"
             When a POST request is made to "/ed-fi/academicWeeks" with
                  """
                  {
                      "weekIdentifier": "post create inverted",
                      "schoolReference": {
                          "schoolId": 255901001
                      },
                      "beginDate": "2023-08-15",
                      "endDate": "2023-08-21",
                      "totalInstructionalDays": 5
                  }
                  """
             Then it should respond with 201 or 200
              And the record can be retrieved with a GET request
                  """
                  {
                      "id": "{id}",
                      "weekIdentifier": "post create inverted",
                      "beginDate": "2023-08-15",
                      "endDate": "2023-08-21",
                      "totalInstructionalDays": 5,
                      "schoolReference": {
                          "schoolId": 255901001
                      }
                  }
                  """

        @relational-backend
        @relational-ci-shard-3
        Scenario: POST create-new returns forbidden when the caller lacks a relationship to the proposed school
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901001, 255901222"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution      | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 255901001 | Authorized school      | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 255901222 | Caller unrelated school | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901222"
             When a POST request is made to "/ed-fi/academicWeeks" with
                  """
                  {
                      "weekIdentifier": "post create forbidden",
                      "schoolReference": {
                          "schoolId": 255901001
                      },
                      "beginDate": "2023-08-08",
                      "endDate": "2023-08-14",
                      "totalInstructionalDays": 5
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
                          "No relationships have been established between the caller's education organization id claims ('255901222') and the resource item's SchoolId value."
                      ]
                  }
                  """
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901001"
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

    Rule: LocalEducationAgency create uses direct EdOrg claim match

        Background:
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901"
              And the system has these descriptors
                  | descriptorValue                                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency        |
                  | uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Regular public school district |

        @relational-backend
        @relational-ci-shard-3
        Scenario: POST LocalEducationAgency succeeds when the proposed LEA id is directly claimed
             When a POST request is made to "/ed-fi/localEducationAgencies" with
                  """
                  {
                      "localEducationAgencyId": 255901,
                      "nameOfInstitution": "Direct Match LEA",
                      "categories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency"
                          }
                      ],
                      "localEducationAgencyCategoryDescriptor": "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Regular public school district"
                  }
                  """
             Then it should respond with 201
              And the record can be retrieved with a GET request
                  """
                  {
                      "id": "{id}",
                      "localEducationAgencyId": 255901,
                      "nameOfInstitution": "Direct Match LEA",
                      "categories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency"
                          }
                      ],
                      "localEducationAgencyCategoryDescriptor": "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Regular public school district"
                  }
                  """

        @relational-backend
        @relational-ci-shard-3
        Scenario: POST LocalEducationAgency returns forbidden when the proposed LEA id is not claimed
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "999999"
             When a POST request is made to "/ed-fi/localEducationAgencies" with
                  """
                  {
                      "localEducationAgencyId": 255901,
                      "nameOfInstitution": "Forbidden Direct Match LEA",
                      "categories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency"
                          }
                      ],
                      "localEducationAgencyCategoryDescriptor": "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Regular public school district"
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
                          "No relationships have been established between the caller's education organization id claims ('999999') and the resource item's LocalEducationAgencyId value."
                      ]
                  }
                  """
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901"
             When a GET request is made to "/ed-fi/localEducationAgencies?totalCount=true"
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
        Scenario: POST LocalEducationAgency returns forbidden when the caller has no EdOrg claims
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds ""
             When a POST request is made to "/ed-fi/localEducationAgencies" with
                  """
                  {
                      "localEducationAgencyId": 255901,
                      "nameOfInstitution": "No Claims Direct Match LEA",
                      "categories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency"
                          }
                      ],
                      "localEducationAgencyCategoryDescriptor": "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Regular public school district"
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
                          "No relationships have been established between the caller's education organization id claims () and the resource item's LocalEducationAgencyId value."
                      ]
                  }
                  """
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901"
             When a GET request is made to "/ed-fi/localEducationAgencies?totalCount=true"
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

    Rule: Existing-target updates use stored and proposed relationship authorization

        @relational-backend
        @relational-ci-shard-3
        Scenario: PUT succeeds when the caller is authorized for the existing academic week school
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901001"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 255901001 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
             When a POST request is made to "/ed-fi/academicWeeks" with
                  """
                  {
                      "weekIdentifier": "put authorized",
                      "schoolReference": {
                          "schoolId": 255901001
                      },
                      "beginDate": "2023-08-01",
                      "endDate": "2023-08-07",
                      "totalInstructionalDays": 5
                  }
                  """
             Then it should respond with 201 or 200
             When a PUT request is made to "/ed-fi/academicWeeks/{id}" with
                  """
                  {
                      "id": "{id}",
                      "weekIdentifier": "put authorized",
                      "schoolReference": {
                          "schoolId": 255901001
                      },
                      "beginDate": "2023-08-01",
                      "endDate": "2023-08-07",
                      "totalInstructionalDays": 6
                  }
                  """
             Then it should respond with 204
             When a GET request is made to "/ed-fi/academicWeeks/{id}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                      "id": "{id}",
                      "weekIdentifier": "put authorized",
                      "beginDate": "2023-08-01",
                      "endDate": "2023-08-07",
                      "totalInstructionalDays": 6,
                      "schoolReference": {
                          "schoolId": 255901001
                      }
                  }
                  """

        @relational-backend
        @relational-ci-shard-3
        Scenario: PUT succeeds through the inverted strategy when a school claim updates its parent local education agency
            # Use broader setup access only to seed the state/LEA/school hierarchy for this scenario.
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "2, 201, 20101"
              And the system has these "stateEducationAgencies"
                  | stateEducationAgencyId | nameOfInstitution | categories                                                                                                            |
                  | 2                      | Test state        | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#State" }] |
              And the system has these descriptors
                  | descriptorValue                                                            |
                  | uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#District       |
                  | uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC                  |
             When a POST request is made to "/ed-fi/localEducationAgencies" with
                  """
                  {
                      "localEducationAgencyId": 201,
                      "nameOfInstitution": "PUT inverted LEA",
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
                  """
             Then it should respond with 201
            Given the system has these "schools"
                  | schoolId | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        | localEducationAgencyReference    |
                  | 20101    | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] | { "localEducationAgencyId": 201} |
            # Switch to the narrower inverted claim set under test before issuing the PUT.
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyInvertedClaimSet" is authorized with educationOrganizationIds "20101"
             When a PUT request is made to "/ed-fi/localEducationAgencies/{id}" with
                  """
                  {
                      "id": "{id}",
                      "localEducationAgencyId": 201,
                      "nameOfInstitution": "PUT inverted LEA updated",
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
                  """
             Then it should respond with 204
             When a GET request is made to "/ed-fi/localEducationAgencies/{id}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                      "id": "{id}",
                      "localEducationAgencyId": 201,
                      "nameOfInstitution": "PUT inverted LEA updated",
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
                  """

        @relational-backend
        @relational-ci-shard-3
        Scenario: PUT returns forbidden and leaves the academic week unchanged when stored authorization fails
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901001"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 255901001 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
             When a POST request is made to "/ed-fi/academicWeeks" with
                  """
                  {
                      "weekIdentifier": "put forbidden",
                      "schoolReference": {
                          "schoolId": 255901001
                      },
                      "beginDate": "2023-08-08",
                      "endDate": "2023-08-14",
                      "totalInstructionalDays": 5
                  }
                  """
             Then it should respond with 201 or 200
             When the resulting id is stored in the "putAcademicWeekId" variable
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901222"
             When a PUT request is made to "/ed-fi/academicWeeks/{putAcademicWeekId}" with
                  """
                  {
                      "id": "{putAcademicWeekId}",
                      "weekIdentifier": "put forbidden",
                      "schoolReference": {
                          "schoolId": 255901001
                      },
                      "beginDate": "2023-08-08",
                      "endDate": "2023-08-14",
                      "totalInstructionalDays": 6
                  }
                  """
             Then it should respond with 403
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901001"
             When a GET request is made to "/ed-fi/academicWeeks/{putAcademicWeekId}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                      "id": "{id}",
                      "weekIdentifier": "put forbidden",
                      "beginDate": "2023-08-08",
                      "endDate": "2023-08-14",
                      "totalInstructionalDays": 5,
                      "schoolReference": {
                          "schoolId": 255901001
                      }
                  }
                  """

        @relational-backend
        @relational-ci-shard-3
        Scenario: PUT returns forbidden and leaves the academic week unchanged when proposed authorization fails
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901001, 255901222"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution     | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 255901001 | Authorized school     | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 255901222 | Proposed denied school | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
             When a POST request is made to "/ed-fi/academicWeeks" with
                  """
                  {
                      "weekIdentifier": "put proposed forbidden",
                      "schoolReference": {
                          "schoolId": 255901001
                      },
                      "beginDate": "2023-08-29",
                      "endDate": "2023-09-04",
                      "totalInstructionalDays": 5
                  }
                  """
             Then it should respond with 201 or 200
             When the resulting id is stored in the "putProposedForbiddenAcademicWeekId" variable
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901001"
             When a PUT request is made to "/ed-fi/academicWeeks/{putProposedForbiddenAcademicWeekId}" with
                  """
                  {
                      "id": "{putProposedForbiddenAcademicWeekId}",
                      "weekIdentifier": "put proposed forbidden",
                      "schoolReference": {
                          "schoolId": 255901222
                      },
                      "beginDate": "2023-08-29",
                      "endDate": "2023-09-04",
                      "totalInstructionalDays": 6
                  }
                  """
             Then it should respond with 403
             When a GET request is made to "/ed-fi/academicWeeks/{putProposedForbiddenAcademicWeekId}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                      "id": "{id}",
                      "weekIdentifier": "put proposed forbidden",
                      "beginDate": "2023-08-29",
                      "endDate": "2023-09-04",
                      "totalInstructionalDays": 5,
                      "schoolReference": {
                          "schoolId": 255901001
                      }
                  }
                  """

        @relational-backend
        @relational-ci-shard-3
        Scenario: POST-as-update succeeds when the caller is authorized for the existing academic week school
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901001"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 255901001 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
             When a POST request is made to "/ed-fi/academicWeeks" with
                  """
                  {
                      "weekIdentifier": "post update authorized",
                      "schoolReference": {
                          "schoolId": 255901001
                      },
                      "beginDate": "2023-08-15",
                      "endDate": "2023-08-21",
                      "totalInstructionalDays": 5
                  }
                  """
             Then it should respond with 201 or 200
             When the resulting id is stored in the "postUpdateAcademicWeekId" variable
             When a POST request is made to "/ed-fi/academicWeeks" with
                  """
                  {
                      "weekIdentifier": "post update authorized",
                      "schoolReference": {
                          "schoolId": 255901001
                      },
                      "beginDate": "2023-08-15",
                      "endDate": "2023-08-21",
                      "totalInstructionalDays": 6
                  }
                  """
             Then it should respond with 200
             When a GET request is made to "/ed-fi/academicWeeks/{postUpdateAcademicWeekId}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                      "id": "{id}",
                      "weekIdentifier": "post update authorized",
                      "beginDate": "2023-08-15",
                      "endDate": "2023-08-21",
                      "totalInstructionalDays": 6,
                      "schoolReference": {
                          "schoolId": 255901001
                      }
                  }
                  """

        @relational-backend
        @relational-ci-shard-3
        Scenario: POST-as-update succeeds through the inverted strategy when a school claim updates its parent local education agency
            # Use broader setup access only to seed the state/LEA/school hierarchy for this scenario.
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "2, 202, 20201"
              And the system has these "stateEducationAgencies"
                  | stateEducationAgencyId | nameOfInstitution | categories                                                                                                            |
                  | 2                      | Test state        | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#State" }] |
              And the system has these descriptors
                  | descriptorValue                                                            |
                  | uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#District       |
                  | uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC                  |
             When a POST request is made to "/ed-fi/localEducationAgencies" with
                  """
                  {
                      "localEducationAgencyId": 202,
                      "nameOfInstitution": "POST update inverted LEA",
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
                  """
             Then it should respond with 201
             When the resulting id is stored in the "postUpdateInvertedLeaId" variable
            Given the system has these "schools"
                  | schoolId | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        | localEducationAgencyReference    |
                  | 20201    | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] | { "localEducationAgencyId": 202} |
            # Switch to the narrower inverted claim set under test before issuing the POST-as-update.
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyInvertedClaimSet" is authorized with educationOrganizationIds "20201"
             When a POST request is made to "/ed-fi/localEducationAgencies" with
                  """
                  {
                      "localEducationAgencyId": 202,
                      "nameOfInstitution": "POST update inverted LEA updated",
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
                  """
             Then it should respond with 200
             When a GET request is made to "/ed-fi/localEducationAgencies/{postUpdateInvertedLeaId}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                      "id": "{id}",
                      "localEducationAgencyId": 202,
                      "nameOfInstitution": "POST update inverted LEA updated",
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
                  """

        @relational-backend
        @relational-ci-shard-3
        Scenario: POST-as-update returns forbidden and leaves the academic week unchanged when stored authorization fails
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901001"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 255901001 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
             When a POST request is made to "/ed-fi/academicWeeks" with
                  """
                  {
                      "weekIdentifier": "post update forbidden",
                      "schoolReference": {
                          "schoolId": 255901001
                      },
                      "beginDate": "2023-08-22",
                      "endDate": "2023-08-28",
                      "totalInstructionalDays": 5
                  }
                  """
             Then it should respond with 201 or 200
             When the resulting id is stored in the "forbiddenPostUpdateAcademicWeekId" variable
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901222"
             When a POST request is made to "/ed-fi/academicWeeks" with
                  """
                  {
                      "weekIdentifier": "post update forbidden",
                      "schoolReference": {
                          "schoolId": 255901001
                      },
                      "beginDate": "2023-08-22",
                      "endDate": "2023-08-28",
                      "totalInstructionalDays": 6
                  }
                  """
             Then it should respond with 403
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901001"
             When a GET request is made to "/ed-fi/academicWeeks/{forbiddenPostUpdateAcademicWeekId}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                      "id": "{id}",
                      "weekIdentifier": "post update forbidden",
                      "beginDate": "2023-08-22",
                      "endDate": "2023-08-28",
                      "totalInstructionalDays": 5,
                      "schoolReference": {
                          "schoolId": 255901001
                      }
                  }
                  """

    Rule: Single-record scenarios use stored-value relationship authorization

        @relational-backend
        @relational-ci-shard-3
        Scenario: GET by id returns forbidden for an academic week outside the caller education organization claims
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901001"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 255901001 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
             When a POST request is made to "/ed-fi/academicWeeks" with
                  """
                  {
                      "weekIdentifier": "single record get",
                      "schoolReference": {
                          "schoolId": 255901001
                      },
                      "beginDate": "2023-08-01",
                      "endDate": "2023-08-07",
                      "totalInstructionalDays": 5
                  }
                  """
             Then it should respond with 201 or 200
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901222"
             When a GET request is made to "/ed-fi/academicWeeks/{id}"
             Then it should respond with 403

        @relational-backend
        @relational-ci-shard-3
        Scenario: DELETE returns forbidden and leaves an academic week outside the caller education organization claims
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901001"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 255901001 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
             When a POST request is made to "/ed-fi/academicWeeks" with
                  """
                  {
                      "weekIdentifier": "single record delete",
                      "schoolReference": {
                          "schoolId": 255901001
                      },
                      "beginDate": "2023-08-08",
                      "endDate": "2023-08-14",
                      "totalInstructionalDays": 5
                  }
                  """
             Then it should respond with 201 or 200
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901222"
             When a DELETE request is made to "/ed-fi/academicWeeks/{id}"
             Then it should respond with 403
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901001"
             When a GET request is made to "/ed-fi/academicWeeks/{id}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                      "id": "{id}",
                      "weekIdentifier": "single record delete",
                      "beginDate": "2023-08-08",
                      "endDate": "2023-08-14",
                      "totalInstructionalDays": 5,
                      "schoolReference": {
                          "schoolId": 255901001
                      }
                  }
                  """
