@reset-data-before-scenario
Feature: NoFurtherAuthorizationRequired strategy is a no-op for relational read and write paths

    Rule: Query and write scenarios use the relational backend authorization lane

        Background:
            # Seed the descriptors required by the write scenario's POST. The read scenario's
            # data-seed step (`the system has these "schools"`) does not require descriptors to
            # be pre-seeded, but a user-issued POST goes through full descriptor validation.
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "2, 201"
              And the system has these descriptors
                  | descriptorValue                                                                  |
                  | uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School              |
                  | uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade                                 |

        @e2e-ci-shard-3
        Scenario: Read paths return resources whose EdOrg context is disjoint from the caller's claim
            # Seed the SEA/LEA/School hierarchy with a claim set authorized for the target EdOrg context.
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "2, 201, 20101"
              And the system has these "stateEducationAgencies"
                  | stateEducationAgencyId | nameOfInstitution | categories                                                                                                            |
                  | 2                      | DMS-1090 SEA      | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#State" }] |
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | stateEducationAgencyReference   | categories                                                                                                               | localEducationAgencyCategoryDescriptor                       |
                  | 201                    | DMS-1090 LEA      | { "stateEducationAgencyId": 2 } | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#District" }] | "uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC" |
              And the system has these "schools"
                  | _storeResultingIdInVariable | schoolId | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        | localEducationAgencyReference    |
                  | SchoolId1                   | 20101    | DMS-1090 school   | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] | { "localEducationAgencyId": 201} |
            # Switch to a claim set whose EdOrg id 999 has no overlap with the seeded hierarchy.
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with educationOrganizationIds "999"
             When a GET request is made to "/ed-fi/schools"
             Then it should respond with 200
              And the response body is
                  """
                  [
                      {
                          "id": "{id}",
                          "schoolId": 20101,
                          "nameOfInstitution": "DMS-1090 school",
                          "gradeLevels": [
                              { "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" }
                          ],
                          "educationOrganizationCategories": [
                              { "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School" }
                          ],
                          "localEducationAgencyReference": {
                              "localEducationAgencyId": 201
                          }
                      }
                  ]
                  """
             When a GET request is made to "/ed-fi/schools/{SchoolId1}"
             Then it should respond with 200

        @e2e-ci-shard-3
        Scenario: Write paths succeed when the caller's claim contains no EdOrgs that cover the target resource
            # Seed only the SEA/LEA hierarchy with a claim set authorized for the target EdOrg context.
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "2, 201"
              And the system has these "stateEducationAgencies"
                  | stateEducationAgencyId | nameOfInstitution | categories                                                                                                            |
                  | 2                      | DMS-1090 SEA      | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#State" }] |
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | stateEducationAgencyReference   | categories                                                                                                               | localEducationAgencyCategoryDescriptor                       |
                  | 201                    | DMS-1090 LEA      | { "stateEducationAgencyId": 2 } | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#District" }] | "uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC" |
            # Switch to a claim set whose EdOrg id 999 has no overlap with the seeded hierarchy.
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with educationOrganizationIds "999"
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 30101,
                      "nameOfInstitution": "DMS-1090 created school",
                      "gradeLevels": [
                          { "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" }
                      ],
                      "educationOrganizationCategories": [
                          { "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School" }
                      ],
                      "localEducationAgencyReference": {
                          "localEducationAgencyId": 201
                      }
                  }
                  """
             Then it should respond with 201
             When a PUT request is made to "/ed-fi/schools/{id}" with
                  """
                  {
                      "id": "{id}",
                      "schoolId": 30101,
                      "nameOfInstitution": "DMS-1090 updated school",
                      "gradeLevels": [
                          { "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" }
                      ],
                      "educationOrganizationCategories": [
                          { "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School" }
                      ],
                      "localEducationAgencyReference": {
                          "localEducationAgencyId": 201
                      }
                  }
                  """
             Then it should respond with 204
             When a DELETE request is made to "/ed-fi/schools/{id}"
             Then it should respond with 204
