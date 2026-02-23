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
                      "Content-Type": "application/json"
                  }
                  """

        Scenario: 02 GET item by id with profile returns profile content type
             When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-IncludeOnly" for resource "School"
             Then the profile response status is 200
              And the profile response headers include
                  """
                  {
                      "Content-Type": "application/json"
                  }
                  """
