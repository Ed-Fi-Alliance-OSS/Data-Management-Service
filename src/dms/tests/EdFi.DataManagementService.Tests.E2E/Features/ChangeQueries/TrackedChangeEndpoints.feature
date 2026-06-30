Feature: TrackedChangeEndpoints report resource deletes and key changes.

        Background:
            Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade              |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |

        @e2e-ci-shard-3
        Scenario: 01 Deleted School appears in deletes response
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": 8118601,
                    "nameOfInstitution": "Tracked Deletes School",
                    "gradeLevels": [
                      {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                      }
                    ],
                    "educationOrganizationCategories": [
                      {
                        "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                      }
                    ]
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "deletedSchoolId" variable
             When a DELETE request is made to "/ed-fi/schools/{deletedSchoolId}"
             Then it should respond with 204
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "deleteChangeVersion"
             When a GET request is made to "/ed-fi/schools/deletes?minChangeVersion={deleteChangeVersion}&maxChangeVersion={deleteChangeVersion}&totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  {
                    "total-count": 1
                  }
                  """
              And total of records should be 1
              And the response body path "0.id" should equal request variable "deletedSchoolId"
              And the response body path "0.keyValues.schoolId" should have value "8118601"

        @e2e-ci-shard-3
        Scenario: 02 Recreated School is suppressed from deletes response
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": 8118602,
                    "nameOfInstitution": "Tracked Recreated School",
                    "gradeLevels": [
                      {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                      }
                    ],
                    "educationOrganizationCategories": [
                      {
                        "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                      }
                    ]
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "recreatedSchoolId" variable
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "recreateMinChangeVersion"
             When a DELETE request is made to "/ed-fi/schools/{recreatedSchoolId}"
             Then it should respond with 204
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": 8118602,
                    "nameOfInstitution": "Tracked Recreated School",
                    "gradeLevels": [
                      {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                      }
                    ],
                    "educationOrganizationCategories": [
                      {
                        "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                      }
                    ]
                  }
                  """
             Then it should respond with 201
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "recreateMaxChangeVersion"
             When a GET request is made to "/ed-fi/schools/deletes?minChangeVersion={recreateMinChangeVersion}&maxChangeVersion={recreateMaxChangeVersion}&totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  {
                    "total-count": 0
                  }
                  """
              And the response body is
                  """
                  []
                  """

        @e2e-ci-shard-3
        Scenario: 03 ClassPeriod key changes collapse to one response item
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": 8118603,
                    "nameOfInstitution": "Tracked Key Changes School",
                    "gradeLevels": [
                      {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                      }
                    ],
                    "educationOrganizationCategories": [
                      {
                        "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                      }
                    ]
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/classPeriods" with
                  """
                  {
                    "classPeriodName": "first period",
                    "schoolReference": {
                      "schoolId": 8118603
                    }
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "classPeriodId" variable
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "keyChangeMinChangeVersion"
             When a PUT request is made to "/ed-fi/classPeriods/{classPeriodId}" with
                  """
                  {
                    "id": "{classPeriodId}",
                    "classPeriodName": "second period",
                    "schoolReference": {
                      "schoolId": 8118603
                    }
                  }
                  """
             Then it should respond with 204
             When a PUT request is made to "/ed-fi/classPeriods/{classPeriodId}" with
                  """
                  {
                    "id": "{classPeriodId}",
                    "classPeriodName": "third period",
                    "schoolReference": {
                      "schoolId": 8118603
                    }
                  }
                  """
             Then it should respond with 204
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "keyChangeMaxChangeVersion"
             When a GET request is made to "/ed-fi/classPeriods/keyChanges?minChangeVersion={keyChangeMinChangeVersion}&maxChangeVersion={keyChangeMaxChangeVersion}&totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  {
                    "total-count": 1
                  }
                  """
              And total of records should be 1
              And the response body path "0.id" should equal request variable "classPeriodId"
              And the response body path "0.oldKeyValues.classPeriodName" should have value "first period"
              And the response body path "0.oldKeyValues.schoolId" should have value "8118603"
              And the response body path "0.newKeyValues.classPeriodName" should have value "third period"
              And the response body path "0.newKeyValues.schoolId" should have value "8118603"

        @e2e-ci-shard-3
        Scenario: 04 Descriptor keyChanges returns empty array
             When a GET request is made to "/ed-fi/gradeLevelDescriptors/keyChanges?totalCount=true&limit=1&offset=0"
             Then it should respond with 200
              And the response headers include
                  """
                  {
                    "total-count": 0
                  }
                  """

        @e2e-ci-shard-3
        Scenario: 05 Invalid change version parameter returns validation ProblemDetails
             When a GET request is made to "/ed-fi/schools/deletes?minChangeVersion=abc"
             Then it should respond with 400
              And the response body is
                  """
                  {
                    "detail": "Parameters supplied to the request were invalid.",
                    "type": "urn:ed-fi:api:bad-request:parameter-validation-failed",
                    "title": "Parameter Validation Failed",
                    "status": 400,
                    "correlationId": null,
                    "validationErrors": {},
                    "errors": [
                      "MinChangeVersion must be a numeric value greater than or equal to 0."
                    ]
                  }
                  """

        @e2e-ci-shard-3
        Scenario: 06 Deleted Descriptor appears in deletes response
             When a POST request is made to "/ed-fi/gradeLevelDescriptors" with
                  """
                  {
                    "codeValue": "Tracked Delete Descriptor",
                    "namespace": "uri://ed-fi.org/GradeLevelDescriptor",
                    "shortDescription": "Tracked Delete Descriptor"
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "deletedDescriptorId" variable
             When a DELETE request is made to "/ed-fi/gradeLevelDescriptors/{deletedDescriptorId}"
             Then it should respond with 204
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "descriptorDeleteChangeVersion"
             When a GET request is made to "/ed-fi/gradeLevelDescriptors/deletes?minChangeVersion={descriptorDeleteChangeVersion}&maxChangeVersion={descriptorDeleteChangeVersion}&totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  {
                    "total-count": 1
                  }
                  """
              And total of records should be 1
              And the response body path "0.id" should equal request variable "deletedDescriptorId"
              And the response body path "0.keyValues.namespace" should have value "uri://ed-fi.org/GradeLevelDescriptor"
              And the response body path "0.keyValues.codeValue" should have value "Tracked Delete Descriptor"

        @e2e-ci-shard-3
        Scenario: 07 Recreated Descriptor is suppressed from deletes response
             When a POST request is made to "/ed-fi/gradeLevelDescriptors" with
                  """
                  {
                    "codeValue": "Tracked Recreated Descriptor",
                    "namespace": "uri://ed-fi.org/GradeLevelDescriptor",
                    "shortDescription": "Tracked Recreated Descriptor"
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "recreatedDescriptorId" variable
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "descriptorRecreateMinChangeVersion"
             When a DELETE request is made to "/ed-fi/gradeLevelDescriptors/{recreatedDescriptorId}"
             Then it should respond with 204
             When a POST request is made to "/ed-fi/gradeLevelDescriptors" with
                  """
                  {
                    "codeValue": "Tracked Recreated Descriptor",
                    "namespace": "uri://ed-fi.org/GradeLevelDescriptor",
                    "shortDescription": "Tracked Recreated Descriptor"
                  }
                  """
             Then it should respond with 201
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "descriptorRecreateMaxChangeVersion"
             When a GET request is made to "/ed-fi/gradeLevelDescriptors/deletes?minChangeVersion={descriptorRecreateMinChangeVersion}&maxChangeVersion={descriptorRecreateMaxChangeVersion}&totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  {
                    "total-count": 0
                  }
                  """
              And the response body is
                  """
                  []
                  """

        @e2e-ci-shard-3
        Scenario: 08 Recreated Program with recreated descriptor identity is suppressed from deletes response
             When a POST request is made to "/ed-fi/programTypeDescriptors" with
                  """
                  {
                    "codeValue": "Tracked Program Type",
                    "namespace": "uri://ed-fi.org/ProgramTypeDescriptor",
                    "shortDescription": "Tracked Program Type"
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "programTypeDescriptorId" variable
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": 8118604,
                    "nameOfInstitution": "Tracked Program School",
                    "gradeLevels": [
                      {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                      }
                    ],
                    "educationOrganizationCategories": [
                      {
                        "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                      }
                    ]
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/programs" with
                  """
                  {
                    "programName": "Tracked Recreated Descriptor Program",
                    "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Tracked Program Type",
                    "educationOrganizationReference": {
                      "educationOrganizationId": 8118604
                    }
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "programId" variable
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "programRecreateMinChangeVersion"
             When a DELETE request is made to "/ed-fi/programs/{programId}"
             Then it should respond with 204
             When a DELETE request is made to "/ed-fi/programTypeDescriptors/{programTypeDescriptorId}"
             Then it should respond with 204
             When a POST request is made to "/ed-fi/programTypeDescriptors" with
                  """
                  {
                    "codeValue": "Tracked Program Type",
                    "namespace": "uri://ed-fi.org/ProgramTypeDescriptor",
                    "shortDescription": "Tracked Program Type"
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/programs" with
                  """
                  {
                    "programName": "Tracked Recreated Descriptor Program",
                    "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Tracked Program Type",
                    "educationOrganizationReference": {
                      "educationOrganizationId": 8118604
                    }
                  }
                  """
             Then it should respond with 201
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "programRecreateMaxChangeVersion"
             When a GET request is made to "/ed-fi/programs/deletes?minChangeVersion={programRecreateMinChangeVersion}&maxChangeVersion={programRecreateMaxChangeVersion}&totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  {
                    "total-count": 0
                  }
                  """
              And the response body is
                  """
                  []
                  """

        @e2e-ci-shard-3
        Scenario: 09 Concrete abstract keyChanges returns empty array
             When a GET request is made to "/ed-fi/schools/keyChanges?totalCount=true&limit=1&offset=0"
             Then it should respond with 200
              And the response headers include
                  """
                  {
                    "total-count": 0
                  }
                  """
              And the response body is
                  """
                  []
                  """

        @e2e-ci-shard-3
        Scenario: 10 Deletes response supports limit and offset
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": 8118605,
                    "nameOfInstitution": "Tracked Paging School A",
                    "gradeLevels": [
                      {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                      }
                    ],
                    "educationOrganizationCategories": [
                      {
                        "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                      }
                    ]
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "pagingSchoolAId" variable
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": 8118606,
                    "nameOfInstitution": "Tracked Paging School B",
                    "gradeLevels": [
                      {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                      }
                    ],
                    "educationOrganizationCategories": [
                      {
                        "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                      }
                    ]
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "pagingSchoolBId" variable
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "pagingDeleteMinChangeVersion"
             When a DELETE request is made to "/ed-fi/schools/{pagingSchoolAId}"
             Then it should respond with 204
             When a DELETE request is made to "/ed-fi/schools/{pagingSchoolBId}"
             Then it should respond with 204
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "pagingDeleteMaxChangeVersion"
             When a GET request is made to "/ed-fi/schools/deletes?minChangeVersion={pagingDeleteMinChangeVersion}&maxChangeVersion={pagingDeleteMaxChangeVersion}&limit=1&offset=1&totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                  {
                    "total-count": 2
                  }
                  """
              And total of records should be 1
              And the response body path "0.id" should equal request variable "pagingSchoolBId"
              And the response body path "0.keyValues.schoolId" should have value "8118606"

        @e2e-ci-shard-3
        Scenario: 13 Deletes response is served when no query parameters are supplied
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": 8118607,
                    "nameOfInstitution": "Tracked Optional Params School",
                    "gradeLevels": [
                      {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                      }
                    ],
                    "educationOrganizationCategories": [
                      {
                        "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                      }
                    ]
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "optionalParamsSchoolId" variable
             When a DELETE request is made to "/ed-fi/schools/{optionalParamsSchoolId}"
             Then it should respond with 204
             When a GET request is made to "/ed-fi/schools/deletes"
             Then it should respond with 200

        @e2e-ci-shard-3
        Scenario: 14 Deletes response is served when only minChangeVersion is supplied
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": 8118608,
                    "nameOfInstitution": "Tracked Min Only School",
                    "gradeLevels": [
                      {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                      }
                    ],
                    "educationOrganizationCategories": [
                      {
                        "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                      }
                    ]
                  }
                  """
             Then it should respond with 201
             When the resulting id is stored in the "minOnlySchoolId" variable
             When a GET request is made to "/changeQueries/v1/availableChangeVersions"
             Then it should respond with 200
              And the response body path "newestChangeVersion" is stored in request variable "minOnlyChangeVersion"
             When a DELETE request is made to "/ed-fi/schools/{minOnlySchoolId}"
             Then it should respond with 204
             When a GET request is made to "/ed-fi/schools/deletes?minChangeVersion={minOnlyChangeVersion}&limit=1&offset=0&totalCount=true"
             Then it should respond with 200
              And total of records should be 1
              And the response body path "0.id" should equal request variable "minOnlySchoolId"
              And the response body path "0.keyValues.schoolId" should have value "8118608"

        Rule: StudentSchoolAssociation deletes carry the student natural key

            Background:
                Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1255901001"
                  And the system has these "schools"
                      | schoolId   | nameOfInstitution  | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                      | 1255901001 | Tracked SSA School | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  And the system has these "students"
                      | studentUniqueId | firstName | lastSurname | birthDate  |
                      | "11"            | Tracked   | Student     | 2008-01-01 |

            @e2e-ci-shard-3
            Scenario: 11 Deleted StudentSchoolAssociation appears in deletes response with student natural key
                 When a POST request is made to "/ed-fi/studentSchoolAssociations" with
                      """
                      {
                        "entryDate": "2023-08-01",
                        "schoolReference": {
                          "schoolId": 1255901001
                        },
                        "studentReference": {
                          "studentUniqueId": "11"
                        },
                        "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                      }
                      """
                 Then it should respond with 201
                 When the resulting id is stored in the "deletedSsaId" variable
                 When a DELETE request is made to "/ed-fi/studentSchoolAssociations/{deletedSsaId}"
                 Then it should respond with 204
                 When a GET request is made to "/changeQueries/v1/availableChangeVersions"
                 Then it should respond with 200
                  And the response body path "newestChangeVersion" is stored in request variable "ssaDeleteChangeVersion"
                 When a GET request is made to "/ed-fi/studentSchoolAssociations/deletes?minChangeVersion={ssaDeleteChangeVersion}&maxChangeVersion={ssaDeleteChangeVersion}&totalCount=true"
                 Then it should respond with 200
                  And the response headers include
                      """
                      {
                        "total-count": 1
                      }
                      """
                  And total of records should be 1
                  And the response body path "0.id" should equal request variable "deletedSsaId"
                  And the response body path "0.keyValues.studentUniqueId" should have value "11"
                  And the response body path "0.keyValues.schoolId" should have value "1255901001"
                  And the response body path "0.keyValues.entryDate" should have value "2023-08-01"

            @e2e-ci-shard-3
            Scenario: 12 Recreated StudentSchoolAssociation is suppressed from deletes response
                 When a POST request is made to "/ed-fi/studentSchoolAssociations" with
                      """
                      {
                        "entryDate": "2023-08-01",
                        "schoolReference": {
                          "schoolId": 1255901001
                        },
                        "studentReference": {
                          "studentUniqueId": "11"
                        },
                        "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                      }
                      """
                 Then it should respond with 201
                 When the resulting id is stored in the "recreatedSsaId" variable
                 When a GET request is made to "/changeQueries/v1/availableChangeVersions"
                 Then it should respond with 200
                  And the response body path "newestChangeVersion" is stored in request variable "ssaRecreateMinChangeVersion"
                 When a DELETE request is made to "/ed-fi/studentSchoolAssociations/{recreatedSsaId}"
                 Then it should respond with 204
                 When a POST request is made to "/ed-fi/studentSchoolAssociations" with
                      """
                      {
                        "entryDate": "2023-08-01",
                        "schoolReference": {
                          "schoolId": 1255901001
                        },
                        "studentReference": {
                          "studentUniqueId": "11"
                        },
                        "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                      }
                      """
                 Then it should respond with 201
                 When a GET request is made to "/changeQueries/v1/availableChangeVersions"
                 Then it should respond with 200
                  And the response body path "newestChangeVersion" is stored in request variable "ssaRecreateMaxChangeVersion"
                 When a GET request is made to "/ed-fi/studentSchoolAssociations/deletes?minChangeVersion={ssaRecreateMinChangeVersion}&maxChangeVersion={ssaRecreateMaxChangeVersion}&totalCount=true"
                 Then it should respond with 200
                  And the response headers include
                      """
                      {
                        "total-count": 0
                      }
                      """
                  And the response body is
                      """
                      []
                      """

        Rule: ReadChanges authorization filters and gates the deletes response

            # These scenarios upload purpose-built claim sets whose ReadChanges action declares the
            # authorization strategy under test, then authorize a client and exercise /deletes.
            # ReadChanges authorization is applied as a SQL filter on the tracked-change rows, so a
            # caller scoped to the wrong education organization or namespace sees an empty page
            # (HTTP 200, total-count 0) rather than a 403 — except for the two configuration-error
            # cases (unsupported strategy => 500, NamespaceBased with no prefixes => 403).

            @e2e-ci-shard-3
            @ResetClaimsetsAfterScenario
            @reset-data-before-scenario
            Scenario: 15 RelationshipsWithEdOrgs ReadChanges shows a deleted association to an authorized education organization and hides it from others
                # Seed and delete the association under a broad no-further-auth client.
                Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with educationOrganizationIds "255901001"
                  And the system has these "schools"
                      | schoolId  | nameOfInstitution      | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                      | 255901001 | Tracked Auth SSA School | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  And the system has these "students"
                      | studentUniqueId | firstName | lastSurname | birthDate  |
                      | "21"            | Tracked   | Auth        | 2008-01-01 |
                 When a POST request is made to "/ed-fi/studentSchoolAssociations" with
                      """
                      {
                        "entryDate": "2023-08-01",
                        "schoolReference": {
                          "schoolId": 255901001
                        },
                        "studentReference": {
                          "studentUniqueId": "21"
                        },
                        "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                      }
                      """
                 Then it should respond with 201
                 When the resulting id is stored in the "authSsaId" variable
                 When a DELETE request is made to "/ed-fi/studentSchoolAssociations/{authSsaId}"
                 Then it should respond with 204
                 When a GET request is made to "/changeQueries/v1/availableChangeVersions"
                 Then it should respond with 200
                  And the response body path "newestChangeVersion" is stored in request variable "authSsaChangeVersion"
                # Grant a claim set whose StudentSchoolAssociation ReadChanges uses RelationshipsWithEdOrgsOnly.
                Given a claim set is uploaded to CMS that grants "StudentSchoolAssociation" access to "E2E-ReadChangesEdOrgClaimSet" using authorization strategy "RelationshipsWithEdOrgsOnly"
                  And the claim set upload to CMS should be successful
                # An authorized client (matching education organization) sees the deleted association.
                Given the claimSet "E2E-ReadChangesEdOrgClaimSet" is authorized with educationOrganizationIds "255901001"
                 When a GET request is made to "/ed-fi/studentSchoolAssociations/deletes?minChangeVersion={authSsaChangeVersion}&maxChangeVersion={authSsaChangeVersion}&totalCount=true"
                 Then it should respond with 200
                  And the response headers include
                      """
                      {
                        "total-count": 1
                      }
                      """
                  And total of records should be 1
                  And the response body path "0.id" should equal request variable "authSsaId"
                  And the response body path "0.keyValues.schoolId" should have value "255901001"
                # A client authorized for a different education organization sees nothing.
                Given the claimSet "E2E-ReadChangesEdOrgClaimSet" is authorized with educationOrganizationIds "255901999"
                 When a GET request is made to "/ed-fi/studentSchoolAssociations/deletes?minChangeVersion={authSsaChangeVersion}&maxChangeVersion={authSsaChangeVersion}&totalCount=true"
                 Then it should respond with 200
                  And the response headers include
                      """
                      {
                        "total-count": 0
                      }
                      """
                  And total of records should be 0
                  And the response body is
                      """
                      []
                      """

            @e2e-ci-shard-3
            @ResetClaimsetsAfterScenario
            @reset-data-before-scenario
            Scenario: 16 NamespaceBased ReadChanges shows a deleted descriptor to a matching namespace and hides it from a non-matching namespace
                # Grant a claim set whose CrisisTypeDescriptor actions all use NamespaceBased.
                Given a claim set is uploaded to CMS that grants "CrisisTypeDescriptor" access to "E2E-ReadChangesNamespaceClaimSet" using authorization strategy "NamespaceBased"
                  And the claim set upload to CMS should be successful
                # Create and delete the descriptor under a matching namespace prefix.
                Given the claimSet "E2E-ReadChangesNamespaceClaimSet" is authorized with namespacePrefixes "uri://ed-fi.org"
                 When a POST request is made to "/ed-fi/crisisTypeDescriptors" with
                      """
                      {
                        "codeValue": "Tracked Crisis Type",
                        "namespace": "uri://ed-fi.org/CrisisTypeDescriptor",
                        "shortDescription": "Tracked Crisis Type"
                      }
                      """
                 Then it should respond with 201
                 When the resulting id is stored in the "crisisDescriptorId" variable
                 When a DELETE request is made to "/ed-fi/crisisTypeDescriptors/{crisisDescriptorId}"
                 Then it should respond with 204
                 When a GET request is made to "/changeQueries/v1/availableChangeVersions"
                 Then it should respond with 200
                  And the response body path "newestChangeVersion" is stored in request variable "crisisDeleteChangeVersion"
                # The matching namespace prefix sees the deleted descriptor.
                 When a GET request is made to "/ed-fi/crisisTypeDescriptors/deletes?minChangeVersion={crisisDeleteChangeVersion}&maxChangeVersion={crisisDeleteChangeVersion}&totalCount=true"
                 Then it should respond with 200
                  And the response headers include
                      """
                      {
                        "total-count": 1
                      }
                      """
                  And total of records should be 1
                  And the response body path "0.id" should equal request variable "crisisDescriptorId"
                  And the response body path "0.keyValues.namespace" should have value "uri://ed-fi.org/CrisisTypeDescriptor"
                  And the response body path "0.keyValues.codeValue" should have value "Tracked Crisis Type"
                # A client whose namespace prefix does not match sees nothing.
                Given the claimSet "E2E-ReadChangesNamespaceClaimSet" is authorized with namespacePrefixes "uri://other.org"
                 When a GET request is made to "/ed-fi/crisisTypeDescriptors/deletes?minChangeVersion={crisisDeleteChangeVersion}&maxChangeVersion={crisisDeleteChangeVersion}&totalCount=true"
                 Then it should respond with 200
                  And the response headers include
                      """
                      {
                        "total-count": 0
                      }
                      """
                  And total of records should be 0
                  And the response body is
                      """
                      []
                      """

            @e2e-ci-shard-3
            @ResetClaimsetsAfterScenario
            @reset-data-before-scenario
            Scenario: 17 NoFurtherAuthorizationRequired ReadChanges returns descriptor deletes regardless of the caller's authorization scope
                # Grant a claim set whose GradeLevelDescriptor actions all use NoFurtherAuthorizationRequired,
                # then authorize a client whose namespace prefix would otherwise exclude the descriptor.
                Given a claim set is uploaded to CMS that grants "GradeLevelDescriptor" access to "E2E-ReadChangesNoFurtherAuthClaimSet" using authorization strategy "NoFurtherAuthorizationRequired"
                  And the claim set upload to CMS should be successful
                Given the claimSet "E2E-ReadChangesNoFurtherAuthClaimSet" is authorized with namespace "uri://other.org" and educationOrganizationIds "255901999"
                 When a POST request is made to "/ed-fi/gradeLevelDescriptors" with
                      """
                      {
                        "codeValue": "Tracked NFA Delete Descriptor",
                        "namespace": "uri://ed-fi.org/GradeLevelDescriptor",
                        "shortDescription": "Tracked NFA Delete Descriptor"
                      }
                      """
                 Then it should respond with 201
                 When the resulting id is stored in the "nfaDescriptorId" variable
                 When a DELETE request is made to "/ed-fi/gradeLevelDescriptors/{nfaDescriptorId}"
                 Then it should respond with 204
                 When a GET request is made to "/changeQueries/v1/availableChangeVersions"
                 Then it should respond with 200
                  And the response body path "newestChangeVersion" is stored in request variable "nfaDeleteChangeVersion"
                 When a GET request is made to "/ed-fi/gradeLevelDescriptors/deletes?minChangeVersion={nfaDeleteChangeVersion}&maxChangeVersion={nfaDeleteChangeVersion}&totalCount=true"
                 Then it should respond with 200
                  And the response headers include
                      """
                      {
                        "total-count": 1
                      }
                      """
                  And total of records should be 1
                  And the response body path "0.id" should equal request variable "nfaDescriptorId"
                  And the response body path "0.keyValues.namespace" should have value "uri://ed-fi.org/GradeLevelDescriptor"
                  And the response body path "0.keyValues.codeValue" should have value "Tracked NFA Delete Descriptor"

            @e2e-ci-shard-3
            @ResetClaimsetsAfterScenario
            @reset-data-before-scenario
            Scenario: 18 ReadChanges configured with an unsupported authorization strategy returns a security configuration ProblemDetails
                Given a claim set is uploaded to CMS that grants "School" access to "E2E-ReadChangesUnsupportedStrategyClaimSet" using authorization strategy "OwnershipBased"
                  And the claim set upload to CMS should be successful
                Given the claimSet "E2E-ReadChangesUnsupportedStrategyClaimSet" is authorized with educationOrganizationIds ""
                 When a GET request is made to "/ed-fi/schools/deletes"
                 Then it should respond with 500
                  And the response headers include
                      """
                      {
                          "content-type": "application/problem+json"
                      }
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
                              "Could not find authorization strategy implementations for the following strategy names: 'OwnershipBased'."
                          ]
                      }
                      """

            # NOTE: The "NamespaceBased ReadChanges with a client that has no namespace prefixes => 403"
            # failure path is verified at the integration level
            # (RelationalChangeQueryRepositoryTests.ReadChanges_returns_no_prefixes_failure_when_namespace_based_has_no_prefixes)
            # and at the unit level
            # (NamespaceAuthorizationFailureResponseTests.It_renders_the_no_prefixes_configured_problem_details).
            # It is intentionally NOT reproduced here because the E2E client-provisioning path cannot
            # create a client with zero namespace prefixes: CMS vendor creation enforces a non-empty
            # NamespacePrefixes (FluentValidation NotEmpty on VendorInsertCommand.NamespacePrefixes), and
            # the JWT namespacePrefixes claim is derived from that vendor value. A scenario authorizing
            # with namespacePrefixes "" fails at vendor creation, not at the /deletes authorization check.
