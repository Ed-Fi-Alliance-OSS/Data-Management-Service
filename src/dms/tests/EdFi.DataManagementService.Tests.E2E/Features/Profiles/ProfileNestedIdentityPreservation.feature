Feature: Profile Nested Identity Preservation
    As an API client with a profile assigned
    I want nested identity reference objects to be preserved during profile write filtering
    So that resources with nested identity paths can be created successfully

    Rule: Nested identity paths preserve parent reference objects during write filtering

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-Calendar-Write-IncludeOnly" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/CalendarTypeDescriptor#Student Specific               |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                      |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
              And the system has these "schools"
                  | schoolId  | nameOfInstitution              | gradeLevels                                                                    | educationOrganizationCategories                                                                       |
                  | 255901107 | Nested Identity Test School    | [{"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"}] | [{"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"}] |
              And the system has these "schoolYearTypes"
                  | schoolYear | currentSchoolYear | schoolYearDescription |
                  | 2029       | false             | School Year 2029      |

        Scenario: 01 POST Calendar with IncludeOnly write profile preserves nested schoolYearTypeReference identity
            # DMS-1032: The profile only includes calendarCode, calendarTypeDescriptor, and gradeLevels.
            # schoolReference and schoolYearTypeReference are NOT in the IncludeOnly list, but they
            # contain nested identity paths ($.schoolReference.schoolId, $.schoolYearTypeReference.schoolYear)
            # and must be automatically preserved by the write filter.
            When a POST request is made to "/ed-fi/calendars" with profile "E2E-Test-Calendar-Write-IncludeOnly" for resource "Calendar" with body
                  """
                  {
                      "calendarCode": "NESTED-IDENTITY-TEST-01",
                      "schoolReference": {
                          "schoolId": 255901107
                      },
                      "schoolYearTypeReference": {
                          "schoolYear": 2029
                      },
                      "calendarTypeDescriptor": "uri://ed-fi.org/CalendarTypeDescriptor#Student Specific",
                      "gradeLevels": []
                  }
                  """
            Then the profile response status is 201
            When a GET request is made to "/ed-fi/calendars/{id}" with profile "E2E-Test-Calendar-Write-IncludeOnly" for resource "Calendar"
            Then the profile response status is 200
             And the response body path "schoolYearTypeReference.schoolYear" should have value "2029"
             And the response body path "schoolReference.schoolId" should have value "255901107"

        Scenario: 02 PUT Calendar with IncludeOnly write profile preserves nested identity references during merge
            # DMS-1032: Create then update a calendar - nested identity references must survive the PUT
            # even though they're not explicitly in the profile's IncludeOnly list.
            When a POST request is made to "/ed-fi/calendars" with profile "E2E-Test-Calendar-Write-IncludeOnly" for resource "Calendar" with body
                  """
                  {
                      "calendarCode": "NESTED-IDENTITY-TEST-02",
                      "schoolReference": {
                          "schoolId": 255901107
                      },
                      "schoolYearTypeReference": {
                          "schoolYear": 2029
                      },
                      "calendarTypeDescriptor": "uri://ed-fi.org/CalendarTypeDescriptor#Student Specific",
                      "gradeLevels": []
                  }
                  """
            Then the profile response status is 201
            When a PUT request is made to "/ed-fi/calendars/{id}" with profile "E2E-Test-Calendar-Write-IncludeOnly" for resource "Calendar" with body
                  """
                  {
                      "id": "{id}",
                      "calendarCode": "NESTED-IDENTITY-TEST-02",
                      "schoolReference": {
                          "schoolId": 255901107
                      },
                      "schoolYearTypeReference": {
                          "schoolYear": 2029
                      },
                      "calendarTypeDescriptor": "uri://ed-fi.org/CalendarTypeDescriptor#Student Specific",
                      "gradeLevels": []
                  }
                  """
            Then the profile response status is 204
            When a GET request is made to "/ed-fi/calendars/{id}" with profile "E2E-Test-Calendar-Write-IncludeOnly" for resource "Calendar"
            Then the profile response status is 200
             And the response body path "schoolYearTypeReference.schoolYear" should have value "2029"
             And the response body path "schoolReference.schoolId" should have value "255901107"
