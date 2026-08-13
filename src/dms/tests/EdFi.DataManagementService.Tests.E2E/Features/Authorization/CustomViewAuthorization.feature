@ResetClaimsetsAfterScenario
@reset-data-before-scenario
Feature: CustomViewAuthorization

    Rule: GET-many custom view authorization filters by basis-resource DocumentId

        @e2e-ci-shard-3 @MssqlRepresentative
        Scenario: Custom view filters GET-many Students to matching DocumentIds
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with namespacePrefixes "uri://ed-fi.org"
              And the system has these "students"
                  | studentUniqueId | firstName | lastSurname | birthDate  |
                  | "604821"        | Authorized | Student     | 2010-01-01 |
                  | "604822"        | Filtered   | Student     | 2010-01-02 |
              And a claim set is uploaded to CMS that grants "Student" access to "E2E-StudentWithCTECourseEnrollmentsClaimSet" using authorization strategy "StudentWithCTECourseEnrollments"
              And the claim set upload to CMS should be successful
              And the claimSet "E2E-StudentWithCTECourseEnrollmentsClaimSet" is authorized with namespace "uri://ed-fi.org" and educationOrganizationIds ""
              And the custom auth view "StudentWithCTECourseEnrollments" authorizes Student "604821"
             When a GET request is made to "/ed-fi/students?totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  {
                      "Total-Count": "1"
                  }
                  """
              And the response body is
                  """
                  [
                      {
                          "id": "{id}",
                          "studentUniqueId": "604821",
                          "firstName": "Authorized",
                          "lastSurname": "Student",
                          "birthDate": "2010-01-01"
                      }
                  ]
                  """

        @e2e-ci-shard-3
        Scenario: Empty custom view returns an empty successful GET-many response
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with namespacePrefixes "uri://ed-fi.org"
              And the system has these "students"
                  | studentUniqueId | firstName | lastSurname | birthDate  |
                  | "604823"        | Empty     | Student     | 2010-01-03 |
              And a claim set is uploaded to CMS that grants "Student" access to "E2E-StudentWithEmptyCustomViewClaimSet" using authorization strategy "StudentWithEmptyCustomView"
              And the claim set upload to CMS should be successful
              And the claimSet "E2E-StudentWithEmptyCustomViewClaimSet" is authorized with namespace "uri://ed-fi.org" and educationOrganizationIds ""
              And the custom auth view "StudentWithEmptyCustomView" authorizes no Students
             When a GET request is made to "/ed-fi/students?totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  {
                      "Total-Count": "0"
                  }
                  """
              And the response body is
                  """
                  []
                  """

        @e2e-ci-shard-3
        Scenario: Missing custom view returns system error
            Given a claim set is uploaded to CMS that grants "Student" access to "E2E-StudentWithMissingCustomViewClaimSet" using authorization strategy "StudentWithMissingCustomView"
              And the claim set upload to CMS should be successful
              And the claimSet "E2E-StudentWithMissingCustomViewClaimSet" is authorized with namespace "uri://ed-fi.org" and educationOrganizationIds ""
             When a GET request is made to "/ed-fi/students"
             Then it should respond with 500
              And the response body should contain "urn:ed-fi:api:system"

        @e2e-ci-shard-3
        Scenario: Custom view without DocumentId returns system error
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with namespacePrefixes "uri://ed-fi.org"
              And the system has these "students"
                  | studentUniqueId | firstName | lastSurname | birthDate  |
                  | "604824"        | Invalid   | Student     | 2010-01-04 |
              And a claim set is uploaded to CMS that grants "Student" access to "E2E-StudentWithInvalidCustomViewClaimSet" using authorization strategy "StudentWithInvalidCustomView"
              And the claim set upload to CMS should be successful
              And the claimSet "E2E-StudentWithInvalidCustomViewClaimSet" is authorized with namespace "uri://ed-fi.org" and educationOrganizationIds ""
              And the custom auth view "StudentWithInvalidCustomView" omits DocumentId
             When a GET request is made to "/ed-fi/students"
             Then it should respond with 500
              And the response body should contain "urn:ed-fi:api:system"

    Rule: Single-record custom view authorization filters by basis-resource DocumentId

        The basis resource is the Student itself, so each scenario's view either contains the target
        Student's DocumentId or contains a different Student's. The view is left non-empty on the denial
        scenarios so a denial cannot come from an empty view instead of from the filter.

        @e2e-ci-shard-3
        Scenario: Custom view authorizes GET by id for a Student it includes
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with namespacePrefixes "uri://ed-fi.org"
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "604831",
                      "firstName": "Included",
                      "lastSurname": "Student",
                      "birthDate": "2010-01-05"
                  }
                  """
             Then it should respond with 201 or 200
            Given a claim set is uploaded to CMS that grants "Student" access to "E2E-StudentWithCTECourseEnrollmentsClaimSet" using authorization strategy "StudentWithCTECourseEnrollments"
              And the claim set upload to CMS should be successful
              And the claimSet "E2E-StudentWithCTECourseEnrollmentsClaimSet" is authorized with namespace "uri://ed-fi.org" and educationOrganizationIds ""
              And the custom auth view "StudentWithCTECourseEnrollments" authorizes Student "604831"
             When a GET request is made to "/ed-fi/students/{id}"
             Then it should respond with 200
              And the record can be retrieved with a GET request
                  """
                  {
                      "id": "{id}",
                      "studentUniqueId": "604831",
                      "firstName": "Included",
                      "lastSurname": "Student",
                      "birthDate": "2010-01-05"
                  }
                  """

        @e2e-ci-shard-3 @MssqlRepresentative
        Scenario: Custom view denies GET by id for a Student it excludes
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with namespacePrefixes "uri://ed-fi.org"
              And the system has these "students"
                  | studentUniqueId | firstName | lastSurname | birthDate  |
                  | "604832"        | Other     | Student     | 2010-01-06 |
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "604833",
                      "firstName": "Excluded",
                      "lastSurname": "Student",
                      "birthDate": "2010-01-07"
                  }
                  """
             Then it should respond with 201 or 200
            Given a claim set is uploaded to CMS that grants "Student" access to "E2E-StudentWithCTECourseEnrollmentsClaimSet" using authorization strategy "StudentWithCTECourseEnrollments"
              And the claim set upload to CMS should be successful
              And the claimSet "E2E-StudentWithCTECourseEnrollmentsClaimSet" is authorized with namespace "uri://ed-fi.org" and educationOrganizationIds ""
              And the custom auth view "StudentWithCTECourseEnrollments" authorizes Student "604832"
             When a GET request is made to "/ed-fi/students/{id}"
             Then it should respond with 403
              And the response body is
                  """
                  {
                      "detail": "Access to the requested data could not be authorized. Hint: You may need a Student with CTE Course Enrollments.",
                      "type": "urn:ed-fi:api:security:authorization",
                      "title": "Authorization Denied",
                      "status": 403,
                      "validationErrors": {},
                      "errors": [
                        "The caller is not authorized to perform the requested operation on the item based on the existing value of the 'StudentUniqueId' property of the item."
                      ]
                  }
                  """
              And the response body has a non-empty correlationId

        @e2e-ci-shard-3
        Scenario: Custom view denies PUT for a Student it excludes
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with namespacePrefixes "uri://ed-fi.org"
              And the system has these "students"
                  | studentUniqueId | firstName | lastSurname | birthDate  |
                  | "604834"        | Other     | Student     | 2010-01-08 |
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "604835",
                      "firstName": "Excluded",
                      "lastSurname": "Student",
                      "birthDate": "2010-01-09"
                  }
                  """
             Then it should respond with 201 or 200
            Given a claim set is uploaded to CMS that grants "Student" access to "E2E-StudentWithCTECourseEnrollmentsClaimSet" using authorization strategy "StudentWithCTECourseEnrollments"
              And the claim set upload to CMS should be successful
              And the claimSet "E2E-StudentWithCTECourseEnrollmentsClaimSet" is authorized with namespace "uri://ed-fi.org" and educationOrganizationIds ""
              And the custom auth view "StudentWithCTECourseEnrollments" authorizes Student "604834"
             When a PUT request is made to "/ed-fi/students/{id}" with
                  """
                  {
                      "id": "{id}",
                      "studentUniqueId": "604835",
                      "firstName": "Renamed",
                      "lastSurname": "Student",
                      "birthDate": "2010-01-09"
                  }
                  """
             Then it should respond with 403
              And the response body is
                  """
                  {
                      "detail": "Access to the requested data could not be authorized. Hint: You may need a Student with CTE Course Enrollments.",
                      "type": "urn:ed-fi:api:security:authorization",
                      "title": "Authorization Denied",
                      "status": 403,
                      "validationErrors": {},
                      "errors": [
                        "The caller is not authorized to perform the requested operation on the item based on the existing value of the 'StudentUniqueId' property of the item."
                      ]
                  }
                  """

        @e2e-ci-shard-3
        Scenario: Custom view denies DELETE for a Student it excludes
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with namespacePrefixes "uri://ed-fi.org"
              And the system has these "students"
                  | studentUniqueId | firstName | lastSurname | birthDate  |
                  | "604836"        | Other     | Student     | 2010-01-10 |
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "604837",
                      "firstName": "Excluded",
                      "lastSurname": "Student",
                      "birthDate": "2010-01-11"
                  }
                  """
             Then it should respond with 201 or 200
            Given a claim set is uploaded to CMS that grants "Student" access to "E2E-StudentWithCTECourseEnrollmentsClaimSet" using authorization strategy "StudentWithCTECourseEnrollments"
              And the claim set upload to CMS should be successful
              And the claimSet "E2E-StudentWithCTECourseEnrollmentsClaimSet" is authorized with namespace "uri://ed-fi.org" and educationOrganizationIds ""
              And the custom auth view "StudentWithCTECourseEnrollments" authorizes Student "604836"
             When a DELETE request is made to "/ed-fi/students/{id}"
             Then it should respond with 403
              And the response body is
                  """
                  {
                      "detail": "Access to the requested data could not be authorized. Hint: You may need a Student with CTE Course Enrollments.",
                      "type": "urn:ed-fi:api:security:authorization",
                      "title": "Authorization Denied",
                      "status": 403,
                      "validationErrors": {},
                      "errors": [
                        "The caller is not authorized to perform the requested operation on the item based on the existing value of the 'StudentUniqueId' property of the item."
                      ]
                  }
                  """

        @e2e-ci-shard-3
        Scenario: Custom view authorizes DELETE for a Student it includes
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with namespacePrefixes "uri://ed-fi.org"
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "604838",
                      "firstName": "Deletable",
                      "lastSurname": "Student",
                      "birthDate": "2010-01-12"
                  }
                  """
             Then it should respond with 201 or 200
            Given a claim set is uploaded to CMS that grants "Student" access to "E2E-StudentWithCTECourseEnrollmentsClaimSet" using authorization strategy "StudentWithCTECourseEnrollments"
              And the claim set upload to CMS should be successful
              And the claimSet "E2E-StudentWithCTECourseEnrollmentsClaimSet" is authorized with namespace "uri://ed-fi.org" and educationOrganizationIds ""
              And the custom auth view "StudentWithCTECourseEnrollments" authorizes Student "604838"
             When a DELETE request is made to "/ed-fi/students/{id}"
             Then it should respond with 204

        @e2e-ci-shard-3
        Scenario: Custom view denies POST create even when the view authorizes the same identity
            Given a claim set is uploaded to CMS that grants "Student" access to "E2E-StudentWithCTECourseEnrollmentsClaimSet" using authorization strategy "StudentWithCTECourseEnrollments"
              And the claim set upload to CMS should be successful
              And the claimSet "E2E-StudentWithCTECourseEnrollmentsClaimSet" is authorized with namespace "uri://ed-fi.org" and educationOrganizationIds ""
              And the custom auth view "StudentWithCTECourseEnrollments" authorizes Student "604839"
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "604839",
                      "firstName": "Created",
                      "lastSurname": "Student",
                      "birthDate": "2010-01-13"
                  }
                  """
             Then it should respond with 403
              And the response body is
                  """
                  {
                      "detail": "Access to the requested data could not be authorized. Hint: You may need a Student with CTE Course Enrollments.",
                      "type": "urn:ed-fi:api:security:authorization",
                      "title": "Authorization Denied",
                      "status": 403,
                      "validationErrors": {},
                      "errors": [
                        "The caller is not authorized to perform the requested operation on the item based on the proposed value of the 'StudentUniqueId' property of the item."
                      ]
                  }
                  """
