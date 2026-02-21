Feature: Multi-Resource Profile Usage
    As an API client with a multi-resource profile assigned
    I want to access all resources included in my profile and be denied access to others
    So that profile boundaries are enforced for both positive and negative cases

    Background:
        Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-Student-And-School-IncludeAll" and namespacePrefixes "uri://ed-fi.org"
          And the system has these descriptors
              | descriptorValue                                                       |
              | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
              | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |
              | uri://ed-fi.org/GradeLevelDescriptor#Tenth grade                      |
              | uri://ed-fi.org/StudentCharacteristicDescriptor#504                   |

    Scenario: GET on both resources included in the profile succeeds
        When a GET request is made to "/ed-fi/schools" with profile "E2E-Test-Student-And-School-IncludeAll" for resource "School"
        Then the profile response status is 200
        When a GET request is made to "/ed-fi/students" with profile "E2E-Test-Student-And-School-IncludeAll" for resource "Student"
        Then the profile response status is 200

    Scenario: POST on both resources included in the profile succeeds
        When a POST request is made to "/ed-fi/schools" with profile "E2E-Test-Student-And-School-IncludeAll" for resource "School" with body
            """
            {
                "schoolId": 99001001,
                "nameOfInstitution": "MultiResource Test School",
                "educationOrganizationCategories": [
                    { "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School" }
                ],
                "gradeLevels": [
                    { "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade" }
                ]
            }
            """
        Then the profile response status is 201
        When a POST request is made to "/ed-fi/students" with profile "E2E-Test-Student-And-School-IncludeAll" for resource "Student" with body
            """
            {
                "studentUniqueId": "99001001",
                "firstName": "MultiResource",
                "lastSurname": "Student",
                "birthDate": "2010-01-01"
            }
            """
        Then the profile response status is 201

    Scenario: GET on resource not included in the profile returns 400/405
        When a GET request is made to "/ed-fi/staffs" with profile "E2E-Test-Student-And-School-IncludeAll" for resource "Staff"
        Then the profile response status is 400 or 405
        And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"

    Scenario: POST on resource not included in the profile returns 400/405
        When a POST request is made to "/ed-fi/staffs" with profile "E2E-Test-Student-And-School-IncludeAll" for resource "Staff" with body
            """
            {
                "staffUniqueId": "99001002",
                "firstName": "Not",
                "lastSurname": "Included"
            }
            """
        Then the profile response status is 400 or 405
        And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"

    Scenario: Accept/Content-Type negotiation for all included resources
        When a GET request is made to "/ed-fi/schools" with Accept header "application/vnd.ed-fi.school.e2e-test-student-and-school-includeall.readable+json"
        Then the profile response status is 200
        When a GET request is made to "/ed-fi/students" with Accept header "application/vnd.ed-fi.student.e2e-test-student-and-school-includeall.readable+json"
        Then the profile response status is 200
        When a GET request is made to "/ed-fi/staffs" with Accept header "application/vnd.ed-fi.staff.e2e-test-student-and-school-includeall.readable+json"
        Then the profile response status is 400 or 405
        And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
