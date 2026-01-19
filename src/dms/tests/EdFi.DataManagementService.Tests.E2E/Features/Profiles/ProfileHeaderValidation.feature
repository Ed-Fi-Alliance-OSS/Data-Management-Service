Feature: Profile Header Validation
    As an API client
    I want clear error messages when my profile header is invalid
    So that I can correct my requests

    Rule: Malformed profile headers return appropriate errors

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-IncludeOnly" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99000301,
                      "nameOfInstitution": "Header Validation Test School",
                      "shortNameOfInstitution": "HVTS",
                      "webSite": "https://headertest.example.com",
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

        Scenario: 01 Malformed profile header returns 400
            When a GET request is made to "/ed-fi/schools/{id}" with Accept header "application/vnd.ed-fi.invalid"
            Then the profile response status is 400
             And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
             And the response body should have error message "The format of the profile-based content type header was invalid"

        Scenario: 02 Profile header with wrong resource name returns 400
            When a GET request is made to "/ed-fi/schools/{id}" with Accept header "application/vnd.ed-fi.student.e2e-test-school-includeonly.readable+json"
            Then the profile response status is 400
             And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
             And the response body should have error message "does not match the requested resource"

        Scenario: 03 Profile header with writable usage for GET returns 400
            When a GET request is made to "/ed-fi/schools/{id}" with Accept header "application/vnd.ed-fi.school.e2e-test-school-includeonly.writable+json"
            Then the profile response status is 400
             And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
             And the response body should have error message "writable cannot be used with GET requests"

    Rule: Profile header on app with no profiles returns 406 Not Acceptable

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized without profiles and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99000302,
                      "nameOfInstitution": "No Profile Assignment Test School",
                      "shortNameOfInstitution": "NPATS",
                      "webSite": "https://noprofileassign.example.com",
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

        # When app has no profiles, using any profile header returns 406 (Not Acceptable)
        Scenario: 04 Using profile header on app without profiles returns 406
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-IncludeOnly" for resource "School"
            Then the profile response status is 406
             And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"

    Rule: Non-existent profile returns 403

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-IncludeOnly" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99000303,
                      "nameOfInstitution": "Nonexistent Profile Test School",
                      "shortNameOfInstitution": "NEPTS",
                      "webSite": "https://nonexistent.example.com",
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

        Scenario: 05 Using nonexistent profile returns 403
            When a GET request is made to "/ed-fi/schools/{id}" with Accept header "application/vnd.ed-fi.school.nonexistent-profile.readable+json"
            Then the profile response status is 403
             And the response body should have error type "urn:ed-fi:api:security:data-policy:incorrect-usage"

    Rule: Valid profile header succeeds

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-IncludeOnly" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99000304,
                      "nameOfInstitution": "Resource Coverage Test School",
                      "shortNameOfInstitution": "RCTS",
                      "webSite": "https://coverage.example.com",
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

        Scenario: 06 Valid profile header for correct resource succeeds
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-IncludeOnly" for resource "School"
            Then the profile response status is 200
