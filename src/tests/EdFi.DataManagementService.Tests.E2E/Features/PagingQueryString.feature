Feature: Paging Support for GET requests for Ed-Fi Resources

        Background:
            Given the following schools exist
                  | schoolId | nameOfInstitution | gradeLevels         | educationOrganizationCategories     |
                  | 1        | School 1          | [ "Postsecondary" ] | [ "Educator Preparation Provider" ] |
                  | 2        | School 2          | [ "Tenth grade" ]   | [ "School" ]                        |
                  | 3        | School 3          | [ "Seventh grade" ] | [ "School" ]                        |
                  | 5        | School 5          | [ "Postsecondary" ] | [ "School" ]                        |
                  | 6        | School 6          | [ "Postsecondary" ] | [ "Educator Preparation Provider" ] |

        @ignore
        Scenario: Ensure clients can not GET information when filtering using invalid limit
             When a GET request is made to "/ed-fi/schools?limit=<Value>"
             Then it should respond with 400
              And the response body is
                  """
                  {
                    "detail": "The limit parameter was incorrect.",
                    "type": "urn:ed-fi:api:bad-request:parameter",
                    "title": "Parameter Validation Failed",
                    "status": 400,
                    "correlationId": null,
                    "errors": ["Limit must be a numeric value greater than or equal to 0."]
                  }
                  """
        Examples:
                  | Value                    |
                  | -1                       |
                  | 'zero'                   |
                  | '5; select * from users' |
                  | '0)'                     |
                  | '1%27'                   |

        @ignore
        Scenario: Ensure clients can get information when filtering by limit and and a valid offset
             When a GET request is made to "/ed-fi/schools?offset=3&limit=5"
             Then it should respond with 200
              And the response body is
                  """
                    [
                        {
                            "id": "{id}",
                            "schoolId": 5,
                            "nameOfInstitution": "School 5",
                            "educationOrganizationCategories": [
                            {
                                "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                            }
                            ],
                            "gradeLevels": [
                            {
                                "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"
                            }
                            ]
                        },
                        {
                            "id": "{id}",
                            "schoolId": 6,
                            "nameOfInstitution": "School 6",
                            "educationOrganizationCategories": [
                            {
                                "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Educator Preparation Provider"
                            }
                            ],
                            "gradeLevels": [
                            {
                                "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"
                            }
                            ]
                        }
                    ]
                  """

        Scenario: Ensure clients can get information when filtering by limit and offset greater than the total
             When a GET request is made to "/ed-fi/schools?offset=6&limit=5"
             Then it should respond with 200
              And the response body is
                  """
                  []
                  """

        @ignore
        Scenario: Ensure clients can't GET information when filtering by limit and offset using invalid offset
             When a GET request is made to "/ed-fi/schools?limit=6&offset=<Value>"
             Then it should respond with 400
              And the response body is
                  """
                  {
                    "detail": "The offset parameter was incorrect.",
                    "type": "urn:ed-fi:api:bad-request:parameter",
                    "title": "Parameter Validation Failed",
                    "status": 400,
                    "correlationId": null,
                    "errors": ["Offset must be a numeric value greater than or equal to 0."]
                  }
                  """
        Examples:
                  | Value                    |
                  | -1                       |
                  | 'zero'                   |
                  | '5; select * from users' |
                  | '0)'                     |
                  | '1%27'                   |

        @ignore
        Scenario: Ensure clients can GET information when querying using an offset without providing any limit in the query string
             When a GET request is made to "/ed-fi/schools?offset=4"
             Then it should respond with 200
              And the response body is
                  """
                    [
                        {
                            "id": "{id}",
                            "schoolId": 6,
                            "nameOfInstitution": "School 6",
                            "educationOrganizationCategories": [
                            {
                                "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Educator Preparation Provider"
                            }
                            ],
                            "gradeLevels": [
                            {
                                "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"
                            }
                            ]
                        }
                    ]
                  """

        @ignore
        Scenario: Ensure clients can GET information when filtering with limits and properties
             When a GET request is made to "/ed-fi/schools?nameOfInstitution=School+5&limit=2"
             Then it should respond with 200
              And the response body is
                  """
                    [
                        {
                            "schoolId": 5,
                            "nameOfInstitution": "School 5",
                            "educationOrganizationCategories": [
                            {
                                "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                            }
                            ],
                            "gradeLevels": [
                            {
                                "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"
                            }
                            ]
                        }
                    ]
                  """
