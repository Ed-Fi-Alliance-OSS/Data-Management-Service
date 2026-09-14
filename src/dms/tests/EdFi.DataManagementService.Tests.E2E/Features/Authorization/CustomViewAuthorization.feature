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

    # ------------------------------------------------------------------------------------------------------------
    # ODS Postman parity. The Rules below mirror, folder by folder, the ODS "Ed-Fi ODS-API Custom View-Based
    # Authorization Test Suite" Postman collection, whose views and "Custom View Test" claim set the ODS
    # integration test harness provisions (Ed-Fi-ODS-Implementation, EdFi.Ods.Api.IntegrationTestHarness).
    # Each migrated lifecycle seeds under a broad client, creates the view, and uploads one claim set
    # carrying every resource claim it touches (upload-claims replaces the whole hierarchy). The additional
    # write-regression scenarios restore an unrestricted claim set to verify the stored data after denial.
    #
    # Known divergence, intentional per auth.md § "Execution order": when a custom view (an AND strategy) and
    # relationship strategies (the OR group) both deny, ODS folds every deferred check into one SQL statement
    # and lists every participating hint in one detail, while DMS runs the AND stage first and reports the
    # first denial alone. The composition scenario therefore asserts the custom-view ProblemDetails on its
    # own; the errors line matches ODS verbatim.
    # ------------------------------------------------------------------------------------------------------------

    Rule: A referenced-person basis authorizes the subject through the Student it references (ODS "Simple View-based Authorization")

        The view is data-driven (every Student with a StudentSectionAssociation), so enrolling and
        unenrolling the Student flips authorization live, without touching the view, exactly like the ODS
        harness's StudentWithCTECourseEnrollments view. The basis is a referenced Student, not the subject
        itself, so this exercises the person-basis path on Create, Read, Update, Delete, and ReadChanges.

        @e2e-ci-shard-3 @MssqlRepresentative
        Scenario: Referenced Student basis authorizes the StudentProgramAssociation lifecycle through live enrollments
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School                 |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency |
                  | uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent             |
                  | uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade                               |
                  | uri://ed-fi.org/ProgramTypeDescriptor#Gifted and Talented                      |
                  | uri://ed-fi.org/TermDescriptor#Fall Semester                                   |
                  | uri://ed-fi.org/CourseIdentificationSystemDescriptor#State course code         |
              And the system has these "schoolYearTypes"
                  | schoolYear | currentSchoolYear | schoolYearDescription |
                  | 2022       | false             | "2021-2022"           |
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | categories                                                                                                                      | localEducationAgencyCategoryDescriptor                                |
                  | 255901                 | Grand Bend ISD    | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency"} ] | "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent" |
              And the system has these "schools"
                  | schoolId  | nameOfInstitution      | localEducationAgencyReference        | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 255901001 | Grand Bend High School | {"localEducationAgencyId": 255901}   | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName | lastSurname | birthDate  |
                  | "604851"        | Lifecycle | Student     | 2010-02-01 |
              And the system has these "programs"
                  | programName         | programTypeDescriptor                                       | educationOrganizationReference        |
                  | Gifted and Talented | uri://ed-fi.org/ProgramTypeDescriptor#Gifted and Talented   | {"educationOrganizationId": 255901}   |
              And the system has these "courses"
                  | courseCode | identificationCodes                                                                                                                                       | educationOrganizationReference        | courseTitle        | numberOfParts |
                  | PHOTJOUR   | [{"identificationCode": "PHOTJOUR", "courseIdentificationSystemDescriptor": "uri://ed-fi.org/CourseIdentificationSystemDescriptor#State course code"}] | {"educationOrganizationId": 255901001} | Photojournalism    | 1             |
              And the system has these "sessions"
                  | sessionName               | schoolReference        | schoolYearTypeReference | beginDate  | endDate    | totalInstructionalDays | termDescriptor                                 |
                  | "2021-2022 Fall Semester" | {"schoolId": 255901001} | {"schoolYear": 2022}    | 2021-08-23 | 2021-12-17 | 81                     | "uri://ed-fi.org/TermDescriptor#Fall Semester" |
              And the system has these "courseOfferings"
                  | localCourseCode | courseReference                                              | schoolReference         | sessionReference                                                                        |
                  | PHOTJOUR        | {"courseCode": "PHOTJOUR", "educationOrganizationId": 255901001} | {"schoolId": 255901001} | {"schoolId": 255901001, "schoolYear": 2022, "sessionName": "2021-2022 Fall Semester"} |
              And the system has these "sections"
                  | sectionIdentifier              | courseOfferingReference                                                                                                            |
                  | 25590100101Trad124PHOTJOUR1201 | {"localCourseCode": "PHOTJOUR", "schoolId": 255901001, "schoolYear": 2022, "sessionName": "2021-2022 Fall Semester"}                |
              And the custom auth view "StudentWithCTECourseEnrollments" authorizes Students with a section enrollment
              And a claim set "E2E-CustomViewLifecycleClaimSet" is uploaded to CMS with these resource claims
                  | resource                  | authorizationStrategies         |
                  | StudentProgramAssociation | StudentWithCTECourseEnrollments |
                  | StudentSectionAssociation | NoFurtherAuthorizationRequired  |
              And the claim set upload to CMS should be successful
              And the claimSet "E2E-CustomViewLifecycleClaimSet" is authorized with namespace "uri://ed-fi.org" and educationOrganizationIds ""
             # Not enrolled: the view has no row for the Student, so the proposed value is denied.
             When a POST request is made to "/ed-fi/studentProgramAssociations" with
                  """
                  {
                      "educationOrganizationReference": { "educationOrganizationId": 255901 },
                      "programReference": { "educationOrganizationId": 255901, "programName": "Gifted and Talented", "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Gifted and Talented" },
                      "studentReference": { "studentUniqueId": "604851" },
                      "beginDate": "2021-08-30"
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
             # Enroll the Student in the section: the view now contains the Student.
             When a POST request is made to "/ed-fi/studentSectionAssociations" with
                  """
                  {
                      "sectionReference": { "localCourseCode": "PHOTJOUR", "schoolId": 255901001, "schoolYear": 2022, "sectionIdentifier": "25590100101Trad124PHOTJOUR1201", "sessionName": "2021-2022 Fall Semester" },
                      "studentReference": { "studentUniqueId": "604851" },
                      "beginDate": "2021-08-23"
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "cteEnrollmentId" variable
             When a POST request is made to "/ed-fi/studentProgramAssociations" with
                  """
                  {
                      "educationOrganizationReference": { "educationOrganizationId": 255901 },
                      "programReference": { "educationOrganizationId": 255901, "programName": "Gifted and Talented", "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Gifted and Talented" },
                      "studentReference": { "studentUniqueId": "604851" },
                      "beginDate": "2021-08-30"
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "cteProgramAssociationId" variable
             When a GET request is made to "/ed-fi/studentProgramAssociations/{cteProgramAssociationId}"
             Then it should respond with 200
             When a GET request is made to "/ed-fi/studentProgramAssociations?studentUniqueId=604851&totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  { "Total-Count": "1" }
                  """
              And total of records should be 1
              And the response body path "0.id" should equal request variable "cteProgramAssociationId"
             # Unenroll: the Student leaves the view and every existing-value check is denied.
             When a DELETE request is made to "/ed-fi/studentSectionAssociations/{cteEnrollmentId}"
             Then it should respond with 204
             When a GET request is made to "/ed-fi/studentProgramAssociations/{cteProgramAssociationId}"
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
             When a GET request is made to "/ed-fi/studentProgramAssociations?studentUniqueId=604851&totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  { "Total-Count": "0" }
                  """
              And the response body is
                  """
                  []
                  """
             When a DELETE request is made to "/ed-fi/studentProgramAssociations/{cteProgramAssociationId}"
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
             # Re-enroll, then the delete succeeds and the tombstone is visible through the view on ReadChanges.
             When a POST request is made to "/ed-fi/studentSectionAssociations" with
                  """
                  {
                      "sectionReference": { "localCourseCode": "PHOTJOUR", "schoolId": 255901001, "schoolYear": 2022, "sectionIdentifier": "25590100101Trad124PHOTJOUR1201", "sessionName": "2021-2022 Fall Semester" },
                      "studentReference": { "studentUniqueId": "604851" },
                      "beginDate": "2021-08-23"
                  }
                  """
             Then it should respond with 201
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "cteBeforeDeleteVersion"
             When a DELETE request is made to "/ed-fi/studentProgramAssociations/{cteProgramAssociationId}"
             Then it should respond with 204
             When a GET request is made to "/ed-fi/studentProgramAssociations/deletes?minChangeVersion={cteBeforeDeleteVersion}&totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  { "total-count": 1 }
                  """
              And total of records should be 1
              And the response body path "0.id" should equal request variable "cteProgramAssociationId"
              And the response body path "0.keyValues.studentUniqueId" should have value "604851"
              And the response body path "0.keyValues.programName" should have value "Gifted and Talented"
              And the response body path "0.keyValues.beginDate" should have value "2021-08-30"
              And the response body path "0.keyValues.educationOrganizationId" should have value "255901"
              And the response body path "0.keyValues.programEducationOrganizationId" should have value "255901"
              And the response body path "0.keyValues.programTypeDescriptor" should have value "uri://ed-fi.org/ProgramTypeDescriptor#Gifted and Talented"
             # ReadChanges must evaluate current view membership even after the subject has been deleted.
             When a GET request is made to "/ed-fi/studentSectionAssociations?studentUniqueId=604851"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.id" is stored in request variable "cteCurrentEnrollmentId"
             When a DELETE request is made to "/ed-fi/studentSectionAssociations/{cteCurrentEnrollmentId}"
             Then it should respond with 204
             When a GET request is made to "/ed-fi/studentProgramAssociations/deletes?minChangeVersion={cteBeforeDeleteVersion}&totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  { "total-count": 0 }
                  """
              And the response body is
                  """
                  []
                  """
             When a POST request is made to "/ed-fi/studentSectionAssociations" with
                  """
                  {
                      "sectionReference": { "localCourseCode": "PHOTJOUR", "schoolId": 255901001, "schoolYear": 2022, "sectionIdentifier": "25590100101Trad124PHOTJOUR1201", "sessionName": "2021-2022 Fall Semester" },
                      "studentReference": { "studentUniqueId": "604851" },
                      "beginDate": "2021-08-23"
                  }
                  """
             Then it should respond with 201
             When a GET request is made to "/ed-fi/studentProgramAssociations/deletes?minChangeVersion={cteBeforeDeleteVersion}&totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  { "total-count": 1 }
                  """
              And total of records should be 1
              And the response body path "0.id" should equal request variable "cteProgramAssociationId"

    Rule: A custom view composes with relationship strategies (ODS "Multiple Relationship-based with Custom view-based authorization")

        StudentSpecialEducationProgramEligibilityAssociation is configured with two relationship strategies
        (OR group) plus the custom view (AND). The relationship is satisfied through a responsibility
        association after the school association is removed, so only the view decides the outcome.

        @e2e-ci-shard-3
        Scenario: Custom view composed with two relationship strategies gates the StudentSpecialEducationProgramEligibilityAssociation lifecycle
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901"
              And the system has these descriptors
                  | descriptorValue                                           |
                  | uri://ed-fi.org/IDEAPartDescriptor#IDEA Part B            |
                  | uri://ed-fi.org/ResponsibilityDescriptor#Attendance       |
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School                 |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency |
                  | uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent             |
                  | uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade                               |
                  | uri://ed-fi.org/ProgramTypeDescriptor#Bilingual                                |
                  | uri://ed-fi.org/TermDescriptor#Fall Semester                                   |
                  | uri://ed-fi.org/CourseIdentificationSystemDescriptor#State course code         |
              And the system has these "schoolYearTypes"
                  | schoolYear | currentSchoolYear | schoolYearDescription |
                  | 2022       | false             | "2021-2022"           |
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | categories                                                                                                                      | localEducationAgencyCategoryDescriptor                                |
                  | 255901                 | Grand Bend ISD    | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency"} ] | "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent" |
              And the system has these "schools"
                  | schoolId  | nameOfInstitution      | localEducationAgencyReference        | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 255901001 | Grand Bend High School | {"localEducationAgencyId": 255901}   | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName | lastSurname | birthDate  |
                  | "604852"        | Composed  | Student     | 2010-02-02 |
              And the system has these "programs"
                  | programName | programTypeDescriptor                            | educationOrganizationReference        |
                  | Bilingual   | uri://ed-fi.org/ProgramTypeDescriptor#Bilingual  | {"educationOrganizationId": 255901}   |
              And the system has these "courses"
                  | courseCode | identificationCodes                                                                                                                                       | educationOrganizationReference        | courseTitle        | numberOfParts |
                  | PHOTJOUR   | [{"identificationCode": "PHOTJOUR", "courseIdentificationSystemDescriptor": "uri://ed-fi.org/CourseIdentificationSystemDescriptor#State course code"}] | {"educationOrganizationId": 255901001} | Photojournalism    | 1             |
              And the system has these "sessions"
                  | sessionName               | schoolReference        | schoolYearTypeReference | beginDate  | endDate    | totalInstructionalDays | termDescriptor                                 |
                  | "2021-2022 Fall Semester" | {"schoolId": 255901001} | {"schoolYear": 2022}    | 2021-08-23 | 2021-12-17 | 81                     | "uri://ed-fi.org/TermDescriptor#Fall Semester" |
              And the system has these "courseOfferings"
                  | localCourseCode | courseReference                                              | schoolReference         | sessionReference                                                                        |
                  | PHOTJOUR        | {"courseCode": "PHOTJOUR", "educationOrganizationId": 255901001} | {"schoolId": 255901001} | {"schoolId": 255901001, "schoolYear": 2022, "sessionName": "2021-2022 Fall Semester"} |
              And the system has these "sections"
                  | sectionIdentifier              | courseOfferingReference                                                                                                            |
                  | 25590100101Trad124PHOTJOUR1201 | {"localCourseCode": "PHOTJOUR", "schoolId": 255901001, "schoolYear": 2022, "sessionName": "2021-2022 Fall Semester"}                |
              And the custom auth view "StudentWithCTECourseEnrollments" authorizes Students with a section enrollment
              And a claim set "E2E-CustomViewComposedClaimSet" is uploaded to CMS with these resource claims
                  | resource                                             | authorizationStrategies                                                                                                   | readChangesAuthorizationStrategies                                                                                                                        |
                  | StudentSpecialEducationProgramEligibilityAssociation | RelationshipsWithEdOrgsAndPeople, RelationshipsWithStudentsOnlyThroughResponsibility, StudentWithCTECourseEnrollments   | RelationshipsWithEdOrgsAndPeopleIncludingDeletes, RelationshipsWithStudentsOnlyThroughResponsibilityIncludingDeletes, StudentWithCTECourseEnrollments |
                  | StudentSchoolAssociation                             | NoFurtherAuthorizationRequired                                                                                            |                                                                                                                                                           |
                  | StudentEducationOrganizationResponsibilityAssociation | NoFurtherAuthorizationRequired                                                                                           |                                                                                                                                                           |
                  | StudentSectionAssociation                            | NoFurtherAuthorizationRequired                                                                                            |                                                                                                                                                           |
              And the claim set upload to CMS should be successful
              And the claimSet "E2E-CustomViewComposedClaimSet" is authorized with namespace "uri://ed-fi.org" and educationOrganizationIds "255901"
             # Without a CTE enrollment the custom view denies the proposed Student.
             When a POST request is made to "/ed-fi/studentSpecialEducationProgramEligibilityAssociations" with
                  """
                  {
                      "consentToEvaluationReceivedDate": "2024-08-02",
                      "educationOrganizationReference": { "educationOrganizationId": 255901 },
                      "programReference": { "educationOrganizationId": 255901, "programName": "Bilingual", "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Bilingual" },
                      "studentReference": { "studentUniqueId": "604852" },
                      "ideaPartDescriptor": "uri://ed-fi.org/IDEAPartDescriptor#IDEA Part B"
                  }
                  """
             Then it should respond with 403
              And the response body should contain "You may need a Student with CTE Course Enrollments."
             # Relate the Student through a school association and a responsibility association, then enroll.
             When a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                      "entryDate": "2022-10-29",
                      "schoolReference": { "schoolId": 255901001 },
                      "studentReference": { "studentUniqueId": "604852" },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "composedSsaId" variable
             When a POST request is made to "/ed-fi/studentEducationOrganizationResponsibilityAssociations" with
                  """
                  {
                      "beginDate": "2024-08-02",
                      "responsibilityDescriptor": "uri://ed-fi.org/ResponsibilityDescriptor#Attendance",
                      "educationOrganizationReference": { "educationOrganizationId": 255901 },
                      "studentReference": { "studentUniqueId": "604852" }
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/studentSectionAssociations" with
                  """
                  {
                      "sectionReference": { "localCourseCode": "PHOTJOUR", "schoolId": 255901001, "schoolYear": 2022, "sectionIdentifier": "25590100101Trad124PHOTJOUR1201", "sessionName": "2021-2022 Fall Semester" },
                      "studentReference": { "studentUniqueId": "604852" },
                      "beginDate": "2021-08-23"
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "composedEnrollmentId" variable
             # Drop the school association: the responsibility relationship alone must carry the OR group.
             When a DELETE request is made to "/ed-fi/studentSchoolAssociations/{composedSsaId}"
             Then it should respond with 204
             When a POST request is made to "/ed-fi/studentSpecialEducationProgramEligibilityAssociations" with
                  """
                  {
                      "consentToEvaluationReceivedDate": "2024-08-02",
                      "educationOrganizationReference": { "educationOrganizationId": 255901 },
                      "programReference": { "educationOrganizationId": 255901, "programName": "Bilingual", "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Bilingual" },
                      "studentReference": { "studentUniqueId": "604852" },
                      "ideaPartDescriptor": "uri://ed-fi.org/IDEAPartDescriptor#IDEA Part B"
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "composedEligibilityId" variable
             When a GET request is made to "/ed-fi/studentSpecialEducationProgramEligibilityAssociations/{composedEligibilityId}"
             Then it should respond with 200
             When a GET request is made to "/ed-fi/studentSpecialEducationProgramEligibilityAssociations?totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  { "Total-Count": "1" }
                  """
              And total of records should be 1
              And the response body path "0.id" should equal request variable "composedEligibilityId"
             # Unenroll: the relationship still holds, the view no longer does. DMS reports the custom-view
             # denial alone (see the divergence note above); the errors line is the ODS text verbatim.
             When a DELETE request is made to "/ed-fi/studentSectionAssociations/{composedEnrollmentId}"
             Then it should respond with 204
             When a GET request is made to "/ed-fi/studentSpecialEducationProgramEligibilityAssociations/{composedEligibilityId}"
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
             When a GET request is made to "/ed-fi/studentSpecialEducationProgramEligibilityAssociations?totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  { "Total-Count": "0" }
                  """
              And the response body is
                  """
                  []
                  """
             When a DELETE request is made to "/ed-fi/studentSpecialEducationProgramEligibilityAssociations/{composedEligibilityId}"
             Then it should respond with 403
              And the response body should contain "You may need a Student with CTE Course Enrollments."
             # Re-enroll, delete, and read the tombstone under the IncludingDeletes relationship variants plus the view.
             When a POST request is made to "/ed-fi/studentSectionAssociations" with
                  """
                  {
                      "sectionReference": { "localCourseCode": "PHOTJOUR", "schoolId": 255901001, "schoolYear": 2022, "sectionIdentifier": "25590100101Trad124PHOTJOUR1201", "sessionName": "2021-2022 Fall Semester" },
                      "studentReference": { "studentUniqueId": "604852" },
                      "beginDate": "2021-08-23"
                  }
                  """
             Then it should respond with 201
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "composedBeforeDeleteVersion"
             When a DELETE request is made to "/ed-fi/studentSpecialEducationProgramEligibilityAssociations/{composedEligibilityId}"
             Then it should respond with 204
             When a GET request is made to "/ed-fi/studentSpecialEducationProgramEligibilityAssociations/deletes?minChangeVersion={composedBeforeDeleteVersion}&totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  { "total-count": 1 }
                  """
              And total of records should be 1
              And the response body path "0.id" should equal request variable "composedEligibilityId"
              And the response body path "0.keyValues.studentUniqueId" should have value "604852"
              And the response body path "0.keyValues.programName" should have value "Bilingual"
              And the response body path "0.keyValues.consentToEvaluationReceivedDate" should have value "2024-08-02"
              And the response body path "0.keyValues.educationOrganizationId" should have value "255901"
              And the response body path "0.keyValues.programEducationOrganizationId" should have value "255901"
              And the response body path "0.keyValues.programTypeDescriptor" should have value "uri://ed-fi.org/ProgramTypeDescriptor#Bilingual"

    Rule: A composite-natural-key basis authorizes through the referenced Assessment (ODS "Composite Key View-based Authorization")

        Assessment is identified by identifier + namespace. The view admits Assessments whose identifier
        starts with ACT; StudentAssessment additionally carries RelationshipsWithEdOrgsOnly, scoped through
        the reported school. ODS had to leave the relationship strategy off ReadChanges because its
        tombstone lacks the reported school; DMS tracks that securable, so both strategies apply to /deletes.

        @e2e-ci-shard-3
        Scenario: Assessment basis composed with RelationshipsWithEdOrgsOnly gates the StudentAssessment lifecycle
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School                 |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency |
                  | uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent             |
                  | uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade                               |
                  | uri://ed-fi.org/AcademicSubjectDescriptor#English                              |
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution   | categories                                                                                                                      | localEducationAgencyCategoryDescriptor                                |
                  | 255901                 | Grand Bend ISD      | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency"} ] | "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent" |
                  | 255902                 | Unaffiliated ISD    | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency"} ] | "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent" |
              And the system has these "schools"
                  | schoolId  | nameOfInstitution       | localEducationAgencyReference        | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 255901001 | Grand Bend High School  | {"localEducationAgencyId": 255901}   | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 255902001 | Unaffiliated High School | {"localEducationAgencyId": 255902}   | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName | lastSurname | birthDate  |
                  | "604853"        | Assessed  | Student     | 2010-02-03 |
              And the system has these "assessments"
                  | assessmentIdentifier | namespace                                     | assessmentTitle | academicSubjects                                                                        |
                  | ACT English          | uri://ed-fi.org/Assessment/Assessment.xml     | ACT English     | [ {"academicSubjectDescriptor": "uri://ed-fi.org/AcademicSubjectDescriptor#English"} ] |
                  | District Benchmark   | uri://ed-fi.org/Assessment/Assessment.xml     | Benchmark       | [ {"academicSubjectDescriptor": "uri://ed-fi.org/AcademicSubjectDescriptor#English"} ] |
              And the custom auth view "AssessmentWithAnACTIdentifier" authorizes Assessments whose identifier starts with "ACT"
              And a claim set "E2E-CustomViewAssessmentClaimSet" is uploaded to CMS with these resource claims
                  | resource          | authorizationStrategies                                      |
                  | StudentAssessment | AssessmentWithAnACTIdentifier, RelationshipsWithEdOrgsOnly   |
              And the claim set upload to CMS should be successful
              And the claimSet "E2E-CustomViewAssessmentClaimSet" is authorized with educationOrganizationIds "255901"
             # Unaffiliated school and a non-ACT assessment: the view (AND stage) denies first.
             When a POST request is made to "/ed-fi/studentAssessments" with
                  """
                  {
                      "studentReference": { "studentUniqueId": "604853" },
                      "reportedSchoolReference": { "schoolId": 255902001 },
                      "assessmentReference": { "assessmentIdentifier": "District Benchmark", "namespace": "uri://ed-fi.org/Assessment/Assessment.xml" },
                      "studentAssessmentIdentifier": "SA-1"
                  }
                  """
             Then it should respond with 403
              And the response body is
                  """
                  {
                      "detail": "Access to the requested data could not be authorized. Hint: You may need an Assessment with an ACT Identifier.",
                      "type": "urn:ed-fi:api:security:authorization",
                      "title": "Authorization Denied",
                      "status": 403,
                      "validationErrors": {},
                      "errors": [
                        "The caller is not authorized to perform the requested operation on the item based on the proposed values of one or more of the following properties of the item: 'AssessmentIdentifier', 'Namespace'."
                      ]
                  }
                  """
             # Affiliated school, still a non-ACT assessment.
             When a POST request is made to "/ed-fi/studentAssessments" with
                  """
                  {
                      "studentReference": { "studentUniqueId": "604853" },
                      "reportedSchoolReference": { "schoolId": 255901001 },
                      "assessmentReference": { "assessmentIdentifier": "District Benchmark", "namespace": "uri://ed-fi.org/Assessment/Assessment.xml" },
                      "studentAssessmentIdentifier": "SA-1"
                  }
                  """
             Then it should respond with 403
              And the response body should contain "Hint: You may need an Assessment with an ACT Identifier."
             When a GET request is made to "/ed-fi/studentAssessments?totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  { "Total-Count": "0" }
                  """
              And the response body is
                  """
                  []
                  """
             # ACT assessment at an unaffiliated school: the view passes and the relationship strategy denies.
             When a POST request is made to "/ed-fi/studentAssessments" with
                  """
                  {
                      "studentReference": { "studentUniqueId": "604853" },
                      "reportedSchoolReference": { "schoolId": 255902001 },
                      "assessmentReference": { "assessmentIdentifier": "ACT English", "namespace": "uri://ed-fi.org/Assessment/Assessment.xml" },
                      "studentAssessmentIdentifier": "SA-1"
                  }
                  """
             Then it should respond with 403
              And the response body should not contain "ACT Identifier"
             # Affiliated school and an ACT assessment.
             When a POST request is made to "/ed-fi/studentAssessments" with
                  """
                  {
                      "studentReference": { "studentUniqueId": "604853" },
                      "reportedSchoolReference": { "schoolId": 255901001 },
                      "assessmentReference": { "assessmentIdentifier": "ACT English", "namespace": "uri://ed-fi.org/Assessment/Assessment.xml" },
                      "studentAssessmentIdentifier": "SA-1"
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "actStudentAssessmentId" variable
             When a GET request is made to "/ed-fi/studentAssessments?totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  { "Total-Count": "1" }
                  """
              And total of records should be 1
              And the response body path "0.assessmentReference.assessmentIdentifier" should have value "ACT English"
             When a GET request is made to "/ed-fi/studentAssessments/{actStudentAssessmentId}"
             Then it should respond with 200
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "actBeforeDeleteVersion"
             When a DELETE request is made to "/ed-fi/studentAssessments/{actStudentAssessmentId}"
             Then it should respond with 204
             When a GET request is made to "/ed-fi/studentAssessments/deletes?minChangeVersion={actBeforeDeleteVersion}&totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  { "total-count": 1 }
                  """
              And total of records should be 1
              And the response body path "0.id" should equal request variable "actStudentAssessmentId"
              And the response body path "0.keyValues.assessmentIdentifier" should have value "ACT English"
              And the response body path "0.keyValues.studentUniqueId" should have value "604853"
              And the response body path "0.keyValues.namespace" should have value "uri://ed-fi.org/Assessment/Assessment.xml"
              And the response body path "0.keyValues.studentAssessmentIdentifier" should have value "SA-1"
             # The relationship strategy also filters /deletes: a client scoped to the other district sees nothing.
            Given the claimSet "E2E-CustomViewAssessmentClaimSet" is authorized with educationOrganizationIds "255902"
             When a GET request is made to "/ed-fi/studentAssessments/deletes?minChangeVersion={actBeforeDeleteVersion}&totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  { "total-count": 0 }
                  """
              And the response body is
                  """
                  []
                  """

    Rule: A descriptor basis on an optional property (ODS "Custom View-based on Optional Property")

        StudentTransportation's transportationTypeDescriptor is optional and not part of the identity. The
        view admits TransportationTypeDescriptors whose code value contains "Bus". A missing value is an
        authorization failure of its own (element-required), and ReadChanges cannot be authorized through a
        non-identifying, non-securable property, so /deletes is a security configuration error.

        @e2e-ci-shard-3 @MssqlRepresentative
        Scenario: Descriptor basis on an optional property gates StudentTransportation and rejects ReadChanges
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901"
              And the system has these descriptors
                  | descriptorValue                                                                  |
                  | uri://ed-fi.org/TransportationTypeDescriptor#Special Needs Bus                   |
                  | uri://ed-fi.org/TransportationTypeDescriptor#School Bus                          |
                  | uri://ed-fi.org/TransportationTypeDescriptor#General Public Transportation       |
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School                 |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency |
                  | uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent             |
                  | uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade                               |
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | categories                                                                                                                      | localEducationAgencyCategoryDescriptor                                |
                  | 255901                 | Grand Bend ISD    | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency"} ] | "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent" |
              And the system has these "schools"
                  | schoolId  | nameOfInstitution      | localEducationAgencyReference        | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 255901001 | Grand Bend High School | {"localEducationAgencyId": 255901}   | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName | lastSurname | birthDate  |
                  | "604861"        | Bus       | Rider       | 2010-02-04 |
                  | "604862"        | Transit   | Rider       | 2010-02-05 |
                  | "604863"        | Unknown   | Rider       | 2010-02-06 |
              And the system has these "studentSchoolAssociations"
                  | studentReference                 | schoolReference         | entryGradeLevelDescriptor                          | entryDate  |
                  | {"studentUniqueId": "604861"}    | {"schoolId": 255901001} | uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade | 2022-08-01 |
                  | {"studentUniqueId": "604862"}    | {"schoolId": 255901001} | uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade | 2022-08-01 |
                  | {"studentUniqueId": "604863"}    | {"schoolId": 255901001} | uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade | 2022-08-01 |
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901"
              And the system has these "studentTransportations"
                  | studentReference                 | transportationEducationOrganizationReference | transportationTypeDescriptor                                                   |
                  | {"studentUniqueId": "604861"}    | {"educationOrganizationId": 255901}          | uri://ed-fi.org/TransportationTypeDescriptor#Special Needs Bus                 |
                  | {"studentUniqueId": "604862"}    | {"educationOrganizationId": 255901}          | uri://ed-fi.org/TransportationTypeDescriptor#General Public Transportation     |
              And the custom auth view "TransportationTypeDescriptorWithABus" authorizes "TransportationTypeDescriptor" descriptors whose code value contains "Bus"
              And a claim set "E2E-CustomViewTransportationClaimSet" is uploaded to CMS with these resource claims
                  | resource              | authorizationStrategies                                                  | readChangesAuthorizationStrategies                                                       |
                  | StudentTransportation | RelationshipsWithEdOrgsAndPeople, TransportationTypeDescriptorWithABus   | RelationshipsWithEdOrgsAndPeopleIncludingDeletes, TransportationTypeDescriptorWithABus   |
              And the claim set upload to CMS should be successful
              And the claimSet "E2E-CustomViewTransportationClaimSet" is authorized with educationOrganizationIds "255901"
             When a GET request is made to "/ed-fi/studentTransportations?totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  { "Total-Count": "1" }
                  """
              And total of records should be 1
              And the response body path "0.transportationTypeDescriptor" should have value "uri://ed-fi.org/TransportationTypeDescriptor#Special Needs Bus"
             # Upsert of the existing item whose stored descriptor is outside the view.
             When a POST request is made to "/ed-fi/studentTransportations" with
                  """
                  {
                      "studentReference": { "studentUniqueId": "604862" },
                      "transportationEducationOrganizationReference": { "educationOrganizationId": 255901 },
                      "transportationTypeDescriptor": "uri://ed-fi.org/TransportationTypeDescriptor#General Public Transportation",
                      "specialAccomodationRequirements": "Wheelchair Accessibility"
                  }
                  """
             Then it should respond with 403
              And the response body should contain "Access to the requested data could not be authorized."
             # A new item without the securable value cannot be authorized at all.
             When a POST request is made to "/ed-fi/studentTransportations" with
                  """
                  {
                      "studentReference": { "studentUniqueId": "604863" },
                      "transportationEducationOrganizationReference": { "educationOrganizationId": 255901 },
                      "specialAccomodationRequirements": "Wheelchair Accessibility"
                  }
                  """
             Then it should respond with 403
              And the response body is
                  """
                  {
                      "detail": "Access to the requested data could not be authorized. The 'TransportationTypeDescriptor' value is required for authorization purposes. Hint: You may need a Transportation Type Descriptor with a Bus.",
                      "type": "urn:ed-fi:api:security:authorization:custom-view:access-denied:element-required",
                      "title": "Authorization Denied",
                      "status": 403,
                      "validationErrors": {},
                      "errors": []
                  }
                  """
             # Upsert of the authorized item to another descriptor the view admits.
             When a POST request is made to "/ed-fi/studentTransportations" with
                  """
                  {
                      "studentReference": { "studentUniqueId": "604861" },
                      "transportationEducationOrganizationReference": { "educationOrganizationId": 255901 },
                      "transportationTypeDescriptor": "uri://ed-fi.org/TransportationTypeDescriptor#School Bus"
                  }
                  """
             Then it should respond with 200
             When the resulting id is stored in the "busTransportationId" variable
             When a GET request is made to "/ed-fi/studentTransportations/{busTransportationId}"
             Then it should respond with 200
              And the response body path "transportationTypeDescriptor" should have value "uri://ed-fi.org/TransportationTypeDescriptor#School Bus"
             When a PUT request is made to "/ed-fi/studentTransportations/{busTransportationId}" with
                  """
                  {
                      "id": "{busTransportationId}",
                      "studentReference": { "studentUniqueId": "604861" },
                      "transportationEducationOrganizationReference": { "educationOrganizationId": 255901 },
                      "transportationTypeDescriptor": "uri://ed-fi.org/TransportationTypeDescriptor#Special Needs Bus"
                  }
                  """
             Then it should respond with 204
             When a GET request is made to "/ed-fi/studentTransportations/{busTransportationId}"
             Then it should respond with 200
              And the response body path "transportationTypeDescriptor" should have value "uri://ed-fi.org/TransportationTypeDescriptor#Special Needs Bus"
             # ReadChanges: the descriptor is neither identifying nor securable, so its old value is not tracked.
             When a GET request is made to "/ed-fi/studentTransportations/deletes"
             Then it should respond with 500
              And the response body should contain "urn:ed-fi:api:system:configuration:security"
              And the response body should contain "Security Configuration Error"
              And the response body should contain "A security configuration problem was detected. The request cannot be authorized."
              And the response body should contain "Strategy 'TransportationTypeDescriptorWithABus' uses custom auth view 'auth.TransportationTypeDescriptorWithABus'."
              And the response body should contain "is neither an identifying property nor a securable element of the subject. This is not supported by Change Queries"

        @e2e-ci-shard-3 @MssqlRepresentative
        Scenario Outline: Rejected optional descriptor updates preserve the stored resource
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901"
              And the system has these descriptors
                  | descriptorValue                                                            |
                  | uri://ed-fi.org/TransportationTypeDescriptor#School Bus                      |
                  | uri://ed-fi.org/TransportationTypeDescriptor#General Public Transportation   |
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                               |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency |
                  | uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent             |
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | categories                                                                                                                      | localEducationAgencyCategoryDescriptor                                |
                  | 255901                 | Grand Bend ISD    | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency"} ] | "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent" |
              And the system has these "students"
                  | studentUniqueId | firstName | lastSurname | birthDate  |
                  | "604864"        | Update    | Rider       | 2010-02-07 |
              And a claim set "E2E-CustomViewTransportationSeedClaimSet" is uploaded to CMS with these resource claims
                  | resource              | authorizationStrategies        |
                  | StudentTransportation | NoFurtherAuthorizationRequired |
              And the claim set upload to CMS should be successful
              And the claimSet "E2E-CustomViewTransportationSeedClaimSet" is authorized with namespace "uri://ed-fi.org" and educationOrganizationIds ""
             When a POST request is made to "/ed-fi/studentTransportations" with
                  """
                  {
                      "studentReference": { "studentUniqueId": "604864" },
                      "transportationEducationOrganizationReference": { "educationOrganizationId": 255901 },
                      "transportationTypeDescriptor": "uri://ed-fi.org/TransportationTypeDescriptor#<storedType>",
                      "specialAccomodationRequirements": "Original"
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "transportationUpdateId" variable
             When a GET request is made to "/ed-fi/studentTransportations/{transportationUpdateId}"
             Then it should respond with 200
              And the response body path "_etag" is stored in request variable "transportationBeforeEtag"
              And the response body path "_lastModifiedDate" is stored in request variable "transportationBeforeModified"
            Given the custom auth view "TransportationTypeDescriptorWithABus" authorizes "TransportationTypeDescriptor" descriptors whose code value contains "Bus"
              And a claim set "E2E-CustomViewTransportationUpdateClaimSet" is uploaded to CMS with these resource claims
                  | resource              | authorizationStrategies            |
                  | StudentTransportation | TransportationTypeDescriptorWithABus |
              And the claim set upload to CMS should be successful
              And the claimSet "E2E-CustomViewTransportationUpdateClaimSet" is authorized with namespace "uri://ed-fi.org" and educationOrganizationIds ""
             When a <method> request is made to "<url>" with
                  """
                  {
                      <idProperty>
                      "studentReference": { "studentUniqueId": "604864" },
                      "transportationEducationOrganizationReference": { "educationOrganizationId": 255901 },
                      <proposedProperty>
                      "specialAccomodationRequirements": "Rejected update"
                  }
                  """
             Then it should respond with 403
              And the response body should contain "<failure>"
              And the response body should contain "TransportationTypeDescriptor"
              And the response body has a non-empty correlationId
             # Read with unrestricted credentials so a failed authorization cannot hide a partial write.
             # Upload replaces the CMS hierarchy, so restore the seed claim set before requesting a new token.
            Given a claim set "E2E-CustomViewTransportationSeedClaimSet" is uploaded to CMS with these resource claims
                  | resource              | authorizationStrategies        |
                  | StudentTransportation | NoFurtherAuthorizationRequired |
              And the claim set upload to CMS should be successful
              And the claimSet "E2E-CustomViewTransportationSeedClaimSet" is authorized with namespace "uri://ed-fi.org" and educationOrganizationIds ""
             When a GET request is made to "/ed-fi/studentTransportations/{transportationUpdateId}"
             Then it should respond with 200
              And the response body path "transportationTypeDescriptor" should have value "uri://ed-fi.org/TransportationTypeDescriptor#<storedType>"
              And the response body path "specialAccomodationRequirements" should have value "Original"
              And the response body path "_etag" should equal request variable "transportationBeforeEtag"
              And the response body path "_lastModifiedDate" should equal request variable "transportationBeforeModified"

            Examples:
                  | method | url                                                    | idProperty                         | storedType                    | proposedProperty                                                                                           | failure                                                                    |
                  | POST   | /ed-fi/studentTransportations                          |                                    | School Bus                    | "transportationTypeDescriptor": "uri://ed-fi.org/TransportationTypeDescriptor#General Public Transportation", | proposed value                                                             |
                  | PUT    | /ed-fi/studentTransportations/{transportationUpdateId} | "id": "{transportationUpdateId}", | School Bus                    | "transportationTypeDescriptor": "uri://ed-fi.org/TransportationTypeDescriptor#General Public Transportation", | proposed value                                                             |
                  | POST   | /ed-fi/studentTransportations                          |                                    | School Bus                    |                                                                                                            | urn:ed-fi:api:security:authorization:custom-view:access-denied:element-required |
                  | PUT    | /ed-fi/studentTransportations/{transportationUpdateId} | "id": "{transportationUpdateId}", | School Bus                    |                                                                                                            | urn:ed-fi:api:security:authorization:custom-view:access-denied:element-required |
                  | POST   | /ed-fi/studentTransportations                          |                                    | General Public Transportation | "transportationTypeDescriptor": "uri://ed-fi.org/TransportationTypeDescriptor#School Bus",                    | existing value                                                             |
                  | PUT    | /ed-fi/studentTransportations/{transportationUpdateId} | "id": "{transportationUpdateId}", | General Public Transportation | "transportationTypeDescriptor": "uri://ed-fi.org/TransportationTypeDescriptor#School Bus",                    | existing value                                                             |

    Rule: An abstract EducationOrganization basis (ODS "Custom View-based on EdOrgId")

        AccountabilityRating references the abstract EducationOrganization. The view admits education
        organizations whose category code value contains an "S" word, so Schools qualify and Local Education
        Agencies do not. DMS resolves the basis through the education organization union view.

        @e2e-ci-shard-3 @MssqlRepresentative
        Scenario: Abstract EducationOrganization basis gates the AccountabilityRating lifecycle
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School                 |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency |
                  | uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent             |
                  | uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade                               |
              And the system has these "schoolYearTypes"
                  | schoolYear | currentSchoolYear | schoolYearDescription |
                  | 2022       | false             | "2021-2022"           |
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | categories                                                                                                                      | localEducationAgencyCategoryDescriptor                                |
                  | 255901                 | Grand Bend ISD    | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency"} ] | "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent" |
              And the system has these "schools"
                  | schoolId  | nameOfInstitution      | localEducationAgencyReference        | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 255901001 | Grand Bend High School | {"localEducationAgencyId": 255901}   | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901"
              And the system has these "accountabilityRatings"
                  | educationOrganizationReference        | schoolYearTypeReference | ratingTitle    | rating       |
                  | {"educationOrganizationId": 255901}    | {"schoolYear": 2022}    | District Grade | Met Standard |
                  | {"educationOrganizationId": 255901001} | {"schoolYear": 2022}    | Campus Grade   | Met Standard |
              And the custom auth view "EducationOrganizationWithACategoryContainingAnSWord" authorizes education organizations with a category containing an S word
              And a claim set "E2E-CustomViewEdOrgClaimSet" is uploaded to CMS with these resource claims
                  | resource             | authorizationStrategies                              |
                  | AccountabilityRating | EducationOrganizationWithACategoryContainingAnSWord |
              And the claim set upload to CMS should be successful
              And the claimSet "E2E-CustomViewEdOrgClaimSet" is authorized with namespace "uri://ed-fi.org" and educationOrganizationIds ""
             When a GET request is made to "/ed-fi/accountabilityRatings?totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  { "Total-Count": "1" }
                  """
              And total of records should be 1
              And the response body path "0.educationOrganizationReference.educationOrganizationId" should have value "255901001"
             # The Local Education Agency has no qualifying category.
             When a POST request is made to "/ed-fi/accountabilityRatings" with
                  """
                  {
                      "educationOrganizationReference": { "educationOrganizationId": 255901 },
                      "schoolYearTypeReference": { "schoolYear": 2022 },
                      "ratingTitle": "Test Rating Title",
                      "rating": "Test Rating"
                  }
                  """
             Then it should respond with 403
              And the response body is
                  """
                  {
                      "detail": "Access to the requested data could not be authorized. Hint: You may need an Education Organization with a Category Containing an S Word.",
                      "type": "urn:ed-fi:api:security:authorization",
                      "title": "Authorization Denied",
                      "status": 403,
                      "validationErrors": {},
                      "errors": [
                        "The caller is not authorized to perform the requested operation on the item based on the proposed value of the 'EducationOrganizationId' property of the item."
                      ]
                  }
                  """
             When a POST request is made to "/ed-fi/accountabilityRatings" with
                  """
                  {
                      "educationOrganizationReference": { "educationOrganizationId": 255901001 },
                      "schoolYearTypeReference": { "schoolYear": 2022 },
                      "ratingTitle": "Test Rating Title",
                      "rating": "Test Rating"
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "schoolRatingId" variable
             When a POST request is made to "/ed-fi/accountabilityRatings" with
                  """
                  {
                      "educationOrganizationReference": { "educationOrganizationId": 255901001 },
                      "schoolYearTypeReference": { "schoolYear": 2022 },
                      "ratingTitle": "Test Rating Title",
                      "rating": "Test Rating UPDATED"
                  }
                  """
             Then it should respond with 200
             When a GET request is made to "/ed-fi/accountabilityRatings/{schoolRatingId}"
             Then it should respond with 200
              And the response body path "rating" should have value "Test Rating UPDATED"
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "ratingBeforeDeleteVersion"
             When a DELETE request is made to "/ed-fi/accountabilityRatings/{schoolRatingId}"
             Then it should respond with 204
             When a GET request is made to "/ed-fi/accountabilityRatings/deletes?minChangeVersion={ratingBeforeDeleteVersion}&totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  { "total-count": 1 }
                  """
              And total of records should be 1
              And the response body path "0.id" should equal request variable "schoolRatingId"
              And the response body path "0.keyValues.educationOrganizationId" should have value "255901001"
              And the response body path "0.keyValues.ratingTitle" should have value "Test Rating Title"
              And the response body path "0.keyValues.schoolYear" should have value "2022"

    Rule: A custom view whose basis the subject never references is a security configuration error (ODS "Invalid Custom View Authorization Usage")

        ChartOfAccount has no path to Student, so a Student-based view cannot be applied to it on any
        operation. ODS reports the same 500 on GET-many, POST, and /deletes.

        @e2e-ci-shard-3
        Scenario: Custom view with no join path from the subject returns a security configuration error on every operation
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901"
              And the system has these descriptors
                  | descriptorValue                                    |
                  | uri://ed-fi.org/AccountTypeDescriptor#Revenue      |
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency |
                  | uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent             |
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | categories                                                                                                                      | localEducationAgencyCategoryDescriptor                                |
                  | 255901                 | Grand Bend ISD    | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency"} ] | "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent" |
              And the custom auth view "StudentWithCTECourseEnrollments" authorizes no Students
              And a claim set "E2E-CustomViewNoJoinPathClaimSet" is uploaded to CMS with these resource claims
                  | resource       | authorizationStrategies         |
                  | ChartOfAccount | StudentWithCTECourseEnrollments |
              And the claim set upload to CMS should be successful
              And the claimSet "E2E-CustomViewNoJoinPathClaimSet" is authorized with namespace "uri://ed-fi.org" and educationOrganizationIds ""
             When a GET request is made to "/ed-fi/chartOfAccounts"
             Then it should respond with 500
              And the response body should contain "urn:ed-fi:api:system:configuration:security"
              And the response body should contain "Security Configuration Error"
              And the response body should contain "A security configuration problem was detected. The request cannot be authorized."
              And the response body should contain "Strategy 'StudentWithCTECourseEnrollments' uses custom auth view 'auth.StudentWithCTECourseEnrollments'."
              And the response body should contain "Should a different authorization strategy be used?"
             When a POST request is made to "/ed-fi/chartOfAccounts" with
                  """
                  {
                      "educationOrganizationReference": { "educationOrganizationId": 255901 },
                      "accountIdentifier": "XYZ",
                      "fiscalYear": 2022,
                      "accountTypeDescriptor": "uri://ed-fi.org/AccountTypeDescriptor#Revenue"
                  }
                  """
             Then it should respond with 500
              And the response body should contain "urn:ed-fi:api:system:configuration:security"
              And the response body should contain "Strategy 'StudentWithCTECourseEnrollments' uses custom auth view 'auth.StudentWithCTECourseEnrollments'."
             When a GET request is made to "/ed-fi/chartOfAccounts/deletes"
             Then it should respond with 500
              And the response body should contain "urn:ed-fi:api:system:configuration:security"
              And the response body should contain "Relational change query authorization metadata is invalid for resource 'Ed-Fi.ChartOfAccount'."
              And the response body should contain "Strategy 'StudentWithCTECourseEnrollments' uses custom auth view 'auth.StudentWithCTECourseEnrollments'."
