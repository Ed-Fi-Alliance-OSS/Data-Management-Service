Feature: Profile Header Validation
              As an API client
              I want clear error messages when my profile header is invalid
    So that I can correct my requests

    Rule: Malformed profile headers return appropriate errors

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-IncludeOnly" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade               |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99000301,
                      "nameOfInstitution": "Header Validation Test School",
                      "shortNameOfInstitution": "HVTS",
                      "webSite": "https://headertest.example.com",
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

        Scenario: 01 Malformed profile header returns 400
             When a GET request is made to "/ed-fi/schools/{id}" with Accept header "application/vnd.ed-fi.invalid"
             Then the profile response status is 400
              And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
              And the response body status should equal the response status code
              And the response body should have error message "The format of the profile-based content type header was invalid"
              And the response body should have detail "The request construction was invalid with respect to usage of a data policy."
              And the response body errors should match regex "(?i)The format of the profile-based content type header was invalid\."

        Scenario: 02 Profile header with wrong resource name returns 400
             When a GET request is made to "/ed-fi/schools/{id}" with Accept header "application/vnd.ed-fi.student.e2e-test-school-includeonly.readable+json"
             Then the profile response status is 400
              And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
              And the response body status should equal the response status code
              And the response body should have error message "does not match the requested resource"
              And the response body should have detail "The request construction was invalid with respect to usage of a data policy."
              And the response body errors should match regex "(?i)The resource specified by the profile-based content type \('student'\) does not match the requested resource \('School'\)\."

        Scenario: 03 Profile header with writable usage for GET returns 400
             When a GET request is made to "/ed-fi/schools/{id}" with Accept header "application/vnd.ed-fi.school.e2e-test-school-includeonly.writable+json"
             Then the profile response status is 400
              And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
              And the response body status should equal the response status code
              And the response body should have error message "writable cannot be used with GET requests"
              And the response body should have detail "The request construction was invalid with respect to usage of a data policy."
              And the response body errors should match regex "(?i)A profile-based content type that is writable cannot be used with GET requests\."

        Scenario: 04 Malformed profile Content-Type header on POST returns 400
             When a POST request is made to "/ed-fi/schools" with Content-Type header "application/vnd.ed-fi.invalid" and body
                 """
                 {
                     "schoolId": 99000311,
                     "nameOfInstitution": "Malformed Content-Type Header POST",
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
             Then the profile response status is 400
              And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
              And the response body status should equal the response status code
              And the response body should have error message "The format of the profile-based content type header was invalid"
              And the response body should have detail "The request construction was invalid with respect to usage of a data policy."
              And the response body errors should match regex "(?i)The format of the profile-based content type header was invalid\."

        Scenario: 05 Readable profile Content-Type on POST returns 400
             When a POST request is made to "/ed-fi/schools" with Content-Type header "application/vnd.ed-fi.school.e2e-test-school-includeonly.readable+json" and body
                 """
                 {
                     "schoolId": 99000312,
                     "nameOfInstitution": "Readable Content-Type POST",
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
             Then the profile response status is 400
              And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
              And the response body status should equal the response status code
              And the response body should have detail "The request construction was invalid with respect to usage of a data policy."
              And the response body errors should match regex "(?i)A profile-based content type that is readable cannot be used with POST requests\."

        Scenario: 06 Malformed profile Content-Type header on PUT returns 400
             When a PUT request is made to "/ed-fi/schools/{id}" with Content-Type header "application/vnd.ed-fi.invalid" and body
                 """
                 {
                     "id": "{id}",
                     "schoolId": 99000301,
                     "nameOfInstitution": "Malformed Content-Type Header PUT",
                     "shortNameOfInstitution": "HVTS",
                     "webSite": "https://headertest.example.com",
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
             Then the profile response status is 400
              And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
              And the response body status should equal the response status code
              And the response body should have error message "The format of the profile-based content type header was invalid"
              And the response body should have detail "The request construction was invalid with respect to usage of a data policy."
              And the response body errors should match regex "(?i)The format of the profile-based content type header was invalid\."

        Scenario: 07 Readable profile Content-Type on PUT returns 400
             When a PUT request is made to "/ed-fi/schools/{id}" with Content-Type header "application/vnd.ed-fi.school.e2e-test-school-includeonly.readable+json" and body
                 """
                 {
                     "id": "{id}",
                     "schoolId": 99000301,
                     "nameOfInstitution": "Readable Content-Type PUT",
                     "shortNameOfInstitution": "HVTS",
                     "webSite": "https://headertest.example.com",
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
             Then the profile response status is 400
              And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
              And the response body status should equal the response status code
              And the response body should have detail "The request construction was invalid with respect to usage of a data policy."
              And the response body errors should match regex "(?i)A profile-based content type that is readable cannot be used with PUT requests\."

    Rule: Profile header on app with no profiles returns 406 Not Acceptable

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized without profiles and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade               |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99000302,
                      "nameOfInstitution": "No Profile Assignment Test School",
                      "shortNameOfInstitution": "NPATS",
                      "webSite": "https://noprofileassign.example.com",
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

        # When app has no profiles, using any profile header returns 406 (Not Acceptable)
        Scenario: 08 Using profile header on app without profiles returns 406
             When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-IncludeOnly" for resource "School"
             Then the profile response status is 406
              And the response body should have error type "urn:ed-fi:api:profile:invalid-profile-usage"
              And the response body status should equal the response status code
              And the response body should have detail "The request construction was invalid with respect to usage of a data policy."
              And the response body errors should match regex "(?i)The profile 'e2e-test-school-includeonly' specified by the content type in the 'Accept' header is not supported by this host\."

    Rule: Non-existent profile returns 403

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-IncludeOnly" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade               |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99000303,
                      "nameOfInstitution": "Nonexistent Profile Test School",
                      "shortNameOfInstitution": "NEPTS",
                      "webSite": "https://nonexistent.example.com",
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

        Scenario: 09 Using nonexistent profile returns 403
             When a GET request is made to "/ed-fi/schools/{id}" with Accept header "application/vnd.ed-fi.school.nonexistent-profile.readable+json"
             Then the profile response status is 403
              And the response body should have error type "urn:ed-fi:api:security:data-policy:incorrect-usage"
              And the response body status should equal the response status code
              And the response body should have detail "A data policy failure was encountered. The request was not constructed correctly for the data policy that has been applied to this data for the caller."
              And the response body errors should match regex "(?i)Based on profile assignments, one of the following profile-specific content types is required when requesting this resource: 'application/vnd\.ed-fi\.school\.e2e-test-school-includeonly\.readable\+json'"


    Rule: Valid profile header succeeds

        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-IncludeOnly" and namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade               |
              And a profile test POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 99000304,
                      "nameOfInstitution": "Resource Coverage Test School",
                      "shortNameOfInstitution": "RCTS",
                      "webSite": "https://coverage.example.com",
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

        Scenario: 10 Valid profile header for correct resource succeeds
             When a GET request is made to "/ed-fi/schools/{id}" with profile "E2E-Test-School-IncludeOnly" for resource "School"
             Then the profile response status is 200

        Scenario: 11 Valid profile Content-Type with media-type parameters for POST succeeds
             When a POST request is made to "/ed-fi/schools" with Content-Type header "application/vnd.ed-fi.school.e2e-test-school-includeonly.writable+json; charset=utf-8" and body
                 """
                 {
                     "schoolId": 99000314,
                     "nameOfInstitution": "Valid Content-Type Header POST",
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
             Then the profile response status is 201

        Scenario: 12 Valid profile Content-Type with media-type parameters for PUT succeeds
             When a PUT request is made to "/ed-fi/schools/{id}" with Content-Type header "application/vnd.ed-fi.school.e2e-test-school-includeonly.writable+json; charset=utf-8" and body
                 """
                 {
                     "id": "{id}",
                     "schoolId": 99000304,
                     "nameOfInstitution": "Valid Content-Type Header PUT",
                     "shortNameOfInstitution": "RCTS",
                     "webSite": "https://coverage.example.com",
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
             Then the profile response status is 204
