Feature: Profile Extension Filtering
    As an API client with a profile assigned
    I want my GET responses to filter extension data according to my profile
    So that I only receive the extension fields I am authorized to access

    Rule: Extension IncludeOnly mode returns only specified extension properties

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-Extension-IncludeOnly" and namespacePrefixes "uri://ed-fi.org, uri://sample.ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                                    |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution |
                  | uri://ed-fi.org/GradeLevelDescriptor#Postsecondary                                 |
                  | uri://ed-fi.org/CTEProgramServiceDescriptor#Architecture and Construction          |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99000301,
                      "nameOfInstitution": "Extension Profile Test School IncludeOnly",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"
                          }
                      ],
                      "_ext": {
                          "sample": {
                              "isExemplary": true,
                              "cteProgramService": {
                                  "cteProgramServiceDescriptor": "uri://ed-fi.org/CTEProgramServiceDescriptor#Architecture and Construction"
                              }
                          }
                      }
                  }
                  """

        Scenario: 01 Extension IncludeOnly profile returns only specified extension properties
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-Extension-IncludeOnly" for resource "School"
            Then the profile response status is 200
             And the response body should contain path "_ext.sample.isExemplary"
             And the response body should not contain path "_ext.sample.cteProgramService"

        Scenario: 02 Extension IncludeOnly profile preserves the isExemplary value
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-Extension-IncludeOnly" for resource "School"
            Then the profile response status is 200
             And the response body path "_ext.sample.isExemplary" should have value "true"

    Rule: Extension ExcludeOnly mode returns all extension properties except those explicitly excluded

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-Extension-ExcludeOnly" and namespacePrefixes "uri://ed-fi.org, uri://sample.ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                                    |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution |
                  | uri://ed-fi.org/GradeLevelDescriptor#Postsecondary                                 |
                  | uri://ed-fi.org/CTEProgramServiceDescriptor#Architecture and Construction          |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99000302,
                      "nameOfInstitution": "Extension Profile Test School ExcludeOnly",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"
                          }
                      ],
                      "_ext": {
                          "sample": {
                              "isExemplary": false,
                              "cteProgramService": {
                                  "cteProgramServiceDescriptor": "uri://ed-fi.org/CTEProgramServiceDescriptor#Architecture and Construction"
                              }
                          }
                      }
                  }
                  """

        Scenario: 03 Extension ExcludeOnly profile excludes specified extension properties
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-Extension-ExcludeOnly" for resource "School"
            Then the profile response status is 200
             And the response body should not contain path "_ext.sample.isExemplary"

        Scenario: 04 Extension ExcludeOnly profile includes non-excluded extension properties
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-Extension-ExcludeOnly" for resource "School"
            Then the profile response status is 200
             And the response body should contain path "_ext.sample.cteProgramService"
             And the response body should contain path "_ext.sample.cteProgramService.cteProgramServiceDescriptor"

    Rule: Parent IncludeOnly without extension rule excludes extensions entirely

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-IncludeOnly-NoExtensionRule" and namespacePrefixes "uri://ed-fi.org, uri://sample.ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                                    |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution |
                  | uri://ed-fi.org/GradeLevelDescriptor#Postsecondary                                 |
                  | uri://ed-fi.org/CTEProgramServiceDescriptor#Architecture and Construction          |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99000303,
                      "nameOfInstitution": "Extension Profile Test School Parent IncludeOnly",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"
                          }
                      ],
                      "_ext": {
                          "sample": {
                              "isExemplary": true,
                              "cteProgramService": {
                                  "cteProgramServiceDescriptor": "uri://ed-fi.org/CTEProgramServiceDescriptor#Architecture and Construction"
                              }
                          }
                      }
                  }
                  """

        Scenario: 05 Parent IncludeOnly profile without extension rule excludes _ext entirely
             When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-IncludeOnly-NoExtensionRule" for resource "School"
             Then the profile response status is 200
              And the response body should not contain fields "_ext"
              And the response body should contain fields "nameOfInstitution, educationOrganizationCategories, gradeLevels"

    Rule: Parent ExcludeOnly without extension rule includes extensions

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-ExcludeOnly-NoExtensionRule" and namespacePrefixes "uri://ed-fi.org, uri://sample.ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                                    |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution |
                  | uri://ed-fi.org/GradeLevelDescriptor#Postsecondary                                 |
                  | uri://ed-fi.org/CTEProgramServiceDescriptor#Architecture and Construction          |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99000304,
                      "nameOfInstitution": "Extension Profile Test School Parent ExcludeOnly",
                      "shortNameOfInstitution": "EPTSPEO",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"
                          }
                      ],
                      "_ext": {
                          "sample": {
                              "isExemplary": true,
                              "cteProgramService": {
                                  "cteProgramServiceDescriptor": "uri://ed-fi.org/CTEProgramServiceDescriptor#Architecture and Construction"
                              }
                          }
                      }
                  }
                  """

        Scenario: 06 Parent ExcludeOnly profile without extension rule includes _ext
             When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-ExcludeOnly-NoExtensionRule" for resource "School"
             Then the profile response status is 200
              And the response body should contain fields "_ext"
              And the response body should contain path "_ext.sample.isExemplary"
              And the response body should contain path "_ext.sample.cteProgramService"

        Scenario: 07 Parent ExcludeOnly profile excludes specified core properties but keeps extensions
             When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-ExcludeOnly-NoExtensionRule" for resource "School"
             Then the profile response status is 200
              And the response body should not contain fields "shortNameOfInstitution"
              And the response body should contain fields "nameOfInstitution, _ext"

    Rule: Extension write profiles preserve or filter extension updates as configured

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized without profiles and namespacePrefixes "uri://ed-fi.org, uri://sample.ed-fi.org"
              And a profile test POST request is made to "/ed-fi/staffs" with
                  """
                  {
                      "staffUniqueId": "99000308",
                      "firstName": "John",
                      "lastSurname": "Doe",
                      "_ext": {
                          "sample": {
                              "firstPetOwnedDate": "2000-01-01",
                              "pets": [
                                  {
                                      "petName": "Rex",
                                      "isFixed": false
                                  }
                              ],
                              "petPreference": {
                                  "minimumWeight": 20,
                                  "maximumWeight": 35
                              }
                          }
                      }
                  }
                  """

        @relational-backend
        Scenario: 08 Extension Not Included write profile is currently unsupported for write content type
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "Sample-Staff-Extension-Not-Included" and namespacePrefixes "uri://ed-fi.org, uri://sample.ed-fi.org"
            When a PUT request is made to "/ed-fi/staffs/{id}" with profile "Sample-Staff-Extension-Not-Included" for resource "Staff" with body
                  """
                  {
                      "id": "{id}",
                      "staffUniqueId": "99000308",
                      "firstName": "John",
                      "lastSurname": "Doe",
                      "_ext": {
                          "sample": {
                              "petPreference": {
                                  "maximumWeight": 200,
                                  "minimumWeight": 100
                              },
                              "pets": [
                                  {
                                      "petName": "Rex",
                                      "isFixed": true
                                  }
                              ]
                          }
                      }
                  }
                  """
            Then the profile response status is 415
             And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
             And the response body should have error message "is not supported by this host"

        Scenario: 09 Extension Excluded write profile applies write payload to extension members
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "Sample-Staff-Extension-Excluded" and namespacePrefixes "uri://ed-fi.org, uri://sample.ed-fi.org"
            When a PUT request is made to "/ed-fi/staffs/{id}" with profile "Sample-Staff-Extension-Excluded" for resource "Staff" with body
                  """
                  {
                      "id": "{id}",
                      "staffUniqueId": "99000308",
                      "firstName": "John",
                      "lastSurname": "Doe",
                      "_ext": {
                          "sample": {
                              "petPreference": {
                                  "maximumWeight": 135,
                                  "minimumWeight": 120
                              },
                              "pets": [
                                  {
                                      "petName": "Rex",
                                      "isFixed": true
                                  }
                              ]
                          }
                      }
                  }
                  """
            Then the profile response status is 204
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized without profiles and namespacePrefixes "uri://ed-fi.org, uri://sample.ed-fi.org"
            When a GET request is made to "/ed-fi/staffs/{id}" without profile header
            Then the profile response status is 200
             And the response body should not contain path "_ext.sample.firstPetOwnedDate"
             And the response body path "_ext.sample.petPreference.minimumWeight" should have value "120"
             And the response body path "_ext.sample.petPreference.maximumWeight" should have value "135"
             And the response body path "_ext.sample.pets.0.isFixed" should have value "true"

        @relational-backend
        Scenario: 10 Extension Include-Only-Deeply write profile is currently unsupported for write content type
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "Sample-Staff-Extension-Include-Only-Deeply" and namespacePrefixes "uri://ed-fi.org, uri://sample.ed-fi.org"
            When a PUT request is made to "/ed-fi/staffs/{id}" with profile "Sample-Staff-Extension-Include-Only-Deeply" for resource "Staff" with body
                  """
                  {
                      "id": "{id}",
                      "staffUniqueId": "99000308",
                      "firstName": "John",
                      "lastSurname": "Doe",
                      "_ext": {
                          "sample": {
                              "firstPetOwnedDate": "2020-02-02",
                              "petPreference": {
                                  "maximumWeight": 200,
                                  "minimumWeight": 100
                              },
                              "pets": [
                                  {
                                      "petName": "Rex",
                                      "isFixed": true
                                  }
                              ]
                          }
                      }
                  }
                  """
            Then the profile response status is 415
             And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
             And the response body should have error message "is not supported by this host"

        @relational-backend
        Scenario: 11 Extension Exclude-Only-Deeply write profile is currently unsupported for write content type
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "Sample-Staff-Extension-Exclude-Only-Deeply" and namespacePrefixes "uri://ed-fi.org, uri://sample.ed-fi.org"
            When a PUT request is made to "/ed-fi/staffs/{id}" with profile "Sample-Staff-Extension-Exclude-Only-Deeply" for resource "Staff" with body
                  """
                  {
                      "id": "{id}",
                      "staffUniqueId": "99000308",
                      "firstName": "John",
                      "lastSurname": "Doe",
                      "_ext": {
                          "sample": {
                              "firstPetOwnedDate": "2020-02-02",
                              "petPreference": {
                                  "maximumWeight": 200,
                                  "minimumWeight": 100
                              },
                              "pets": [
                                  {
                                      "petName": "Rex",
                                      "isFixed": true
                                  }
                              ]
                          }
                      }
                  }
                  """
            Then the profile response status is 415
             And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
             And the response body should have error message "is not supported by this host"

        @relational-backend
        Scenario: 12 Extension Exclude-Everything write profile is currently unsupported for write content type
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "Sample-Staff-Extension-Exclude-Everything" and namespacePrefixes "uri://ed-fi.org, uri://sample.ed-fi.org"
            When a PUT request is made to "/ed-fi/staffs/{id}" with profile "Sample-Staff-Extension-Exclude-Everything" for resource "Staff" with body
                  """
                  {
                      "id": "{id}",
                      "staffUniqueId": "99000308",
                      "firstName": "John",
                      "lastSurname": "Doe",
                      "_ext": {
                          "sample": {
                              "firstPetOwnedDate": "2020-02-02",
                              "petPreference": {
                                  "maximumWeight": 200,
                                  "minimumWeight": 100
                              },
                              "pets": [
                                  {
                                      "petName": "Rex",
                                      "isFixed": true
                                  }
                              ]
                          }
                      }
                  }
                  """
            Then the profile response status is 415
             And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
             And the response body should have error message "is not supported by this host"
