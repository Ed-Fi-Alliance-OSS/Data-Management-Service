Feature: TrackedChange endpoints apply relationship-based ReadChanges authorization.

        # Every scenario follows the proven TrackedChangeEndpoints.feature scenario-15 pattern:
        #   1. Seed + mutate (create/delete or key-change) the resource under a broad seeding client
        #      (E2E-NoFurtherAuthRequiredClaimSet, or EdFiSandbox where the broad set lacks the grant).
        #   2. Capture the change version window from /changeQueries/v1/availableChangeVersions.
        #   3. Upload a purpose-built claim set whose ReadChanges action declares the relationship
        #      authorization strategy under test (upload-claims REPLACES the whole hierarchy, so the
        #      uploaded claim set is the ONLY grant afterwards — seeding therefore happens first).
        #   4. Authorize that claim set with a MATCHING educationOrganizationId -> the tracked-change
        #      row IS visible (total-count 1, asserted by key values).
        #   5. Re-authorize the SAME claim set with a NON-matching educationOrganizationId -> the row
        #      is hidden (total-count 0, []). ReadChanges authorization is applied as a SQL filter on
        #      the tracked-change rows, so the wrong scope sees an empty page (HTTP 200), not a 403.

        Background:
            Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"
              # SISVendor can only Read system descriptors; it can Create managed descriptors
              # (GradeLevel, EducationOrganizationCategory, ProgramType, Term,
              # CourseIdentificationSystem, LocalEducationAgencyCategory). ResponsibilityDescriptor
              # lives under domains/systemDescriptors, so it is created in scenario 03 under the
              # EdFiSandbox client (which grants Create on that domain).
              And the system has these descriptors
                  | descriptorValue                                                      |
                  | uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade                     |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School       |
                  | uri://ed-fi.org/ProgramTypeDescriptor#Bilingual                      |
                  | uri://ed-fi.org/TermDescriptor#Fall Semester                         |
                  | uri://ed-fi.org/CourseIdentificationSystemDescriptor#CSSC course code |
                  | uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent   |
              And the system has these "schoolYearTypes"
                  | schoolYear | currentSchoolYear | schoolYearDescription |
                  | 2023       | true              | "year 2023"           |

    Rule: Relationship-based ReadChanges authorization filters the change-query response

        # ODS-style: seed + read under the standard EdFiSandbox claim set, which binds
        # RelationshipsWithEdOrgsAndPeopleIncludingDeletes to StudentSchoolAssociation ReadChanges, and
        # vary only the client's authorized educationOrganizationId — the direct analog of ODS swapping
        # among its pre-provisioned LEA/School clients. No bespoke claim set is uploaded.
        @ods-migrated
        @e2e-ci-shard-1
        @reset-data-before-scenario
        Scenario: 01 RelationshipsWithEdOrgsAndPeopleIncludingDeletes shows a deleted association to an authorized education organization and hides it from others
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901001"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution      | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 255901001 | EdOrgPeople SSA School | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName | lastSurname | birthDate  |
                  | "31"            | EdOrgPpl  | Student     | 2008-01-01 |
             When a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                    "entryDate": "2023-08-01",
                    "schoolReference": { "schoolId": 255901001 },
                    "studentReference": { "studentUniqueId": "31" },
                    "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "edOrgPeopleSsaId" variable
             When a DELETE request is made to "/ed-fi/studentSchoolAssociations/{edOrgPeopleSsaId}"
             Then it should respond with 204
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "edOrgPeopleSsaVersion"
             When a GET request is made to "/ed-fi/studentSchoolAssociations/deletes?minChangeVersion={edOrgPeopleSsaVersion}&maxChangeVersion={edOrgPeopleSsaVersion}&totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  { "total-count": 1 }
                  """
              And total of records should be 1
              And the response body path "0.id" should equal request variable "edOrgPeopleSsaId"
              And the response body path "0.keyValues.schoolId" should have value "255901001"
              And the response body path "0.keyValues.studentUniqueId" should have value "31"
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901999"
             When a GET request is made to "/ed-fi/studentSchoolAssociations/deletes?minChangeVersion={edOrgPeopleSsaVersion}&maxChangeVersion={edOrgPeopleSsaVersion}&totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  { "total-count": 0 }
                  """
              And total of records should be 0
              And the response body is
                  """
                  []
                  """

        @e2e-ci-shard-1
        @ResetClaimsetsAfterScenario
        @reset-data-before-scenario
        Scenario: 02 RelationshipsWithStudentsOnlyIncludingDeletes shows a deleted association to a client reachable through the student and hides it from others
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with educationOrganizationIds "255901001"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution   | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 255901001 | StudentsOnly School | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName    | lastSurname | birthDate  |
                  | "41"            | StudentsOnly | Student     | 2008-01-01 |
             When a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                    "entryDate": "2023-08-01",
                    "schoolReference": { "schoolId": 255901001 },
                    "studentReference": { "studentUniqueId": "41" },
                    "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "studentsOnlySsaId" variable
             When a DELETE request is made to "/ed-fi/studentSchoolAssociations/{studentsOnlySsaId}"
             Then it should respond with 204
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "studentsOnlySsaVersion"
            Given a claim set is uploaded to CMS that grants "StudentSchoolAssociation" access to "E2E-ReadChangesStudentsOnlyClaimSet" using authorization strategy "RelationshipsWithStudentsOnlyIncludingDeletes"
              And the claim set upload to CMS should be successful
            Given the claimSet "E2E-ReadChangesStudentsOnlyClaimSet" is authorized with educationOrganizationIds "255901001"
             When a GET request is made to "/ed-fi/studentSchoolAssociations/deletes?minChangeVersion={studentsOnlySsaVersion}&maxChangeVersion={studentsOnlySsaVersion}&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.id" should equal request variable "studentsOnlySsaId"
              And the response body path "0.keyValues.studentUniqueId" should have value "41"
            Given the claimSet "E2E-ReadChangesStudentsOnlyClaimSet" is authorized with educationOrganizationIds "255901999"
             When a GET request is made to "/ed-fi/studentSchoolAssociations/deletes?minChangeVersion={studentsOnlySsaVersion}&maxChangeVersion={studentsOnlySsaVersion}&totalCount=true"
             Then it should respond with 200
              And total of records should be 0
              And the response body is
                  """
                  []
                  """

        # NOTE (seeding-client swap): studentEducationOrganizationResponsibilityAssociation is not in the
        # broad E2E-NoFurtherAuthRequiredClaimSet (it lives under the domains/relationshipBasedData domain,
        # which the broad set does not grant). It IS granted by the built-in EdFiSandbox claim set (used the
        # same way by RelationshipsWithStudentsOnlyThroughResponsibility.feature). The seed+delete therefore
        # runs under EdFiSandbox; the /deletes query still runs under the uploaded strategy claim set.
        @e2e-ci-shard-1
        @ResetClaimsetsAfterScenario
        @reset-data-before-scenario
        Scenario: 03 RelationshipsWithStudentsOnlyThroughResponsibilityIncludingDeletes filters a deleted responsibility association by the responsibility relationship
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901001"
              And the system has these descriptors
                  | descriptorValue                                         |
                  | uri://ed-fi.org/ResponsibilityDescriptor#Accountability |
              And the system has these "schools"
                  | schoolId  | nameOfInstitution     | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 255901001 | Responsibility School | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName      | lastSurname | birthDate  |
                  | "51"            | Responsibility | Student     | 2008-01-01 |
             # Creating the responsibility association under EdFiSandbox is itself relationship-authorized
             # (RelationshipsWithEdOrgsAndPeople), so the student must first be enrolled at the education
             # organization via a StudentSchoolAssociation.
             When a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                    "entryDate": "2023-08-01",
                    "schoolReference": { "schoolId": 255901001 },
                    "studentReference": { "studentUniqueId": "51" },
                    "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/studentEducationOrganizationResponsibilityAssociations" with
                  """
                  {
                    "beginDate": "2023-08-01",
                    "educationOrganizationReference": { "educationOrganizationId": 255901001 },
                    "studentReference": { "studentUniqueId": "51" },
                    "responsibilityDescriptor": "uri://ed-fi.org/ResponsibilityDescriptor#Accountability"
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "responsibilityId" variable
             When a DELETE request is made to "/ed-fi/studentEducationOrganizationResponsibilityAssociations/{responsibilityId}"
             Then it should respond with 204
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "responsibilityVersion"
            Given a claim set is uploaded to CMS that grants "StudentEducationOrganizationResponsibilityAssociation" access to "E2E-ReadChangesResponsibilityClaimSet" using authorization strategy "RelationshipsWithStudentsOnlyThroughResponsibilityIncludingDeletes"
              And the claim set upload to CMS should be successful
            Given the claimSet "E2E-ReadChangesResponsibilityClaimSet" is authorized with educationOrganizationIds "255901001"
             When a GET request is made to "/ed-fi/studentEducationOrganizationResponsibilityAssociations/deletes?minChangeVersion={responsibilityVersion}&maxChangeVersion={responsibilityVersion}&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.id" should equal request variable "responsibilityId"
              And the response body path "0.keyValues.studentUniqueId" should have value "51"
            Given the claimSet "E2E-ReadChangesResponsibilityClaimSet" is authorized with educationOrganizationIds "255901999"
             When a GET request is made to "/ed-fi/studentEducationOrganizationResponsibilityAssociations/deletes?minChangeVersion={responsibilityVersion}&maxChangeVersion={responsibilityVersion}&totalCount=true"
             Then it should respond with 200
              And total of records should be 0
              And the response body is
                  """
                  []
                  """

        # INVERTED SEMANTICS: the RelationshipsWithEdOrgsOnlyInverted lane matches the tracked
        # education-organization id against the inverted EdOrg hierarchy view (claim column =
        # TargetEdOrgId, subject column = SourceEdOrgId), which includes the self-edge. The deleted
        # resource is a School secured by its own edOrg (255901001); a client scoped to that same edOrg
        # (255901001) resolves through the inverted view and sees the delete, while an unrelated edOrg
        # (255901999) sees nothing. The LEA 255901 is seeded only so the School has a valid parent
        # reference. (Confirmed at run time which scope resolves; see report.)
        @e2e-ci-shard-1
        @ResetClaimsetsAfterScenario
        @reset-data-before-scenario
        Scenario: 04 RelationshipsWithEdOrgsOnlyInverted filters a deleted education organization by the inverted hierarchy
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with educationOrganizationIds "255901, 255901001"
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | categories                                                                                                       | localEducationAgencyCategoryDescriptor                            |
                  | 255901                 | Inverted LEA      | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] | uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent |
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": 255901001,
                    "nameOfInstitution": "Inverted School",
                    "gradeLevels": [ { "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" } ],
                    "educationOrganizationCategories": [ { "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School" } ],
                    "localEducationAgencyReference": { "localEducationAgencyId": 255901 }
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "invertedSchoolId" variable
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "invertedMinVersion"
             When a DELETE request is made to "/ed-fi/schools/{invertedSchoolId}"
             Then it should respond with 204
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "invertedMaxVersion"
            Given a claim set is uploaded to CMS that grants "School" access to "E2E-ReadChangesInvertedClaimSet" using authorization strategy "RelationshipsWithEdOrgsOnlyInverted"
              And the claim set upload to CMS should be successful
            Given the claimSet "E2E-ReadChangesInvertedClaimSet" is authorized with educationOrganizationIds "255901001"
             When a GET request is made to "/ed-fi/schools/deletes?minChangeVersion={invertedMinVersion}&maxChangeVersion={invertedMaxVersion}&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.keyValues.schoolId" should have value "255901001"
            Given the claimSet "E2E-ReadChangesInvertedClaimSet" is authorized with educationOrganizationIds "255901999"
             When a GET request is made to "/ed-fi/schools/deletes?minChangeVersion={invertedMinVersion}&maxChangeVersion={invertedMaxVersion}&totalCount=true"
             Then it should respond with 200
              And total of records should be 0
              And the response body is
                  """
                  []
                  """

        @e2e-ci-shard-1
        @ResetClaimsetsAfterScenario
        @reset-data-before-scenario
        Scenario: 05 RelationshipsWithEdOrgsOnly filters a ClassPeriod key change by education organization
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with educationOrganizationIds "255901001"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution     | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 255901001 | KeyChange Auth School | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
             When a POST request is made to "/ed-fi/classPeriods" with
                  """
                  { "classPeriodName": "auth first period", "schoolReference": { "schoolId": 255901001 } }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "authClassPeriodId" variable
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "authKeyChangeMinVersion"
             When a PUT request is made to "/ed-fi/classPeriods/{authClassPeriodId}" with
                  """
                  {
                    "id": "{authClassPeriodId}",
                    "classPeriodName": "auth second period",
                    "schoolReference": { "schoolId": 255901001 }
                  }
                  """
             Then it should respond with 204
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "authKeyChangeMaxVersion"
            Given a claim set is uploaded to CMS that grants "ClassPeriod" access to "E2E-ReadChangesClassPeriodEdOrgClaimSet" using authorization strategy "RelationshipsWithEdOrgsOnly"
              And the claim set upload to CMS should be successful
            Given the claimSet "E2E-ReadChangesClassPeriodEdOrgClaimSet" is authorized with educationOrganizationIds "255901001"
             When a GET request is made to "/ed-fi/classPeriods/keyChanges?minChangeVersion={authKeyChangeMinVersion}&maxChangeVersion={authKeyChangeMaxVersion}&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.oldKeyValues.classPeriodName" should have value "auth first period"
              And the response body path "0.newKeyValues.classPeriodName" should have value "auth second period"
              And the response body path "0.oldKeyValues.schoolId" should have value "255901001"
            Given the claimSet "E2E-ReadChangesClassPeriodEdOrgClaimSet" is authorized with educationOrganizationIds "255901999"
             When a GET request is made to "/ed-fi/classPeriods/keyChanges?minChangeVersion={authKeyChangeMinVersion}&maxChangeVersion={authKeyChangeMaxVersion}&totalCount=true"
             Then it should respond with 200
              And total of records should be 0
              And the response body is
                  """
                  []
                  """

        # SSA identity update is supported by DMS (PUT 204; confirmed in TrackedChangeKeyChangesByResource
        # scenario 05). The enrollment school changes from 255901001 to 255901002, producing a /keyChanges
        # row. Under RelationshipsWithEdOrgsAndPeopleIncludingDeletes a client scoped to either school in
        # the key change sees the row; an unrelated edOrg sees nothing.
        @ods-migrated
        @e2e-ci-shard-1
        @reset-data-before-scenario
        Scenario: 06 RelationshipsWithEdOrgsAndPeopleIncludingDeletes filters a StudentSchoolAssociation key change by education organization
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901001, 255901002"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution      | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 255901001 | SSA KeyChange School A  | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 255901002 | SSA KeyChange School B  | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName | lastSurname | birthDate  |
                  | "61"            | SsaKey    | Student     | 2008-01-01 |
             When a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                    "entryDate": "2023-08-01",
                    "schoolReference": { "schoolId": 255901001 },
                    "studentReference": { "studentUniqueId": "61" },
                    "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "ssaKeyChangeAuthId" variable
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "ssaKeyChangeAuthMinVersion"
             When a PUT request is made to "/ed-fi/studentSchoolAssociations/{ssaKeyChangeAuthId}" with
                  """
                  {
                    "id": "{ssaKeyChangeAuthId}",
                    "entryDate": "2023-08-01",
                    "schoolReference": { "schoolId": 255901002 },
                    "studentReference": { "studentUniqueId": "61" },
                    "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 204
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "ssaKeyChangeAuthMaxVersion"
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901001"
             When a GET request is made to "/ed-fi/studentSchoolAssociations/keyChanges?minChangeVersion={ssaKeyChangeAuthMinVersion}&maxChangeVersion={ssaKeyChangeAuthMaxVersion}&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.id" should equal request variable "ssaKeyChangeAuthId"
              And the response body path "0.oldKeyValues.schoolId" should have value "255901001"
              And the response body path "0.newKeyValues.schoolId" should have value "255901002"
              And the response body path "0.newKeyValues.studentUniqueId" should have value "61"
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901999"
             When a GET request is made to "/ed-fi/studentSchoolAssociations/keyChanges?minChangeVersion={ssaKeyChangeAuthMinVersion}&maxChangeVersion={ssaKeyChangeAuthMaxVersion}&totalCount=true"
             Then it should respond with 200
              And total of records should be 0
              And the response body is
                  """
                  []
                  """

        # DEEP CHAIN + identity update (mirrors ODS Sections LEA-vs-School keyChanges). LEA 255901 ->
        # School 255901001; the full Section dependency chain (Session/Course/CourseOffering/Section)
        # is built under EdFiSandbox, then the Section sectionIdentifier is changed (PUT 204, supported).
        # EdFiSandbox binds RelationshipsWithEdOrgsAndPeopleIncludingDeletes to Section ReadChanges; a
        # Section has no person in its identity, so the education-organization hierarchy does the
        # filtering: a client scoped to the parent LEA (255901) sees the Section key change; an unrelated
        # edOrg (255901999) sees nothing. (Explicit RelationshipsWithEdOrgsOnly coverage lives in scenario 05.)
        @ods-migrated
        @e2e-ci-shard-1
        @reset-data-before-scenario
        Scenario: 07 Education-organization ReadChanges filters a Section key change by the parent LEA scope
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901, 255901001"
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | categories                                                                                                       | localEducationAgencyCategoryDescriptor                            |
                  | 255901                 | Section Auth LEA  | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] | uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent |
              And the system has these "schools"
                  | schoolId  | nameOfInstitution   | gradeLevels                                                                      | educationOrganizationCategories                                                                                   | localEducationAgencyReference        |
                  | 255901001 | Section Auth School | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] | { "localEducationAgencyId": 255901 } |
             When a POST request is made to "/ed-fi/sessions" with
                  """
                  {
                    "sessionName": "Section Auth Session",
                    "schoolReference": { "schoolId": 255901001 },
                    "schoolYearTypeReference": { "schoolYear": 2023 },
                    "beginDate": "2023-08-01",
                    "endDate": "2023-12-20",
                    "totalInstructionalDays": 90,
                    "termDescriptor": "uri://ed-fi.org/TermDescriptor#Fall Semester"
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/courses" with
                  """
                  {
                    "courseCode": "SEC-AUTH-CRS",
                    "educationOrganizationReference": { "educationOrganizationId": 255901001 },
                    "courseTitle": "Section Auth Course",
                    "numberOfParts": 1,
                    "identificationCodes": [
                      {
                        "courseIdentificationSystemDescriptor": "uri://ed-fi.org/CourseIdentificationSystemDescriptor#CSSC course code",
                        "identificationCode": "SEC-AUTH-CRS"
                      }
                    ]
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/courseOfferings" with
                  """
                  {
                    "localCourseCode": "SEC-AUTH-LCC",
                    "courseReference": { "courseCode": "SEC-AUTH-CRS", "educationOrganizationId": 255901001 },
                    "schoolReference": { "schoolId": 255901001 },
                    "sessionReference": { "schoolId": 255901001, "schoolYear": 2023, "sessionName": "Section Auth Session" }
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/sections" with
                  """
                  {
                    "sectionIdentifier": "SEC-AUTH-AAA",
                    "courseOfferingReference": { "localCourseCode": "SEC-AUTH-LCC", "schoolId": 255901001, "schoolYear": 2023, "sessionName": "Section Auth Session" }
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "sectionAuthId" variable
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "sectionAuthMinVersion"
             When a PUT request is made to "/ed-fi/sections/{sectionAuthId}" with
                  """
                  {
                    "id": "{sectionAuthId}",
                    "sectionIdentifier": "SEC-AUTH-BBB",
                    "courseOfferingReference": { "localCourseCode": "SEC-AUTH-LCC", "schoolId": 255901001, "schoolYear": 2023, "sessionName": "Section Auth Session" }
                  }
                  """
             Then it should respond with 204
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "sectionAuthMaxVersion"
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901"
             When a GET request is made to "/ed-fi/sections/keyChanges?minChangeVersion={sectionAuthMinVersion}&maxChangeVersion={sectionAuthMaxVersion}&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.id" should equal request variable "sectionAuthId"
              And the response body path "0.oldKeyValues.sectionIdentifier" should have value "SEC-AUTH-AAA"
              And the response body path "0.newKeyValues.sectionIdentifier" should have value "SEC-AUTH-BBB"
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901999"
             When a GET request is made to "/ed-fi/sections/keyChanges?minChangeVersion={sectionAuthMinVersion}&maxChangeVersion={sectionAuthMaxVersion}&totalCount=true"
             Then it should respond with 200
              And total of records should be 0
              And the response body is
                  """
                  []
                  """

        # SECURABLE OVERRIDE (DMS improvement over ODS): OrganizationDepartment's
        # ParentEducationOrganizationId override lets relationship auth filter its /deletes (in ODS this
        # securable fell back to NoFurtherAuthorizationRequired and could not be relationship-filtered).
        # OrganizationDepartment is seedable under the broad set via the domains/educationOrganizations
        # grant. A client scoped to the parent edOrg (255901001) sees the delete; 255901999 does not.
        @e2e-ci-shard-1
        @ResetClaimsetsAfterScenario
        @reset-data-before-scenario
        Scenario: 08 RelationshipsWithEdOrgsOnly filters a deleted OrganizationDepartment by its parent education organization
            # The broad seeding claim set does not grant organizationDepartment (its only built-in grant
            # is DistrictHostedSISVendor), so the seed runs under the uploaded strategy claim set itself:
            # the parent school is seeded under the broad set first, then the OrganizationDepartment is
            # created under E2E-ReadChangesOrgDeptClaimSet whose RelationshipsWithEdOrgsOnly Create lets a
            # client scoped to the parent edOrg (255901001) create and delete it. The managed
            # EducationOrganizationCategoryDescriptor is created under the broad set before the upload.
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with educationOrganizationIds "255901001"
              And the system has these descriptors
                  | descriptorValue                                                                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Organization Department |
              And the system has these "schools"
                  | schoolId  | nameOfInstitution    | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 255901001 | OrgDept Auth School  | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
            Given a claim set is uploaded to CMS that grants "OrganizationDepartment" access to "E2E-ReadChangesOrgDeptClaimSet" using authorization strategy "RelationshipsWithEdOrgsOnly"
              And the claim set upload to CMS should be successful
            Given the claimSet "E2E-ReadChangesOrgDeptClaimSet" is authorized with educationOrganizationIds "255901001"
             When a POST request is made to "/ed-fi/organizationDepartments" with
                  """
                  {
                    "organizationDepartmentId": 255901777,
                    "nameOfInstitution": "Auth Org Department",
                    "parentEducationOrganizationReference": { "educationOrganizationId": 255901001 },
                    "categories": [
                      { "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Organization Department" }
                    ]
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "orgDeptAuthId" variable
             When a DELETE request is made to "/ed-fi/organizationDepartments/{orgDeptAuthId}"
             Then it should respond with 204
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "orgDeptAuthVersion"
            Given the claimSet "E2E-ReadChangesOrgDeptClaimSet" is authorized with educationOrganizationIds "255901001"
             When a GET request is made to "/ed-fi/organizationDepartments/deletes?minChangeVersion={orgDeptAuthVersion}&maxChangeVersion={orgDeptAuthVersion}&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.id" should equal request variable "orgDeptAuthId"
              And the response body path "0.keyValues.organizationDepartmentId" should have value "255901777"
            Given the claimSet "E2E-ReadChangesOrgDeptClaimSet" is authorized with educationOrganizationIds "255901999"
             When a GET request is made to "/ed-fi/organizationDepartments/deletes?minChangeVersion={orgDeptAuthVersion}&maxChangeVersion={orgDeptAuthVersion}&totalCount=true"
             Then it should respond with 200
              And total of records should be 0
              And the response body is
                  """
                  []
                  """

        # Mirrors ODS "Student Contact Association (Full Delete/Recreate in Change Window)": a delete
        # followed by a recreate of the SAME natural key within the [min,max] window collapses to zero
        # tracked-delete rows. Run under the standard EdFiSandbox client (single matching scope, as ODS
        # does) to confirm the recreate-suppression holds through ReadChanges authorization.
        @ods-migrated
        @e2e-ci-shard-1
        @reset-data-before-scenario
        Scenario: 09 StudentContactAssociation deleted then recreated within the change window is suppressed from deletes
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901001"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution     | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 255901001 | ContactAssoc School   | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName | lastSurname | birthDate  |
                  | "71"            | Contact   | Student     | 2008-01-01 |
              And the system has these "contacts"
                  | contactUniqueId | firstName | lastSurname |
                  | "C71"           | Primary   | Contact     |
             When a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                    "entryDate": "2023-08-01",
                    "schoolReference": { "schoolId": 255901001 },
                    "studentReference": { "studentUniqueId": "71" },
                    "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/studentContactAssociations" with
                  """
                  {
                    "studentReference": { "studentUniqueId": "71" },
                    "contactReference": { "contactUniqueId": "C71" }
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "contactAssocId" variable
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "contactAssocMinVersion"
             When a DELETE request is made to "/ed-fi/studentContactAssociations/{contactAssocId}"
             Then it should respond with 204
             When a POST request is made to "/ed-fi/studentContactAssociations" with
                  """
                  {
                    "studentReference": { "studentUniqueId": "71" },
                    "contactReference": { "contactUniqueId": "C71" }
                  }
                  """
             Then it should respond with 201
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "contactAssocMaxVersion"
             When a GET request is made to "/ed-fi/studentContactAssociations/deletes?minChangeVersion={contactAssocMinVersion}&maxChangeVersion={contactAssocMaxVersion}&totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  { "total-count": 0 }
                  """
              And total of records should be 0
              And the response body is
                  """
                  []
                  """

        # Mirrors ODS "Deletes / Derived Resource (Multiple Key Columns)" (StudentProgramAssociation):
        # a multi-column-key association deleted under two child schools of one LEA. EdFiSandbox binds
        # RelationshipsWithEdOrgsAndPeopleIncludingDeletes to StudentProgramAssociation ReadChanges, so a
        # client scoped to the parent LEA (255901) sees BOTH child schools' deletes; a client scoped to a
        # single child school (255901001) sees only its own; an unrelated edOrg (255901999) sees none —
        # the direct analog of ODS swapping among its LEA-level and School-level clients.
        # keyValues parity: DMS surfaces all six ODS identity parts, including the program reference's
        # education organization as programEducationOrganizationId (confirmed against the live API):
        # educationOrganizationId, programEducationOrganizationId, programName, programTypeDescriptor,
        # beginDate, studentUniqueId. The program here is defined at the LEA (255901), so
        # programEducationOrganizationId is 255901 while educationOrganizationId is the school.
        @ods-migrated
        @e2e-ci-shard-1
        @reset-data-before-scenario
        Scenario: 10 RelationshipsWithEdOrgsAndPeopleIncludingDeletes shows deleted StudentProgramAssociations to the parent LEA and scopes a single school to its own
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901, 255901001, 255901002"
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | categories                                                                                                       | localEducationAgencyCategoryDescriptor                            |
                  | 255901                 | SPA Deletes LEA   | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] | uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent |
              And the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                   | localEducationAgencyReference        |
                  | 255901001 | SPA School A      | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] | { "localEducationAgencyId": 255901 } |
                  | 255901002 | SPA School B      | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] | { "localEducationAgencyId": 255901 } |
              And the system has these "students"
                  | studentUniqueId | firstName | lastSurname | birthDate  |
                  | "81"            | Program   | StudentA    | 2008-01-01 |
                  | "82"            | Program   | StudentB    | 2008-01-01 |
             When a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  { "entryDate": "2023-08-01", "schoolReference": { "schoolId": 255901001 }, "studentReference": { "studentUniqueId": "81" }, "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  { "entryDate": "2023-08-01", "schoolReference": { "schoolId": 255901002 }, "studentReference": { "studentUniqueId": "82" }, "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/programs" with
                  """
                  { "programName": "SPA Bilingual", "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Bilingual", "educationOrganizationReference": { "educationOrganizationId": 255901 } }
                  """
             Then it should respond with 201
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "spaDeletesMinVersion"
             When a POST request is made to "/ed-fi/studentProgramAssociations" with
                  """
                  { "beginDate": "2023-08-01", "educationOrganizationReference": { "educationOrganizationId": 255901001 }, "programReference": { "educationOrganizationId": 255901, "programName": "SPA Bilingual", "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Bilingual" }, "studentReference": { "studentUniqueId": "81" } }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "spaDeleteId1" variable
             When a POST request is made to "/ed-fi/studentProgramAssociations" with
                  """
                  { "beginDate": "2023-08-02", "educationOrganizationReference": { "educationOrganizationId": 255901002 }, "programReference": { "educationOrganizationId": 255901, "programName": "SPA Bilingual", "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Bilingual" }, "studentReference": { "studentUniqueId": "82" } }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "spaDeleteId2" variable
             When a DELETE request is made to "/ed-fi/studentProgramAssociations/{spaDeleteId1}"
             Then it should respond with 204
             When a DELETE request is made to "/ed-fi/studentProgramAssociations/{spaDeleteId2}"
             Then it should respond with 204
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "spaDeletesMaxVersion"
            # The parent LEA sees both child schools' deletes, ordered ascending by change version.
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901"
             When a GET request is made to "/ed-fi/studentProgramAssociations/deletes?minChangeVersion={spaDeletesMinVersion}&maxChangeVersion={spaDeletesMaxVersion}&totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  { "total-count": 2 }
                  """
              And total of records should be 2
              And the response body path "0.id" should equal request variable "spaDeleteId1"
              And the response body path "0.keyValues.educationOrganizationId" should have value "255901001"
              And the response body path "0.keyValues.programEducationOrganizationId" should have value "255901"
              And the response body path "0.keyValues.programName" should have value "SPA Bilingual"
              And the response body path "0.keyValues.programTypeDescriptor" should have value "uri://ed-fi.org/ProgramTypeDescriptor#Bilingual"
              And the response body path "0.keyValues.beginDate" should have value "2023-08-01"
              And the response body path "0.keyValues.studentUniqueId" should have value "81"
              And the response body path "1.id" should equal request variable "spaDeleteId2"
              And the response body path "1.keyValues.educationOrganizationId" should have value "255901002"
              And the response body path "1.keyValues.programEducationOrganizationId" should have value "255901"
              And the response body path "1.keyValues.programName" should have value "SPA Bilingual"
              And the response body path "1.keyValues.programTypeDescriptor" should have value "uri://ed-fi.org/ProgramTypeDescriptor#Bilingual"
              And the response body path "1.keyValues.beginDate" should have value "2023-08-02"
              And the response body path "1.keyValues.studentUniqueId" should have value "82"
            # A client scoped to a single child school sees only its own delete.
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901001"
             When a GET request is made to "/ed-fi/studentProgramAssociations/deletes?minChangeVersion={spaDeletesMinVersion}&maxChangeVersion={spaDeletesMaxVersion}&totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  { "total-count": 1 }
                  """
              And total of records should be 1
              And the response body path "0.id" should equal request variable "spaDeleteId1"
              And the response body path "0.keyValues.educationOrganizationId" should have value "255901001"
              And the response body path "0.keyValues.studentUniqueId" should have value "81"
            # An unrelated education organization sees nothing.
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901999"
             When a GET request is made to "/ed-fi/studentProgramAssociations/deletes?minChangeVersion={spaDeletesMinVersion}&maxChangeVersion={spaDeletesMaxVersion}&totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  { "total-count": 0 }
                  """
              And total of records should be 0
              And the response body is
                  """
                  []
                  """

        # Mirrors ODS "Student Contact Association (recreate after the referenced Contact is recreated)":
        # the referenced Contact is deleted and recreated with the SAME contactUniqueId (a NEW internal
        # id but the SAME natural key) BETWEEN the association delete and its recreate. This stresses
        # that /deletes recreate-suppression keys off the association's NATURAL key (studentUniqueId +
        # contactUniqueId), which survives the Contact's internal-id churn, rather than any internal
        # reference/surrogate id (which changes). The intermediate step confirms the tombstone IS present
        # after the first delete, before the recreate suppresses it. Self-contained (globally unique
        # ids), so no data reset is required.
        @ods-migrated
        @e2e-ci-shard-1
        Scenario: 11 StudentContactAssociation recreate suppresses the delete even after its Contact is deleted and recreated with the same contactUniqueId
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "920201197"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution       | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 920201197 | Contact Recreate School | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName | lastSurname | birthDate  |
                  | "S1197A"        | Recreate  | Student     | 2008-01-01 |
             When a POST request is made to "/ed-fi/contacts" with
                  """
                  { "contactUniqueId": "C1197A", "firstName": "Recreate", "lastSurname": "Contact" }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "firstContactId" variable
             When a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                    "entryDate": "2023-08-01",
                    "schoolReference": { "schoolId": 920201197 },
                    "studentReference": { "studentUniqueId": "S1197A" },
                    "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/studentContactAssociations" with
                  """
                  {
                    "studentReference": { "studentUniqueId": "S1197A" },
                    "contactReference": { "contactUniqueId": "C1197A" }
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "contactAssocId" variable
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "contactAssocMinVersion"
             When a DELETE request is made to "/ed-fi/studentContactAssociations/{contactAssocId}"
             Then it should respond with 204
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "contactAssocAfterDeleteVersion"
             # The tombstone IS present after the delete, before any recreate.
             When a GET request is made to "/ed-fi/studentContactAssociations/deletes?minChangeVersion={contactAssocMinVersion}&maxChangeVersion={contactAssocAfterDeleteVersion}&totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  { "total-count": 1 }
                  """
              And total of records should be 1
              And the response body path "0.id" should equal request variable "contactAssocId"
              And the response body path "0.keyValues.studentUniqueId" should have value "S1197A"
              And the response body path "0.keyValues.contactUniqueId" should have value "C1197A"
             # Delete the referenced Contact, then recreate it with the SAME contactUniqueId (new internal id).
             When a DELETE request is made to "/ed-fi/contacts/{firstContactId}"
             Then it should respond with 204
             When a POST request is made to "/ed-fi/contacts" with
                  """
                  { "contactUniqueId": "C1197A", "firstName": "Recreate", "lastSurname": "Contact" }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "secondContactId" variable
             # Recreate the association with the same natural key.
             When a POST request is made to "/ed-fi/studentContactAssociations" with
                  """
                  {
                    "studentReference": { "studentUniqueId": "S1197A" },
                    "contactReference": { "contactUniqueId": "C1197A" }
                  }
                  """
             Then it should respond with 201
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "contactAssocMaxVersion"
             # The recreated association suppresses the earlier tombstone across the full window.
             When a GET request is made to "/ed-fi/studentContactAssociations/deletes?minChangeVersion={contactAssocMinVersion}&maxChangeVersion={contactAssocMaxVersion}&totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  { "total-count": 0 }
                  """
              And total of records should be 0
              And the response body is
                  """
                  []
                  """

    Rule: Namespace and composition ReadChanges authorization

        # Assessment identity is (assessmentIdentifier, namespace); the namespace value
        # "uri://ed-fi.org/Assessment" is prefixed by "uri://ed-fi.org" (matching client) and not by
        # "uri://other.org" (non-matching client). EdFiSandbox inherits NamespaceBased ReadChanges for
        # assessment metadata, matching the ODS Postman ChangeQueries sandbox-client authorization shape.
        @ods-migrated
        @e2e-ci-shard-1
        @reset-data-before-scenario
        Scenario: 01 NamespaceBased shows a deleted resource to a matching namespace prefix and hides it from a non-matching one
            Given the system has these descriptors
                  | descriptorValue                                    |
                  | uri://ed-fi.org/AcademicSubjectDescriptor#Reading  |
            Given the claimSet "EdFiSandbox" is authorized with namespacePrefixes "uri://ed-fi.org"
             When a POST request is made to "/ed-fi/assessments" with
                  """
                  {
                    "assessmentIdentifier": "TRACK-ASSESS-1",
                    "namespace": "uri://ed-fi.org/Assessment",
                    "assessmentTitle": "Tracked Assessment",
                    "academicSubjects": [
                      { "academicSubjectDescriptor": "uri://ed-fi.org/AcademicSubjectDescriptor#Reading" }
                    ]
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "nsAssessmentId" variable
             When a DELETE request is made to "/ed-fi/assessments/{nsAssessmentId}"
             Then it should respond with 204
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "nsAssessmentVersion"
             When a GET request is made to "/ed-fi/assessments/deletes?minChangeVersion={nsAssessmentVersion}&maxChangeVersion={nsAssessmentVersion}&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.id" should equal request variable "nsAssessmentId"
              And the response body path "0.keyValues.namespace" should have value "uri://ed-fi.org/Assessment"
              And the response body path "0.keyValues.assessmentIdentifier" should have value "TRACK-ASSESS-1"
            Given the claimSet "EdFiSandbox" is authorized with namespacePrefixes "uri://other.org"
             When a GET request is made to "/ed-fi/assessments/deletes?minChangeVersion={nsAssessmentVersion}&maxChangeVersion={nsAssessmentVersion}&totalCount=true"
             Then it should respond with 200
              And total of records should be 0
              And the response body is
                  """
                  []
                  """

        # NamespaceBased on /keyChanges: a namespace resource generally cannot change a non-namespace
        # identity, so this verifies the endpoint is authorized for a matching namespace prefix and
        # returns an empty page (no key changes were produced).
        @ods-migrated
        @e2e-ci-shard-1
        @reset-data-before-scenario
        Scenario: 02 NamespaceBased authorizes the keyChanges endpoint for a matching namespace prefix
            Given the claimSet "EdFiSandbox" is authorized with namespacePrefixes "uri://ed-fi.org"
             When a GET request is made to "/ed-fi/assessments/keyChanges?totalCount=true&limit=1&offset=0"
             Then it should respond with 200
              And the response headers include
                  """
                  { "total-count": 0 }
                  """

        # Mirrors ODS "EducationContent (Non-Key Namespace Based)". EducationContent identity is
        # contentIdentifier only; namespace is a non-key field. Two EducationContents are created (one
        # under uri://ed-fi.org, one under uri://other.org) and both deleted under a broad client that
        # carries both prefixes. EdFiSandbox inherits NamespaceBased ReadChanges for educationContent, so
        # the uri://ed-fi.org-scoped client sees only the ed-fi.org delete and the uri://other.org-scoped
        # client sees only the other.org delete.
        @ods-migrated
        @e2e-ci-shard-1
        @reset-data-before-scenario
        Scenario: 03 NamespaceBased on EducationContent isolates deletes by client namespace
            Given the claimSet "EdFiSandbox" is authorized with namespacePrefixes "uri://ed-fi.org, uri://other.org"
             When a POST request is made to "/ed-fi/educationContents" with
                  """
                  {
                    "contentIdentifier": "TRACK-EDU-EDFI",
                    "namespace": "uri://ed-fi.org/EducationContent"
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "eduEdfiId" variable
             When a POST request is made to "/ed-fi/educationContents" with
                  """
                  {
                    "contentIdentifier": "TRACK-EDU-OTHER",
                    "namespace": "uri://other.org/EducationContent"
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "eduOtherId" variable
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "eduMinVersion"
             When a DELETE request is made to "/ed-fi/educationContents/{eduEdfiId}"
             Then it should respond with 204
             When a DELETE request is made to "/ed-fi/educationContents/{eduOtherId}"
             Then it should respond with 204
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "eduMaxVersion"
            Given the claimSet "EdFiSandbox" is authorized with namespacePrefixes "uri://ed-fi.org"
             When a GET request is made to "/ed-fi/educationContents/deletes?minChangeVersion={eduMinVersion}&maxChangeVersion={eduMaxVersion}&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.id" should equal request variable "eduEdfiId"
              And the response body path "0.keyValues.contentIdentifier" should have value "TRACK-EDU-EDFI"
            Given the claimSet "EdFiSandbox" is authorized with namespacePrefixes "uri://other.org"
             When a GET request is made to "/ed-fi/educationContents/deletes?minChangeVersion={eduMinVersion}&maxChangeVersion={eduMaxVersion}&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.id" should equal request variable "eduOtherId"
              And the response body path "0.keyValues.contentIdentifier" should have value "TRACK-EDU-OTHER"

        # COMPOSITION (NoFurtherAuthorizationRequired composed with a relationship strategy): a School's
        # ReadChanges is configured with BOTH NoFurtherAuthorizationRequired AND RelationshipsWithEdOrgsOnly
        # (via the multi-strategy upload step). NoFurther is a no-op that does NOT widen results, so the
        # relationship filter still applies: a client scoped to the School's edOrg (255901001) sees the
        # deleted School, and an unrelated edOrg (255901999) sees nothing. The School is seeded+deleted
        # under the broad claim set first (the proven seed-before-upload pattern), so the composed
        # strategies only affect the ReadChanges query.
        @e2e-ci-shard-1
        @ResetClaimsetsAfterScenario
        @reset-data-before-scenario
        Scenario: 04 NoFurtherAuthorizationRequired composed with a relationship strategy remains a no-op
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with educationOrganizationIds "255901001"
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": 255901001,
                    "nameOfInstitution": "Composition NoFurther School",
                    "gradeLevels": [ { "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" } ],
                    "educationOrganizationCategories": [ { "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School" } ]
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "composeSchoolId" variable
             When a DELETE request is made to "/ed-fi/schools/{composeSchoolId}"
             Then it should respond with 204
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "composeSchoolVersion"
            Given a claim set is uploaded to CMS that grants "School" access to "E2E-ReadChangesComposeNoFurtherClaimSet" using authorization strategies "NoFurtherAuthorizationRequired" and "RelationshipsWithEdOrgsOnly"
              And the claim set upload to CMS should be successful
            Given the claimSet "E2E-ReadChangesComposeNoFurtherClaimSet" is authorized with educationOrganizationIds "255901001"
             When a GET request is made to "/ed-fi/schools/deletes?minChangeVersion={composeSchoolVersion}&maxChangeVersion={composeSchoolVersion}&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.id" should equal request variable "composeSchoolId"
              And the response body path "0.keyValues.schoolId" should have value "255901001"
            Given the claimSet "E2E-ReadChangesComposeNoFurtherClaimSet" is authorized with educationOrganizationIds "255901999"
             When a GET request is made to "/ed-fi/schools/deletes?minChangeVersion={composeSchoolVersion}&maxChangeVersion={composeSchoolVersion}&totalCount=true"
             Then it should respond with 200
              And total of records should be 0
              And the response body is
                  """
                  []
                  """

        # NoFurtherAuthorizationRequired on a resource is a no-op: the deleted School is returned to a
        # caller whose namespace+edOrg scope does not match anything about the resource, because NoFurther
        # applies no filtering at all. The School is seeded+deleted under the broad claim set first, then
        # the NoFurther strategy claim set is uploaded and authorized with a deliberately non-matching
        # namespace and edOrg; the delete is still returned (total-count 1).
        @e2e-ci-shard-1
        @ResetClaimsetsAfterScenario
        @reset-data-before-scenario
        Scenario: 05 NoFurtherAuthorizationRequired on a resource returns deletes regardless of caller scope
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with educationOrganizationIds "255901001"
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": 940300001,
                    "nameOfInstitution": "NoFurther Resource School",
                    "gradeLevels": [ { "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" } ],
                    "educationOrganizationCategories": [ { "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School" } ]
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "noFurtherSchoolId" variable
             When a DELETE request is made to "/ed-fi/schools/{noFurtherSchoolId}"
             Then it should respond with 204
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "noFurtherResourceVersion"
            Given a claim set is uploaded to CMS that grants "School" access to "E2E-ReadChangesNoFurtherResourceClaimSet" using authorization strategy "NoFurtherAuthorizationRequired"
              And the claim set upload to CMS should be successful
            Given the claimSet "E2E-ReadChangesNoFurtherResourceClaimSet" is authorized with namespace "uri://other.org" and educationOrganizationIds "255901999"
             When a GET request is made to "/ed-fi/schools/deletes?minChangeVersion={noFurtherResourceVersion}&maxChangeVersion={noFurtherResourceVersion}&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.id" should equal request variable "noFurtherSchoolId"
              And the response body path "0.keyValues.schoolId" should have value "940300001"

    Rule: Unsupported ReadChanges strategies fail with a security configuration ProblemDetails

        # Mirrors TrackedChangeEndpoints scenario 18 (OwnershipBased -> 500), but exercises
        # RelationshipsWithPeopleOnly, which has no ReadChanges implementation. DMS returns a security
        # configuration ProblemDetails (HTTP 500). The errors[0] text is copied verbatim from the live
        # response.
        @e2e-ci-shard-1
        @ResetClaimsetsAfterScenario
        @reset-data-before-scenario
        Scenario: 06 ReadChanges configured with RelationshipsWithPeopleOnly returns a security configuration ProblemDetails
            Given a claim set is uploaded to CMS that grants "School" access to "E2E-ReadChangesPeopleOnlyClaimSet" using authorization strategy "RelationshipsWithPeopleOnly"
              And the claim set upload to CMS should be successful
            Given the claimSet "E2E-ReadChangesPeopleOnlyClaimSet" is authorized with educationOrganizationIds "255901001"
             When a GET request is made to "/ed-fi/schools/deletes"
             Then it should respond with 500
              And the response headers include
                  """
                  { "content-type": "application/problem+json" }
                  """
              And the response body has a non-empty correlationId
              And the response body is
                  """
                  {
                    "type": "urn:ed-fi:api:system:configuration:security",
                    "title": "Security Configuration Error",
                    "status": 500,
                    "detail": "A security configuration problem was detected. The request cannot be authorized.",
                    "correlationId": null,
                    "validationErrors": {},
                    "errors": [
                      "Could not find authorization strategy implementations for the following strategy names: 'RelationshipsWithPeopleOnly'."
                    ]
                  }
                  """

        @e2e-ci-shard-1
        @ResetClaimsetsAfterScenario
        @reset-data-before-scenario
        Scenario: 07 ReadChanges configured with RelationshipsWithEdOrgsAndPeopleInverted returns a security configuration ProblemDetails
            Given a claim set is uploaded to CMS that grants "School" access to "E2E-ReadChangesEdOrgsAndPeopleInvertedClaimSet" using authorization strategy "RelationshipsWithEdOrgsAndPeopleInverted"
              And the claim set upload to CMS should be successful
            Given the claimSet "E2E-ReadChangesEdOrgsAndPeopleInvertedClaimSet" is authorized with educationOrganizationIds "255901001"
             When a GET request is made to "/ed-fi/schools/deletes"
             Then it should respond with 500
              And the response headers include
                  """
                  { "content-type": "application/problem+json" }
                  """
              And the response body has a non-empty correlationId
              And the response body is
                  """
                  {
                    "type": "urn:ed-fi:api:system:configuration:security",
                    "title": "Security Configuration Error",
                    "status": 500,
                    "detail": "A security configuration problem was detected. The request cannot be authorized.",
                    "correlationId": null,
                    "validationErrors": {},
                    "errors": [
                      "Could not find authorization strategy implementations for the following strategy names: 'RelationshipsWithEdOrgsAndPeopleInverted'."
                    ]
                  }
                  """
