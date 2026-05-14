Feature: Profile Reference Filtering
    As an API client using profile read/write rules
    I want reference properties to be explicitly included or excluded
    So that references do not leak or update outside profile rules

    Rule: IncludeOnly profile includes only configured reference members on read

        Background:
            Given the claimSet "EdFiSandbox" is authorized without profiles and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                         |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#ESC             |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#LEA             |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School          |
                  | uri://ed-fi.org/GradeLevelDescriptor#Postsecondary                      |
                  | uri://ed-fi.org/OperationalStatusDescriptor#Active                       |
                  | uri://ed-fi.org/SchoolTypeDescriptor#Regular                             |
                  | uri://ed-fi.org/AdministrativeFundingControlDescriptor#Other             |
                  | uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent charter district |
              And the system has these "educationServiceCenters"
                  | educationServiceCenterId | nameOfInstitution | categories                                                                                                     |
                  | 255950                   | ESC Test          | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#ESC"} ] |
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | categories                                                                                                     | educationServiceCenterReference        | localEducationAgencyCategoryDescriptor                                                |
                  | 255901                 | LEA Test          | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#LEA"} ] | {"educationServiceCenterId": "255950"} | "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent charter district" |
              And the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized without profiles and namespacePrefixes "uri://ed-fi.org"
              And the system has these "schoolYearTypes"
                  | schoolYear | currentSchoolYear | schoolYearDescription |
                  | 2022       | true              | 2021-2022            |
                  | 2023       | false             | 2022-2023            |
                  | 2024       | false             | 2023-2024            |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99006021,
                      "nameOfInstitution": "IncludeOnly Reference School",
                      "shortNameOfInstitution": "IORS",
                      "localEducationAgencyReference": {
                          "localEducationAgencyId": 255901
                      },
                      "operationalStatusDescriptor": "uri://ed-fi.org/OperationalStatusDescriptor#Active",
                      "schoolTypeDescriptor": "uri://ed-fi.org/SchoolTypeDescriptor#Regular",
                      "administrativeFundingControlDescriptor": "uri://ed-fi.org/AdministrativeFundingControlDescriptor#Other",
                      "charterApprovalSchoolYearTypeReference": {
                          "schoolYear": 2022
                      },
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"
                          }
                      ]
                  }
                  """

        @relational-backend
        @relational-ci-shard-3
        Scenario: 01 IncludeOnly reference profile preserves server-generated link on surviving references
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-Profile-Resource-References-IncludeOnly-Read" and namespacePrefixes "uri://ed-fi.org"
            When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-Profile-Resource-References-IncludeOnly-Read" for resource "School"
            Then the profile response status is 200
             And the response body should contain fields "id, schoolId, localEducationAgencyReference, charterApprovalSchoolYearTypeReference"
             And the response body should not contain fields "nameOfInstitution, shortNameOfInstitution, operationalStatusDescriptor"
             And the response body should contain path "localEducationAgencyReference.link.rel"
             And the response body path "localEducationAgencyReference.link.rel" should have value "LocalEducationAgency"
             And the response body should contain path "localEducationAgencyReference.link.href"
             And the response body should contain path "charterApprovalSchoolYearTypeReference.link.rel"
             And the response body path "charterApprovalSchoolYearTypeReference.link.rel" should have value "SchoolYearType"
             And the response body should contain path "charterApprovalSchoolYearTypeReference.link.href"

    Rule: ExcludeOnly profile excludes configured reference members on read

        Background:
            Given the claimSet "EdFiSandbox" is authorized without profiles and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                         |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#ESC             |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#LEA             |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School          |
                  | uri://ed-fi.org/GradeLevelDescriptor#Postsecondary                      |
                  | uri://ed-fi.org/OperationalStatusDescriptor#Active                       |
                  | uri://ed-fi.org/SchoolTypeDescriptor#Regular                             |
                  | uri://ed-fi.org/AdministrativeFundingControlDescriptor#Other             |
                  | uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent charter district |
              And the system has these "educationServiceCenters"
                  | educationServiceCenterId | nameOfInstitution | categories                                                                                                     |
                  | 255950                   | ESC Test          | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#ESC"} ] |
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | categories                                                                                                     | educationServiceCenterReference        | localEducationAgencyCategoryDescriptor                                                |
                  | 255901                 | LEA Test          | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#LEA"} ] | {"educationServiceCenterId": "255950"} | "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent charter district" |
              And the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized without profiles and namespacePrefixes "uri://ed-fi.org"
              And the system has these "schoolYearTypes"
                  | schoolYear | currentSchoolYear | schoolYearDescription |
                  | 2022       | true              | 2021-2022            |
                  | 2023       | false             | 2022-2023            |
                  | 2024       | false             | 2023-2024            |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99006022,
                      "nameOfInstitution": "ExcludeOnly Reference School",
                      "shortNameOfInstitution": "EORS",
                      "localEducationAgencyReference": {
                          "localEducationAgencyId": 255901
                      },
                      "operationalStatusDescriptor": "uri://ed-fi.org/OperationalStatusDescriptor#Active",
                      "schoolTypeDescriptor": "uri://ed-fi.org/SchoolTypeDescriptor#Regular",
                      "administrativeFundingControlDescriptor": "uri://ed-fi.org/AdministrativeFundingControlDescriptor#Other",
                      "charterApprovalSchoolYearTypeReference": {
                          "schoolYear": 2022
                      },
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"
                          }
                      ]
                  }
                  """

        @relational-backend
        @relational-ci-shard-3
        Scenario: 02 ExcludeOnly profile excludes configured reference properties on read
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "Test-Profile-Resource-References-ExcludeOnly" and namespacePrefixes "uri://ed-fi.org"
            When a GET request is made to "/ed-fi/schools/{id}" with profile "Test-Profile-Resource-References-ExcludeOnly" for resource "School"
            Then the profile response status is 200
             And the response body should contain fields "id, schoolId, localEducationAgencyReference, charterApprovalSchoolYearTypeReference"
             And the response body should contain fields "nameOfInstitution, shortNameOfInstitution"

    Rule: IncludeOnly and ExcludeOnly reference rules are enforced on write

        Background:
            Given the claimSet "EdFiSandbox" is authorized without profiles and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                         |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#ESC             |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#LEA             |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School          |
                  | uri://ed-fi.org/GradeLevelDescriptor#Postsecondary                      |
                  | uri://ed-fi.org/OperationalStatusDescriptor#Active                       |
                  | uri://ed-fi.org/SchoolTypeDescriptor#Regular                             |
                  | uri://ed-fi.org/AdministrativeFundingControlDescriptor#Other             |
                  | uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent charter district |
              And the system has these "educationServiceCenters"
                  | educationServiceCenterId | nameOfInstitution | categories                                                                                                     |
                  | 255950                   | ESC Test          | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#ESC"} ] |
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | categories                                                                                                     | educationServiceCenterReference        | localEducationAgencyCategoryDescriptor                                                |
                  | 255901                 | LEA Test          | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#LEA"} ] | {"educationServiceCenterId": "255950"} | "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent charter district" |
              And the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized without profiles and namespacePrefixes "uri://ed-fi.org"
              And the system has these "schoolYearTypes"
                  | schoolYear | currentSchoolYear | schoolYearDescription |
                  | 2022       | true              | 2021-2022            |
                  | 2023       | false             | 2022-2023            |
                  | 2024       | false             | 2023-2024            |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99006023,
                      "nameOfInstitution": "Write Reference School",
                      "shortNameOfInstitution": "WRS",
                      "localEducationAgencyReference": {
                          "localEducationAgencyId": 255901
                      },
                      "operationalStatusDescriptor": "uri://ed-fi.org/OperationalStatusDescriptor#Active",
                      "schoolTypeDescriptor": "uri://ed-fi.org/SchoolTypeDescriptor#Regular",
                      "administrativeFundingControlDescriptor": "uri://ed-fi.org/AdministrativeFundingControlDescriptor#Other",
                      "charterApprovalSchoolYearTypeReference": {
                          "schoolYear": 2022
                      },
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"
                          }
                      ]
                  }
                  """

        @relational-backend
        @relational-ci-shard-3
        Scenario: 03 IncludeOnly reference write profile is currently unsupported on write
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "Test-Profile-Resource-References-IncludeOnly" and namespacePrefixes "uri://ed-fi.org"
            When a PUT request is made to "/ed-fi/schools/{id}" with profile "Test-Profile-Resource-References-IncludeOnly" for resource "School" with body
                  """
                  {
                      "id": "{id}",
                      "schoolId": 99006023,
                      "nameOfInstitution": "Write IncludeOnly Reference Updated",
                      "shortNameOfInstitution": "WIORU",
                      "localEducationAgencyReference": {
                          "localEducationAgencyId": 255901
                      },
                      "charterApprovalSchoolYearTypeReference": {
                          "schoolYear": 2023
                      },
                      "schoolTypeDescriptor": "uri://ed-fi.org/SchoolTypeDescriptor#Regular",
                      "operationalStatusDescriptor": "uri://ed-fi.org/OperationalStatusDescriptor#Active",
                      "administrativeFundingControlDescriptor": "uri://ed-fi.org/AdministrativeFundingControlDescriptor#Other",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"
                          }
                      ]
                  }
                  """
            Then the profile response status is 415
             And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
             And the response body should have error message "is not supported by this host"

        @relational-backend
        @relational-ci-shard-3
        Scenario: 04 ExcludeOnly reference write profile excludes configured reference members
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "Test-Profile-Resource-References-ExcludeOnly" and namespacePrefixes "uri://ed-fi.org"
            When a PUT request is made to "/ed-fi/schools/{id}" with profile "Test-Profile-Resource-References-ExcludeOnly" for resource "School" with body
                  """
                  {
                      "id": "{id}",
                      "schoolId": 99006023,
                      "nameOfInstitution": "Write ExcludeOnly Reference Updated",
                      "shortNameOfInstitution": "WEORU",
                      "localEducationAgencyReference": {
                          "localEducationAgencyId": 255901
                      },
                      "charterApprovalSchoolYearTypeReference": {
                          "schoolYear": 2024
                      },
                      "schoolTypeDescriptor": "uri://ed-fi.org/SchoolTypeDescriptor#Regular",
                      "operationalStatusDescriptor": "uri://ed-fi.org/OperationalStatusDescriptor#Active",
                      "administrativeFundingControlDescriptor": "uri://ed-fi.org/AdministrativeFundingControlDescriptor#Other",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"
                          }
                      ]
                  }
                  """
            Then the profile response status is 204
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized without profiles and namespacePrefixes "uri://ed-fi.org"
            When a GET request is made to "/ed-fi/schools/{id}" without profile header
            Then the profile response status is 200
             And the response body path "nameOfInstitution" should have value "Write ExcludeOnly Reference Updated"
             And the response body path "shortNameOfInstitution" should have value "WEORU"
             And the response body path "charterApprovalSchoolYearTypeReference.schoolYear" should have value "2024"

    Rule: IncludeOnly profile preserves server-generated link on a nested collection-scoped reference

        Background:
            Given the claimSet "EdFiSandbox" is authorized without profiles and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                          |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School           |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                         |
              And the system has these "schools"
                  | schoolId | nameOfInstitution         | educationOrganizationCategories                                                                                   | gradeLevels                                                                          |
                  | 99006030 | Nested Reference School   | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"} ]      |
              And the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized without profiles and namespacePrefixes "uri://ed-fi.org"
              And the system has these "classPeriods"
                  | classPeriodName | schoolReference            |
                  | First Period    | {"schoolId": 99006030}     |
              And a profile test POST request is made to "/ed-fi/bellSchedules" with
                  """
                  {
                      "bellScheduleName": "Nested Reference Schedule",
                      "schoolReference": {
                          "schoolId": 99006030
                      },
                      "classPeriods": [
                          {
                              "classPeriodReference": {
                                  "schoolId": 99006030,
                                  "classPeriodName": "First Period"
                              }
                          }
                      ]
                  }
                  """

        # bellScheduleName and schoolReference are identity members for BellSchedule
        # (identity = bellScheduleName + schoolReference.schoolId). The readable-profile
        # projector preserves identity properties at the document root regardless of
        # profile rules (IReadableProfileProjector.ExtractIdentityPropertyNames +
        # ReadableProfileProjector.ProjectRoot "Always preserve metadata and identity
        # fields at the document root"). This scenario therefore only asserts the
        # DMS-1145 link-preservation contract on the nested classPeriodReference.
        @relational-backend
        Scenario: 05 IncludeOnly profile preserves link on classPeriods[*].classPeriodReference
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-BellSchedule-ClassPeriods-IncludeOnly" and namespacePrefixes "uri://ed-fi.org"
            When a GET request is made to "/ed-fi/bellSchedules/{id}" with profile "E2E-Test-BellSchedule-ClassPeriods-IncludeOnly" for resource "BellSchedule"
            Then the profile response status is 200
             And the response body should contain fields "id, classPeriods"
             And the response body should contain path "classPeriods.0.classPeriodReference.link.rel"
             And the response body path "classPeriods.0.classPeriodReference.link.rel" should have value "ClassPeriod"
             And the response body should contain path "classPeriods.0.classPeriodReference.link.href"
