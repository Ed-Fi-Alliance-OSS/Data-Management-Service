Feature: TrackedChange /deletes endpoints across resource and key shapes.

        Background:
            Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                                |
                  | uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade                               |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School                 |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency |
                  | uri://ed-fi.org/AcademicSubjectDescriptor#English Language Arts                |
                  | uri://ed-fi.org/CourseIdentificationSystemDescriptor#CSSC course code           |
                  | uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent              |
                  | uri://ed-fi.org/ProgramTypeDescriptor#Bilingual                                |

        @ods-migrated
        @relational-backend
        @relational-ci-shard-4
        Scenario: 01 Deleted AcademicWeek appears in deletes response with composite key
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": 920200001,
                    "nameOfInstitution": "AcademicWeek School",
                    "gradeLevels": [ { "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" } ],
                    "educationOrganizationCategories": [ { "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School" } ]
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/academicWeeks" with
                  """
                  {
                    "weekIdentifier": "AcadWeek01",
                    "schoolReference": { "schoolId": 920200001 },
                    "beginDate": "2023-08-01",
                    "endDate": "2023-08-07",
                    "totalInstructionalDays": 5
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "deletedAcademicWeekId" variable
             When a DELETE request is made to "/ed-fi/academicWeeks/{deletedAcademicWeekId}"
             Then it should respond with 204
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "academicWeekDeleteVersion"
             When a GET request is made to "/ed-fi/academicWeeks/deletes?minChangeVersion={academicWeekDeleteVersion}&maxChangeVersion={academicWeekDeleteVersion}&totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  { "total-count": 1 }
                  """
              And total of records should be 1
              And the response body path "0.id" should equal request variable "deletedAcademicWeekId"
              And the response body path "0.keyValues.weekIdentifier" should have value "AcadWeek01"
              And the response body path "0.keyValues.schoolId" should have value "920200001"

        @ods-migrated
        @relational-backend
        @relational-ci-shard-4
        Scenario: 02 Deleted AcademicSubjectDescriptor appears in deletes response
             When a POST request is made to "/ed-fi/academicSubjectDescriptors" with
                  """
                  {
                    "codeValue": "Tracked Delete Subject",
                    "namespace": "uri://ed-fi.org/AcademicSubjectDescriptor",
                    "shortDescription": "Tracked Delete Subject"
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "deletedSubjectDescriptorId" variable
             When a DELETE request is made to "/ed-fi/academicSubjectDescriptors/{deletedSubjectDescriptorId}"
             Then it should respond with 204
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "subjectDescriptorDeleteVersion"
             When a GET request is made to "/ed-fi/academicSubjectDescriptors/deletes?minChangeVersion={subjectDescriptorDeleteVersion}&maxChangeVersion={subjectDescriptorDeleteVersion}&totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  { "total-count": 1 }
                  """
              And total of records should be 1
              And the response body path "0.id" should equal request variable "deletedSubjectDescriptorId"
              And the response body path "0.keyValues.namespace" should have value "uri://ed-fi.org/AcademicSubjectDescriptor"
              And the response body path "0.keyValues.codeValue" should have value "Tracked Delete Subject"

        @ods-migrated
        @relational-backend
        @relational-ci-shard-4
        @reset-data-before-scenario
        Scenario: 03 Deleted AssessmentPeriodDescriptor appears in deletes response
            Given the claimSet "EdFiSandbox" is authorized with namespacePrefixes "uri://ed-fi.org"
             When a POST request is made to "/ed-fi/assessmentPeriodDescriptors" with
                  """
                  {
                    "codeValue": "To Be Deleted",
                    "description": "This is to be deleted.",
                    "namespace": "uri://ed-fi.org/AssessmentPeriodDescriptor",
                    "shortDescription": "This is to be deleted."
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "deletedPeriodDescriptorId" variable
             When a DELETE request is made to "/ed-fi/assessmentPeriodDescriptors/{deletedPeriodDescriptorId}"
             Then it should respond with 204
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "periodDescriptorDeleteVersion"
             When a GET request is made to "/ed-fi/assessmentPeriodDescriptors/deletes?minChangeVersion={periodDescriptorDeleteVersion}&maxChangeVersion={periodDescriptorDeleteVersion}&totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  { "total-count": 1 }
                  """
              And total of records should be 1
              And the response body path "0.id" should equal request variable "deletedPeriodDescriptorId"
              And the response body path "0.keyValues.namespace" should have value "uri://ed-fi.org/AssessmentPeriodDescriptor"
              And the response body path "0.keyValues.codeValue" should have value "To Be Deleted"

        @ods-migrated
        @relational-backend
        @relational-ci-shard-4
        Scenario: 04 Course full lifecycle then delete appears in deletes response
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": 920200004,
                    "nameOfInstitution": "Course Lifecycle School",
                    "gradeLevels": [ { "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" } ],
                    "educationOrganizationCategories": [ { "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School" } ]
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/courses" with
                  """
                  {
                    "courseCode": "TRACK-CRS-1",
                    "educationOrganizationReference": { "educationOrganizationId": 920200004 },
                    "courseTitle": "Tracked Course",
                    "numberOfParts": 1,
                    "identificationCodes": [
                      {
                        "courseIdentificationSystemDescriptor": "uri://ed-fi.org/CourseIdentificationSystemDescriptor#CSSC course code",
                        "identificationCode": "TRACK-CRS-1"
                      }
                    ]
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "deletedCourseId" variable
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "courseDeleteMinVersion"
             When a DELETE request is made to "/ed-fi/courses/{deletedCourseId}"
             Then it should respond with 204
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "courseDeleteMaxVersion"
             When a GET request is made to "/ed-fi/courses/deletes?minChangeVersion={courseDeleteMinVersion}&maxChangeVersion={courseDeleteMaxVersion}&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.id" should equal request variable "deletedCourseId"
              And the response body path "0.keyValues.courseCode" should have value "TRACK-CRS-1"
              And the response body path "0.keyValues.educationOrganizationId" should have value "920200004"

        @ods-migrated
        @relational-backend
        @relational-ci-shard-4
        Scenario: 05 LocalEducationAgency delete appears in deletes response
             When a POST request is made to "/ed-fi/localEducationAgencies" with
                  """
                  {
                    "localEducationAgencyId": 920200500,
                    "nameOfInstitution": "Tracked Delete LEA",
                    "categories": [ { "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School" } ],
                    "localEducationAgencyCategoryDescriptor": "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent"
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "deletedLeaId" variable
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "leaDeleteMinVersion"
             When a DELETE request is made to "/ed-fi/localEducationAgencies/{deletedLeaId}"
             Then it should respond with 204
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "leaDeleteMaxVersion"
             When a GET request is made to "/ed-fi/localEducationAgencies/deletes?minChangeVersion={leaDeleteMinVersion}&maxChangeVersion={leaDeleteMaxVersion}&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.id" should equal request variable "deletedLeaId"
              And the response body path "0.keyValues.localEducationAgencyId" should have value "920200500"

        @ods-migrated
        @relational-backend
        @relational-ci-shard-4
        @reset-data-before-scenario
        Scenario: 06 Deleted StaffEducationOrganizationEmploymentAssociation appears with descriptor and staff natural key
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901,255901001"
              And the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/EmploymentStatusDescriptor#Tenured or permanent |
             When a POST request is made to "/ed-fi/localEducationAgencies" with
                  """
                  {
                    "localEducationAgencyId": 255901,
                    "nameOfInstitution": "Tracked Employment LEA",
                    "categories": [ { "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency" } ],
                    "localEducationAgencyCategoryDescriptor": "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Independent"
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": 255901001,
                    "nameOfInstitution": "Employment Association School",
                    "gradeLevels": [ { "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" } ],
                    "educationOrganizationCategories": [ { "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School" } ],
                    "localEducationAgencyReference": { "localEducationAgencyId": 255901 }
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/staffs" with
                  """
                  {
                    "staffUniqueId": "TRACK-STAFF-EMP",
                    "firstName": "Tracked",
                    "lastSurname": "Employment"
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/staffEducationOrganizationEmploymentAssociations" with
                  """
                  {
                    "employmentStatusDescriptor": "uri://ed-fi.org/EmploymentStatusDescriptor#Tenured or permanent",
                    "hireDate": "2021-01-01",
                    "educationOrganizationReference": { "educationOrganizationId": 255901001 },
                    "staffReference": { "staffUniqueId": "TRACK-STAFF-EMP" }
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "deletedEmploymentId" variable
             When a DELETE request is made to "/ed-fi/staffEducationOrganizationEmploymentAssociations/{deletedEmploymentId}"
             Then it should respond with 204
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "employmentDeleteVersion"
             When a GET request is made to "/ed-fi/staffEducationOrganizationEmploymentAssociations/deletes?minChangeVersion={employmentDeleteVersion}&maxChangeVersion={employmentDeleteVersion}&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.id" should equal request variable "deletedEmploymentId"
              And the response body path "0.keyValues.staffUniqueId" should have value "TRACK-STAFF-EMP"
              And the response body path "0.keyValues.educationOrganizationId" should have value "255901001"
              And the response body path "0.keyValues.employmentStatusDescriptor" should have value "uri://ed-fi.org/EmploymentStatusDescriptor#Tenured or permanent"
              And the response body path "0.keyValues.hireDate" should have value "2021-01-01"

        @ods-migrated
        @relational-backend
        @relational-ci-shard-4
        Scenario: 07 Deleted Staff person resource appears in deletes response
             When a POST request is made to "/ed-fi/staffs" with
                  """
                  {
                    "staffUniqueId": "TRACK-STAFF-2",
                    "firstName": "Tracked",
                    "lastSurname": "Person"
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "deletedStaffId" variable
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "staffDeleteMinVersion"
             When a DELETE request is made to "/ed-fi/staffs/{deletedStaffId}"
             Then it should respond with 204
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "staffDeleteMaxVersion"
             When a GET request is made to "/ed-fi/staffs/deletes?minChangeVersion={staffDeleteMinVersion}&maxChangeVersion={staffDeleteMaxVersion}&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.id" should equal request variable "deletedStaffId"
              And the response body path "0.keyValues.staffUniqueId" should have value "TRACK-STAFF-2"

        @ods-migrated
        @relational-backend
        @relational-ci-shard-4
        Scenario: 08 Program deleted, recreated, and deleted again yields two tracked-delete rows
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": 920200008,
                    "nameOfInstitution": "Double Delete Program School",
                    "gradeLevels": [ { "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" } ],
                    "educationOrganizationCategories": [ { "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School" } ]
                  }
                  """
             Then it should respond with 201
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "doubleDeleteMinVersion"
             When a POST request is made to "/ed-fi/programs" with
                  """
                  {
                    "programName": "Double Delete Program",
                    "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Bilingual",
                    "educationOrganizationReference": { "educationOrganizationId": 920200008 }
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "firstProgramId" variable
             When a DELETE request is made to "/ed-fi/programs/{firstProgramId}"
             Then it should respond with 204
             When a POST request is made to "/ed-fi/programs" with
                  """
                  {
                    "programName": "Double Delete Program",
                    "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Bilingual",
                    "educationOrganizationReference": { "educationOrganizationId": 920200008 }
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "secondProgramId" variable
             When a DELETE request is made to "/ed-fi/programs/{secondProgramId}"
             Then it should respond with 204
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "doubleDeleteMaxVersion"
             When a GET request is made to "/ed-fi/programs/deletes?minChangeVersion={doubleDeleteMinVersion}&maxChangeVersion={doubleDeleteMaxVersion}&totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  { "total-count": 2 }
                  """
              And total of records should be 2
              # Both tombstones carry the same natural key but distinct resource ids, ordered ascending
              # by change version (first delete first) — ODS "Should return the same key values for both items".
              And the response body path "0.id" should equal request variable "firstProgramId"
              And the response body path "0.keyValues.programName" should have value "Double Delete Program"
              And the response body path "0.keyValues.programTypeDescriptor" should have value "uri://ed-fi.org/ProgramTypeDescriptor#Bilingual"
              And the response body path "0.keyValues.educationOrganizationId" should have value "920200008"
              And the response body path "1.id" should equal request variable "secondProgramId"
              And the response body path "1.keyValues.programName" should have value "Double Delete Program"
              And the response body path "1.keyValues.programTypeDescriptor" should have value "uri://ed-fi.org/ProgramTypeDescriptor#Bilingual"
              And the response body path "1.keyValues.educationOrganizationId" should have value "920200008"

        @ods-migrated
        @relational-backend
        @relational-ci-shard-4
        Scenario: 09 Deletes response filters by maxChangeVersion only
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "maxOnlyBaseline"
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": 920200009,
                    "nameOfInstitution": "Max Only School",
                    "gradeLevels": [ { "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" } ],
                    "educationOrganizationCategories": [ { "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School" } ]
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "maxOnlySchoolId" variable
             When a DELETE request is made to "/ed-fi/schools/{maxOnlySchoolId}"
             Then it should respond with 204
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "maxOnlyVersion"
             When a GET request is made to "/ed-fi/schools/deletes?maxChangeVersion={maxOnlyVersion}&limit=500&totalCount=true"
             Then it should respond with 200
              And the response body path "0.keyValues.schoolId" should have value "920200009"

        @ods-migrated
        @relational-backend
        @relational-ci-shard-4
        # NOTE: Expected body copied verbatim from DMS's actual ProblemDetails response. DMS reports
        # invalid limit/offset as a generic bad-request (not parameter-validation-failed), lists Offset
        # before Limit, and phrases the bounds as "numeric value between 0 and 500".
        Scenario: 10 Deletes response rejects invalid limit and offset
             When a GET request is made to "/ed-fi/schools/deletes?limit=-1&offset=-1"
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
        Scenario: 11 Deletes request for an unknown resource returns 404
             When a GET request is made to "/ed-fi/nonExistingResources/deletes"
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

        @ods-migrated
        @relational-backend
        @relational-ci-shard-4
        Scenario: 12 Registered then deleted Student appears in deletes response
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": 920200012,
                    "nameOfInstitution": "Registered Student School",
                    "gradeLevels": [ { "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" } ],
                    "educationOrganizationCategories": [ { "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School" } ]
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                    "studentUniqueId": "TRACK-STU-REG",
                    "firstName": "Registered",
                    "lastSurname": "Student",
                    "birthDate": "2008-01-01"
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "registeredStudentId" variable
             When a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                    "entryDate": "2023-08-01",
                    "schoolReference": { "schoolId": 920200012 },
                    "studentReference": { "studentUniqueId": "TRACK-STU-REG" },
                    "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "registeredSsaId" variable
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "registeredStudentMinVersion"
             When a DELETE request is made to "/ed-fi/studentSchoolAssociations/{registeredSsaId}"
             Then it should respond with 204
             When a DELETE request is made to "/ed-fi/students/{registeredStudentId}"
             Then it should respond with 204
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "registeredStudentMaxVersion"
             When a GET request is made to "/ed-fi/students/deletes?minChangeVersion={registeredStudentMinVersion}&maxChangeVersion={registeredStudentMaxVersion}&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.id" should equal request variable "registeredStudentId"
              And the response body path "0.keyValues.studentUniqueId" should have value "TRACK-STU-REG"

        @ods-migrated
        @relational-backend
        @relational-ci-shard-4
        Scenario: 13 Unregistered deleted Student is not returned in deletes response
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901"
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                    "studentUniqueId": "TRACK-STU-UNREG",
                    "firstName": "Unregistered",
                    "lastSurname": "Student",
                    "birthDate": "2008-01-01"
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "unregisteredStudentId" variable
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "unregisteredStudentMinVersion"
             When a DELETE request is made to "/ed-fi/students/{unregisteredStudentId}"
             Then it should respond with 204
             When a GET request is made to "/ed-fi/students/deletes?minChangeVersion={unregisteredStudentMinVersion}"
             Then it should respond with 200
              And total of records should be 0
