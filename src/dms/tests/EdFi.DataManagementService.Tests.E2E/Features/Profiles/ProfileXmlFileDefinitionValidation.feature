Feature: Profile XML File Definition Validation
    As a system administrator
    I want to create profiles from XML files
    So that profile definitions can be validated when they are used by the API

    Rule: Profiles loaded from XML files behave consistently with invalid definitions

        Scenario: 01 Profile with non-existent resource from XML file is rejected on use
            Given a profile "Test-Profile-With-Unexisting-Resource" is created from XML file "Profiles/TestXmls/InvalidProfiles.xml"
              And the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "Test-Profile-With-Unexisting-Resource" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade               |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99000901,
                      "nameOfInstitution": "XML Invalid Resource School",
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
             When a GET request is made to "/ed-fi/schools/{id}" with Accept header "application/vnd.ed-fi.school.test-profile-with-unexisting-resource.readable+json"
             Then the profile response status is 406
              And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"

        Scenario: 02 Profile with non-existent property from XML file is rejected on use
            Given a profile "Test-Profile-With-Unexisting-Property" is created from XML file "Profiles/TestXmls/InvalidProfiles.xml"
              And the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "Test-Profile-With-Unexisting-Property" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade               |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99000902,
                      "nameOfInstitution": "XML Invalid Property School",
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
             When a GET request is made to "/ed-fi/schools/{id}" with Accept header "application/vnd.ed-fi.school.test-profile-with-unexisting-property.readable+json"
             Then the profile response status is 406
              And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"

        Scenario: 03 Profile with non-existent resource from XML file is rejected on write
            Given a profile "Test-Profile-With-Unexisting-Resource" is created from XML file "Profiles/TestXmls/InvalidProfiles.xml"
              And the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "Test-Profile-With-Unexisting-Resource" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade               |
             When a POST request is made to "/ed-fi/schools" with profile "Test-Profile-With-Unexisting-Resource" for resource "School" with body
                  """
                  {
                      "schoolId": 99000903,
                      "nameOfInstitution": "XML Invalid Resource Write School",
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

        Scenario: 04 Profile with non-existent property from XML file is rejected on write
            Given a profile "Test-Profile-With-Unexisting-Property" is created from XML file "Profiles/TestXmls/InvalidProfiles.xml"
              And the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "Test-Profile-With-Unexisting-Property" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade               |
             When a POST request is made to "/ed-fi/schools" with profile "Test-Profile-With-Unexisting-Property" for resource "School" with body
                  """
                  {
                      "schoolId": 99000904,
                      "nameOfInstitution": "XML Invalid Property Write School",
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
