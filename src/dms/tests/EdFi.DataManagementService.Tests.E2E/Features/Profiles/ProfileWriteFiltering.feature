Feature: Profile Write Filtering
    As an API client with a profile assigned
    I want my POST/PUT requests to have excluded fields silently stripped
    So that only allowed data fields are persisted according to my profile

    Rule: IncludeOnly WriteContentType silently strips fields not in the allowed list

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-Write-IncludeOnly" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |

        Scenario: 01 POST with IncludeOnly write profile silently strips excluded fields
            When a POST request is made to "/ed-fi/schools" with profile "E2E-Test-School-Write-IncludeOnly" for resource "School" with body
                  """
                  {
                      "schoolId": 99000601,
                      "nameOfInstitution": "Write Filter Test School IncludeOnly",
                      "shortNameOfInstitution": "WFTSIO",
                      "webSite": "https://should-be-stripped.example.com",
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
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-Write-IncludeOnly" for resource "School"
            Then the profile response status is 200
             And the response body should contain fields "schoolId, nameOfInstitution, shortNameOfInstitution"
             And the response body should not contain fields "webSite"

        Scenario: 02 POST with IncludeOnly write profile preserves identity and allowed fields
            When a POST request is made to "/ed-fi/schools" with profile "E2E-Test-School-Write-IncludeOnly" for resource "School" with body
                  """
                  {
                      "schoolId": 99000602,
                      "nameOfInstitution": "Write Filter Preserve Test",
                      "shortNameOfInstitution": "WFPT",
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
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-Write-IncludeOnly" for resource "School"
            Then the profile response status is 200
             And the response body should contain fields "id, schoolId, nameOfInstitution, shortNameOfInstitution"

    Rule: ExcludeOnly WriteContentType silently strips fields in the excluded list

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-Write-ExcludeOnly" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |

        Scenario: 03 POST with ExcludeOnly write profile silently strips excluded fields
            When a POST request is made to "/ed-fi/schools" with profile "E2E-Test-School-Write-ExcludeOnly" for resource "School" with body
                  """
                  {
                      "schoolId": 99000603,
                      "nameOfInstitution": "Write Filter Test School ExcludeOnly",
                      "shortNameOfInstitution": "WFTSEO",
                      "webSite": "https://should-be-stripped.example.com",
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
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-Write-ExcludeOnly" for resource "School"
            Then the profile response status is 200
             And the response body should contain fields "schoolId, nameOfInstitution"
             And the response body should not contain fields "webSite, shortNameOfInstitution"

        Scenario: 04 POST with ExcludeOnly write profile preserves non-excluded fields
            When a POST request is made to "/ed-fi/schools" with profile "E2E-Test-School-Write-ExcludeOnly" for resource "School" with body
                  """
                  {
                      "schoolId": 99000604,
                      "nameOfInstitution": "Write Filter Preserve Non-Excluded",
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
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-Write-ExcludeOnly" for resource "School"
            Then the profile response status is 200
             And the response body should contain fields "nameOfInstitution, educationOrganizationCategories, gradeLevels"

    Rule: Collection item filter on WriteContentType silently strips non-matching items

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-Write-GradeLevelFilter" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |
                  | uri://ed-fi.org/GradeLevelDescriptor#Tenth grade                      |
                  | uri://ed-fi.org/GradeLevelDescriptor#Eleventh grade                   |

        Scenario: 05 POST with collection item filter silently strips non-matching items
            When a POST request is made to "/ed-fi/schools" with profile "E2E-Test-School-Write-GradeLevelFilter" for resource "School" with body
                  """
                  {
                      "schoolId": 99000605,
                      "nameOfInstitution": "Write Collection Filter Test School",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                          },
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade"
                          },
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Eleventh grade"
                          }
                      ]
                  }
                  """
            Then the profile response status is 201
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-Write-GradeLevelFilter" for resource "School"
            Then the profile response status is 200
             And the "gradeLevels" collection should have 1 item
             And the "gradeLevels" collection should only contain items where "gradeLevelDescriptor" is "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"

        Scenario: 06 POST with collection item filter excludes non-matching items from persisted data
            When a POST request is made to "/ed-fi/schools" with profile "E2E-Test-School-Write-GradeLevelFilter" for resource "School" with body
                  """
                  {
                      "schoolId": 99000606,
                      "nameOfInstitution": "Write Collection Exclude Test School",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                          },
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade"
                          }
                      ]
                  }
                  """
            Then the profile response status is 201
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-Write-GradeLevelFilter" for resource "School"
            Then the profile response status is 200
             And the "gradeLevels" collection should not contain items where "gradeLevelDescriptor" is "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade"
