Feature: Profile Response Filtering
    As an API client with a profile assigned
    I want my GET responses to be filtered according to my profile
    So that I only receive the data fields I am authorized to access

    Rule: IncludeOnly mode returns only specified fields plus identity fields

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-IncludeOnly" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99000101,
                      "nameOfInstitution": "Profile Test School IncludeOnly",
                      "shortNameOfInstitution": "PTSIO",
                      "webSite": "https://profiletest.example.com",
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

        @e2e-ci-shard-2
        Scenario: 01 GET by ID with IncludeOnly profile returns only included fields
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-IncludeOnly" for resource "School"
            Then the profile response status is 200
             And the response body should only contain fields "id, schoolId, nameOfInstitution, webSite"
             And the response body should not contain fields "shortNameOfInstitution, educationOrganizationCategories, gradeLevels"

        @e2e-ci-shard-2
        Scenario: 02 GET by ID with IncludeOnly profile preserves identity fields
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-IncludeOnly" for resource "School"
            Then the profile response status is 200
             And the response body should contain fields "id, schoolId"

    Rule: ExcludeOnly mode returns all fields except those explicitly excluded

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-ExcludeOnly" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99000102,
                      "nameOfInstitution": "Profile Test School ExcludeOnly",
                      "shortNameOfInstitution": "PTSEO",
                      "webSite": "https://excludetest.example.com",
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

        @e2e-ci-shard-2
        Scenario: 03 GET by ID with ExcludeOnly profile excludes specified fields
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-ExcludeOnly" for resource "School"
            Then the profile response status is 200
             And the response body should not contain fields "shortNameOfInstitution, webSite"
             And the response body should contain fields "id, schoolId, nameOfInstitution"

        @e2e-ci-shard-2
        Scenario: 04 GET by ID with ExcludeOnly profile includes non-excluded fields
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-ExcludeOnly" for resource "School"
            Then the profile response status is 200
             And the response body should contain fields "educationOrganizationCategories, gradeLevels"

    Rule: IncludeAll mode returns all fields without filtering

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-IncludeAll" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99000103,
                      "nameOfInstitution": "Profile Test School IncludeAll",
                      "shortNameOfInstitution": "PTSIA",
                      "webSite": "https://includealltest.example.com",
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

        @e2e-ci-shard-2
        Scenario: 05 GET by ID with IncludeAll profile returns all fields
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-IncludeAll" for resource "School"
            Then the profile response status is 200
             And the response body should contain fields "id, schoolId, nameOfInstitution, shortNameOfInstitution, webSite, educationOrganizationCategories, gradeLevels"

    Rule: Profile filtering works on query results (arrays)

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-IncludeOnly" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99000104,
                      "nameOfInstitution": "Query Test School 1",
                      "shortNameOfInstitution": "QTS1",
                      "webSite": "https://query1.example.com",
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
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99000105,
                      "nameOfInstitution": "Query Test School 2",
                      "shortNameOfInstitution": "QTS2",
                      "webSite": "https://query2.example.com",
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

        @e2e-ci-shard-2
        Scenario: 06 Query with IncludeOnly profile filters all items in array
            When a GET request is made to "/ed-fi/schools?schoolId=99000104" with profile "E2E-Test-School-IncludeOnly" for resource "School"
            Then the profile response status is 200
             And the response body should only contain fields "id, schoolId, nameOfInstitution, webSite"
             And the response body should not contain fields "shortNameOfInstitution"

    Rule: Profile filtering serves a profile-sensitive etag distinct from the full-resource etag

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-IncludeOnly" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99000106,
                      "nameOfInstitution": "Profile ETag Test School",
                      "shortNameOfInstitution": "PETS",
                      "webSite": "https://profile-etag.example.com",
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

        @e2e-ci-shard-2
        Scenario: 07 Profiled GET and query serve a profile-sensitive etag distinct from the full-resource etag
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized without profiles and namespacePrefixes "uri://ed-fi.org"
            When a GET request is made to "/ed-fi/schools/{id}"
            Then it should respond with 200
             And the response body path "_etag" is stored as variable "fullSchoolEtag"
             And the response body path "shortNameOfInstitution" should have value "PETS"
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-IncludeOnly" and namespacePrefixes "uri://ed-fi.org"
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-IncludeOnly" for resource "School"
            Then the profile response status is 200
             And the response body should only contain fields "id, schoolId, nameOfInstitution, webSite"
             And the response body should not contain fields "shortNameOfInstitution, educationOrganizationCategories, gradeLevels"
             And the response body path "_etag" is stored as variable "profiledSchoolEtag"
             And the response body path "_etag" should not equal variable "fullSchoolEtag"
            When a GET request is made to "/ed-fi/schools?schoolId=99000106" with profile "E2E-Test-School-IncludeOnly" for resource "School"
            Then the profile response status is 200
             And the response body should only contain fields "id, schoolId, nameOfInstitution, webSite"
             And the response body should not contain fields "shortNameOfInstitution, educationOrganizationCategories, gradeLevels"
             And the response body path "0._etag" should equal variable "profiledSchoolEtag"
             And the response body path "0._etag" should not equal variable "fullSchoolEtag"

        @e2e-ci-shard-2
        Scenario: 08 Profiled PUT succeeds with a profiled If-Match and returns a profile-sensitive etag distinct from the full-resource etag
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-IncludeOnly" for resource "School"
            Then the profile response status is 200
             And the response body path "_etag" is stored as variable "profiledSchoolEtag"
            When a PUT request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-IncludeOnly" for resource "School" and if-match variable "profiledSchoolEtag" with body
                  """
                  {
                      "id": "{id}",
                      "schoolId": 99000106,
                      "nameOfInstitution": "Profile ETag Test School Updated",
                      "shortNameOfInstitution": "PETS-UPD",
                      "webSite": "https://profile-etag-updated.example.com",
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
            When the response header "etag" is stored as variable "profiledWriteEtag"
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized without profiles and namespacePrefixes "uri://ed-fi.org"
            When a GET request is made to "/ed-fi/schools/{id}"
            Then it should respond with 200
             And the response body path "_etag" should not equal variable "profiledWriteEtag"
             And the response body path "shortNameOfInstitution" should have value "PETS-UPD"

        @e2e-ci-shard-2
        Scenario: 09 Hidden field changes invalidate a stale profiled etag
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-IncludeOnly" for resource "School"
            Then the profile response status is 200
             And the response body path "_etag" is stored as variable "staleProfiledEtag"
             And the response body should not contain fields "shortNameOfInstitution"
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized without profiles and namespacePrefixes "uri://ed-fi.org"
            When a PUT request is made to "/ed-fi/schools/{id}" with
                  """
                  {
                      "id": "{id}",
                      "schoolId": 99000106,
                      "nameOfInstitution": "Profile ETag Test School",
                      "shortNameOfInstitution": "PETS-HIDDEN",
                      "webSite": "https://profile-etag.example.com",
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
            Then it should respond with 204
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-IncludeOnly" and namespacePrefixes "uri://ed-fi.org"
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-IncludeOnly" for resource "School"
            Then the profile response status is 200
             And the response body should only contain fields "id, schoolId, nameOfInstitution, webSite"
             And the response body should not contain fields "shortNameOfInstitution, educationOrganizationCategories, gradeLevels"
             And the response body path "nameOfInstitution" should have value "Profile ETag Test School"
             And the response body path "_etag" should not equal variable "staleProfiledEtag"
            When a PUT request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-IncludeOnly" for resource "School" and if-match variable "staleProfiledEtag" with body
                  """
                  {
                      "id": "{id}",
                      "schoolId": 99000106,
                      "nameOfInstitution": "Profile ETag Stale Write",
                      "shortNameOfInstitution": "PETS-HIDDEN",
                      "webSite": "https://profile-etag-stale.example.com",
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
            Then the profile response status is 412
             And the response body should have error type "urn:ed-fi:api:optimistic-lock-failed"
             And the response body should have error message "etag value does not match"
