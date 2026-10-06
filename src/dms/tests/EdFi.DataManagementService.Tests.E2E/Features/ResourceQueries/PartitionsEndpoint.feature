@reset-data-before-scenario
Feature: The partitions endpoint for Ed-Fi Resources
    The public partitions surface as a client meets it through the deployed stack: the token array, the
    count that is an upper bound rather than a promise, the parameters the operation ignores and the
    malformed values it still rejects, the profile outcome it shares with the collection GET, and what the
    served documents publish about it.

        Background:
            Given the claimSet "EdFiSandbox" is authorized with namespacePrefixes "uri://ed-fi.org"

        @e2e-ci-shard-3
        Scenario: 01 An omitted count returns a token array that covers the collection
            Given the system has these "schools"
                  | schoolId | nameOfInstitution | gradeLevels                                                                        | educationOrganizationCategories                                                                                       |
                  | 31       | School 31         | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 32       | School 32         | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 33       | School 33         | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
             When the partitions of "/ed-fi/schools" are requested
             Then it should respond with 200
              And at least one partition token was returned
              And at most 10 partition tokens were returned
             When every returned partition is walked with page size 2
             Then the walk returned 3 documents with no duplicates
              And the walk returned exactly these "schoolId" values
                  | schoolId |
                  | 31       |
                  | 32       |
                  | 33       |

        @e2e-ci-shard-3
        Scenario: 02 A requested count is an upper bound, and the partitions cover the filtered set
            Given the system has these "schools"
                  | schoolId | nameOfInstitution | gradeLevels                                                                        | educationOrganizationCategories                                                                                       |
                  | 41       | Partitioned       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 42       | Partitioned       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 43       | Not partitioned   | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
             When the partitions of "/ed-fi/schools" are requested with "number=3&nameOfInstitution=Partitioned"
             Then it should respond with 200
              And at least one partition token was returned
              And at most 3 partition tokens were returned
              # The filter is repeated on every page request because the token stores none; dropping it
              # would pull the unfiltered school into the union.
             When every returned partition is walked with page size 2 repeating the query "nameOfInstitution=Partitioned"
             Then the walk returned 2 documents with no duplicates
              And the walk returned exactly these "schoolId" values
                  | schoolId |
                  | 41       |
                  | 42       |

        @e2e-ci-shard-3
        Scenario Outline: 03 A malformed or out-of-range partition count is refused
             When a GET request is made to "/ed-fi/schools/partitions?number=<number>"
             Then it should respond with 400
              And the response content type is "application/json"
              And the response body is the parameter validation shell
              And the response body has exactly one error "Number of partitions must be between 1 and 200."

        Examples:
                  | number |
                  |        |
                  | abc    |
                  | 0      |
                  | 201    |

        @e2e-ci-shard-3
        Scenario: 04 Every malformed reserved paging value is reported, in the canonical order, and every reserved parameter is named in the warning
             When a GET request is made to "/ed-fi/schools/partitions?totalCount=perhaps&offset=-1&limit=abc&pageSize=abc&pageToken=abc"
             Then it should respond with 400
              And the response content type is "application/json"
              And the response body is the parameter validation shell
              And the response body errors are
                  | error                                                               |
                  | The page token provided was invalid.                                |
                  | PageSize must be a value between 0 and 500.                         |
                  | Limit must be omitted or set to a numeric value between 0 and 500.  |
                  | Offset must be a numeric value greater than or equal to 0.          |
                  | TotalCount must be a boolean value.                                 |
              And the response headers include
                  """
                  {
                      "X-EdFi-Warning": "Ignored query parameters: totalCount, offset, limit, pageSize, pageToken"
                  }
                  """

        @e2e-ci-shard-3
        Scenario Outline: 05 A well-formed reserved paging parameter or a parameter only ODS defines is ignored and named in the warning
             When a GET request is made to "/ed-fi/schools/partitions?<parameter>=<value>"
             Then it should respond with 200
              And the response headers include
                  """
                  {
                      "X-EdFi-Warning": "Ignored query parameters: <parameter>"
                  }
                  """

        Examples:
                  | parameter            | value |
                  | limit                | 5     |
                  | totalCount           | true  |
                  | allowSmallPartitions | true  |
                  | useJoinAuth          | true  |

        @e2e-ci-shard-3
        Scenario: 06 A partitions request naming a write-only profile is refused exactly as the collection GET is
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-WriteOnly" and namespacePrefixes "uri://ed-fi.org"
             When a GET request is made to "/ed-fi/schools/partitions" with header "Accept" value "application/vnd.ed-fi.school.e2e-test-school-writeonly.readable+json"
             Then it should respond with 405
             When a GET request is made to "/ed-fi/schools" with header "Accept" value "application/vnd.ed-fi.school.e2e-test-school-writeonly.readable+json"
             Then it should respond with 405

        @e2e-ci-shard-3
        Scenario: 07 The served resources document publishes the partitions operation and its count parameter
             When a GET request is made to "/metadata/specifications/resources-spec.json"
             Then it should respond with 200
              And the served OpenAPI document publishes at least one path
              And the served OpenAPI document contains path "/ed-fi/schools"
              And the served OpenAPI document contains path "/ed-fi/schools/partitions"
              And the served OpenAPI operation "get" on path "/ed-fi/schools/partitions" references parameter "numberOfPartitions"

        @e2e-ci-shard-3
        Scenario: 08 A write-only profile document keeps the collection path and omits its partitions sibling
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with profile "E2E-Test-School-WriteOnly" and namespacePrefixes "uri://ed-fi.org"
             When a GET request is made to "/metadata/specifications/profiles/E2E-Test-School-WriteOnly/resources-spec.json"
             Then it should respond with 200
              # Non-vacuity first: a document with no paths at all would satisfy the omission below
              # without publishing anything.
              And the served OpenAPI document publishes at least one path
              And the served OpenAPI document contains path "/ed-fi/schools"
              # The retained path must be the writable one. A document that kept a read-only or empty
              # collection path would otherwise satisfy the omission below without publishing what the
              # profile actually grants.
              And the served OpenAPI path "/ed-fi/schools" has operation "post"
              And the served OpenAPI path "/ed-fi/schools" does not have operation "get"
              And the served OpenAPI document does not contain path "/ed-fi/schools/partitions"
