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

        Scenario: 01 IncludeOnly reference profile is currently unsupported on read
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "Test-Profile-Resource-References-IncludeOnly" and namespacePrefixes "uri://ed-fi.org"
            When a GET request is made to "/ed-fi/schools/{id}" with profile "Test-Profile-Resource-References-IncludeOnly" for resource "School"
            Then the profile response status is 406
             And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
             And the response body should have error message "is not supported by this host"

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
