Feature: Paging and Total Count for GET Requests
        Background:
            Given the system has these "schools"
                  | schoolId  | nameOfInstitution                             | gradeLevels                                                                         | educationOrganizationCategories                                                                                        |
                  | 5         | School with max edorgId value                 | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade"} ]    | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 6         | UT Austin College of Education Under Graduate | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Eleventh grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 255901001 | Grand Bend High School                        | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade"} ]    | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 255901044 | Grand Bend Middle School                      | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"} ]    | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 255901045 | UT Austin Extended Campus                     | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Twelfth grade"} ]  | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |

        @API-136
        Scenario: 01 Validate totalCount value when there are no existing schools in the Database
            Given there are no matching schools
             When a GET request is made to "/ed-fi/schools?totalCount=true&nameOfInstitution=does+not+exist"
             Then it should respond with 200
              And the response headers include total-count 0

        @API-137
        Scenario: 02 Validate totalCount is not included when there are no existing schools in the Database and value equals to false
             When a GET request is made to "/ed-fi/schools?totalCount=false"
             Then it should respond with 200
              And the response headers does not include total-count

        @API-138
        Scenario: 03 Validate totalCount is not included when is not included in the URL
             When a GET request is made to "/ed-fi/schools"
             Then it should respond with 200
              And the response headers does not include total-count

        @API-140
        Scenario: 05 Ensure that schools return the total count
             When a GET request is made to "/ed-fi/schools?totalCount=true"
             Then it should respond with 200
              And the response headers include total-count 5

        @API-141
        Scenario: 06 Validate totalCount Header is not included when equals to false
             When a GET request is made to "/ed-fi/schools?totalCount=false"
             Then it should respond with 200
              And the response headers does not include total-count

        @API-142
        Scenario: 07 Validate totalCount is not included when it is not present in the URL
             When a GET request is made to "/ed-fi/schools"
             Then it should respond with 200
              And the response headers does not include total-count

        @API-143
        Scenario: 08 Ensure results can be limited and totalCount matches the actual number of existing records
             When a GET request is made to "/ed-fi/schools?totalCount=true&limit=2"
             Then getting less schools than the total-count
              And the response headers include total-count 5

        @API-144
        Scenario: 09 Ensure clients can get information when filtering by limit and and a valid offset
             When a GET request is made to "/ed-fi/schools?totalCount=true&offset=3&limit=5"
             Then it should respond with 200
              And the response headers include total-count 5
              And the response body is
                  """
                  [
                    {
                        "id": "{id}",
                        "schoolId": 255901044,
                        "gradeLevels": [
                            {
                                "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                            }
                        ],
                        "nameOfInstitution": "Grand Bend Middle School",
                        "educationOrganizationCategories": [
                            {
                                "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                            }
                        ]
                    },
                    {
                        "id": "{id}",
                        "schoolId": 255901045,
                        "gradeLevels": [
                            {
                                "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Twelfth grade"
                            }
                        ],
                        "nameOfInstitution": "UT Austin Extended Campus",
                        "educationOrganizationCategories": [
                            {
                                "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                            }
                        ]
                    }
                  ]
                  """

        @API-145
        Scenario: 10 Ensure clients can get information when filtering by limit and offset greater than the total
             When a GET request is made to "/ed-fi/schools?offset=6&limit=5"
             Then it should respond with 200
              And the response body is
                  """
                  []
                  """

        @API-146
        Scenario: 11 Ensure clients can't GET information when filtering using invalid values
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
                        "detail": "The request could not be processed. See 'errors' for details.",
                        "type": "urn:ed-fi:api:bad-request",
                        "title": "Bad Request",
                        "status": 400,
                        "correlationId": null,
                        "validationErrors": {},
                        "errors": [
                            "Limit must be a numeric value greater than or equal to 0."
                        ]
                    }
                  """

        @API-147
        Scenario: 12 Ensure clients can not GET information when filtering by limit and offset using invalid values
             When a GET request is made to "/ed-fi/schools?offset=-1" using values as
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
                        "detail": "The request could not be processed. See 'errors' for details.",
                        "type": "urn:ed-fi:api:bad-request",
                        "title": "Bad Request",
                        "status": 400,
                        "correlationId": null,
                        "validationErrors": {},
                        "errors": [
                            "Offset must be a numeric value greater than or equal to 0."
                        ]
                    }
                  """

        @API-148
        Scenario: 12 Ensure clients can GET information changing the casing of the query value to be all lowercase
             When a GET request is made to "/ed-fi/schools?nameOfInstitution=school+with+max+edorgid+value"
             Then it should respond with 200
              And the response body is
                  """
                  [
                        {
                            "id": "{id}",
                            "schoolId": 5,
                            "gradeLevels": [
                                {
                                    "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade"
                                }
                            ],
                            "nameOfInstitution": "School with max edorgId value",
                            "educationOrganizationCategories": [
                                {
                                    "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                                }
                            ]
                        }
                      ]
                  """


        @API-149
        Scenario: 13 Ensure clients can GET information changing the casing of the query value to be all uppercase
             When a GET request is made to "/ed-fi/schools?nameOfInstitution=SCHOOL+WITH+MAX+EDORGID+VALUE"
             Then it should respond with 200
              And the response body is
                  """
                  [
                        {
                            "id": "{id}",
                            "schoolId": 5,
                            "gradeLevels": [
                                {
                                    "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade"
                                }
                            ],
                            "nameOfInstitution": "School with max edorgId value",
                            "educationOrganizationCategories": [
                                {
                                    "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                                }
                            ]
                        }
                      ]
                  """

        @API-150
        Scenario: 14 Ensure empty array is returned if school name does not match
             When a GET request is made to "/ed-fi/schools?nameOfInstitution=nonExisting+school"
             Then it should respond with 200
              And the response body is
                  """
                  []
                  """

        @API-151
        Scenario: 15 Ensure clients can't GET information when querying with filter and offset using offset without limit
             When a GET request is made to "/ed-fi/schools?offset=6"
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
