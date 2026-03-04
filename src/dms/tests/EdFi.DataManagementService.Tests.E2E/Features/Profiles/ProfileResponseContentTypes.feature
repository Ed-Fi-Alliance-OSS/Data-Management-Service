Feature: Profile Response Content Types
              As an API client using profile-specific content types
              I want profile GET responses to return profile content types
    So that clients can identify the applied profile in the response

    Rule: Read responses include profile-specific content types

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-IncludeOnly" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade               |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99002001,
                      "nameOfInstitution": "Response ContentType School",
                      "shortNameOfInstitution": "RCTS",
                      "webSite": "https://response-content-type.example.com",
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

        Scenario: 01 GET collection with profile returns profile content type
             When a GET request is made to "/ed-fi/schools?schoolId=99002001" with profile "E2E-Test-School-IncludeOnly" for resource "School"
             Then the profile response status is 200
              And the profile response headers include
                  """
                  {
                      "Content-Type": "application/vnd.ed-fi.school.e2e-test-school-includeonly.readable+json"
                  }
                  """

        Scenario: 02 GET item by id with profile returns profile content type
             When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-IncludeOnly" for resource "School"
             Then the profile response status is 200
              And the profile response headers include
                  """
                  {
                      "Content-Type": "application/vnd.ed-fi.school.e2e-test-school-includeonly.readable+json"
                  }
                  """

        Scenario: 03 GET with explicit profile returns content type including profile and readable suffix
             When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-IncludeOnly" for resource "School"
             Then the profile response status is 200
              And the profile response headers include
                  """
                  {
                      "Content-Type": "application/vnd.ed-fi.school.e2e-test-school-includeonly.readable+json"
                  }
                  """

        Scenario: 04 GET collection with explicit profile returns profile-specific media type
             When a GET request is made to "/ed-fi/schools?schoolId=99002001" with profile "E2E-Test-School-IncludeOnly" for resource "School"
             Then the profile response status is 200
              And the profile response headers include
                  """
                  {
                      "Content-Type": "application/vnd.ed-fi.school.e2e-test-school-includeonly.readable+json"
                  }
                  """

        Scenario: 05 GET item by id with profile Accept header and media-type parameters succeeds
             When a GET request is made to "/ed-fi/schools/{id}" with Accept header "application/vnd.ed-fi.school.e2e-test-school-includeonly.readable+json; charset=utf-8"
             Then the profile response status is 200
              And the profile response headers include
                  """
                  {
                      "Content-Type": "application/vnd.ed-fi.school.e2e-test-school-includeonly.readable+json"
                  }
                  """

        Scenario: 06 GET collection with profile Accept header and media-type parameters succeeds
             When a GET request is made to "/ed-fi/schools?schoolId=99002001" with Accept header "application/vnd.ed-fi.school.e2e-test-school-includeonly.readable+json; q=1.0"
             Then the profile response status is 200
              And the profile response headers include
                  """
                  {
                      "Content-Type": "application/vnd.ed-fi.school.e2e-test-school-includeonly.readable+json"
                  }
                  """
