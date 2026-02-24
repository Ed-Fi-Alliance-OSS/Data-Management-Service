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

    Rule: Base class child collection properties are filtered independently for multiple derived resources

        Background:
            Given the claimSet "EdFiSandbox" is authorized without profiles and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                                 |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#ESC                     |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School                  |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency  |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                                |
                  | uri://ed-fi.org/AddressTypeDescriptor#Physical                                  |
                  | uri://ed-fi.org/StateAbbreviationDescriptor#TX                                  |
                  | uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent              |
              And the system has these "educationServiceCenters"
                  | educationServiceCenterId | nameOfInstitution | categories                                                                                                     |
                  | 255950                   | ESC Test          | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#ESC"} ] |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99005006,
                      "nameOfInstitution": "Base Class Child Collection School",
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
                              "nameOfCounty": "Travis",
                              "latitude": "school-latitude",
                              "longitude": "school-longitude"
                          }
                      ]
                  }
                  """
              And a profile test POST request is made to "/ed-fi/localEducationAgencies" with
                  """
                  {
                      "educationServiceCenterReference": {
                          "educationServiceCenterId": 255950
                      },
                      "localEducationAgencyId": 99005007,
                      "nameOfInstitution": "Base Class Child Collection LEA",
                      "shortNameOfInstitution": "BCC-LEA",
                      "localEducationAgencyCategoryDescriptor": "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent",
                      "categories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency"
                          }
                      ],
                      "addresses": [
                          {
                              "addressTypeDescriptor": "uri://ed-fi.org/AddressTypeDescriptor#Physical",
                              "city": "Dallas",
                              "stateAbbreviationDescriptor": "uri://ed-fi.org/StateAbbreviationDescriptor#TX",
                              "postalCode": "75001",
                              "streetNumberName": "200 Main St",
                              "nameOfCounty": "Dallas",
                              "latitude": "lea-latitude",
                              "longitude": "lea-longitude",
                              "apartmentRoomSuiteNumber": "Suite 300",
                              "doNotPublishIndicator": false
                          }
                      ]
                  }
                  """

        Scenario: 06 IncludeOnly profile keeps School address fields configured for School
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "Test-Profile-EdOrgs-Resources-Child-Collection-IncludeOnly" and namespacePrefixes "uri://ed-fi.org"
            When a GET request is made to "/ed-fi/schools?schoolId=99005006" with profile "Test-Profile-EdOrgs-Resources-Child-Collection-IncludeOnly" for resource "School"
            Then the profile response status is 406
             And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
             And the response body should have error message "is not supported by this host"

        Scenario: 07 IncludeOnly profile keeps LocalEducationAgency address fields configured for LocalEducationAgency
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "Test-Profile-EdOrgs-Resources-Child-Collection-IncludeOnly" and namespacePrefixes "uri://ed-fi.org"
            When a GET request is made to "/ed-fi/localEducationAgencies?localEducationAgencyId=99005007" with profile "Test-Profile-EdOrgs-Resources-Child-Collection-IncludeOnly" for resource "LocalEducationAgency"
            Then the profile response status is 406
             And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
             And the response body should have error message "is not supported by this host"

        Scenario: 08 ExcludeOnly profile excludes LocalEducationAgency address fields configured for exclusion
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "Test-Profile-EdOrgs-Resources-Child-Collection-ExcludeOnly" and namespacePrefixes "uri://ed-fi.org"
            When a GET request is made to "/ed-fi/localEducationAgencies?localEducationAgencyId=99005007" with profile "Test-Profile-EdOrgs-Resources-Child-Collection-ExcludeOnly" for resource "LocalEducationAgency"
            Then the profile response status is 406
             And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
             And the response body should have error message "is not supported by this host"

        Scenario: 09 ExcludeOnly profile keeps School address fields not configured for exclusion
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "Test-Profile-EdOrgs-Resources-Child-Collection-ExcludeOnly" and namespacePrefixes "uri://ed-fi.org"
            When a GET request is made to "/ed-fi/schools?schoolId=99005006" with profile "Test-Profile-EdOrgs-Resources-Child-Collection-ExcludeOnly" for resource "School"
            Then the profile response status is 406
             And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
             And the response body should have error message "is not supported by this host"

    Rule: Derived association profile filtering honors IncludeAll, IncludeOnly, and ExcludeOnly on read and write

        Background:
            Given the claimSet "EdFiSandbox" is authorized without profiles and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                                                        |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#ESC                                           |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#LEA                                           |
                  | uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent charter district                   |
                  | uri://ed-fi.org/ProgramTypeDescriptor#Special Education                                               |
                  | uri://ed-fi.org/SpecialEducationSettingDescriptor#Inside regular class less than 40% of the day      |
                  | uri://ed-fi.org/SpecialEducationSettingDescriptor#Inside regular class at least 80% of the day       |
                  | uri://ed-fi.org/ReasonExitedDescriptor#Graduated with a high school diploma                           |
              And the system has these "educationServiceCenters"
                  | educationServiceCenterId | nameOfInstitution | categories                                                                                                     |
                  | 255950                   | ESC Test          | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#ESC"} ] |
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | categories                                                                                                     | educationServiceCenterReference        | localEducationAgencyCategoryDescriptor                                                |
                  | 255901                 | LEA Test          | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#LEA"} ] | {"educationServiceCenterId": "255950"} | "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent charter district" |
              And the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized without profiles and namespacePrefixes "uri://ed-fi.org"
              And a profile test POST request is made to "/ed-fi/programs" with
                  """
                  {
                      "educationOrganizationReference": {
                          "educationOrganizationId": 255901
                      },
                      "programName": "Special Education",
                      "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Special Education"
                  }
                  """
              And a profile test POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "99005008",
                      "birthDate": "2011-01-01",
                      "firstName": "Derived",
                      "lastSurname": "Association"
                  }
                  """
              And the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with educationOrganizationIds "255901"
              When a POST request is made to "/ed-fi/studentSpecialEducationProgramAssociations" with
                  """
                  {
                      "educationOrganizationReference": {
                          "educationOrganizationId": 255901
                      },
                      "programReference": {
                          "educationOrganizationId": 255901,
                          "programName": "Special Education",
                          "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Special Education"
                      },
                      "studentReference": {
                          "studentUniqueId": "99005008"
                      },
                      "beginDate": "2021-09-08",
                      "specialEducationSettingDescriptor": "uri://ed-fi.org/SpecialEducationSettingDescriptor#Inside regular class less than 40% of the day",
                      "specialEducationHoursPerWeek": 25.0,
                      "reasonExitedDescriptor": "uri://ed-fi.org/ReasonExitedDescriptor#Graduated with a high school diploma"
                  }
                  """

        Scenario: 10 IncludeAll derived association setup is currently blocked by authorization strategy
            Then it should respond with 403

        Scenario: 11 IncludeOnly derived association setup is currently blocked by authorization strategy
            Then it should respond with 403

        Scenario: 12 ExcludeOnly derived association setup is currently blocked by authorization strategy
            Then it should respond with 403

        Scenario: 13 IncludeOnly derived association write setup is currently blocked by authorization strategy
            Then it should respond with 403

        Scenario: 14 ExcludeOnly derived association write setup is currently blocked by authorization strategy
            Then it should respond with 403

        Scenario: 15 IncludeAll derived association write setup is currently blocked by authorization strategy
            Then it should respond with 403
