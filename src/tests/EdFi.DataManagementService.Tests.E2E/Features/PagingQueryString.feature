Feature: Paging Support for GET requests for Ed-Fi Resources

        Background:
            Given the following schools exist
                  | schoolId | nameOfInstitution | gradeLevels         | educationOrganizationCategories     |
                  | 1        | School 1          | [ "Postsecondary" ] | [ "Educator Preparation Provider" ] |
                  | 2        | School 2          | [ "Tenth grade" ]   | [ "School" ]                        |
                  | 3        | School 3          | [ "Seventh grade" ] | [ "School" ]                        |
                  | 4        | School 4          | [ "Postsecondary" ] | [ "School" ]                        |
                  | 5        | School 5          | [ "Postsecondary" ] | [ "Educator Preparation Provider" ] |

        Scenario: 01 Ensure clients can get information when filtering by limit and and a valid offset
             When a GET request is made to "/ed-fi/schools?offset=3&limit=5"
             Then it should respond with 200
              And the response body is
                  """
                    [
                        {
                            "id": "{id}",
                            "schoolId": 4,
                            "nameOfInstitution": "School 4",
                            "educationOrganizationCategories": [
                            {
                                "educationOrganizationCategoryDescriptor": "School"
                            }
                            ],
                            "gradeLevels": [
                            {
                                "gradeLevelDescriptor": "Postsecondary"
                            }
                            ]
                        },
                        {
                            "id": "{id}",
                            "schoolId": 5,
                            "nameOfInstitution": "School 5",
                            "educationOrganizationCategories": [
                            {
                                "educationOrganizationCategoryDescriptor": "Educator Preparation Provider"
                            }
                            ],
                            "gradeLevels": [
                            {
                                "gradeLevelDescriptor": "Postsecondary"
                            }
                            ]
                        }
                    ]
                  """

        Scenario: 02 Ensure clients can get information when filtering by limit and offset greater than the total
             When a GET request is made to "/ed-fi/schools?offset=6&limit=5"
             Then it should respond with 200
              And the response body is
                  """
                  []
                  """

        Scenario: 03 Ensure clients can GET information when querying using an offset without providing any limit in the query string
             When a GET request is made to "/ed-fi/schools?offset=4"
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
                                "educationOrganizationCategoryDescriptor": "Educator Preparation Provider"
                            }
                            ],
                            "gradeLevels": [
                            {
                                "gradeLevelDescriptor": "Postsecondary"
                            }
                            ]
                        }
                    ]
                  """

# TODO GET by parameters
        @ignore
        Scenario: 04 Ensure clients can GET information when filtering with limits and properties
             When a GET request is made to "/ed-fi/schools?nameOfInstitution=School+5&limit=2"
             Then it should respond with 200
              And the response body is
                  """
                    [
                        {
                            "id": "{id}",
                            "schoolId": 2,
                            "nameOfInstitution": "School 2",
                            "educationOrganizationCategories": [
                            {
                                "educationOrganizationCategoryDescriptor": "School"
                            }
                            ],
                            "gradeLevels": [
                            {
                                "gradeLevelDescriptor": "Tenth grade"
                            }
                            ]
                        }
                    ]
                  """
