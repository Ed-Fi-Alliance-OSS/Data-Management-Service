@reset-data-before-scenario
Feature: Cursor paging for GET requests for Ed-Fi Resources
    The public cursor surface as a client meets it through the deployed stack: a walk that follows the
    continuation it was handed, the zero-size page, the exact rejection contract, the operations that do
    not page by cursor, and what the served OpenAPI document publishes about all of it.

        Background:
            Given the claimSet "EdFiSandbox" is authorized with namespacePrefixes "uri://ed-fi.org, uri://tpdm.ed-fi.org"

        @e2e-ci-shard-2
        Scenario: 01 A cursor walk over a regular resource returns every seeded document exactly once
            Given the system has these "schools"
                  | schoolId | nameOfInstitution | gradeLevels                                                                        | educationOrganizationCategories                                                                                       |
                  | 1        | School 1          | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 2        | School 2          | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 3        | School 3          | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 4        | School 4          | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 5        | School 5          | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
             When a cursor walk is made over "/ed-fi/schools" with page size 2
             Then the walk returned 5 documents with no duplicates
              And the walk returned exactly these "schoolId" values
                  | schoolId |
                  | 1        |
                  | 2        |
                  | 3        |
                  | 4        |
                  | 5        |
              And the walk ended with an empty page and no continuation

        @e2e-ci-shard-2
        Scenario: 02 A cursor walk over an extension resource returns every seeded document exactly once
             When a POST request is made to "/tpdm/candidates" with
                  """
                  { "candidateIdentifier": "CursorWalk-1", "birthDate": "2005-10-03", "firstName": "Ada", "lastSurname": "One" }
                  """
             Then it should respond with 201
             When a POST request is made to "/tpdm/candidates" with
                  """
                  { "candidateIdentifier": "CursorWalk-2", "birthDate": "2005-10-04", "firstName": "Ada", "lastSurname": "Two" }
                  """
             Then it should respond with 201
             When a POST request is made to "/tpdm/candidates" with
                  """
                  { "candidateIdentifier": "CursorWalk-3", "birthDate": "2005-10-05", "firstName": "Ada", "lastSurname": "Three" }
                  """
             Then it should respond with 201
             When a cursor walk is made over "/tpdm/candidates" with page size 2
             Then the walk returned 3 documents with no duplicates
              And the walk returned exactly these "candidateIdentifier" values
                  | candidateIdentifier |
                  | CursorWalk-1        |
                  | CursorWalk-2        |
                  | CursorWalk-3        |
              And the walk ended with an empty page and no continuation

        @e2e-ci-shard-2
        Scenario: 03 A cursor walk over descriptors returns every seeded descriptor exactly once
             When a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  """
                  { "codeValue": "CursorWalk1", "shortDescription": "Cursor walk 1", "description": "Cursor walk 1", "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor", "effectiveBeginDate": "2099-01-01" }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  """
                  { "codeValue": "CursorWalk2", "shortDescription": "Cursor walk 2", "description": "Cursor walk 2", "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor", "effectiveBeginDate": "2099-01-01" }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  """
                  { "codeValue": "CursorWalk3", "shortDescription": "Cursor walk 3", "description": "Cursor walk 3", "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor", "effectiveBeginDate": "2099-01-01" }
                  """
             Then it should respond with 201
              # The filter is repeated on every page request, because the token stores none and the
              # deployment already carries descriptors this scenario did not create.
             When a cursor walk is made over "/ed-fi/absenceEventCategoryDescriptors" with page size 2 repeating the query "effectiveBeginDate=2099-01-01"
             Then the walk returned 3 documents with no duplicates
              And the walk returned exactly these "codeValue" values
                  | codeValue   |
                  | CursorWalk1 |
                  | CursorWalk2 |
                  | CursorWalk3 |
              And the walk ended with an empty page and no continuation

        @e2e-ci-shard-2
        Scenario: 04 A zero-size page succeeds, returns nothing, and cannot advance a walk
            Given the system has these "schools"
                  | schoolId | nameOfInstitution | gradeLevels                                                                        | educationOrganizationCategories                                                                                       |
                  | 11       | School 11         | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 12       | School 12         | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
             When a GET request is made to "/ed-fi/schools?limit=1"
             Then it should respond with 200
              And the response header "Next-Page-Token" is present
              And the response header "Next-Page-Token" is captured as "walkToken"
             When a GET request is made to "/ed-fi/schools?pageToken={walkToken}&pageSize=0"
             Then it should respond with 200
              And the response body is
                  """
                  []
                  """
              And the response header "Next-Page-Token" is absent

        @e2e-ci-shard-2
        Scenario: 05 A page size without a page token is rejected with exactly one error
             When a GET request is made to "/ed-fi/schools?pageSize=5&limit=10"
             Then it should respond with 400
              And the response content type is "application/json"
              And the response body is the parameter validation shell
              And the response body has exactly one error "PageToken is required when pageSize is specified."

        @e2e-ci-shard-2
        Scenario: 05a An undecodable page token is refused before any other rule is considered
              # Phase 0 of the approved precedence: an undecodable token makes every rule that reasons
              # about a valid token meaningless, so this is the one error reported.
             When a GET request is made to "/ed-fi/schools?pageToken=!!!"
             Then it should respond with 400
              And the response content type is "application/json"
              And the response body is the parameter validation shell
              And the response body has exactly one error "The page token provided was invalid."

        @e2e-ci-shard-2
        Scenario: 06 An offset alongside a page token is a paging-mode conflict
            Given the system has these "schools"
                  | schoolId | nameOfInstitution | gradeLevels                                                                        | educationOrganizationCategories                                                                                       |
                  | 21       | School 21         | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
             When a GET request is made to "/ed-fi/schools?limit=1"
             Then it should respond with 200
              And the response header "Next-Page-Token" is captured as "conflictToken"
             When a GET request is made to "/ed-fi/schools?pageToken={conflictToken}&offset=-1"
             Then it should respond with 400
              And the response content type is "application/json"
              And the response body is the parameter validation shell
              And the response body has exactly one error "Both offset and pageToken parameters were provided, but they support alternative paging approaches and cannot be used together."

        @e2e-ci-shard-2
        Scenario: 07 A deletes request does not recognize a page token
             When a GET request is made to "/ed-fi/schools/deletes?pageToken=abc"
             Then it should respond with 400
              And the response content type is "application/json"
              And the response body is the bad request shell
              And the response body has exactly one error "The query field 'pageToken' is not valid for this Change Query endpoint."

        @e2e-ci-shard-2
        Scenario: 08 A keyChanges request does not recognize a page size
             When a GET request is made to "/ed-fi/schools/keyChanges?pageSize=5"
             Then it should respond with 400
              And the response content type is "application/json"
              And the response body is the bad request shell
              And the response body has exactly one error "The query field 'pageSize' is not valid for this Change Query endpoint."

        @e2e-ci-shard-2
        Scenario: 09 The served resources document publishes the cursor parameters and the continuation header
             When a GET request is made to "/metadata/specifications/resources-spec.json"
             Then it should respond with 200
              And the served OpenAPI document publishes at least one path
              And the served OpenAPI operation "get" on path "/ed-fi/schools" references parameter "pageToken"
              And the served OpenAPI operation "get" on path "/ed-fi/schools" references parameter "pageSize"
              And the served OpenAPI operation "get" on path "/ed-fi/schools" declares response header "Next-Page-Token"
