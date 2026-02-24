Feature: Profile Embedded Object Filtering
    As an API client using profile rules on nested objects and collections
    I want nested members included or excluded on write according to profile definitions
    So that embedded object handling follows the profile contract

    Rule: Read content includes expected nested collections

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
                      "schoolId": 99003001,
                      "nameOfInstitution": "Embedded Read Filtering School",
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

        Scenario: 01 IncludeAll profile returns nested collection data
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-IncludeAll" for resource "School"
            Then the profile response status is 200
             And the "addresses" collection item at index 0 should have "nameOfCounty" value "Travis"

        Scenario: 02 IncludeOnly profile still returns allowed nested collection members
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-IncludeOnly" and namespacePrefixes "uri://ed-fi.org"
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-IncludeOnly" for resource "School"
            Then the profile response status is 200
             And the response body should contain fields "id, schoolId, nameOfInstitution"

    Rule: Write content includes and excludes nested object members according to profile

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized without profiles and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade               |
                  | uri://ed-fi.org/AddressTypeDescriptor#Physical                 |
                  | uri://ed-fi.org/StateAbbreviationDescriptor#TX                 |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99003002,
                      "nameOfInstitution": "Embedded Write Filtering School",
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
                              "streetNumberName": "200 Main St",
                              "nameOfCounty": "Travis"
                          }
                      ]
                  }
                  """

        Scenario: 03 Write profile excluding nested member does not persist excluded nested update
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-Write-AddressExcludeNameOfCounty" and namespacePrefixes "uri://ed-fi.org"
            When a PUT request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-Write-AddressExcludeNameOfCounty" for resource "School" with body
                  """
                  {
                      "id": "{id}",
                      "schoolId": 99003002,
                      "nameOfInstitution": "Embedded Write Filtering School",
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
                              "streetNumberName": "200 Main St",
                              "nameOfCounty": "UpdatedCounty"
                          }
                      ]
                  }
                  """
            Then the profile response status is 204
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-Write-AddressExcludeNameOfCounty" for resource "School"
            Then the profile response status is 200
             And the "addresses" collection item at index 0 should have "nameOfCounty" value "Travis"

        Scenario: 04 Write profile including nested member persists nested update
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-Write-IncludeAll" and namespacePrefixes "uri://ed-fi.org"
            When a PUT request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-Write-IncludeAll" for resource "School" with body
                  """
                  {
                      "id": "{id}",
                      "schoolId": 99003002,
                      "nameOfInstitution": "Embedded Write Filtering School",
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
                              "streetNumberName": "200 Main St",
                              "nameOfCounty": "IncludedCounty"
                          }
                      ]
                  }
                  """
            Then the profile response status is 204
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-Write-IncludeAll" for resource "School"
            Then the profile response status is 200
             And the "addresses" collection item at index 0 should have "nameOfCounty" value "IncludedCounty"

    Rule: True embedded object filtering on Assessment contentStandard is enforced

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized without profiles and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                 |
                  | uri://ed-fi.org/AssessmentCategoryDescriptor#Class quiz         |
                  | uri://ed-fi.org/AcademicSubjectDescriptor#English Language Arts |
              And a profile test POST request is made to "/ed-fi/assessments" with
                  """
                  {
                      "assessmentIdentifier": "EMBED-99003005",
                      "namespace": "uri://ed-fi.org/Assessment/Assessment.xml",
                      "assessmentCategoryDescriptor": "uri://ed-fi.org/AssessmentCategoryDescriptor#Class quiz",
                      "assessmentTitle": "Embedded Object Assessment",
                      "contentStandard": {
                          "title": "Original Content Standard"
                      },
                      "academicSubjects": [
                          {
                              "academicSubjectDescriptor": "uri://ed-fi.org/AcademicSubjectDescriptor#English Language Arts"
                          }
                      ]
                  }
                  """

        Scenario: 05 Read profile excluding embedded object currently returns contentStandard
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "Assessment-Readable-Excludes-Embedded-Object" and namespacePrefixes "uri://ed-fi.org"
            When a GET request is made to "/ed-fi/assessments/{id}" with profile "Assessment-Readable-Excludes-Embedded-Object" for resource "Assessment"
            Then the profile response status is 200
             And the response body should contain path "contentStandard"

        Scenario: 06 Read profile including embedded object is currently unsupported by host
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "Assessment-Readable-Includes-Embedded-Object" and namespacePrefixes "uri://ed-fi.org"
            When a GET request is made to "/ed-fi/assessments/{id}" with profile "Assessment-Readable-Includes-Embedded-Object" for resource "Assessment"
            Then the profile response status is 406
             And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
             And the response body should have error message "is not supported by this host"

        Scenario: 07 Write profile excluding embedded object is currently unsupported by host
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "Assessment-Writable-Excludes-Embedded-Object" and namespacePrefixes "uri://ed-fi.org"
            When a PUT request is made to "/ed-fi/assessments/{id}" with profile "Assessment-Writable-Excludes-Embedded-Object" for resource "Assessment" with body
                  """
                  {
                      "id": "{id}",
                      "assessmentIdentifier": "EMBED-99003005",
                      "namespace": "uri://ed-fi.org/Assessment/Assessment.xml",
                      "assessmentCategoryDescriptor": "uri://ed-fi.org/AssessmentCategoryDescriptor#Class quiz",
                      "assessmentTitle": "Embedded Object Assessment",
                      "contentStandard": {
                          "title": "Excluded Updated Title"
                      },
                      "academicSubjects": [
                          {
                              "academicSubjectDescriptor": "uri://ed-fi.org/AcademicSubjectDescriptor#English Language Arts"
                          }
                      ]
                  }
                  """
            Then the profile response status is 415
             And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
             And the response body should have error message "is not supported by this host"

        Scenario: 08 Write profile including embedded object is currently unsupported by host
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "Assessment-Writable-Includes-Embedded-Object" and namespacePrefixes "uri://ed-fi.org"
            When a PUT request is made to "/ed-fi/assessments/{id}" with profile "Assessment-Writable-Includes-Embedded-Object" for resource "Assessment" with body
                  """
                  {
                      "id": "{id}",
                      "assessmentIdentifier": "EMBED-99003005",
                      "namespace": "uri://ed-fi.org/Assessment/Assessment.xml",
                      "assessmentCategoryDescriptor": "uri://ed-fi.org/AssessmentCategoryDescriptor#Class quiz",
                      "assessmentTitle": "Embedded Object Assessment",
                      "contentStandard": {
                          "title": "Included Updated Title"
                      },
                      "academicSubjects": [
                          {
                              "academicSubjectDescriptor": "uri://ed-fi.org/AcademicSubjectDescriptor#English Language Arts"
                          }
                      ]
                  }
                  """
            Then the profile response status is 415
             And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
             And the response body should have error message "is not supported by this host"

        Scenario: 09 Data validation with invalid embedded object is currently accepted without profile
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized without profiles and namespacePrefixes "uri://ed-fi.org"
            When a PUT request is made to "/ed-fi/assessments/{id}" with
                  """
                  {
                      "id": "{id}",
                      "assessmentIdentifier": "EMBED-99003005",
                      "namespace": "uri://ed-fi.org/Assessment/Assessment.xml",
                      "assessmentCategoryDescriptor": "uri://ed-fi.org/AssessmentCategoryDescriptor#Class quiz",
                      "assessmentTitle": "Embedded Object Assessment",
                      "contentStandard": {
                          "authors": []
                      },
                      "academicSubjects": [
                          {
                              "academicSubjectDescriptor": "uri://ed-fi.org/AcademicSubjectDescriptor#English Language Arts"
                          }
                      ]
                  }
                  """
            Then the profile response status is 200

        Scenario: 10 Data validation with invalid embedded object profile is currently unsupported by host
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "Assessment-Writable-Includes-Embedded-Object" and namespacePrefixes "uri://ed-fi.org"
            When a PUT request is made to "/ed-fi/assessments/{id}" with profile "Assessment-Writable-Includes-Embedded-Object" for resource "Assessment" with body
                  """
                  {
                      "id": "{id}",
                      "assessmentIdentifier": "EMBED-99003005",
                      "namespace": "uri://ed-fi.org/Assessment/Assessment.xml",
                      "assessmentCategoryDescriptor": "uri://ed-fi.org/AssessmentCategoryDescriptor#Class quiz",
                      "assessmentTitle": "Embedded Object Assessment",
                      "contentStandard": {
                          "authors": []
                      },
                      "academicSubjects": [
                          {
                              "academicSubjectDescriptor": "uri://ed-fi.org/AcademicSubjectDescriptor#English Language Arts"
                          }
                      ]
                  }
                  """
            Then the profile response status is 415
             And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
             And the response body should have error message "is not supported by this host"

        Scenario: 11 Data validation with invalid embedded object exclude profile is currently unsupported by host
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "Assessment-Writable-Excludes-Embedded-Object" and namespacePrefixes "uri://ed-fi.org"
            When a PUT request is made to "/ed-fi/assessments/{id}" with profile "Assessment-Writable-Excludes-Embedded-Object" for resource "Assessment" with body
                  """
                  {
                      "id": "{id}",
                      "assessmentIdentifier": "EMBED-99003005",
                      "namespace": "uri://ed-fi.org/Assessment/Assessment.xml",
                      "assessmentCategoryDescriptor": "uri://ed-fi.org/AssessmentCategoryDescriptor#Class quiz",
                      "assessmentTitle": "Embedded Object Assessment",
                      "contentStandard": {
                          "authors": []
                      },
                      "academicSubjects": [
                          {
                              "academicSubjectDescriptor": "uri://ed-fi.org/AcademicSubjectDescriptor#English Language Arts"
                          }
                      ]
                  }
                  """
            Then the profile response status is 415
             And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
             And the response body should have error message "is not supported by this host"
