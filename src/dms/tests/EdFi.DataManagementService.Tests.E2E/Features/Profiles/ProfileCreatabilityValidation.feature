Feature: Profile Creatability Validation
    As an API client with a profile assigned
    I want POST requests to fail with a clear error when the profile excludes required fields
    So that I understand why my resource cannot be created with the assigned profile

    Rule: POST with profile excluding required fields returns data-policy-enforced error

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-Write-ExcludeRequired" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |

        Scenario: 01 POST with profile excluding required field returns 400 with data-policy-enforced error
            When a POST request is made to "/ed-fi/schools" with profile "E2E-Test-School-Write-ExcludeRequired" for resource "School" with body
                  """
                  {
                      "schoolId": 99000701,
                      "nameOfInstitution": "Test School That Should Fail",
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
            Then the profile response status is 400
             And the response body should have error type "urn:ed-fi:api:data-policy-enforced"
             And the response body should have error message "Profile definition for 'E2E-Test-School-Write-ExcludeRequired'"

    Rule: PUT with profile excluding required fields succeeds because existing resource has the value

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-Write-IncludeOnly" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |

        Scenario: 02 PUT with profile excluding required field succeeds
            # First create a school without the profile (using a profile that includes all fields)
            When a POST request is made to "/ed-fi/schools" with profile "E2E-Test-School-Write-IncludeOnly" for resource "School" with body
                  """
                  {
                      "schoolId": 99000702,
                      "nameOfInstitution": "Test School For PUT",
                      "shortNameOfInstitution": "TSFP",
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
            # Now update using a profile that would exclude nameOfInstitution if it were a POST
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-Write-ExcludeRequired" and namespacePrefixes "uri://ed-fi.org"
            When a PUT request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-Write-ExcludeRequired" for resource "School" with body
                  """
                  {
                      "id": "{id}",
                      "schoolId": 99000702,
                      "nameOfInstitution": "Updated School Name",
                      "shortNameOfInstitution": "USN",
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
            Then the profile response status is 200
            # Verify the update succeeded - nameOfInstitution should be stripped but existing value preserved
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-Write-ExcludeRequired" for resource "School"
            Then the profile response status is 200
             And the response body should contain fields "schoolId, nameOfInstitution, shortNameOfInstitution"
