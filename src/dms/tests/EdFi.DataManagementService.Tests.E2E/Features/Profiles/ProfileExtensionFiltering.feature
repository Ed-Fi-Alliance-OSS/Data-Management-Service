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
             And the response body path "_ext.sample.isExemplary" should have value "True"

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
