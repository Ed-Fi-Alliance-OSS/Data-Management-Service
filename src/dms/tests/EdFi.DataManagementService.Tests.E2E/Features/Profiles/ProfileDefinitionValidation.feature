Feature: Profile Definition Validation
              As a system administrator
              I want profiles with invalid definitions to be rejected at load time
    So that API clients receive clear error messages when attempting to use malformed profiles

    Rule: Profiles with non-existent resource names are rejected

        Scenario: 01 Profile referencing non-existent resource is not loaded
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Invalid-NonExistentResource" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade               |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99000801,
                      "nameOfInstitution": "Test School for Invalid Profile",
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
             When a GET request is made to "/ed-fi/schools/{id}" with Accept header "application/vnd.ed-fi.school.e2e-invalid-nonexistentresource.readable+json"
             Then the profile response status is 406
              And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
    Rule: Profiles with non-existent properties in IncludeOnly mode are rejected

        Scenario: 02 Profile with invalid IncludeOnly property is not loaded
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Invalid-IncludeOnlyProperty" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade               |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99000802,
                      "nameOfInstitution": "Test School for Invalid Property",
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
             When a GET request is made to "/ed-fi/schools/{id}" with Accept header "application/vnd.ed-fi.school.e2e-invalid-includeonlyproperty.readable+json"
             Then the profile response status is 406
              And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
    Rule: Profiles with non-existent objects in IncludeOnly mode are rejected

        Scenario: 03 Profile with invalid IncludeOnly nested object is not loaded
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Invalid-IncludeOnlyObject" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade               |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99000803,
                      "nameOfInstitution": "Test School for Invalid Object",
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
             When a GET request is made to "/ed-fi/schools/{id}" with Accept header "application/vnd.ed-fi.school.e2e-invalid-includeonlyobject.readable+json"
             Then the profile response status is 406
              And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
    Rule: Profiles with non-existent collections in IncludeOnly mode are rejected

        Scenario: 04 Profile with invalid IncludeOnly collection is not loaded
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Invalid-IncludeOnlyCollection" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade               |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99000804,
                      "nameOfInstitution": "Test School for Invalid Collection",
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
             When a GET request is made to "/ed-fi/schools/{id}" with Accept header "application/vnd.ed-fi.school.e2e-invalid-includeonlycollection.readable+json"
             Then the profile response status is 406
              And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
    Rule: Profiles with non-existent extension properties in IncludeOnly mode are rejected

        Scenario: 05 Profile with invalid IncludeOnly extension property is not loaded
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Invalid-ExtensionProperty" and namespacePrefixes "uri://ed-fi.org, uri://sample.ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade               |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99000805,
                      "nameOfInstitution": "Test School for Invalid Extension",
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
             When a GET request is made to "/ed-fi/schools/{id}" with Accept header "application/vnd.ed-fi.school.e2e-invalid-extensionproperty.readable+json"
             Then the profile response status is 406
              And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
    Rule: Profiles with ExcludeOnly excluding identity members produce warnings but are loaded

        Scenario: 06 Profile with ExcludeOnly excluding identity member is loaded with warning
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Warning-ExcludeIdentity" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade               |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99000806,
                      "nameOfInstitution": "Test School for Identity Exclusion Warning",
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
             When a GET request is made to "/ed-fi/schools/{id}" with Accept header "application/vnd.ed-fi.school.e2e-warning-excludeidentity.readable+json"
             Then the profile response status is 200
              And the response body should contain fields "schoolId"
    Rule: Profiles with nested invalid properties in collections are rejected

        Scenario: 07 Profile with invalid property in nested collection is not loaded
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Invalid-NestedCollectionProperty" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade               |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99000807,
                      "nameOfInstitution": "Test School for Invalid Nested Property",
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
             When a GET request is made to "/ed-fi/schools/{id}" with Accept header "application/vnd.ed-fi.school.e2e-invalid-nestedcollectionproperty.readable+json"
             Then the profile response status is 406
              And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
