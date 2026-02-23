Feature: Profile Undefined and Misconfigured Usage
    As an API client using profile headers
    I want undefined and misconfigured profiles to be rejected on read and write
    So that invalid profile usage is surfaced consistently

    Rule: Undefined profile usage returns an authorization/policy failure

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized without profiles and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99001001,
                      "nameOfInstitution": "Undefined Profile Test School",
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
              And the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-IncludeAll" and namespacePrefixes "uri://ed-fi.org"

        Scenario: 01 GET with undefined profile returns 403
            When a GET request is made to "/ed-fi/schools/{id}" with Accept header "application/vnd.ed-fi.school.profile-does-not-exist.readable+json"
            Then the profile response status is 403
             And the response body should have error type "urn:ed-fi:api:security:data-policy:incorrect-usage"
             And the response body status should equal the response status code

        Scenario: 02 POST with undefined profile returns 403
            When a POST request is made to "/ed-fi/schools" with profile "Profile-Does-Not-Exist" for resource "School" with body
                  """
                  {
                      "schoolId": 99001002,
                      "nameOfInstitution": "Undefined Profile Write Test School",
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
             And the response body status should equal the response status code

    Rule: Misconfigured profile usage returns invalid profile usage failures

        Background:
            Given a profile "Test-Profile-With-Unexisting-Resource" is created from XML file "Profiles/TestXmls/InvalidProfiles.xml"
              And a profile "Test-Profile-With-Unexisting-Property" is created from XML file "Profiles/TestXmls/InvalidProfiles.xml"
              And the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized without profiles and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99001003,
                      "nameOfInstitution": "Misconfigured Profile Test School",
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
              And the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-IncludeAll" and namespacePrefixes "uri://ed-fi.org"

        Scenario: 03 GET with misconfigured resource profile returns 406
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "Test-Profile-With-Unexisting-Resource" and namespacePrefixes "uri://ed-fi.org"
            When a GET request is made to "/ed-fi/schools/{id}" with Accept header "application/vnd.ed-fi.school.test-profile-with-unexisting-resource.readable+json"
            Then the profile response status is 406
             And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
             And the response body status should equal the response status code

        Scenario: 04 POST with misconfigured property profile returns 406
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "Test-Profile-With-Unexisting-Property" and namespacePrefixes "uri://ed-fi.org"
            When a POST request is made to "/ed-fi/schools" with profile "Test-Profile-With-Unexisting-Property" for resource "School" with body
                  """
                  {
                      "schoolId": 99001004,
                      "nameOfInstitution": "Misconfigured Profile Write Test School",
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
            Then the profile response status is 415
             And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
             And the response body status should equal the response status code
