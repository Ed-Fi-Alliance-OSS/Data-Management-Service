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

        Scenario: 01 POST with profile excluding required scalar field returns 400 with data-policy-enforced error
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

    Rule: POST with IncludeOnly profile omitting required field returns data-policy-enforced error

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-Write-IncludeOnlyMissingRequired" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |

        Scenario: 02 POST with IncludeOnly profile omitting required field returns 400 with data-policy-enforced error
            When a POST request is made to "/ed-fi/schools" with profile "E2E-Test-School-Write-IncludeOnlyMissingRequired" for resource "School" with body
                  """
                  {
                      "schoolId": 99000704,
                      "nameOfInstitution": "Test School IncludeOnly Missing Required",
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
             And the response body should have error message "Profile definition for 'E2E-Test-School-Write-IncludeOnlyMissingRequired'"

    Rule: POST with profile excluding required collection returns data-policy-enforced error

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-Write-ExcludeRequiredCollection" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |

        Scenario: 03 POST with profile excluding required collection returns 400 with data-policy-enforced error
            When a POST request is made to "/ed-fi/schools" with profile "E2E-Test-School-Write-ExcludeRequiredCollection" for resource "School" with body
                  """
                  {
                      "schoolId": 99000703,
                      "nameOfInstitution": "Test School With Excluded Collection",
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
             And the response body should have error message "Profile definition for 'E2E-Test-School-Write-ExcludeRequiredCollection'"

    Rule: PUT with profile excluding required fields succeeds because existing resource has the value

        Background:
            # Authorize with BOTH profiles so the same DMS instance is used throughout
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profiles "E2E-Test-School-Write-IncludeOnly, E2E-Test-School-Write-ExcludeRequired" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |

        Scenario: 04 PUT with profile excluding required field succeeds
            # First create a school using a profile that includes nameOfInstitution
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
            # Now update using a profile that excludes nameOfInstitution - it should still succeed
            # because PUT merges with the existing document, preserving the excluded field's value
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
            Then the profile response status is 204
            # Verify the update succeeded - nameOfInstitution should be preserved from the original document
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-Write-ExcludeRequired" for resource "School"
            Then the profile response status is 200
             And the response body should contain fields "schoolId, nameOfInstitution, shortNameOfInstitution"

    Rule: POST with IncludeAll profile succeeds because no required fields are excluded

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-Write-IncludeAll" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |

        Scenario: 05 POST with IncludeAll profile succeeds
            When a POST request is made to "/ed-fi/schools" with profile "E2E-Test-School-Write-IncludeAll" for resource "School" with body
                  """
                  {
                      "schoolId": 99000705,
                      "nameOfInstitution": "Test School IncludeAll Success",
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

    Rule: POST with CollectionRule on required collection succeeds because collection is filtered not excluded

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-Write-RequiredCollectionWithRule" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |
                  | uri://ed-fi.org/GradeLevelDescriptor#Tenth grade                      |

        Scenario: 06 POST with CollectionRule on required collection succeeds and filters collection items
            When a POST request is made to "/ed-fi/schools" with profile "E2E-Test-School-Write-RequiredCollectionWithRule" for resource "School" with body
                  """
                  {
                      "schoolId": 99000706,
                      "nameOfInstitution": "Test School CollectionRule Success",
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
            # Verify the collection was filtered (only Ninth grade should remain)
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-Write-RequiredCollectionWithRule" for resource "School"
            Then the profile response status is 200
             And the "gradeLevels" collection should have 1 item
             And the "gradeLevels" collection should only contain items where "gradeLevelDescriptor" is "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
