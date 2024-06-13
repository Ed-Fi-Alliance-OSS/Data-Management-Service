Feature: Query Strings handling for GET requests with a given set of data

    Background: 
        Given the following schools exist
                  | schoolId  | nameOfInstitution                               | gradeLevels                                                         | educationOrganizationCategories |
                  | 5         | School with max edorgId value                   | [ "Tenth grade", "Ninth grade", "Eleventh grade", "Twelfth grade" ] | [ "School" ]                    |
                  | 6         | UT Austin College of Education Under Graduate   | [ "Eleventh grade" ]                                                | [ "School" ]                    |
                  | 255901001 | Grand Bend High School                          | [ "Tenth grade" ]                                                   | [ "School" ]                    |
                  | 255901044 | Grand Bend Middle School                        | [ "Ninth grade" ]                                                   | [ "School" ]                    |
                  | 255901045 | UT Austin Extended Campus                       | [ "Twelfth grade" ]                                                 | [ "School" ]                    |

        Scenario: Ensure that schools return the total count            
             When a GET request is made to "/ed-fi/schools?totalCount=true"
             Then it should respond with 200
              And the response headers includes total-count 5

        Scenario: Validate totalCount Header is not included when equals to false
             When a GET request is made to "/ed-fi/schools?totalCount=false"
             Then it should respond with 200
              And the response headers does not include total-count

        Scenario: Validate totalCount is not included when it is not present in the URL
             When a GET request is made to "/ed-fi/schools"
             Then it should respond with 200
              And the response headers does not include total-count

        Scenario: Ensure results can be limited and totalCount matches the actual number of existing records
             When a GET request is made to "/ed-fi/schools?totalCount=true&limit=2"
             Then getting less schools than the total-count
              And the response headers includes total-count 5

        Scenario: Ensure clients can get information when filtering by limit and and a valid offset
             When a GET request is made to "/ed-fi/schools?offset=3&limit=5"
             Then it should respond with 200
              And schools returned
                  | schoolId  | nameOfInstitution          |
                  | 255901044 | Grand Bend Middle School   |
                  | 255901045 | UT Austin Extended Campus  |

        Scenario: Ensure clients can get information when filtering by limit and offset greater than the total
             When a GET request is made to "/ed-fi/schools?offset=6&limit=5"
             Then it should respond with 200
              And the response body is
                  """
                  []
                  """
# TODO GET by parameters

        @ignore
        Scenario: Ensure clients can GET information changing the casing of the query value to be all lowercase
            Given the following schools exist
                  | schoolId | nameOfInstitution             | gradeLevels         | educationOrganizationCategories |
                  | 5        | School with max edorgId value | [ "Postsecondary" ] | [ "School" ]                    |
             When a GET request is made to "/ed-fi/schools?nameOfInstitution=school+with+max+edorgid+value"
             Then it should respond with 200
              And the response body includes "nameOfInstitution: School with max edorgId value"

        @ignore
        Scenario: Ensure clients can GET information changing the casing of the query value to be all uppercase
            Given the following schools exist
                  | schoolId | nameOfInstitution                             | gradeLevels         | educationOrganizationCategories     |
                  | 6        | UT Austin College of Education Under Graduate | [ "Postsecondary" ] | [ "Educator Preparation Provider" ] |
             When a GET request is made to "/ed-fi/schools?nameOfInstitution=UT+AUSTIN+COLLEGE+OF+EDUCATION+UNDER+GRADUATE"
             Then it should respond with 200
              And the response body includes "nameOfInstitution: UT Austin College of Education Under Graduate"

        @ignore
        Scenario: Ensure empty array is returned if school name does not match
            Given the following schools exist
                  | schoolId | nameOfInstitution                             | gradeLevels         | educationOrganizationCategories     |
                  | 6        | UT Austin College of Education Under Graduate | [ "Postsecondary" ] | [ "Educator Preparation Provider" ] |
             When a GET request is made to "/ed-fi/schools?nameOfInstitution=nonExisting+school"
             Then it should respond with 200
              And the response body is
                  """
                  []
                  """

        @ignore
        Scenario: Ensure clients can't GET information when filtering using invalid values
             When a GET request is made to "/ed-fi/schools?limit=-1" using values as
                  | Values                   |
                  | -1                       |
                  | 'zero'                   |
                  | '5; select * from users' |
                  | '0)'                     |
                  | '1%27'                   |
             Then it should respond with 400
              And the response body is
                  """
                   {
                     "detail": "Data validation failed. See 'validationErrors' for details.",
                     "type": "urn:ed-fi:api:bad-request:data",
                     "title": "Data Validation Failed",
                     "status": 400,
                     "correlationId": "9e4f71dd-013c-41ee-9eaf-9c3dc368d206",
                     "validationErrors": {
                       "$.Limit": [
                         "The value 'zero' is not valid for Limit."
                       ]
                     }
                   }
                  """

        @ignore
        Scenario: Ensure clients can not GET information when filtering by limit and offset using invalid values
             When a GET request is made to "/ed-fi/schools?limit=-6&offset=-1" using values as
                  | Values                   |
                  | -1                       |
                  | 'zero'                   |
                  | '5; select * from users' |
                  | '0)'                     |
                  | '1%27'                   |
             Then it should respond with 400
              And the response body is
                  """
                  {
                     "detail": "The limit parameter was incorrect.",
                     "type": "urn:ed-fi:api:bad-request:parameter",
                     "title": "Parameter Validation Failed",
                     "status": 400,
                     "correlationId": "b68e4051-8ed4-4055-9e59-4f9c2ef5f4ce",
                     "errors": [
                     "Limit must be omitted or set to a value between 0 and 500."
                     ]
                  }
                  """

                  ##I need a few more details on this scenario
        @ignore
        Scenario: Ensure clients can't GET information when querying with filter and offset using limit without offset
            Given the following schools exist
                  | schoolId | nameOfInstitution                             |
                  | 5        | School with max edorgId value                 |
                  | 6        | UT Austin College of Education Under Graduate |
             When a GET request is made to "/ed-fi/schools?limit=-6&offset=-1"
             Then it should respond with 400
              And the response body is
                  """
                  {
                      "error": "The request is invalid.",
                      "modelState": {
                            "limit": [
                                  "Limit must be provided when using offset",
                            ],
                            "offset": [],
                      },
                  }
                  """
