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

    Rule: Nested child collection item filtering is enforced for StudentAssessment

        Background:
            Given the claimSet "EdFiSandbox" is authorized without profiles and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                        |
                  | uri://ed-fi.org/AssessmentCategoryDescriptor#A1                        |
                  | uri://ed-fi.org/AcademicSubjectDescriptor#A1                           |
                  | uri://ed-fi.org/AssessmentReportingMethodDescriptor#A1                 |
                  | uri://ed-fi.org/AssessmentReportingMethodDescriptor#A2                 |
                  | uri://ed-fi.org/AssessmentReportingMethodDescriptor#A3                 |
                  | uri://ed-fi.org/AssessmentReportingMethodDescriptor#A4                 |
                  | uri://ed-fi.org/PerformanceLevelDescriptor#P1                          |
                  | uri://ed-fi.org/PerformanceLevelDescriptor#P2                          |
                  | uri://ed-fi.org/PerformanceLevelDescriptor#P3                          |
                  | uri://ed-fi.org/PerformanceLevelDescriptor#P4                          |
                  | uri://ed-fi.org/ResultDatatypeTypeDescriptor#A1                        |
                  | uri://ed-fi.org/ResultDatatypeTypeDescriptor#A2                        |
                  | uri://ed-fi.org/ResultDatatypeTypeDescriptor#A3                        |
                  | uri://ed-fi.org/ResultDatatypeTypeDescriptor#A4                        |
              And a profile test POST request is made to "/ed-fi/assessments" with
                  """
                  {
                      "assessmentIdentifier": "NESTED-99000408",
                      "namespace": "uri://ed-fi.org/Assessment/Assessment.xml",
                      "assessmentCategoryDescriptor": "uri://ed-fi.org/AssessmentCategoryDescriptor#A1",
                      "assessmentTitle": "Nested Collection Filtering Assessment",
                      "academicSubjects": [
                          {
                              "academicSubjectDescriptor": "uri://ed-fi.org/AcademicSubjectDescriptor#A1"
                          }
                      ]
                  }
                  """
              And a profile test POST request is made to "/ed-fi/objectiveAssessments" with
                  """
                  {
                      "assessmentReference": {
                          "assessmentIdentifier": "NESTED-99000408",
                          "namespace": "uri://ed-fi.org/Assessment/Assessment.xml"
                      },
                      "identificationCode": "OA-99000408",
                      "description": "Nested Collection Filtering Objective"
                  }
                  """
              And a profile test POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "99000408",
                      "birthDate": "2010-01-01",
                      "firstName": "Nested",
                      "lastSurname": "Collection"
                  }
                  """
              And the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized without profiles and namespacePrefixes "uri://ed-fi.org"
              And the system has these "schoolYearTypes"
                  | schoolYear | currentSchoolYear | schoolYearDescription |
                  | 2022       | true              | 2021-2022            |
                  | 2023       | false             | 2022-2023            |
              And a profile test POST request is made to "/ed-fi/studentAssessments" with
                  """
                  {
                      "assessmentReference": {
                          "assessmentIdentifier": "NESTED-99000408",
                          "namespace": "uri://ed-fi.org/Assessment/Assessment.xml"
                      },
                      "studentReference": {
                          "studentUniqueId": "99000408"
                      },
                      "studentAssessmentIdentifier": "SA-99000408",
                      "administrationDate": "2025-01-01",
                      "studentObjectiveAssessments": [
                          {
                              "objectiveAssessmentReference": {
                                  "assessmentIdentifier": "NESTED-99000408",
                                  "namespace": "uri://ed-fi.org/Assessment/Assessment.xml",
                                  "identificationCode": "OA-99000408"
                              },
                              "performanceLevels": [
                                  {
                                      "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#A1",
                                      "performanceLevelDescriptor": "uri://ed-fi.org/PerformanceLevelDescriptor#P1"
                                  },
                                  {
                                      "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#A2",
                                      "performanceLevelDescriptor": "uri://ed-fi.org/PerformanceLevelDescriptor#P2"
                                  },
                                  {
                                      "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#A3",
                                      "performanceLevelDescriptor": "uri://ed-fi.org/PerformanceLevelDescriptor#P3"
                                  },
                                  {
                                      "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#A4",
                                      "performanceLevelDescriptor": "uri://ed-fi.org/PerformanceLevelDescriptor#P4"
                                  }
                              ],
                              "scoreResults": [
                                  {
                                      "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#A1",
                                      "resultDatatypeTypeDescriptor": "uri://ed-fi.org/ResultDatatypeTypeDescriptor#A1",
                                      "result": "result-a1"
                                  },
                                  {
                                      "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#A2",
                                      "resultDatatypeTypeDescriptor": "uri://ed-fi.org/ResultDatatypeTypeDescriptor#A2",
                                      "result": "result-a2"
                                  },
                                  {
                                      "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#A3",
                                      "resultDatatypeTypeDescriptor": "uri://ed-fi.org/ResultDatatypeTypeDescriptor#A3",
                                      "result": "result-a3"
                                  },
                                  {
                                      "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#A4",
                                      "resultDatatypeTypeDescriptor": "uri://ed-fi.org/ResultDatatypeTypeDescriptor#A4",
                                      "result": "result-a4"
                                  }
                              ]
                          }
                      ],
                      "schoolYearTypeReference": {
                          "schoolYear": 2022
                      }
                  }
                  """

        Scenario: 08 IncludeOnly nested filter profile is currently unsupported on read
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "Test-Profile-Resource-Nested-Child-Collection-Filtered-To-IncludeOnly-Specific-Types-and-Descriptors" and namespacePrefixes "uri://ed-fi.org"
            When a GET request is made to "/ed-fi/studentAssessments/{id}" with profile "Test-Profile-Resource-Nested-Child-Collection-Filtered-To-IncludeOnly-Specific-Types-and-Descriptors" for resource "StudentAssessment"
            Then the profile response status is 406
             And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
             And the response body should have error message "is not supported by this host"

        Scenario: 09 ExcludeOnly nested filter profile is currently unsupported on read
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "Test-Profile-Resource-Nested-Child-Collection-Filtered-To-ExcludeOnly-Specific-Types-and-Descriptors" and namespacePrefixes "uri://ed-fi.org"
            When a GET request is made to "/ed-fi/studentAssessments/{id}" with profile "Test-Profile-Resource-Nested-Child-Collection-Filtered-To-ExcludeOnly-Specific-Types-and-Descriptors" for resource "StudentAssessment"
            Then the profile response status is 406
             And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
             And the response body should have error message "is not supported by this host"

        Scenario: 10 IncludeOnly nested filter profile is currently unsupported on write
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "Test-Profile-Resource-Nested-Child-Collection-Filtered-To-IncludeOnly-Specific-Types-and-Descriptors" and namespacePrefixes "uri://ed-fi.org"
            When a PUT request is made to "/ed-fi/studentAssessments/{id}" with profile "Test-Profile-Resource-Nested-Child-Collection-Filtered-To-IncludeOnly-Specific-Types-and-Descriptors" for resource "StudentAssessment" with body
                  """
                  {
                      "id": "{id}",
                      "assessmentReference": {
                          "assessmentIdentifier": "NESTED-99000408",
                          "namespace": "uri://ed-fi.org/Assessment/Assessment.xml"
                      },
                      "studentReference": {
                          "studentUniqueId": "99000408"
                      },
                      "studentAssessmentIdentifier": "SA-99000408",
                      "administrationDate": "2025-01-01",
                      "studentObjectiveAssessments": [
                          {
                              "objectiveAssessmentReference": {
                                  "assessmentIdentifier": "NESTED-99000408",
                                  "namespace": "uri://ed-fi.org/Assessment/Assessment.xml",
                                  "identificationCode": "OA-99000408"
                              },
                              "performanceLevels": [
                                  {
                                      "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#A2",
                                      "performanceLevelDescriptor": "uri://ed-fi.org/PerformanceLevelDescriptor#P2"
                                  }
                              ],
                              "scoreResults": [
                                  {
                                      "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#A2",
                                      "resultDatatypeTypeDescriptor": "uri://ed-fi.org/ResultDatatypeTypeDescriptor#A2",
                                      "result": "result-a2-updated"
                                  }
                              ]
                          }
                      ],
                      "schoolYearTypeReference": {
                          "schoolYear": 2022
                      }
                  }
                  """
            Then the profile response status is 415
             And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
             And the response body should have error message "is not supported by this host"

        Scenario: 11 ExcludeOnly nested filter profile is currently unsupported on write
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "Test-Profile-Resource-Nested-Child-Collection-Filtered-To-ExcludeOnly-Specific-Types-and-Descriptors" and namespacePrefixes "uri://ed-fi.org"
            When a PUT request is made to "/ed-fi/studentAssessments/{id}" with profile "Test-Profile-Resource-Nested-Child-Collection-Filtered-To-ExcludeOnly-Specific-Types-and-Descriptors" for resource "StudentAssessment" with body
                  """
                  {
                      "id": "{id}",
                      "assessmentReference": {
                          "assessmentIdentifier": "NESTED-99000408",
                          "namespace": "uri://ed-fi.org/Assessment/Assessment.xml"
                      },
                      "studentReference": {
                          "studentUniqueId": "99000408"
                      },
                      "studentAssessmentIdentifier": "SA-99000408",
                      "administrationDate": "2025-01-01",
                      "studentObjectiveAssessments": [
                          {
                              "objectiveAssessmentReference": {
                                  "assessmentIdentifier": "NESTED-99000408",
                                  "namespace": "uri://ed-fi.org/Assessment/Assessment.xml",
                                  "identificationCode": "OA-99000408"
                              },
                              "performanceLevels": [
                                  {
                                      "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#A1",
                                      "performanceLevelDescriptor": "uri://ed-fi.org/PerformanceLevelDescriptor#P1"
                                  }
                              ],
                              "scoreResults": [
                                  {
                                      "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#A1",
                                      "resultDatatypeTypeDescriptor": "uri://ed-fi.org/ResultDatatypeTypeDescriptor#A1",
                                      "result": "result-a1-updated"
                                  }
                              ]
                          }
                      ],
                      "schoolYearTypeReference": {
                          "schoolYear": 2022
                      }
                  }
                  """
            Then the profile response status is 415
             And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
             And the response body should have error message "is not supported by this host"
