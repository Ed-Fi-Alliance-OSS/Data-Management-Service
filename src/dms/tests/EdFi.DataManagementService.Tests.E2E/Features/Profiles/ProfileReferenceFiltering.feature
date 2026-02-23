Feature: Profile Reference Filtering
    As an API client using profile read/write rules
    I want allowed and excluded members to be consistently enforced
    So that profile behavior is predictable on both read and write requests

    Rule: IncludeOnly profile includes only configured members on read

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-IncludeAll" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade               |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99006001,
                      "nameOfInstitution": "IncludeOnly Filtering School",
                      "shortNameOfInstitution": "IOFS",
                      "webSite": "https://includeonly-filter.example.com",
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

        Scenario: 01 IncludeOnly profile returns only configured read members
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-IncludeOnly" and namespacePrefixes "uri://ed-fi.org"
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-IncludeOnly" for resource "School"
            Then the profile response status is 200
             And the response body should contain fields "id, schoolId, nameOfInstitution, webSite"
             And the response body should not contain fields "shortNameOfInstitution, gradeLevels, educationOrganizationCategories"

    Rule: ExcludeOnly profile excludes configured members on read

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-IncludeAll" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade               |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99006002,
                      "nameOfInstitution": "ExcludeOnly Filtering School",
                      "shortNameOfInstitution": "EOFS",
                      "webSite": "https://excludeonly-filter.example.com",
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

        Scenario: 02 ExcludeOnly profile excludes configured read members
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-ExcludeOnly" and namespacePrefixes "uri://ed-fi.org"
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-ExcludeOnly" for resource "School"
            Then the profile response status is 200
             And the response body should not contain fields "shortNameOfInstitution, webSite"
             And the response body should contain fields "id, schoolId, nameOfInstitution, gradeLevels, educationOrganizationCategories"

    Rule: IncludeOnly and ExcludeOnly write profiles are enforced on write

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized without profiles and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade               |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99006003,
                      "nameOfInstitution": "Write Filtering School",
                      "shortNameOfInstitution": "WFS",
                      "webSite": "https://write-filter.example.com",
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

        Scenario: 03 IncludeOnly write profile strips excluded write members
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-Write-IncludeOnly" and namespacePrefixes "uri://ed-fi.org"
            When a PUT request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-Write-IncludeOnly" for resource "School" with body
                  """
                  {
                      "id": "{id}",
                      "schoolId": 99006003,
                      "nameOfInstitution": "Write IncludeOnly Updated",
                      "shortNameOfInstitution": "WIOU",
                      "webSite": "https://this-should-be-stripped.example.com",
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
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-Write-IncludeOnly" for resource "School"
            Then the profile response status is 200
             And the response body path "webSite" should have value "https://write-filter.example.com"
             And the response body path "shortNameOfInstitution" should have value "WIOU"

        Scenario: 04 ExcludeOnly write profile excludes configured write members
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-Write-ExcludeOnly" and namespacePrefixes "uri://ed-fi.org"
            When a PUT request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-Write-ExcludeOnly" for resource "School" with body
                  """
                  {
                      "id": "{id}",
                      "schoolId": 99006003,
                      "nameOfInstitution": "Write ExcludeOnly Updated",
                      "shortNameOfInstitution": "WEOU",
                      "webSite": "https://excludeonly-stripped.example.com",
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
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-Write-ExcludeOnly" for resource "School"
            Then the profile response status is 200
             And the response body path "webSite" should have value "https://write-filter.example.com"
             And the response body path "shortNameOfInstitution" should have value "WFS"
