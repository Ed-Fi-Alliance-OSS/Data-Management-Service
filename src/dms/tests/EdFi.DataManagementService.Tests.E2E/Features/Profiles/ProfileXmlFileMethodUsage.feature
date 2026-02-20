Feature: Profile XML File Method Usage
    As an API client with XML-defined profiles
    I want method access to respect profile read/write definitions
    So that read-only and write-only profiles enforce correct HTTP methods

    Rule: Read-only profile allows GET and rejects POST

        Scenario: 01 Read-only profile allows GET and rejects POST
            Given a profile "Test-Profile-Resource-ReadOnly" is created from XML file "Profiles/TestXmls/Profiles.xml"
              And the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized without profiles and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade               |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99000911,
                      "nameOfInstitution": "ReadOnly Profile School",
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
              And the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "Test-Profile-Resource-ReadOnly" and namespacePrefixes "uri://ed-fi.org"
             When a GET request is made to "/ed-fi/schools" with profile "Test-Profile-Resource-ReadOnly" for resource "School"
             Then the profile response status is 200
             When a GET request is made to "/ed-fi/schools/{id}" with profile "Test-Profile-Resource-ReadOnly" for resource "School"
             Then the profile response status is 200
             When a PUT request is made to "/ed-fi/schools/{id}" with profile "Test-Profile-Resource-ReadOnly" for resource "School" with body
                  """
                  {
                      "schoolId": 99000911,
                      "nameOfInstitution": "ReadOnly Profile School Put",
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
             Then the profile response status is 405
             When a POST request is made to "/ed-fi/schools" with profile "Test-Profile-Resource-ReadOnly" for resource "School" with body
                  """
                  {
                      "schoolId": 99000912,
                      "nameOfInstitution": "ReadOnly Profile School Post",
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
             Then the profile response status is 405

    Rule: Write-only profile rejects GET

        Scenario: 02 Write-only profile rejects GET
            Given a profile "Test-Profile-Resource-WriteOnly" is created from XML file "Profiles/TestXmls/Profiles.xml"
              And the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized without profiles and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade               |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99000913,
                      "nameOfInstitution": "WriteOnly Profile School",
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
              And the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "Test-Profile-Resource-WriteOnly" and namespacePrefixes "uri://ed-fi.org"
             When a GET request is made to "/ed-fi/schools/{id}" with profile "Test-Profile-Resource-WriteOnly" for resource "School"
             Then the profile response status is 405
             When a POST request is made to "/ed-fi/schools" with profile "Test-Profile-Resource-WriteOnly" for resource "School" with body
                  """
                  {
                      "schoolId": 99000914,
                      "nameOfInstitution": "WriteOnly Profile School Post",
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
             When a PUT request is made to "/ed-fi/schools/{id}" with profile "Test-Profile-Resource-WriteOnly" for resource "School" with body
                  """
                  {
                      "id": "{id}",
                      "schoolId": 99000914,
                      "nameOfInstitution": "WriteOnly Profile School Put",
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
