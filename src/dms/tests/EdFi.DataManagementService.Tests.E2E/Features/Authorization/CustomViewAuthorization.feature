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
