Feature: Profile PUT Merge Functionality
    As an API client with a write profile that excludes certain fields
    I want excluded fields to be preserved from the existing document during PUT operations
    So that I don't lose data I'm not authorized to modify

    Rule: Collection item non-key properties excluded by profile are preserved during PUT

        Background:
            # Authorize with BOTH profiles: IncludeAll for POST (to save all fields), AddressExcludeNameOfCounty for PUT (to test merge)
            # Note: Only non-key properties can be excluded and preserved. Key properties (like city, postalCode,
            # streetNumberName, addressTypeDescriptor, stateAbbreviationDescriptor) form the collection item identity.
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profiles "E2E-Test-School-Write-IncludeAll, E2E-Test-School-Write-AddressExcludeNameOfCounty" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |
                  | uri://ed-fi.org/AddressTypeDescriptor#Physical                        |
                  | uri://ed-fi.org/StateAbbreviationDescriptor#TX                        |

        Scenario: 01 PUT with collection non-key property exclusion preserves excluded property from existing document
            # First create the school with full address data using IncludeAll profile (nameOfCounty IS saved)
            When a POST request is made to "/ed-fi/schools" with profile "E2E-Test-School-Write-IncludeAll" for resource "School" with body
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
                              "streetNumberName": "123 Main St",
                              "nameOfCounty": "Travis"
                          }
                      ]
                  }
                  """
            Then the profile response status is 201
            # Now PUT with a different nameOfCounty value - but nameOfCounty is excluded by profile, so it should be stripped
            # and the original nameOfCounty value should be preserved.
            # IMPORTANT: All key fields (addressTypeDescriptor, city, postalCode, stateAbbreviationDescriptor, streetNumberName)
            # must remain the same so the item can be matched to the existing item for merging.
            When a PUT request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-Write-AddressExcludeNameOfCounty" for resource "School" with body
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
                              "city": "Austin",
                              "postalCode": "78712",
                              "stateAbbreviationDescriptor": "uri://ed-fi.org/StateAbbreviationDescriptor#TX",
                              "streetNumberName": "123 Main St",
                              "nameOfCounty": "Harris"
                          }
                      ]
                  }
                  """
            Then the profile response status is 204
            # GET the school and verify: nameOfInstitution updated, but nameOfCounty preserved from original
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-Write-AddressExcludeNameOfCounty" for resource "School"
            Then the profile response status is 200
             And the response body path "nameOfInstitution" should have value "PUT Merge Test School Updated"
             And the "addresses" collection item at index 0 should have "streetNumberName" value "123 Main St"
             And the "addresses" collection item at index 0 should have "nameOfCounty" value "Travis"

    Rule: Collection items filtered out by ItemFilter are preserved during PUT

        Background:
            # Authorize with BOTH profiles: IncludeAll for POST (to save all grade levels), GradeLevelFilterPreserve for PUT (to test merge)
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profiles "E2E-Test-School-Write-IncludeAll, E2E-Test-School-Write-GradeLevelFilterPreserve" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |
                  | uri://ed-fi.org/GradeLevelDescriptor#Tenth grade                      |
                  | uri://ed-fi.org/GradeLevelDescriptor#Eleventh grade                   |

        Scenario: 02 PUT with collection item filter preserves filtered-out items from existing document
            # First create the school with all grade levels using IncludeAll profile (all ARE saved)
            When a POST request is made to "/ed-fi/schools" with profile "E2E-Test-School-Write-IncludeAll" for resource "School" with body
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
