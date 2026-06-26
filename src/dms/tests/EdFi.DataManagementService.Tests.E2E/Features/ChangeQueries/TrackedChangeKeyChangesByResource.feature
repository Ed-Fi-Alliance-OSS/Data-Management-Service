Feature: TrackedChange /keyChanges endpoints across resource and key shapes.

        # This feature exercises the Change Queries /keyChanges endpoint for resource and key shapes
        # beyond the single ClassPeriod collapse already covered in TrackedChangeEndpoints.feature
        # scenario 03, keeping the DMS E2E coverage close to the ODS Postman parity scenarios.
        #
        # Authorization note: scenarios 02 and 04 use EdFiSandbox with LEA/school data under the
        # 255901 scope so they can exercise the ODS Location and GradebookEntry key-change shapes
        # directly instead of substituting granted resources from E2E-NoFurtherAuthRequiredClaimSet.
        # Identity-update note: a /keyChanges row only appears when a PUT changes an identity field.
        # DMS gates this per resource via allowIdentityUpdates. Confirmed at run time:
        #   ALLOWED  (PUT 204, keyChange row produced): Session, Location, Grade, GradebookEntry,
        #            Section, StudentSectionAssociation, StudentSchoolAssociation, ClassPeriod.
        #   REJECTED (PUT 400 key-change-not-supported): Student, Contact, Course, CourseOffering.
        # CourseOffering rejects a DIRECT identity edit yet its identity still changes (and a
        # keyChange row appears) when a referenced parent Session's identity changes — see scenario 11.

        Background:
            Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade                      |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School        |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#District      |
                  | uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent charter district |
                  | uri://ed-fi.org/TermDescriptor#Fall Semester                          |
                  | uri://ed-fi.org/GradeTypeDescriptor#Final                             |
                  | uri://ed-fi.org/GradeTypeDescriptor#Summative                         |
                  | uri://ed-fi.org/GradingPeriodDescriptor#First Six Weeks               |
                  | uri://ed-fi.org/CourseIdentificationSystemDescriptor#CSSC course code  |
              And the system has these "schoolYearTypes"
                  | schoolYear | currentSchoolYear | schoolYearDescription |
                  | 2023       | true              | "year 2023"           |

        @ods-migrated
        @relational-backend
        @relational-ci-shard-4
        Scenario: 01 Session key change is reported in keyChanges
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": 930100101,
                    "nameOfInstitution": "Session KeyChange School",
                    "gradeLevels": [ { "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" } ],
                    "educationOrganizationCategories": [ { "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School" } ]
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/sessions" with
                  """
                  {
                    "sessionName": "Session One",
                    "schoolReference": { "schoolId": 930100101 },
                    "schoolYearTypeReference": { "schoolYear": 2023 },
                    "beginDate": "2023-08-01",
                    "endDate": "2023-12-20",
                    "totalInstructionalDays": 90,
                    "termDescriptor": "uri://ed-fi.org/TermDescriptor#Fall Semester"
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "sessionKeyChangeId" variable
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "sessionKeyChangeMinVersion"
             When a PUT request is made to "/ed-fi/sessions/{sessionKeyChangeId}" with
                  """
                  {
                    "id": "{sessionKeyChangeId}",
                    "sessionName": "Session Two",
                    "schoolReference": { "schoolId": 930100101 },
                    "schoolYearTypeReference": { "schoolYear": 2023 },
                    "beginDate": "2023-08-01",
                    "endDate": "2023-12-20",
                    "totalInstructionalDays": 90,
                    "termDescriptor": "uri://ed-fi.org/TermDescriptor#Fall Semester"
                  }
                  """
             Then it should respond with 204
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "sessionKeyChangeMaxVersion"
             When a GET request is made to "/ed-fi/sessions/keyChanges?minChangeVersion={sessionKeyChangeMinVersion}&maxChangeVersion={sessionKeyChangeMaxVersion}&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.id" should equal request variable "sessionKeyChangeId"
              And the response body path "0.oldKeyValues.sessionName" should have value "Session One"
              And the response body path "0.newKeyValues.sessionName" should have value "Session Two"
              And the response body path "0.oldKeyValues.schoolId" should have value "930100101"
              And the response body path "0.newKeyValues.schoolId" should have value "930100101"

        @ods-migrated
        @relational-backend
        @relational-ci-shard-4
        @reset-data-before-scenario
        Scenario: 02 Location key change reports the changed school and classroom identity columns
            Given the claimSet "EdFiSandbox" is authorized with namespace "uri://ed-fi.org" and educationOrganizationIds "255901"
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | categories                                                                                                           | localEducationAgencyCategoryDescriptor                                    |
                  | 255901                 | Location KC LEA   | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#District" }]  | "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent charter district" |
              And the system has these "schools"
                  | schoolId  | nameOfInstitution         | gradeLevels                                                                      | educationOrganizationCategories                                                                                   | localEducationAgencyReference     |
                  | 255901777 | Location KeyChange School A | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] | { "localEducationAgencyId": 255901 } |
                  | 255901001 | Location KeyChange School B | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] | { "localEducationAgencyId": 255901 } |
             When a POST request is made to "/ed-fi/locations" with
                  """
                  {
                    "schoolReference": { "schoolId": 255901777 },
                    "classroomIdentificationCode": "AAA",
                    "maximumNumberOfSeats": 20,
                    "optimalNumberOfSeats": 18
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "locationKeyChangeId" variable
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "locationKeyChangeMinVersion"
             When a PUT request is made to "/ed-fi/locations/{locationKeyChangeId}" with
                  """
                  {
                    "id": "{locationKeyChangeId}",
                    "schoolReference": { "schoolId": 255901001 },
                    "classroomIdentificationCode": "BBB",
                    "maximumNumberOfSeats": 20,
                    "optimalNumberOfSeats": 18
                  }
                  """
             Then it should respond with 204
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "locationKeyChangeMaxVersion"
             When a GET request is made to "/ed-fi/locations/keyChanges?minChangeVersion={locationKeyChangeMinVersion}&maxChangeVersion={locationKeyChangeMaxVersion}&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.id" should equal request variable "locationKeyChangeId"
              And the response body path "0.oldKeyValues.schoolId" should have value "255901777"
              And the response body path "0.newKeyValues.schoolId" should have value "255901001"
              And the response body path "0.oldKeyValues.classroomIdentificationCode" should have value "AAA"
              And the response body path "0.newKeyValues.classroomIdentificationCode" should have value "BBB"

        @ods-migrated
        @relational-backend
        @relational-ci-shard-4
        Scenario: 03 Grade key change is reported in keyChanges
            # DEEP CHAIN + identity update. Build order (all links granted by the broad set):
            # School -> Session -> Course -> CourseOffering -> Section -> Student ->
            # StudentSchoolAssociation -> StudentSectionAssociation -> GradingPeriod -> Grade,
            # then PUT the Grade changing gradeTypeDescriptor from #Final to #Summative.
            # Every POST body below was validated against the live DS-5.2 API.
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": 930100003,
                    "nameOfInstitution": "Grade KeyChange School",
                    "gradeLevels": [ { "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" } ],
                    "educationOrganizationCategories": [ { "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School" } ]
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/sessions" with
                  """
                  {
                    "sessionName": "Grade KC Session",
                    "schoolReference": { "schoolId": 930100003 },
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
                    "courseCode": "GRADE-KC-CRS",
                    "educationOrganizationReference": { "educationOrganizationId": 930100003 },
                    "courseTitle": "Grade KC Course",
                    "numberOfParts": 1,
                    "identificationCodes": [
                      {
                        "courseIdentificationSystemDescriptor": "uri://ed-fi.org/CourseIdentificationSystemDescriptor#CSSC course code",
                        "identificationCode": "GRADE-KC-CRS"
                      }
                    ]
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/courseOfferings" with
                  """
                  {
                    "localCourseCode": "GRADE-KC-LCC",
                    "courseReference": { "courseCode": "GRADE-KC-CRS", "educationOrganizationId": 930100003 },
                    "schoolReference": { "schoolId": 930100003 },
                    "sessionReference": { "schoolId": 930100003, "schoolYear": 2023, "sessionName": "Grade KC Session" }
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/sections" with
                  """
                  {
                    "sectionIdentifier": "GRADE-KC-SEC",
                    "courseOfferingReference": { "localCourseCode": "GRADE-KC-LCC", "schoolId": 930100003, "schoolYear": 2023, "sessionName": "Grade KC Session" }
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/students" with
                  """
                  { "studentUniqueId": "GRADE-KC-STU", "firstName": "Grade", "lastSurname": "Student", "birthDate": "2008-01-01" }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                    "entryDate": "2023-08-01",
                    "schoolReference": { "schoolId": 930100003 },
                    "studentReference": { "studentUniqueId": "GRADE-KC-STU" },
                    "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/studentSectionAssociations" with
                  """
                  {
                    "beginDate": "2023-08-01",
                    "sectionReference": { "localCourseCode": "GRADE-KC-LCC", "schoolId": 930100003, "schoolYear": 2023, "sectionIdentifier": "GRADE-KC-SEC", "sessionName": "Grade KC Session" },
                    "studentReference": { "studentUniqueId": "GRADE-KC-STU" }
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/gradingPeriods" with
                  """
                  {
                    "gradingPeriodName": "First Six Weeks",
                    "gradingPeriodDescriptor": "uri://ed-fi.org/GradingPeriodDescriptor#First Six Weeks",
                    "schoolReference": { "schoolId": 930100003 },
                    "schoolYearTypeReference": { "schoolYear": 2023 },
                    "beginDate": "2023-08-01",
                    "endDate": "2023-09-15",
                    "totalInstructionalDays": 30,
                    "periodSequence": 1
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/grades" with
                  """
                  {
                    "gradingPeriodReference": { "gradingPeriodName": "First Six Weeks", "gradingPeriodDescriptor": "uri://ed-fi.org/GradingPeriodDescriptor#First Six Weeks", "schoolId": 930100003, "schoolYear": 2023, "periodSequence": 1 },
                    "studentSectionAssociationReference": { "beginDate": "2023-08-01", "localCourseCode": "GRADE-KC-LCC", "schoolId": 930100003, "schoolYear": 2023, "sectionIdentifier": "GRADE-KC-SEC", "sessionName": "Grade KC Session", "studentUniqueId": "GRADE-KC-STU" },
                    "gradeTypeDescriptor": "uri://ed-fi.org/GradeTypeDescriptor#Final",
                    "letterGradeEarned": "A"
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "gradeKeyChangeId" variable
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "gradeKeyChangeMinVersion"
             When a PUT request is made to "/ed-fi/grades/{gradeKeyChangeId}" with
                  """
                  {
                    "id": "{gradeKeyChangeId}",
                    "gradingPeriodReference": { "gradingPeriodName": "First Six Weeks", "gradingPeriodDescriptor": "uri://ed-fi.org/GradingPeriodDescriptor#First Six Weeks", "schoolId": 930100003, "schoolYear": 2023, "periodSequence": 1 },
                    "studentSectionAssociationReference": { "beginDate": "2023-08-01", "localCourseCode": "GRADE-KC-LCC", "schoolId": 930100003, "schoolYear": 2023, "sectionIdentifier": "GRADE-KC-SEC", "sessionName": "Grade KC Session", "studentUniqueId": "GRADE-KC-STU" },
                    "gradeTypeDescriptor": "uri://ed-fi.org/GradeTypeDescriptor#Summative",
                    "letterGradeEarned": "A"
                  }
                  """
             Then it should respond with 204
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "gradeKeyChangeMaxVersion"
             When a GET request is made to "/ed-fi/grades/keyChanges?minChangeVersion={gradeKeyChangeMinVersion}&maxChangeVersion={gradeKeyChangeMaxVersion}&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.id" should equal request variable "gradeKeyChangeId"
              And the response body path "0.oldKeyValues.gradeTypeDescriptor" should have value "uri://ed-fi.org/GradeTypeDescriptor#Final"
              And the response body path "0.newKeyValues.gradeTypeDescriptor" should have value "uri://ed-fi.org/GradeTypeDescriptor#Summative"
              And the response body path "0.oldKeyValues.studentUniqueId" should have value "GRADE-KC-STU"
              And the response body path "0.newKeyValues.studentUniqueId" should have value "GRADE-KC-STU"

        @ods-migrated
        @relational-backend
        @relational-ci-shard-4
        @reset-data-before-scenario
        Scenario: 04 GradebookEntry key change is reported in keyChanges
            Given the claimSet "EdFiSandbox" is authorized with namespace "uri://ed-fi.org" and educationOrganizationIds "255901"
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution   | categories                                                                                                           | localEducationAgencyCategoryDescriptor                                    |
                  | 255901                 | GradebookEntry LEA  | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#District" }]  | "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent charter district" |
              And the system has these "schools"
                  | schoolId  | nameOfInstitution             | gradeLevels                                                                      | educationOrganizationCategories                                                                                   | localEducationAgencyReference     |
                  | 255901401 | GradebookEntry KeyChange School A | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] | { "localEducationAgencyId": 255901 } |
                  | 255901407 | GradebookEntry KeyChange School B | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] | { "localEducationAgencyId": 255901 } |
             When a POST request is made to "/ed-fi/sessions" with
                  """
                  {
                    "sessionName": "GradebookEntry KC Session",
                    "schoolReference": { "schoolId": 255901401 },
                    "schoolYearTypeReference": { "schoolYear": 2023 },
                    "beginDate": "2023-08-01",
                    "endDate": "2023-12-20",
                    "totalInstructionalDays": 90,
                    "termDescriptor": "uri://ed-fi.org/TermDescriptor#Fall Semester"
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/sessions" with
                  """
                  {
                    "sessionName": "GradebookEntry KC Session",
                    "schoolReference": { "schoolId": 255901407 },
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
                    "courseCode": "GBE-KC-CRS-A",
                    "educationOrganizationReference": { "educationOrganizationId": 255901401 },
                    "courseTitle": "GradebookEntry KC Course A",
                    "numberOfParts": 1,
                    "identificationCodes": [
                      {
                        "courseIdentificationSystemDescriptor": "uri://ed-fi.org/CourseIdentificationSystemDescriptor#CSSC course code",
                        "identificationCode": "GBE-KC-CRS-A"
                      }
                    ]
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/courses" with
                  """
                  {
                    "courseCode": "GBE-KC-CRS-B",
                    "educationOrganizationReference": { "educationOrganizationId": 255901407 },
                    "courseTitle": "GradebookEntry KC Course B",
                    "numberOfParts": 1,
                    "identificationCodes": [
                      {
                        "courseIdentificationSystemDescriptor": "uri://ed-fi.org/CourseIdentificationSystemDescriptor#CSSC course code",
                        "identificationCode": "GBE-KC-CRS-B"
                      }
                    ]
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/courseOfferings" with
                  """
                  {
                    "localCourseCode": "GBE-KC-LCC-A",
                    "courseReference": { "courseCode": "GBE-KC-CRS-A", "educationOrganizationId": 255901401 },
                    "schoolReference": { "schoolId": 255901401 },
                    "sessionReference": { "schoolId": 255901401, "schoolYear": 2023, "sessionName": "GradebookEntry KC Session" }
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/courseOfferings" with
                  """
                  {
                    "localCourseCode": "GBE-KC-LCC-B",
                    "courseReference": { "courseCode": "GBE-KC-CRS-B", "educationOrganizationId": 255901407 },
                    "schoolReference": { "schoolId": 255901407 },
                    "sessionReference": { "schoolId": 255901407, "schoolYear": 2023, "sessionName": "GradebookEntry KC Session" }
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/sections" with
                  """
                  {
                    "sectionIdentifier": "GBE-KC-SEC-A",
                    "courseOfferingReference": { "localCourseCode": "GBE-KC-LCC-A", "schoolId": 255901401, "schoolYear": 2023, "sessionName": "GradebookEntry KC Session" }
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/sections" with
                  """
                  {
                    "sectionIdentifier": "GBE-KC-SEC-B",
                    "courseOfferingReference": { "localCourseCode": "GBE-KC-LCC-B", "schoolId": 255901407, "schoolYear": 2023, "sessionName": "GradebookEntry KC Session" }
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/gradebookEntries" with
                  """
                  {
                    "sectionReference": { "localCourseCode": "GBE-KC-LCC-A", "schoolId": 255901401, "schoolYear": 2023, "sectionIdentifier": "GBE-KC-SEC-A", "sessionName": "GradebookEntry KC Session" },
                    "sourceSectionIdentifier": "GBE-KC-SEC-A",
                    "dateAssigned": "2023-07-04",
                    "gradebookEntryIdentifier": "GBE-KC-ENTRY-A",
                    "namespace": "uri://ed-fi.org/GradebookEntry/GradebookEntry.xml",
                    "title": "Test GradeBookEntry Title1"
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "gradebookEntryKeyChangeId" variable
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "gradebookEntryKeyChangeMinVersion"
             When a PUT request is made to "/ed-fi/gradebookEntries/{gradebookEntryKeyChangeId}" with
                  """
                  {
                    "id": "{gradebookEntryKeyChangeId}",
                    "sectionReference": { "localCourseCode": "GBE-KC-LCC-B", "schoolId": 255901407, "schoolYear": 2023, "sectionIdentifier": "GBE-KC-SEC-B", "sessionName": "GradebookEntry KC Session" },
                    "sourceSectionIdentifier": "GBE-KC-SEC-B",
                    "dateAssigned": "2023-08-04",
                    "gradebookEntryIdentifier": "GBE-KC-ENTRY-B",
                    "namespace": "uri://ed-fi.org/GradebookEntry/GradebookEntry.xml",
                    "title": "Test GradeBookEntry Title2"
                  }
                  """
             Then it should respond with 204
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "gradebookEntryKeyChangeMaxVersion"
             When a GET request is made to "/ed-fi/gradebookEntries/keyChanges?minChangeVersion={gradebookEntryKeyChangeMinVersion}&maxChangeVersion={gradebookEntryKeyChangeMaxVersion}&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.id" should equal request variable "gradebookEntryKeyChangeId"
              And the response body path "0.oldKeyValues.gradebookEntryIdentifier" should have value "GBE-KC-ENTRY-A"
              And the response body path "0.newKeyValues.gradebookEntryIdentifier" should have value "GBE-KC-ENTRY-B"
              And the response body path "0.oldKeyValues.namespace" should have value "uri://ed-fi.org/GradebookEntry/GradebookEntry.xml"
              And the response body path "0.newKeyValues.namespace" should have value "uri://ed-fi.org/GradebookEntry/GradebookEntry.xml"

        @ods-migrated
        @relational-backend
        @relational-ci-shard-4
        Scenario: 05 StudentSchoolAssociation key change reports the changed school and the student natural key
            # identity update on StudentSchoolAssociation, changing the school portion of the
            # composite key. Confirmed at run time: DMS allows this (PUT 204) and the keyChange row
            # carries the translated person natural key (studentUniqueId) alongside schoolId and
            # entryDate, matching the design's Grade/ODS-6480 person-key expectation.
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": 930100051,
                    "nameOfInstitution": "SSA KeyChange School A",
                    "gradeLevels": [ { "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" } ],
                    "educationOrganizationCategories": [ { "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School" } ]
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": 930100052,
                    "nameOfInstitution": "SSA KeyChange School B",
                    "gradeLevels": [ { "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" } ],
                    "educationOrganizationCategories": [ { "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School" } ]
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/students" with
                  """
                  { "studentUniqueId": "KC-SSA-STU-1", "firstName": "KeyChange", "lastSurname": "Student", "birthDate": "2008-01-01" }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                    "entryDate": "2023-08-01",
                    "schoolReference": { "schoolId": 930100051 },
                    "studentReference": { "studentUniqueId": "KC-SSA-STU-1" },
                    "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "ssaKeyChangeId" variable
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "ssaKeyChangeMinVersion"
             When a PUT request is made to "/ed-fi/studentSchoolAssociations/{ssaKeyChangeId}" with
                  """
                  {
                    "id": "{ssaKeyChangeId}",
                    "entryDate": "2023-08-01",
                    "schoolReference": { "schoolId": 930100052 },
                    "studentReference": { "studentUniqueId": "KC-SSA-STU-1" },
                    "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 204
             # Second key change to the SAME association (entryDate 2023-08-01 -> 2023-09-01) within the
             # window. ODS asserts multiple key changes coalesce to ONE keyChanges row whose oldKeyValues
             # is the pre-window ORIGINAL (not the intermediate value) and whose newKeyValues is the latest.
             When a PUT request is made to "/ed-fi/studentSchoolAssociations/{ssaKeyChangeId}" with
                  """
                  {
                    "id": "{ssaKeyChangeId}",
                    "entryDate": "2023-09-01",
                    "schoolReference": { "schoolId": 930100052 },
                    "studentReference": { "studentUniqueId": "KC-SSA-STU-1" },
                    "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 204
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "ssaKeyChangeMaxVersion"
             When a GET request is made to "/ed-fi/studentSchoolAssociations/keyChanges?minChangeVersion={ssaKeyChangeMinVersion}&maxChangeVersion={ssaKeyChangeMaxVersion}&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.id" should equal request variable "ssaKeyChangeId"
              And the response body path "0.oldKeyValues.schoolId" should have value "930100051"
              And the response body path "0.newKeyValues.schoolId" should have value "930100052"
              And the response body path "0.oldKeyValues.entryDate" should have value "2023-08-01"
              And the response body path "0.newKeyValues.entryDate" should have value "2023-09-01"
              And the response body path "0.oldKeyValues.studentUniqueId" should have value "KC-SSA-STU-1"
              And the response body path "0.newKeyValues.studentUniqueId" should have value "KC-SSA-STU-1"

        # Mirrors ODS "Key Changes / StaffSectionAssociations / Change Section CourseOffering":
        # StaffSectionAssociation is not updated directly. Updating the referenced Section's
        # courseOfferingReference cascades the Section key into StaffSectionAssociation and records
        # the association key change.
        @ods-migrated
        @relational-backend
        @relational-ci-shard-4
        Scenario: 06 StaffSectionAssociation key change via a changed Section course offering reference
            Given the claimSet "EdFiSandbox" is authorized with namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                               |
                  | uri://ed-fi.org/ClassroomPositionDescriptor#Assistant Teacher |
                  | uri://ed-fi.org/StaffClassificationDescriptor#Teacher         |
            Given the claimSet "EdFiSandbox" is authorized with namespace "uri://ed-fi.org" and educationOrganizationIds "930100006"
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": 930100006,
                    "nameOfInstitution": "SSecA CourseOffering School",
                    "gradeLevels": [ { "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" } ],
                    "educationOrganizationCategories": [ { "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School" } ]
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/sessions" with
                  """
                  {
                    "sessionName": "SSecA CO Session",
                    "schoolReference": { "schoolId": 930100006 },
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
                    "courseCode": "SSECA-CO-CRS",
                    "educationOrganizationReference": { "educationOrganizationId": 930100006 },
                    "courseTitle": "SSecA CO Course",
                    "numberOfParts": 1,
                    "identificationCodes": [
                      {
                        "courseIdentificationSystemDescriptor": "uri://ed-fi.org/CourseIdentificationSystemDescriptor#CSSC course code",
                        "identificationCode": "SSECA-CO-CRS"
                      }
                    ]
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/courseOfferings" with
                  """
                  {
                    "localCourseCode": "SSECA-CO-LCC-A",
                    "courseReference": { "courseCode": "SSECA-CO-CRS", "educationOrganizationId": 930100006 },
                    "schoolReference": { "schoolId": 930100006 },
                    "sessionReference": { "schoolId": 930100006, "schoolYear": 2023, "sessionName": "SSecA CO Session" }
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/courseOfferings" with
                  """
                  {
                    "localCourseCode": "SSECA-CO-LCC-B",
                    "courseReference": { "courseCode": "SSECA-CO-CRS", "educationOrganizationId": 930100006 },
                    "schoolReference": { "schoolId": 930100006 },
                    "sessionReference": { "schoolId": 930100006, "schoolYear": 2023, "sessionName": "SSecA CO Session" }
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/sections" with
                  """
                  {
                    "sectionIdentifier": "SSECA-CO-SEC",
                    "courseOfferingReference": { "localCourseCode": "SSECA-CO-LCC-A", "schoolId": 930100006, "schoolYear": 2023, "sessionName": "SSecA CO Session" }
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "ssecaSectionId" variable
             When a POST request is made to "/ed-fi/staffs" with
                  """
                  {
                    "staffUniqueId": "SSECA-CO-STAFF",
                    "firstName": "CourseOffering",
                    "lastSurname": "Staff"
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/staffEducationOrganizationAssignmentAssociations" with
                  """
                  {
                    "beginDate": "2023-08-01",
                    "educationOrganizationReference": { "educationOrganizationId": 930100006 },
                    "staffReference": { "staffUniqueId": "SSECA-CO-STAFF" },
                    "staffClassificationDescriptor": "uri://ed-fi.org/StaffClassificationDescriptor#Teacher",
                    "positionTitle": "Math Teacher"
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/staffSectionAssociations" with
                  """
                  {
                    "beginDate": "2023-08-01",
                    "sectionReference": { "localCourseCode": "SSECA-CO-LCC-A", "schoolId": 930100006, "schoolYear": 2023, "sectionIdentifier": "SSECA-CO-SEC", "sessionName": "SSecA CO Session" },
                    "staffReference": { "staffUniqueId": "SSECA-CO-STAFF" },
                    "classroomPositionDescriptor": "uri://ed-fi.org/ClassroomPositionDescriptor#Assistant Teacher",
                    "endDate": "2023-12-20",
                    "highlyQualifiedTeacher": true,
                    "percentageContribution": 0,
                    "teacherStudentDataLinkExclusion": true
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "ssecaCoKeyChangeId" variable
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "ssecaCoKeyChangeMinVersion"
             When a PUT request is made to "/ed-fi/sections/{ssecaSectionId}" with
                  """
                  {
                    "id": "{ssecaSectionId}",
                    "sectionIdentifier": "SSECA-CO-SEC",
                    "courseOfferingReference": { "localCourseCode": "SSECA-CO-LCC-B", "schoolId": 930100006, "schoolYear": 2023, "sessionName": "SSecA CO Session" }
                  }
                  """
             Then it should respond with 204
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "ssecaCoKeyChangeMaxVersion"
             When a GET request is made to "/ed-fi/staffSectionAssociations/keyChanges?minChangeVersion={ssecaCoKeyChangeMinVersion}&maxChangeVersion={ssecaCoKeyChangeMaxVersion}&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.id" should equal request variable "ssecaCoKeyChangeId"
              And the response body path "0.oldKeyValues.localCourseCode" should have value "SSECA-CO-LCC-A"
              And the response body path "0.newKeyValues.localCourseCode" should have value "SSECA-CO-LCC-B"
              And the response body path "0.oldKeyValues.sectionIdentifier" should have value "SSECA-CO-SEC"
              And the response body path "0.newKeyValues.sectionIdentifier" should have value "SSECA-CO-SEC"
              And the response body path "0.oldKeyValues.staffUniqueId" should have value "SSECA-CO-STAFF"
              And the response body path "0.newKeyValues.staffUniqueId" should have value "SSECA-CO-STAFF"

        # NOTE: Expected body copied verbatim from DMS's actual ProblemDetails response. DMS reports
        # invalid limit/offset on /keyChanges as a generic bad-request (urn:ed-fi:api:bad-request),
        # lists Offset before Limit, and phrases the bounds as "numeric value between 0 and 500".
        @ods-migrated
        @relational-backend
        @relational-ci-shard-4
        Scenario: 07 KeyChanges response rejects invalid limit and offset
             When a GET request is made to "/ed-fi/students/keyChanges?limit=-1&offset=-1"
             Then it should respond with 400
              And the response body is
                  """
                  {
                    "detail": "The request could not be processed. See 'errors' for details.",
                    "type": "urn:ed-fi:api:bad-request",
                    "title": "Bad Request",
                    "status": 400,
                    "correlationId": null,
                    "validationErrors": {},
                    "errors": [
                      "Offset must be a numeric value greater than or equal to 0.",
                      "Limit must be omitted or set to a numeric value between 0 and 500."
                    ]
                  }
                  """

        @ods-migrated
        @relational-backend
        @relational-ci-shard-4
        Scenario: 08 KeyChanges request for an unknown resource returns 404
             When a GET request is made to "/ed-fi/nonExistingResources/keyChanges"
             Then it should respond with 404
              And the response body is
                  """
                  {
                    "detail": "The specified data could not be found.",
                    "type": "urn:ed-fi:api:not-found",
                    "title": "Not Found",
                    "status": 404,
                    "correlationId": null,
                    "validationErrors": {},
                    "errors": []
                  }
                  """

        # Person resources are immutable: a PUT that changes the identity is rejected with
        # key-change-not-supported. Body copied verbatim from DMS's actual response.
        @ods-migrated
        @relational-backend
        @relational-ci-shard-4
        Scenario: 09 Changing a Student identity is rejected as key-change-not-supported
             When a POST request is made to "/ed-fi/students" with
                  """
                  { "studentUniqueId": "KC-STU-IMMUTABLE", "firstName": "Immutable", "lastSurname": "Student", "birthDate": "2008-01-01" }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "immutableStudentId" variable
             When a PUT request is made to "/ed-fi/students/{immutableStudentId}" with
                  """
                  {
                    "id": "{immutableStudentId}",
                    "studentUniqueId": "KC-STU-IMMUTABLE-CHANGED",
                    "firstName": "Immutable",
                    "lastSurname": "Student",
                    "birthDate": "2008-01-01"
                  }
                  """
             Then it should respond with 400
              And the response body is
                  """
                  {
                    "detail": "Identifying values for the Student resource cannot be changed. Delete and recreate the resource item instead.",
                    "type": "urn:ed-fi:api:bad-request:data-validation-failed:key-change-not-supported",
                    "title": "Key Change Not Supported",
                    "status": 400,
                    "correlationId": null,
                    "validationErrors": {},
                    "errors": []
                  }
                  """

        @ods-migrated
        @relational-backend
        @relational-ci-shard-4
        Scenario: 10 Changing a Contact identity is rejected as key-change-not-supported
             When a POST request is made to "/ed-fi/contacts" with
                  """
                  { "contactUniqueId": "KC-CON-IMMUTABLE", "firstName": "Immutable", "lastSurname": "Contact" }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "immutableContactId" variable
             When a PUT request is made to "/ed-fi/contacts/{immutableContactId}" with
                  """
                  {
                    "id": "{immutableContactId}",
                    "contactUniqueId": "KC-CON-IMMUTABLE-CHANGED",
                    "firstName": "Immutable",
                    "lastSurname": "Contact"
                  }
                  """
             Then it should respond with 400
              And the response body is
                  """
                  {
                    "detail": "Identifying values for the Contact resource cannot be changed. Delete and recreate the resource item instead.",
                    "type": "urn:ed-fi:api:bad-request:data-validation-failed:key-change-not-supported",
                    "title": "Key Change Not Supported",
                    "status": 400,
                    "correlationId": null,
                    "validationErrors": {},
                    "errors": []
                  }
                  """

        # Mirrors ODS "Change Class Period Key (Invalid key value)". classPeriodName is the
        # ClassPeriod string identity (maxLength 60). A PUT that changes it to an over-long value is
        # rejected by request-body validation before any key change is tracked. Body copied verbatim
        # from DMS's actual data-validation-failed ProblemDetails response.
        @ods-migrated
        @relational-backend
        @relational-ci-shard-4
        Scenario: 13 Changing a ClassPeriod key to an invalid over-long value is rejected with a validation error
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": 930100013,
                    "nameOfInstitution": "Invalid Key ClassPeriod School",
                    "gradeLevels": [ { "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" } ],
                    "educationOrganizationCategories": [ { "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School" } ]
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/classPeriods" with
                  """
                  {
                    "classPeriodName": "Invalid Key Period",
                    "schoolReference": { "schoolId": 930100013 }
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "invalidKeyClassPeriodId" variable
             When a PUT request is made to "/ed-fi/classPeriods/{invalidKeyClassPeriodId}" with
                  """
                  {
                    "id": "{invalidKeyClassPeriodId}",
                    "classPeriodName": "ThisClassPeriodNameIsDeliberatelyLongerThanSixtyCharactersToFailValidation",
                    "schoolReference": { "schoolId": 930100013 }
                  }
                  """
             Then it should respond with 400
              And the response body is
                  """
                  {
                    "detail": "Data validation failed. See 'validationErrors' for details.",
                    "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                    "title": "Data Validation Failed",
                    "status": 400,
                    "correlationId": null,
                    "validationErrors": {
                      "$.classPeriodName": [
                        "classPeriodName Value should be at most 60 characters"
                      ]
                    },
                    "errors": []
                  }
                  """

        # CASCADE (parent -> child). A Session key change cascades into its CourseOffering: the
        # CourseOffering's identity contains sessionName, so renaming the Session rewrites the
        # CourseOffering in place. The cascade write bumps the CourseOffering's ContentVersion AND
        # its ContentLastModifiedAt (surfaced as _lastModifiedDate) in the same UPDATE. ODS's
        # cascade test asserts the child's _lastModifiedDate changed; this scenario asserts the same
        # effect three ways: (a) the ContentVersion bump (the CourseOffering reappears in its live
        # collection filtered by minChangeVersion with the NEW sessionName), (b) a CourseOffering
        # /keyChanges row (its identity includes sessionName, even though a DIRECT identity edit is
        # rejected), and (c) the CourseOffering's _lastModifiedDate equals the renamed Session's
        # _lastModifiedDate — both rows are stamped with the cascade write's timestamp. We compare
        # to the parent's fresh timestamp rather than a before/after delta because DMS exposes
        # _lastModifiedDate at whole-second precision, so a same-second create/cascade would defeat
        # a naive "changed" check.
        @ods-migrated
        @relational-backend
        @relational-ci-shard-4
        Scenario: 11 Session key change cascades a change version bump and key change to its CourseOffering
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": 930100011,
                    "nameOfInstitution": "Cascade CourseOffering School",
                    "gradeLevels": [ { "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" } ],
                    "educationOrganizationCategories": [ { "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School" } ]
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/sessions" with
                  """
                  {
                    "sessionName": "Cascade One",
                    "schoolReference": { "schoolId": 930100011 },
                    "schoolYearTypeReference": { "schoolYear": 2023 },
                    "beginDate": "2023-08-01",
                    "endDate": "2023-12-20",
                    "totalInstructionalDays": 90,
                    "termDescriptor": "uri://ed-fi.org/TermDescriptor#Fall Semester"
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "cascadeSessionId" variable
             When a POST request is made to "/ed-fi/courses" with
                  """
                  {
                    "courseCode": "CASCADE-CRS",
                    "educationOrganizationReference": { "educationOrganizationId": 930100011 },
                    "courseTitle": "Cascade Course",
                    "numberOfParts": 1,
                    "identificationCodes": [
                      {
                        "courseIdentificationSystemDescriptor": "uri://ed-fi.org/CourseIdentificationSystemDescriptor#CSSC course code",
                        "identificationCode": "CASCADE-CRS"
                      }
                    ]
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/courseOfferings" with
                  """
                  {
                    "localCourseCode": "CASCADE-LCC",
                    "courseReference": { "courseCode": "CASCADE-CRS", "educationOrganizationId": 930100011 },
                    "schoolReference": { "schoolId": 930100011 },
                    "sessionReference": { "schoolId": 930100011, "schoolYear": 2023, "sessionName": "Cascade One" }
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "cascadeCourseOfferingId" variable
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "cascadeCoBaselineVersion"
             When a PUT request is made to "/ed-fi/sessions/{cascadeSessionId}" with
                  """
                  {
                    "id": "{cascadeSessionId}",
                    "sessionName": "Cascade Two",
                    "schoolReference": { "schoolId": 930100011 },
                    "schoolYearTypeReference": { "schoolYear": 2023 },
                    "beginDate": "2023-08-01",
                    "endDate": "2023-12-20",
                    "totalInstructionalDays": 90,
                    "termDescriptor": "uri://ed-fi.org/TermDescriptor#Fall Semester"
                  }
                  """
             Then it should respond with 204
             When a GET request is made to "/ed-fi/sessions/{cascadeSessionId}"
             Then it should respond with 200
              And the response body path "_lastModifiedDate" is stored in request variable "cascadeSessionLastModified"
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "cascadeCoAfterVersion"
             When a GET request is made to "/ed-fi/courseOfferings?minChangeVersion={cascadeCoBaselineVersion}&maxChangeVersion={cascadeCoAfterVersion}&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.id" should equal request variable "cascadeCourseOfferingId"
              And the response body path "0.sessionReference.sessionName" should have value "Cascade Two"
              And the response body path "0._lastModifiedDate" should equal request variable "cascadeSessionLastModified"
             When a GET request is made to "/ed-fi/courseOfferings/keyChanges?minChangeVersion={cascadeCoBaselineVersion}&maxChangeVersion={cascadeCoAfterVersion}&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.id" should equal request variable "cascadeCourseOfferingId"
              And the response body path "0.oldKeyValues.sessionName" should have value "Cascade One"
              And the response body path "0.newKeyValues.sessionName" should have value "Cascade Two"

        # CASCADE (child reference -> referencing parent). A ClassPeriod key change cascades into the
        # BellSchedule that references it: BellSchedule's own identity (bellScheduleName) is unchanged,
        # so no BellSchedule keyChange row is produced, but the cascade write rewrites the embedded
        # classPeriodReference and bumps the BellSchedule's ContentVersion AND its ContentLastModifiedAt
        # (surfaced as _lastModifiedDate) in the same UPDATE. ODS's cascade test asserts the child's
        # _lastModifiedDate changed; this scenario asserts the same effect two ways: (a) the
        # ContentVersion bump (the BellSchedule reappears in its live collection filtered by
        # minChangeVersion with the renamed classPeriod reference), and (b) the BellSchedule's
        # _lastModifiedDate equals the renamed ClassPeriod's _lastModifiedDate — both rows are stamped
        # with the cascade write's timestamp. We compare to the parent's fresh timestamp rather than a
        # before/after delta because DMS exposes _lastModifiedDate at whole-second precision.
        @ods-migrated
        @relational-backend
        @relational-ci-shard-4
        Scenario: 12 ClassPeriod key change cascades a change version bump to its BellSchedule
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": 930100012,
                    "nameOfInstitution": "Cascade BellSchedule School",
                    "gradeLevels": [ { "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" } ],
                    "educationOrganizationCategories": [ { "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School" } ]
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/classPeriods" with
                  """
                  {
                    "classPeriodName": "Cascade Period",
                    "schoolReference": { "schoolId": 930100012 }
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "cascadeClassPeriodId" variable
             When a POST request is made to "/ed-fi/bellSchedules" with
                  """
                  {
                    "bellScheduleName": "Cascade Bell",
                    "schoolReference": { "schoolId": 930100012 },
                    "classPeriods": [
                      { "classPeriodReference": { "classPeriodName": "Cascade Period", "schoolId": 930100012 } }
                    ]
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "cascadeBellScheduleId" variable
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "cascadeBellBaselineVersion"
             When a PUT request is made to "/ed-fi/classPeriods/{cascadeClassPeriodId}" with
                  """
                  {
                    "id": "{cascadeClassPeriodId}",
                    "classPeriodName": "Cascade Period Renamed",
                    "schoolReference": { "schoolId": 930100012 }
                  }
                  """
             Then it should respond with 204
             When a GET request is made to "/ed-fi/classPeriods/{cascadeClassPeriodId}"
             Then it should respond with 200
              And the response body path "_lastModifiedDate" is stored in request variable "cascadeClassPeriodLastModified"
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "cascadeBellAfterVersion"
             When a GET request is made to "/ed-fi/bellSchedules?minChangeVersion={cascadeBellBaselineVersion}&maxChangeVersion={cascadeBellAfterVersion}&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.id" should equal request variable "cascadeBellScheduleId"
              And the response body path "0.classPeriods.0.classPeriodReference.classPeriodName" should have value "Cascade Period Renamed"
              And the response body path "0._lastModifiedDate" should equal request variable "cascadeClassPeriodLastModified"

        # Mirrors ODS "Key Changes / Sections" coalescing + Total-Count semantics: two sectionIdentifier
        # changes to the SAME Section collapse to ONE keyChanges row whose oldKeyValues is the pre-window
        # ORIGINAL (SECTION-KC-AAA, not the intermediate SECTION-KC-BBB) and whose newKeyValues is the
        # latest (SECTION-KC-CCC). Total-Count is 1 — it counts the single affected Section, not the two
        # key-change operations. (EdOrg-scoped Section keyChanges authorization is covered in
        # TrackedChangeAuthorization.feature scenario 07; multi-item paging/limit is covered by
        # TrackedChangeEndpoints.feature scenario 10.)
        @ods-migrated
        @relational-backend
        @relational-ci-shard-4
        Scenario: 14 Section key changes collapse to one keyChanges row carrying the original old identity
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": 930100014,
                    "nameOfInstitution": "Section KeyChange School",
                    "gradeLevels": [ { "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" } ],
                    "educationOrganizationCategories": [ { "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School" } ]
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/sessions" with
                  """
                  {
                    "sessionName": "Section KC Session",
                    "schoolReference": { "schoolId": 930100014 },
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
                    "courseCode": "SECTION-KC-CRS",
                    "educationOrganizationReference": { "educationOrganizationId": 930100014 },
                    "courseTitle": "Section KC Course",
                    "numberOfParts": 1,
                    "identificationCodes": [
                      {
                        "courseIdentificationSystemDescriptor": "uri://ed-fi.org/CourseIdentificationSystemDescriptor#CSSC course code",
                        "identificationCode": "SECTION-KC-CRS"
                      }
                    ]
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/courseOfferings" with
                  """
                  {
                    "localCourseCode": "SECTION-KC-LCC",
                    "courseReference": { "courseCode": "SECTION-KC-CRS", "educationOrganizationId": 930100014 },
                    "schoolReference": { "schoolId": 930100014 },
                    "sessionReference": { "schoolId": 930100014, "schoolYear": 2023, "sessionName": "Section KC Session" }
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/sections" with
                  """
                  {
                    "sectionIdentifier": "SECTION-KC-AAA",
                    "courseOfferingReference": { "localCourseCode": "SECTION-KC-LCC", "schoolId": 930100014, "schoolYear": 2023, "sessionName": "Section KC Session" }
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "sectionKeyChangeId" variable
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "sectionKeyChangeMinVersion"
             When a PUT request is made to "/ed-fi/sections/{sectionKeyChangeId}" with
                  """
                  {
                    "id": "{sectionKeyChangeId}",
                    "sectionIdentifier": "SECTION-KC-BBB",
                    "courseOfferingReference": { "localCourseCode": "SECTION-KC-LCC", "schoolId": 930100014, "schoolYear": 2023, "sessionName": "Section KC Session" }
                  }
                  """
             Then it should respond with 204
             When a PUT request is made to "/ed-fi/sections/{sectionKeyChangeId}" with
                  """
                  {
                    "id": "{sectionKeyChangeId}",
                    "sectionIdentifier": "SECTION-KC-CCC",
                    "courseOfferingReference": { "localCourseCode": "SECTION-KC-LCC", "schoolId": 930100014, "schoolYear": 2023, "sessionName": "Section KC Session" }
                  }
                  """
             Then it should respond with 204
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "sectionKeyChangeMaxVersion"
             When a GET request is made to "/ed-fi/sections/keyChanges?minChangeVersion={sectionKeyChangeMinVersion}&maxChangeVersion={sectionKeyChangeMaxVersion}&totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  { "total-count": 1 }
                  """
              And total of records should be 1
              And the response body path "0.id" should equal request variable "sectionKeyChangeId"
              And the response body path "0.oldKeyValues.sectionIdentifier" should have value "SECTION-KC-AAA"
              And the response body path "0.newKeyValues.sectionIdentifier" should have value "SECTION-KC-CCC"
              And the response body path "0.oldKeyValues.localCourseCode" should have value "SECTION-KC-LCC"
              And the response body path "0.newKeyValues.localCourseCode" should have value "SECTION-KC-LCC"

        # Mirrors ODS "Key Changes / Sections" multi-item paging: four distinct Sections each get one
        # identity change inside the [min, max] window, then keyChanges is requested with limit=3.
        # ODS asserts the page returns exactly `limit` items while Total-Count reports the number of
        # DISTINCT items affected by key changes (4) — not the page size and not the count of tracked
        # key-change operations. The single-item collapse + Total-Count=1 case is covered by scenario 14;
        # this scenario covers the page-smaller-than-the-result-set case that scenario 14's note pointed
        # at TrackedChangeEndpoints scenario 10 for, which only exercises /deletes paging.
        @ods-migrated
        @relational-backend
        @relational-ci-shard-4
        Scenario: 15 Section keyChanges paging returns a partial page while total-count reports all affected items
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": 930100015,
                    "nameOfInstitution": "Section Paging School",
                    "gradeLevels": [ { "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" } ],
                    "educationOrganizationCategories": [ { "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School" } ]
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/sessions" with
                  """
                  {
                    "sessionName": "Section PG Session",
                    "schoolReference": { "schoolId": 930100015 },
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
                    "courseCode": "SECTION-PG-CRS",
                    "educationOrganizationReference": { "educationOrganizationId": 930100015 },
                    "courseTitle": "Section PG Course",
                    "numberOfParts": 1,
                    "identificationCodes": [
                      {
                        "courseIdentificationSystemDescriptor": "uri://ed-fi.org/CourseIdentificationSystemDescriptor#CSSC course code",
                        "identificationCode": "SECTION-PG-CRS"
                      }
                    ]
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/courseOfferings" with
                  """
                  {
                    "localCourseCode": "SECTION-PG-LCC",
                    "courseReference": { "courseCode": "SECTION-PG-CRS", "educationOrganizationId": 930100015 },
                    "schoolReference": { "schoolId": 930100015 },
                    "sessionReference": { "schoolId": 930100015, "schoolYear": 2023, "sessionName": "Section PG Session" }
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/sections" with
                  """
                  { "sectionIdentifier": "SECTION-PG-A1", "courseOfferingReference": { "localCourseCode": "SECTION-PG-LCC", "schoolId": 930100015, "schoolYear": 2023, "sessionName": "Section PG Session" } }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "sectionPgAId" variable
             When a POST request is made to "/ed-fi/sections" with
                  """
                  { "sectionIdentifier": "SECTION-PG-B1", "courseOfferingReference": { "localCourseCode": "SECTION-PG-LCC", "schoolId": 930100015, "schoolYear": 2023, "sessionName": "Section PG Session" } }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "sectionPgBId" variable
             When a POST request is made to "/ed-fi/sections" with
                  """
                  { "sectionIdentifier": "SECTION-PG-C1", "courseOfferingReference": { "localCourseCode": "SECTION-PG-LCC", "schoolId": 930100015, "schoolYear": 2023, "sessionName": "Section PG Session" } }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "sectionPgCId" variable
             When a POST request is made to "/ed-fi/sections" with
                  """
                  { "sectionIdentifier": "SECTION-PG-D1", "courseOfferingReference": { "localCourseCode": "SECTION-PG-LCC", "schoolId": 930100015, "schoolYear": 2023, "sessionName": "Section PG Session" } }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "sectionPgDId" variable
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "sectionPgMinVersion"
             When a PUT request is made to "/ed-fi/sections/{sectionPgAId}" with
                  """
                  {
                    "id": "{sectionPgAId}",
                    "sectionIdentifier": "SECTION-PG-A2",
                    "courseOfferingReference": { "localCourseCode": "SECTION-PG-LCC", "schoolId": 930100015, "schoolYear": 2023, "sessionName": "Section PG Session" }
                  }
                  """
             Then it should respond with 204
             When a PUT request is made to "/ed-fi/sections/{sectionPgBId}" with
                  """
                  {
                    "id": "{sectionPgBId}",
                    "sectionIdentifier": "SECTION-PG-B2",
                    "courseOfferingReference": { "localCourseCode": "SECTION-PG-LCC", "schoolId": 930100015, "schoolYear": 2023, "sessionName": "Section PG Session" }
                  }
                  """
             Then it should respond with 204
             When a PUT request is made to "/ed-fi/sections/{sectionPgCId}" with
                  """
                  {
                    "id": "{sectionPgCId}",
                    "sectionIdentifier": "SECTION-PG-C2",
                    "courseOfferingReference": { "localCourseCode": "SECTION-PG-LCC", "schoolId": 930100015, "schoolYear": 2023, "sessionName": "Section PG Session" }
                  }
                  """
             Then it should respond with 204
             When a PUT request is made to "/ed-fi/sections/{sectionPgDId}" with
                  """
                  {
                    "id": "{sectionPgDId}",
                    "sectionIdentifier": "SECTION-PG-D2",
                    "courseOfferingReference": { "localCourseCode": "SECTION-PG-LCC", "schoolId": 930100015, "schoolYear": 2023, "sessionName": "Section PG Session" }
                  }
                  """
             Then it should respond with 204
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "sectionPgMaxVersion"
             When a GET request is made to "/ed-fi/sections/keyChanges?minChangeVersion={sectionPgMinVersion}&maxChangeVersion={sectionPgMaxVersion}&totalCount=true&limit=3"
             Then it should respond with 200
              And the response headers include
                  """
                  { "total-count": 4 }
                  """
              And total of records should be 3

        # Mirrors ODS "Key Changes / Descriptors / Get Key Changes" (request sent with no query
        # parameters). Descriptor identities cannot change, so keyChanges is always empty; and because
        # totalCount was not requested the Total-Count header must be omitted entirely (distinct from
        # scenario 04 in TrackedChangeEndpoints, which requests totalCount=true and asserts the header
        # is present with value 0).
        @ods-migrated
        @relational-backend
        @relational-ci-shard-4
        Scenario: 16 Descriptor keyChanges without totalCount returns an empty array and omits the total-count header
             When a GET request is made to "/ed-fi/gradeLevelDescriptors/keyChanges"
             Then it should respond with 200
              And the response headers does not include total-count
              And the response body is
                  """
                  []
                  """

        # Mirrors the ODS keyChanges query shape: every ODS keyChanges request passes only
        # minChangeVersion (no maxChangeVersion) and does not request totalCount. This exercises the
        # min-only optional-parameter path for keyChanges (the equivalent /deletes path is covered by
        # TrackedChangeEndpoints scenarios 13 and 14) and confirms the Total-Count header is omitted
        # when totalCount is not requested even though a key-change row is returned.
        @ods-migrated
        @relational-backend
        @relational-ci-shard-4
        Scenario: 17 ClassPeriod keyChanges served with only minChangeVersion omits the total-count header
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": 930100016,
                    "nameOfInstitution": "Min Only KeyChange School",
                    "gradeLevels": [ { "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" } ],
                    "educationOrganizationCategories": [ { "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School" } ]
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/classPeriods" with
                  """
                  {
                    "classPeriodName": "Min Only Period",
                    "schoolReference": { "schoolId": 930100016 }
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "minOnlyKcClassPeriodId" variable
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "minOnlyKcMinVersion"
             When a PUT request is made to "/ed-fi/classPeriods/{minOnlyKcClassPeriodId}" with
                  """
                  {
                    "id": "{minOnlyKcClassPeriodId}",
                    "classPeriodName": "Min Only Period Renamed",
                    "schoolReference": { "schoolId": 930100016 }
                  }
                  """
             Then it should respond with 204
             When a GET request is made to "/ed-fi/classPeriods/keyChanges?minChangeVersion={minOnlyKcMinVersion}"
             Then it should respond with 200
              And the response headers does not include total-count
              And total of records should be 1
              And the response body path "0.id" should equal request variable "minOnlyKcClassPeriodId"
              And the response body path "0.oldKeyValues.classPeriodName" should have value "Min Only Period"
              And the response body path "0.newKeyValues.classPeriodName" should have value "Min Only Period Renamed"

        # Mirrors the ODS "Key Changes / StudentSchoolAssociations" Total-Count defect (ODS reported
        # Total-Count 1 when two distinct associations had key changes inside the window). The
        # discriminating shape is a MIX of multiplicities: association A changes once and association B
        # changes three times, giving four tracked key-change operations across two distinct items.
        # Total-Count must report the number of DISTINCT affected associations (2) — not the operation
        # count (4, which scenario 15 cannot distinguish because there every item changes exactly once)
        # and not the ODS-bug undercount (1). B's three changes also coalesce to a single response row,
        # so total of records is 2 as well.
        @ods-migrated
        @relational-backend
        @relational-ci-shard-4
        Scenario: 18 Total-Count reports distinct key-changed StudentSchoolAssociations when one changes multiple times
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": 930100018,
                    "nameOfInstitution": "KeyChange Count School",
                    "gradeLevels": [ { "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" } ],
                    "educationOrganizationCategories": [ { "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School" } ]
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/students" with
                  """
                  { "studentUniqueId": "KC-CNT-STU-A", "firstName": "Count", "lastSurname": "StudentA", "birthDate": "2008-01-01" }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/students" with
                  """
                  { "studentUniqueId": "KC-CNT-STU-B", "firstName": "Count", "lastSurname": "StudentB", "birthDate": "2008-01-01" }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                    "entryDate": "2023-08-01",
                    "schoolReference": { "schoolId": 930100018 },
                    "studentReference": { "studentUniqueId": "KC-CNT-STU-A" },
                    "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "countSsaAId" variable
             When a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                    "entryDate": "2023-08-01",
                    "schoolReference": { "schoolId": 930100018 },
                    "studentReference": { "studentUniqueId": "KC-CNT-STU-B" },
                    "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "countSsaBId" variable
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "countKeyChangeMinVersion"
             # Association A: a single key change (entryDate 2023-08-01 -> 2023-09-01).
             When a PUT request is made to "/ed-fi/studentSchoolAssociations/{countSsaAId}" with
                  """
                  {
                    "id": "{countSsaAId}",
                    "entryDate": "2023-09-01",
                    "schoolReference": { "schoolId": 930100018 },
                    "studentReference": { "studentUniqueId": "KC-CNT-STU-A" },
                    "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 204
             # Association B: three key changes (entryDate 2023-08-01 -> 2023-09-01 -> 2023-10-01 -> 2023-11-01).
             When a PUT request is made to "/ed-fi/studentSchoolAssociations/{countSsaBId}" with
                  """
                  {
                    "id": "{countSsaBId}",
                    "entryDate": "2023-09-01",
                    "schoolReference": { "schoolId": 930100018 },
                    "studentReference": { "studentUniqueId": "KC-CNT-STU-B" },
                    "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 204
             When a PUT request is made to "/ed-fi/studentSchoolAssociations/{countSsaBId}" with
                  """
                  {
                    "id": "{countSsaBId}",
                    "entryDate": "2023-10-01",
                    "schoolReference": { "schoolId": 930100018 },
                    "studentReference": { "studentUniqueId": "KC-CNT-STU-B" },
                    "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 204
             When a PUT request is made to "/ed-fi/studentSchoolAssociations/{countSsaBId}" with
                  """
                  {
                    "id": "{countSsaBId}",
                    "entryDate": "2023-11-01",
                    "schoolReference": { "schoolId": 930100018 },
                    "studentReference": { "studentUniqueId": "KC-CNT-STU-B" },
                    "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 204
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "countKeyChangeMaxVersion"
             When a GET request is made to "/ed-fi/studentSchoolAssociations/keyChanges?minChangeVersion={countKeyChangeMinVersion}&maxChangeVersion={countKeyChangeMaxVersion}&totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  { "total-count": 2 }
                  """
              And total of records should be 2
