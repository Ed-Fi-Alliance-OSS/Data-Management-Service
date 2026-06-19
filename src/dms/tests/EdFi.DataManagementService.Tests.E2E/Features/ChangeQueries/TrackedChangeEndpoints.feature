Feature: TrackedChangeEndpoints report resource deletes and key changes.

        Background:
            Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade              |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |

        @relational-backend
        @relational-ci-shard-3
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

        @relational-backend
        @relational-ci-shard-3
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

        @relational-backend
        @relational-ci-shard-3
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

        @relational-backend
        @relational-ci-shard-3
        Scenario: 04 Descriptor keyChanges returns empty array
             When a GET request is made to "/ed-fi/gradeLevelDescriptors/keyChanges?totalCount=true&limit=1&offset=0"
             Then it should respond with 200
              And the response headers include
                  """
                  {
                    "total-count": 0
                  }
                  """

        @relational-backend
        @relational-ci-shard-3
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

        @relational-backend
        @relational-ci-shard-3
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

        @relational-backend
        @relational-ci-shard-3
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

        @relational-backend
        @relational-ci-shard-3
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

        @relational-backend
        @relational-ci-shard-3
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

        @relational-backend
        @relational-ci-shard-3
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

        @relational-backend
        @relational-ci-shard-3
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

        @relational-backend
        @relational-ci-shard-3
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

            @relational-backend
            @relational-ci-shard-3
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

            @relational-backend
            @relational-ci-shard-3
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
