Feature: Live resource endpoints filter by change version.

        Background:
            Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade               |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
                  | uri://ed-fi.org/ProgramTypeDescriptor#Bilingual                |
              And the system has these "schools"
                  | schoolId  | nameOfInstitution    | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 920100001 | Live Filter School   | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |

        @ods-migrated
        @e2e-ci-shard-3
        @reset-data-before-scenario
        Scenario: 01 Live programs collection filters by minChangeVersion
             When a POST request is made to "/ed-fi/programs" with
                  """
                  {
                    "programName": "Live Filter Program A",
                    "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Bilingual",
                    "educationOrganizationReference": { "educationOrganizationId": 920100001 }
                  }
                  """
             Then it should respond with 201
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "liveMidVersion"
             When a POST request is made to "/ed-fi/programs" with
                  """
                  {
                    "programName": "Live Filter Program B",
                    "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Bilingual",
                    "educationOrganizationReference": { "educationOrganizationId": 920100001 }
                  }
                  """
             Then it should respond with 201
             When a GET request is made to "/ed-fi/programs?minChangeVersion={liveMidVersion}&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.programName" should have value "Live Filter Program B"

        @ods-migrated
        @e2e-ci-shard-3
        @reset-data-before-scenario
        Scenario: 02 Live programs collection filters by maxChangeVersion
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "liveMaxBaseline"
             When a POST request is made to "/ed-fi/programs" with
                  """
                  {
                    "programName": "Live Max Program A",
                    "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Bilingual",
                    "educationOrganizationReference": { "educationOrganizationId": 920100001 }
                  }
                  """
             Then it should respond with 201
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "liveMaxAfterA"
             When a POST request is made to "/ed-fi/programs" with
                  """
                  {
                    "programName": "Live Max Program B",
                    "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Bilingual",
                    "educationOrganizationReference": { "educationOrganizationId": 920100001 }
                  }
                  """
             Then it should respond with 201
             When a GET request is made to "/ed-fi/programs?minChangeVersion={liveMaxBaseline}&maxChangeVersion={liveMaxAfterA}&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.programName" should have value "Live Max Program A"

        @ods-migrated
        @e2e-ci-shard-3
        @reset-data-before-scenario
        Scenario: 03 Live programs collection filters by a change version window
             # The window relies on the ContentVersion-advances-per-write invariant: each write gets a
             # strictly greater newestChangeVersion, so afterA < B's version <= afterB and the
             # (afterA, afterB] window contains only Program B.
             When a POST request is made to "/ed-fi/programs" with
                  """
                  {
                    "programName": "Live Window Program A",
                    "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Bilingual",
                    "educationOrganizationReference": { "educationOrganizationId": 920100001 }
                  }
                  """
             Then it should respond with 201
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "liveWindowAfterA"
             When a POST request is made to "/ed-fi/programs" with
                  """
                  {
                    "programName": "Live Window Program B",
                    "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Bilingual",
                    "educationOrganizationReference": { "educationOrganizationId": 920100001 }
                  }
                  """
             Then it should respond with 201
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "liveWindowAfterB"
             When a GET request is made to "/ed-fi/programs?minChangeVersion={liveWindowAfterA}&maxChangeVersion={liveWindowAfterB}&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.programName" should have value "Live Window Program B"

        @e2e-ci-shard-3
        @reset-data-before-scenario
        Scenario: 04 Live descriptor endpoint filters an updated descriptor by maxChangeVersion
            # A descriptor's change version only ever increases on write, so an update can only push it
            # ABOVE an earlier boundary, never below. The upper bound (maxChangeVersion) is therefore the
            # filter that observes an update EVICTING a row: the pre-update snapshot version stops matching
            # once the descriptor is updated, and the post-update snapshot version matches it again. A lower
            # bound cannot evict a row this way (once a row is at or above a floor it stays there as its
            # version grows); scenario 05 is the complementary minChangeVersion mirror, where an update
            # instead ADMITS the row past a floor. Scoping by namespace + codeValue keeps the count exact
            # regardless of other descriptors present.
             When a POST request is made to "/ed-fi/academicSubjectDescriptors" with
                  """
                  {
                    "codeValue": "LiveFilterSubject",
                    "namespace": "uri://ed-fi.org/AcademicSubjectDescriptor",
                    "shortDescription": "Before Update"
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "liveDescriptorId" variable
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "descriptorMaxBeforeUpdate"
             When a GET request is made to "/ed-fi/academicSubjectDescriptors?namespace=uri://ed-fi.org/AcademicSubjectDescriptor&codeValue=LiveFilterSubject&maxChangeVersion={descriptorMaxBeforeUpdate}&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.id" should equal request variable "liveDescriptorId"
              And the response body path "0.shortDescription" should have value "Before Update"
             When a PUT request is made to "/ed-fi/academicSubjectDescriptors/{liveDescriptorId}" with
                  """
                  {
                    "id": "{liveDescriptorId}",
                    "codeValue": "LiveFilterSubject",
                    "namespace": "uri://ed-fi.org/AcademicSubjectDescriptor",
                    "shortDescription": "After Update"
                  }
                  """
             Then it should respond with 204
             When a GET request is made to "/ed-fi/academicSubjectDescriptors?namespace=uri://ed-fi.org/AcademicSubjectDescriptor&codeValue=LiveFilterSubject&maxChangeVersion={descriptorMaxBeforeUpdate}&totalCount=true"
             Then it should respond with 200
              And total of records should be 0
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "descriptorMaxAfterUpdate"
             When a GET request is made to "/ed-fi/academicSubjectDescriptors?namespace=uri://ed-fi.org/AcademicSubjectDescriptor&codeValue=LiveFilterSubject&maxChangeVersion={descriptorMaxAfterUpdate}&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.id" should equal request variable "liveDescriptorId"
              And the response body path "0.shortDescription" should have value "After Update"

        @e2e-ci-shard-3
        @reset-data-before-scenario
        Scenario: 05 Live descriptor endpoint filters an updated descriptor by minChangeVersion
            # Mirror of scenario 04 on the LOWER bound. Both bounds are inclusive (min uses >=). A later,
            # unrelated write advances the floor past the freshly created descriptor, excluding it until its
            # own update moves it back into the minChangeVersion result set. Scoping by namespace + codeValue
            # keeps the count exact.
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "descriptorMinBaseline"
             When a POST request is made to "/ed-fi/academicSubjectDescriptors" with
                  """
                  {
                    "codeValue": "MinFilterSubject",
                    "namespace": "uri://ed-fi.org/AcademicSubjectDescriptor",
                    "shortDescription": "Before Update"
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "minDescriptorId" variable
             # Floor below the descriptor's version includes it.
             When a GET request is made to "/ed-fi/academicSubjectDescriptors?namespace=uri://ed-fi.org/AcademicSubjectDescriptor&codeValue=MinFilterSubject&minChangeVersion={descriptorMinBaseline}&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.id" should equal request variable "minDescriptorId"
              And the response body path "0.shortDescription" should have value "Before Update"
             When a POST request is made to "/ed-fi/academicSubjectDescriptors" with
                  """
                  {
                    "codeValue": "MinFilterSubjectFloor",
                    "namespace": "uri://ed-fi.org/AcademicSubjectDescriptor",
                    "shortDescription": "Floor Advancer"
                  }
                  """
             Then it should respond with 201
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "descriptorMinFloor"
             # Floor advanced by a later write excludes the unchanged descriptor.
             When a GET request is made to "/ed-fi/academicSubjectDescriptors?namespace=uri://ed-fi.org/AcademicSubjectDescriptor&codeValue=MinFilterSubject&minChangeVersion={descriptorMinFloor}&totalCount=true"
             Then it should respond with 200
              And total of records should be 0
             When a PUT request is made to "/ed-fi/academicSubjectDescriptors/{minDescriptorId}" with
                  """
                  {
                    "id": "{minDescriptorId}",
                    "codeValue": "MinFilterSubject",
                    "namespace": "uri://ed-fi.org/AcademicSubjectDescriptor",
                    "shortDescription": "After Update"
                  }
                  """
             Then it should respond with 204
             # Update advanced the descriptor's version past the floor, so it is included again.
             When a GET request is made to "/ed-fi/academicSubjectDescriptors?namespace=uri://ed-fi.org/AcademicSubjectDescriptor&codeValue=MinFilterSubject&minChangeVersion={descriptorMinFloor}&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.id" should equal request variable "minDescriptorId"
              And the response body path "0.shortDescription" should have value "After Update"

        @e2e-ci-shard-3
        @reset-data-before-scenario
        Scenario: 06 Live resource endpoint filters an updated resource by maxChangeVersion
            # Regular-resource counterpart of scenario 04. ClassPeriod is childless here (its only collection,
            # meetingTimes, is omitted) and is authorized through its school reference. A resource's change
            # version only ever increases on write, so a direct update pushes it ABOVE a fixed ceiling; the
            # upper bound (maxChangeVersion) observes the eviction, and raising the ceiling re-admits it.
            # officialAttendancePeriod is a non-identity field, so flipping it is an ordinary content change
            # that the stamping trigger detects and versions. Scoping the query by schoolId keeps the count exact.
             When a POST request is made to "/ed-fi/classPeriods" with
                  """
                  {
                    "classPeriodName": "MaxFilterPeriod",
                    "schoolReference": { "schoolId": 920100001 },
                    "officialAttendancePeriod": false
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "liveClassPeriodId" variable
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "classPeriodMaxBeforeUpdate"
             When a GET request is made to "/ed-fi/classPeriods?schoolId=920100001&maxChangeVersion={classPeriodMaxBeforeUpdate}&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.id" should equal request variable "liveClassPeriodId"
             When a PUT request is made to "/ed-fi/classPeriods/{liveClassPeriodId}" with
                  """
                  {
                    "id": "{liveClassPeriodId}",
                    "classPeriodName": "MaxFilterPeriod",
                    "schoolReference": { "schoolId": 920100001 },
                    "officialAttendancePeriod": true
                  }
                  """
             Then it should respond with 204
             When a GET request is made to "/ed-fi/classPeriods?schoolId=920100001&maxChangeVersion={classPeriodMaxBeforeUpdate}&totalCount=true"
             Then it should respond with 200
              And total of records should be 0
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "classPeriodMaxAfterUpdate"
             When a GET request is made to "/ed-fi/classPeriods?schoolId=920100001&maxChangeVersion={classPeriodMaxAfterUpdate}&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.id" should equal request variable "liveClassPeriodId"
              And the response body path "0.officialAttendancePeriod" should have value "true"

        @e2e-ci-shard-3
        @reset-data-before-scenario
        Scenario: 07 Live resource endpoint filters an updated resource by minChangeVersion
            # Regular-resource counterpart of scenario 05. A later, unrelated write advances the floor past the
            # freshly created ClassPeriod, excluding it until its own update moves it back into the
            # minChangeVersion result set. Authorized through its school reference; scoping by schoolId +
            # classPeriodName keeps the count exact.
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "classPeriodMinBaseline"
             When a POST request is made to "/ed-fi/classPeriods" with
                  """
                  {
                    "classPeriodName": "MinFilterPeriod",
                    "schoolReference": { "schoolId": 920100001 },
                    "officialAttendancePeriod": false
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "minClassPeriodId" variable
             # Floor below the ClassPeriod's version includes it.
             When a GET request is made to "/ed-fi/classPeriods?schoolId=920100001&classPeriodName=MinFilterPeriod&minChangeVersion={classPeriodMinBaseline}&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.id" should equal request variable "minClassPeriodId"
             When a POST request is made to "/ed-fi/classPeriods" with
                  """
                  {
                    "classPeriodName": "MinFilterPeriodFloor",
                    "schoolReference": { "schoolId": 920100001 },
                    "officialAttendancePeriod": false
                  }
                  """
             Then it should respond with 201
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "classPeriodMinFloor"
             # Floor advanced by a later write excludes the unchanged ClassPeriod.
             When a GET request is made to "/ed-fi/classPeriods?schoolId=920100001&classPeriodName=MinFilterPeriod&minChangeVersion={classPeriodMinFloor}&totalCount=true"
             Then it should respond with 200
              And total of records should be 0
             When a PUT request is made to "/ed-fi/classPeriods/{minClassPeriodId}" with
                  """
                  {
                    "id": "{minClassPeriodId}",
                    "classPeriodName": "MinFilterPeriod",
                    "schoolReference": { "schoolId": 920100001 },
                    "officialAttendancePeriod": true
                  }
                  """
             Then it should respond with 204
             # Update advanced the ClassPeriod's version past the floor, so it is included again.
             When a GET request is made to "/ed-fi/classPeriods?schoolId=920100001&classPeriodName=MinFilterPeriod&minChangeVersion={classPeriodMinFloor}&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.id" should equal request variable "minClassPeriodId"
              And the response body path "0.officialAttendancePeriod" should have value "true"
