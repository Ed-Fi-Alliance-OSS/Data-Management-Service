Feature: Profile Definition Advanced Filtering
    As an API client using profile definitions
    I want include-all, include/exclude property rules, and collection filters to be honored
    So that profile definition behavior matches parity expectations

    Rule: Profile definitions can include all members of a resource or collection

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-IncludeAll" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade               |
                  | uri://ed-fi.org/AddressTypeDescriptor#Physical                 |
                  | uri://ed-fi.org/StateAbbreviationDescriptor#TX                 |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99005001,
                      "nameOfInstitution": "IncludeAll Profile School",
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
                              "stateAbbreviationDescriptor": "uri://ed-fi.org/StateAbbreviationDescriptor#TX",
                              "postalCode": "78701",
                              "streetNumberName": "100 Main St",
                              "nameOfCounty": "Travis"
                          }
                      ]
                  }
                  """

        Scenario: 01 IncludeAll profile returns all populated fields and child collection members
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-IncludeAll" for resource "School"
            Then the profile response status is 200
             And the response body should contain fields "id, schoolId, nameOfInstitution, educationOrganizationCategories, gradeLevels, addresses"
             And the "addresses" collection item at index 0 should have "nameOfCounty" value "Travis"

    Rule: Profile definitions can include and exclude certain properties on resources

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-IncludeAll" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade               |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99005002,
                      "nameOfInstitution": "Property Filter Profile School",
                      "shortNameOfInstitution": "PFPS",
                      "webSite": "https://property-filter.example.com",
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

        Scenario: 02 IncludeOnly profile returns only included resource properties
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-IncludeOnly" and namespacePrefixes "uri://ed-fi.org"
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-IncludeOnly" for resource "School"
            Then the profile response status is 200
             And the response body should contain fields "id, schoolId, nameOfInstitution, webSite"
             And the response body should not contain fields "shortNameOfInstitution"

        Scenario: 03 ExcludeOnly profile excludes configured resource properties
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-ExcludeOnly" and namespacePrefixes "uri://ed-fi.org"
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-ExcludeOnly" for resource "School"
            Then the profile response status is 200
             And the response body should not contain fields "shortNameOfInstitution, webSite"
             And the response body should contain fields "id, schoolId, nameOfInstitution"

    Rule: Profile definitions can include and exclude specific items in child collections

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-IncludeAll" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade               |
                  | uri://ed-fi.org/GradeLevelDescriptor#Tenth grade               |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99005003,
                      "nameOfInstitution": "Collection Filter Profile School",
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

        Scenario: 04 IncludeOnly collection filter keeps only configured items
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-GradeLevelFilter" and namespacePrefixes "uri://ed-fi.org"
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-GradeLevelFilter" for resource "School"
            Then the profile response status is 200
             And the "gradeLevels" collection should have 1 item
             And the "gradeLevels" collection item at index 0 should have "gradeLevelDescriptor" value "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"

        Scenario: 05 ExcludeOnly collection filter excludes configured items
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-GradeLevelExcludeFilter" and namespacePrefixes "uri://ed-fi.org"
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-GradeLevelExcludeFilter" for resource "School"
            Then the profile response status is 200
             And the "gradeLevels" collection should have 1 item
             And the "gradeLevels" collection item at index 0 should have "gradeLevelDescriptor" value "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
