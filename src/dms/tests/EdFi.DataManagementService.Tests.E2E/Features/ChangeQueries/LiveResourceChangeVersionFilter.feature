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
        @relational-backend
        @relational-ci-shard-3
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
        @relational-backend
        @relational-ci-shard-3
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
        @relational-backend
        @relational-ci-shard-3
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
