Feature: Profile PUT Merge Functionality
    As an API client with a write profile that excludes certain fields
    I want excluded fields to be preserved from the existing document during PUT operations
    So that I don't lose data I'm not authorized to modify

    Rule: Collection item properties excluded by profile are preserved during PUT

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-Write-AddressExcludeCity" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |
                  | uri://ed-fi.org/AddressTypeDescriptor#Physical                        |
                  | uri://ed-fi.org/StateAbbreviationDescriptor#TX                        |

        Scenario: 01 PUT with collection property exclusion preserves excluded property from existing document
            # First create the school with full address data (city included)
            When a POST request is made to "/ed-fi/schools" with profile "E2E-Test-School-Write-AddressExcludeCity" for resource "School" with body
                  """
                  {
                      "schoolId": 99000701,
                      "nameOfInstitution": "PUT Merge Test School",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                          }
                      ],
                      "addresses": [
                          {
                              "addressTypeDescriptor": "uri://ed-fi.org/AddressTypeDescriptor#Physical",
                              "city": "Austin",
                              "postalCode": "78712",
                              "stateAbbreviationDescriptor": "uri://ed-fi.org/StateAbbreviationDescriptor#TX",
                              "streetNumberName": "123 Main St"
                          }
                      ]
                  }
                  """
            Then the profile response status is 201
            # Now PUT with a different city value - but city is excluded by profile, so it should be stripped
            # and the original city value should be preserved
            When a PUT request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-Write-AddressExcludeCity" for resource "School" with body
                  """
                  {
                      "id": "{id}",
                      "schoolId": 99000701,
                      "nameOfInstitution": "PUT Merge Test School Updated",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                          }
                      ],
                      "addresses": [
                          {
                              "addressTypeDescriptor": "uri://ed-fi.org/AddressTypeDescriptor#Physical",
                              "city": "Houston",
                              "postalCode": "78712",
                              "stateAbbreviationDescriptor": "uri://ed-fi.org/StateAbbreviationDescriptor#TX",
                              "streetNumberName": "456 Oak Ave"
                          }
                      ]
                  }
                  """
            Then the profile response status is 204
            # GET the school and verify: nameOfInstitution and streetNumberName updated, but city preserved
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-Write-AddressExcludeCity" for resource "School"
            Then the profile response status is 200
             And the response body path "nameOfInstitution" should have value "PUT Merge Test School Updated"
             And the "addresses" collection item at index 0 should have "streetNumberName" value "456 Oak Ave"
             And the "addresses" collection item at index 0 should have "city" value "Austin"

    Rule: Collection items filtered out by ItemFilter are preserved during PUT

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-Write-GradeLevelFilterPreserve" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |
                  | uri://ed-fi.org/GradeLevelDescriptor#Tenth grade                      |
                  | uri://ed-fi.org/GradeLevelDescriptor#Eleventh grade                   |

        Scenario: 02 PUT with collection item filter preserves filtered-out items from existing document
            # First create the school with multiple grade levels
            When a POST request is made to "/ed-fi/schools" with profile "E2E-Test-School-Write-GradeLevelFilterPreserve" for resource "School" with body
                  """
                  {
                      "schoolId": 99000702,
                      "nameOfInstitution": "Grade Filter Preserve Test School",
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
            # Now PUT with only Ninth grade (the only one we can modify via this profile)
            # Tenth and Eleventh grade should be preserved from the existing document
            When a PUT request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-Write-GradeLevelFilterPreserve" for resource "School" with body
                  """
                  {
                      "id": "{id}",
                      "schoolId": 99000702,
                      "nameOfInstitution": "Grade Filter Preserve Test School Updated",
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
            # GET the school and verify all three grade levels are still present
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-Write-GradeLevelFilterPreserve" for resource "School"
            Then the profile response status is 200
             And the response body path "nameOfInstitution" should have value "Grade Filter Preserve Test School Updated"
             And the "gradeLevels" collection should have 3 items
