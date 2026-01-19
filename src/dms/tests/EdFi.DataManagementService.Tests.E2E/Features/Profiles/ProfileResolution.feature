Feature: Profile Resolution
    As an API client with profiles assigned
    I want profile filtering to be applied correctly based on my request headers
    So that I receive appropriately filtered responses

    Rule: Single profile is applied implicitly when no Accept header is provided

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-IncludeOnly" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99000201,
                      "nameOfInstitution": "Implicit Profile Test School",
                      "shortNameOfInstitution": "IPTS",
                      "webSite": "https://implicit.example.com",
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

        Scenario: 01 Single profile applied implicitly without Accept header
            When a GET request is made to "/ed-fi/schools/{id}" without profile header
            Then the profile response status is 200
             And the response body should only contain fields "id, schoolId, nameOfInstitution, webSite"
             And the response body should not contain fields "shortNameOfInstitution"

        Scenario: 02 Single profile can also be applied explicitly with Accept header
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-IncludeOnly" for resource "School"
            Then the profile response status is 200
             And the response body should only contain fields "id, schoolId, nameOfInstitution, webSite"

    Rule: Multiple profiles require explicit Accept header

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profiles "E2E-Test-School-IncludeOnly, E2E-Test-School-IncludeOnly-Alt" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99000202,
                      "nameOfInstitution": "Multiple Profile Test School",
                      "shortNameOfInstitution": "MPTS",
                      "webSite": "https://multiple.example.com",
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

        Scenario: 03 Multiple profiles without Accept header returns 403
            When a GET request is made to "/ed-fi/schools/{id}" without profile header
            Then the profile response status is 403
             And the response body should have error type "urn:ed-fi:api:security:data-policy:incorrect-usage"

        Scenario: 04 Multiple profiles with explicit Accept header for first profile succeeds
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-IncludeOnly" for resource "School"
            Then the profile response status is 200
             And the response body should only contain fields "id, schoolId, nameOfInstitution, webSite"
             And the response body should not contain fields "shortNameOfInstitution"

        Scenario: 05 Multiple profiles with explicit Accept header for second profile succeeds
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-IncludeOnly-Alt" for resource "School"
            Then the profile response status is 200
             And the response body should only contain fields "id, schoolId, nameOfInstitution, shortNameOfInstitution"
             And the response body should not contain fields "webSite"

    Rule: Applications without profiles do not filter responses

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized without profiles and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99000203,
                      "nameOfInstitution": "No Profile Test School",
                      "shortNameOfInstitution": "NPTS",
                      "webSite": "https://noprofile.example.com",
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

        Scenario: 06 No profile assigned returns all fields
            When a GET request is made to "/ed-fi/schools/{id}" without profile header
            Then the profile response status is 200
             And the response body should contain fields "id, schoolId, nameOfInstitution, shortNameOfInstitution, webSite, educationOrganizationCategories, gradeLevels"
