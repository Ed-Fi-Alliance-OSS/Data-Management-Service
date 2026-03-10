Feature: Profile Assigned Profiles
              As an API client with assigned profiles
              I want covered resources to require assigned profiles while non-covered resources keep standard behavior
    So that profile assignment is enforced consistently

    Rule: Covered resources must use an assigned profile

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "Test-Profile-Resource-IncludeAll" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade               |

        Scenario: 01 Covered resource with assigned profile content type succeeds
             When a POST request is made to "/ed-fi/schools" with profile "Test-Profile-Resource-IncludeAll" for resource "School" with body
                  """
                  {
                      "schoolId": 99004001,
                      "nameOfInstitution": "Assigned Profile Covered School",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                          }
                      ]
                  }
                  """
             Then the profile response status is 201
             When a GET request is made to "/ed-fi/schools/{id}" with profile "Test-Profile-Resource-IncludeAll" for resource "School"
             Then the profile response status is 200

        Scenario: 02 Covered resource with different profile content type fails
             When a GET request is made to "/ed-fi/schools" with profile "Test-Profile-StudentOnly-Resource-IncludeAll" for resource "School"
             Then the profile response status is 403
              And the response body should have error type "urn:ed-fi:api:security:data-policy:incorrect-usage"

        Scenario: 03 Covered resource write with assigned profile content type succeeds
             When a POST request is made to "/ed-fi/schools" with profile "Test-Profile-Resource-IncludeAll" for resource "School" with body
                  """
                  {
                      "schoolId": 99004003,
                      "nameOfInstitution": "Assigned Profile Covered School Write",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                          }
                      ]
                  }
                  """
             Then the profile response status is 201

        Scenario: 04 Covered resource write with different profile content type fails
             When a POST request is made to "/ed-fi/schools" with profile "Test-Profile-StudentOnly-Resource-IncludeAll" for resource "School" with body
                  """
                  {
                      "schoolId": 99004004,
                      "nameOfInstitution": "Assigned Profile Covered School Wrong Profile",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                          }
                      ]
                  }
                  """
             Then the profile response status is 403
              And the response body should have error type "urn:ed-fi:api:security:data-policy:incorrect-usage"

        # Current DMS behavior follows the implicit-profile design:
        # with no profile header, the request succeeds when exactly one assigned
        # profile applies to the target resource/verb.
        Scenario: 05 Covered resource with standard content type and one assigned profile
             When a GET request is made to "/ed-fi/schools" without profile header
             Then the profile response status is 200

        Scenario: 06 Covered resource with one of several assigned profiles succeeds
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profiles "Test-Profile-Resource-IncludeAll, Test-Profile-StudentOnly-Resource-IncludeAll" and namespacePrefixes "uri://ed-fi.org"
             When a POST request is made to "/ed-fi/students" with profile "Test-Profile-StudentOnly-Resource-IncludeAll" for resource "Student" with body
                  """
                  {
                      "studentUniqueId": "99004061",
                      "birthDate": "2010-02-15",
                      "firstName": "Multi",
                      "lastSurname": "Assigned"
                  }
                  """
             Then the profile response status is 201
             When a GET request is made to "/ed-fi/students/{id}" with profile "Test-Profile-StudentOnly-Resource-IncludeAll" for resource "Student"
             Then the profile response status is 200

        Scenario: 07 Covered resource with different profile content type for API client with several assigned profiles fails with incorrect-usage
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profiles "Test-Profile-Resource-IncludeAll, Test-Profile-StudentOnly-Resource-IncludeAll" and namespacePrefixes "uri://ed-fi.org"
             When a GET request is made to "/ed-fi/schools" with profile "Test-Profile-StudentOnly-Resource-IncludeAll" for resource "School"
             Then the profile response status is 403
              And the response body should have error type "urn:ed-fi:api:security:data-policy:incorrect-usage"

        Scenario: 08 Covered resource write with one of several assigned profiles succeeds
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profiles "Test-Profile-Resource-IncludeAll, Test-Profile-StudentOnly-Resource-IncludeAll" and namespacePrefixes "uri://ed-fi.org"
             When a POST request is made to "/ed-fi/students" with profile "Test-Profile-StudentOnly-Resource-IncludeAll" for resource "Student" with body
                  """
                  {
                      "studentUniqueId": "99004062",
                      "birthDate": "2010-02-15",
                      "firstName": "MultiWrite",
                      "lastSurname": "Assigned"
                  }
                  """
             Then the profile response status is 201

        # Current DMS behavior follows the implicit-profile design:
        # with no profile header, the request succeeds when exactly one assigned
        # profile applies to the target resource/verb.
        Scenario: 09 Covered resource write with standard content type and one assigned profile succeeds
             When a POST request is made to "/ed-fi/schools" without profile header with body
                  """
                  {
                      "schoolId": 99004009,
                      "nameOfInstitution": "Assigned Profile Covered School Standard Content Type",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                          }
                      ]
                  }
                  """
             Then the profile response status is 201

        # Current DMS behavior follows the implicit-profile design:
        # even with multiple assigned profiles, the request succeeds when only one
        # assigned profile applies to the target resource/verb.
        Scenario: 10 Covered resource write with standard content type and several assigned profiles succeeds when only one applies
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profiles "Test-Profile-Resource-IncludeAll, Test-Profile-StudentOnly-Resource-IncludeAll" and namespacePrefixes "uri://ed-fi.org"
             When a POST request is made to "/ed-fi/schools" without profile header with body
                  """
                  {
                      "schoolId": 99004010,
                      "nameOfInstitution": "Assigned Profile Covered School Multi Standard Content Type",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                          }
                      ]
                  }
                  """
             Then the profile response status is 201

        # Current DMS behavior follows the implicit-profile design:
        # even with multiple assigned profiles, the request succeeds when only one
        # assigned profile applies to the target resource/verb.
        Scenario: 11 Covered resource with standard content type and several assigned profiles succeeds when only one applies
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profiles "Test-Profile-Resource-IncludeAll, Test-Profile-StudentOnly-Resource-IncludeAll" and namespacePrefixes "uri://ed-fi.org"
             When a GET request is made to "/ed-fi/schools" without profile header
             Then the profile response status is 200

        # Current DMS behavior follows the implicit-profile design:
        # with no profile header, the request succeeds when exactly one assigned
        # profile applies to the target resource/verb.
        Scenario: 12 Covered resource update with standard content type and one assigned profile succeeds
             When a POST request is made to "/ed-fi/schools" with profile "Test-Profile-Resource-IncludeAll" for resource "School" with body
                  """
                  {
                      "schoolId": 99004012,
                      "nameOfInstitution": "Assigned Profile Covered School Update Seed",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                          }
                      ]
                  }
                  """
             Then the profile response status is 201
             When a PUT request is made to "/ed-fi/schools/{id}" without profile header with body
                  """
                  {
                      "id": "{id}",
                      "schoolId": 99004012,
                      "nameOfInstitution": "Assigned Profile Covered School Update Standard Content Type",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                          }
                      ]
                  }
                  """
             Then the profile response status is 204

        # Current DMS behavior follows the implicit-profile design:
        # even with multiple assigned profiles, the request succeeds when only one
        # assigned profile applies to the target resource/verb.
        Scenario: 13 Covered resource update with standard content type and several assigned profiles succeeds when only one applies
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profiles "Test-Profile-Resource-IncludeAll, Test-Profile-StudentOnly-Resource-IncludeAll" and namespacePrefixes "uri://ed-fi.org"
             When a POST request is made to "/ed-fi/schools" with profile "Test-Profile-Resource-IncludeAll" for resource "School" with body
                  """
                  {
                      "schoolId": 99004013,
                      "nameOfInstitution": "Assigned Profile Covered School Multi Update Seed",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                          }
                      ]
                  }
                  """
             Then the profile response status is 201
             When a PUT request is made to "/ed-fi/schools/{id}" without profile header with body
                  """
                  {
                      "id": "{id}",
                      "schoolId": 99004013,
                      "nameOfInstitution": "Assigned Profile Covered School Multi Update Standard Content Type",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                          }
                      ]
                  }
                  """
             Then the profile response status is 204

        # Current DMS returns incorrect-usage when the caller omits the profile
        # header and multiple assigned profiles apply to the same resource/verb.
        Scenario: 14 Covered resource read with standard content type and multiple applicable assigned profiles returns incorrect-usage
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profiles "E2E-Test-School-IncludeOnly, E2E-Test-School-IncludeOnly-Alt" and namespacePrefixes "uri://ed-fi.org"
             When a GET request is made to "/ed-fi/schools" without profile header
             Then the profile response status is 403
              And the response body should have error type "urn:ed-fi:api:security:data-policy:incorrect-usage"

        Scenario: 15 Covered resource create with standard content type and multiple applicable assigned profiles returns incorrect-usage
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profiles "E2E-Test-School-IncludeOnly, E2E-Test-School-IncludeOnly-Alt" and namespacePrefixes "uri://ed-fi.org"
             When a POST request is made to "/ed-fi/schools" without profile header with body
                  """
                  {
                      "schoolId": 99004015,
                      "nameOfInstitution": "Assigned Profile Covered School Standard Content Type Rejected",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                          }
                      ]
                  }
                  """
             Then the profile response status is 403
              And the response body should have error type "urn:ed-fi:api:security:data-policy:incorrect-usage"

        Scenario: 16 Covered resource create with standard content type and several assigned profiles returns incorrect-usage when multiple apply
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profiles "E2E-Test-School-IncludeOnly, E2E-Test-School-IncludeOnly-Alt" and namespacePrefixes "uri://ed-fi.org"
             When a POST request is made to "/ed-fi/schools" without profile header with body
                  """
                  {
                      "schoolId": 99004016,
                      "nameOfInstitution": "Assigned Profile Covered School Multi Standard Content Type Rejected",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                          }
                      ]
                  }
                  """
             Then the profile response status is 403
              And the response body should have error type "urn:ed-fi:api:security:data-policy:incorrect-usage"

        Scenario: 17 Covered resource read with standard content type and several assigned profiles returns incorrect-usage when multiple apply
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profiles "E2E-Test-School-IncludeOnly, E2E-Test-School-IncludeOnly-Alt" and namespacePrefixes "uri://ed-fi.org"
             When a GET request is made to "/ed-fi/schools" without profile header
             Then the profile response status is 403
              And the response body should have error type "urn:ed-fi:api:security:data-policy:incorrect-usage"

        Scenario: 18 Covered resource update with standard content type and multiple applicable assigned profiles returns incorrect-usage
             When a POST request is made to "/ed-fi/schools" with profile "Test-Profile-Resource-IncludeAll" for resource "School" with body
                  """
                  {
                      "schoolId": 99004018,
                      "nameOfInstitution": "Assigned Profile Covered School Update Seed Rejected",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                          }
                      ]
                  }
                  """
             Then the profile response status is 201
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profiles "E2E-Test-School-IncludeOnly, E2E-Test-School-IncludeOnly-Alt" and namespacePrefixes "uri://ed-fi.org"
             When a PUT request is made to "/ed-fi/schools/{id}" without profile header with body
                  """
                  {
                      "id": "{id}",
                      "schoolId": 99004018,
                      "nameOfInstitution": "Assigned Profile Covered School Update Standard Content Type Rejected",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                          }
                      ]
                  }
                  """
             Then the profile response status is 403
              And the response body should have error type "urn:ed-fi:api:security:data-policy:incorrect-usage"

        Scenario: 19 Covered resource update with standard content type and several assigned profiles returns incorrect-usage when multiple apply
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profiles "E2E-Test-School-IncludeOnly, E2E-Test-School-IncludeOnly-Alt" and namespacePrefixes "uri://ed-fi.org"
             When a POST request is made to "/ed-fi/schools" with profile "E2E-Test-School-IncludeOnly" for resource "School" with body
                  """
                  {
                      "schoolId": 99004019,
                      "nameOfInstitution": "Assigned Profile Covered School Multi Update Seed Rejected",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                          }
                      ]
                  }
                  """
             Then the profile response status is 201
             When a PUT request is made to "/ed-fi/schools/{id}" without profile header with body
                  """
                  {
                      "id": "{id}",
                      "schoolId": 99004019,
                      "nameOfInstitution": "Assigned Profile Covered School Multi Update Standard Content Type Rejected",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                          }
                      ]
                  }
                  """
             Then the profile response status is 403
              And the response body should have error type "urn:ed-fi:api:security:data-policy:incorrect-usage"

    Rule: Resources not covered by an assigned profile use standard behavior

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized without profiles and namespacePrefixes "uri://ed-fi.org"
              And a profile test POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "99004002",
                      "birthDate": "2010-02-15",
                      "firstName": "Uncovered",
                      "lastSurname": "Resource"
                  }
                  """
              And the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "Test-Profile-Resource-IncludeAll" and namespacePrefixes "uri://ed-fi.org"

        Scenario: 03 Not-covered resource with standard content type succeeds
             When a GET request is made to "/ed-fi/students/{id}" without profile header
             Then the profile response status is 200
              And the response body should contain fields "id, studentUniqueId, firstName, lastSurname"

        Scenario: 04 Not-covered resource with unassigned profile succeeds
             When a GET request is made to "/ed-fi/students/{id}" with profile "Test-Profile-StudentOnly-Resource-IncludeAll" for resource "Student"
             Then the profile response status is 200
              And the response body should contain fields "id, studentUniqueId, firstName, lastSurname"

        Scenario: 05 Not-covered resource write with standard content type succeeds
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "Test-Profile-Resource-IncludeAll" and namespacePrefixes "uri://ed-fi.org"
              And a profile test POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "99004005",
                      "birthDate": "2010-02-15",
                      "firstName": "Write",
                      "lastSurname": "Standard"
                  }
                  """
             Then the profile response status is 201

        Scenario: 06 Not-covered resource write with unassigned profile succeeds
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "Test-Profile-Resource-IncludeAll" and namespacePrefixes "uri://ed-fi.org"
             When a POST request is made to "/ed-fi/students" with profile "Test-Profile-StudentOnly-Resource-IncludeAll" for resource "Student" with body
                  """
                  {
                      "studentUniqueId": "99004006",
                      "birthDate": "2010-02-15",
                      "firstName": "Write",
                      "lastSurname": "Unassigned"
                  }
                  """
             Then the profile response status is 201

        Scenario: 07 Not-covered resource update with standard content type succeeds
             When a PUT request is made to "/ed-fi/students/{id}" without profile header with body
                  """
                  {
                      "id": "{id}",
                      "studentUniqueId": "99004002",
                      "birthDate": "2010-02-15",
                      "firstName": "Updated",
                      "lastSurname": "Resource"
                  }
                  """
             Then the profile response status is 204
             When a GET request is made to "/ed-fi/students/{id}" without profile header
             Then the profile response status is 200
              And the response body path "firstName" should have value "Updated"
