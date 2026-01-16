Feature: Profile Collection Item Filtering
    As an API client with a profile that filters collection items
    I want collection items filtered by descriptor values
    So that I only receive authorized data within collections

    Rule: Collection item filter with IncludeOnly mode includes matching items

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-GradeLevelFilter" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |
                  | uri://ed-fi.org/GradeLevelDescriptor#Tenth grade                      |
                  | uri://ed-fi.org/GradeLevelDescriptor#Eleventh grade                   |
                  | uri://ed-fi.org/GradeLevelDescriptor#Twelfth grade                    |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99000401,
                      "nameOfInstitution": "Collection Filter Include Test School",
                      "shortNameOfInstitution": "CFITS",
                      "webSite": "https://collectionfilter.example.com",
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

        Scenario: 01 Collection filter includes only matching items
             When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-GradeLevelFilter" for resource "School"
             Then the profile response status is 200
              And the "gradeLevels" collection should only contain items where "gradeLevelDescriptor" is "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"

        Scenario: 02 Collection filter excludes non-matching items
             When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-GradeLevelFilter" for resource "School"
             Then the profile response status is 200
              And the "gradeLevels" collection should not contain items where "gradeLevelDescriptor" is "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade"
              And the "gradeLevels" collection should not contain items where "gradeLevelDescriptor" is "uri://ed-fi.org/GradeLevelDescriptor#Eleventh grade"

        Scenario: 03 Collection filter reduces item count
             When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-GradeLevelFilter" for resource "School"
             Then the profile response status is 200
              And the "gradeLevels" collection should have 1 item

    Rule: Collection item filter with ExcludeOnly mode excludes matching items

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-GradeLevelExcludeFilter" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |
                  | uri://ed-fi.org/GradeLevelDescriptor#Tenth grade                      |
                  | uri://ed-fi.org/GradeLevelDescriptor#Eleventh grade                   |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99000402,
                      "nameOfInstitution": "Collection Filter Exclude Test School",
                      "shortNameOfInstitution": "CFETS",
                      "webSite": "https://excludefilter.example.com",
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

        Scenario: 04 ExcludeOnly filter excludes matching items
             When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-GradeLevelExcludeFilter" for resource "School"
             Then the profile response status is 200
              And the "gradeLevels" collection should not contain items where "gradeLevelDescriptor" is "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade"

        Scenario: 05 ExcludeOnly filter includes non-matching items
             When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-GradeLevelExcludeFilter" for resource "School"
             Then the profile response status is 200
              And the "gradeLevels" collection should have 2 items

    Rule: Collection filter works on query results

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-GradeLevelFilter" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |
                  | uri://ed-fi.org/GradeLevelDescriptor#Tenth grade                      |
                  | uri://ed-fi.org/GradeLevelDescriptor#Eleventh grade                   |
                  | uri://ed-fi.org/GradeLevelDescriptor#Twelfth grade                    |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99000403,
                      "nameOfInstitution": "Query Collection Filter School 1",
                      "shortNameOfInstitution": "QCFS1",
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

        Scenario: 06 Collection filter applies to all items in query results
             When a GET request is made to "/ed-fi/schools?schoolId=99000403" with profile "E2E-Test-School-GradeLevelFilter" for resource "School"
             Then the profile response status is 200
              And the "gradeLevels" collection should have 1 item
              And the "gradeLevels" collection should only contain items where "gradeLevelDescriptor" is "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"

    Rule: Empty collection after filtering is handled correctly

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-GradeLevelFilter" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/GradeLevelDescriptor#Tenth grade                      |
                  | uri://ed-fi.org/GradeLevelDescriptor#Eleventh grade                   |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99000405,
                      "nameOfInstitution": "Empty Collection Filter School",
                      "shortNameOfInstitution": "ECFS",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade"
                          },
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Eleventh grade"
                          }
                      ]
                  }
                  """

        Scenario: 07 Collection becomes empty when no items match filter
             When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-GradeLevelFilter" for resource "School"
             Then the profile response status is 200
              And the "gradeLevels" collection should have 0 items
