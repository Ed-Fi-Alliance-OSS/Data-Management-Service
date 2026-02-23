Feature: Profile Assigned Profiles
    As an API client with assigned profiles
    I want covered resources to require assigned profiles while non-covered resources keep standard behavior
    So that profile assignment is enforced consistently

    Rule: Covered resources must use an assigned profile

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "Test-Profile-Resource-IncludeAll" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |

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

        Scenario: 04 Not-covered resource with unassigned profile fails
            When a GET request is made to "/ed-fi/students/{id}" with profile "Test-Profile-StudentOnly-Resource-IncludeAll" for resource "Student"
            Then the profile response status is 403
             And the response body should have error type "urn:ed-fi:api:security:data-policy:incorrect-usage"
