Feature: Paging Support for GET requests for Ed-Fi Resources

        @addwait
        Scenario: 00 Background
            Given the system has these "schools"
                  | schoolId | nameOfInstitution | gradeLevels                                                                        | educationOrganizationCategories                                                                                                               |
                  | 1        | School 1          | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#Educator Preparation Provider"} ] |
                  | 2        | School 2          | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade"} ]   | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ]                        |
                  | 3        | School 3          | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Seventh grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ]                        |
                  | 4        | School 4          | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ]                        |
                  | 5        | School 5          | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#Educator Preparation Provider"} ] |

        @API-120
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
                                "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"
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
                            "schoolId": 5,
                            "nameOfInstitution": "School 5",
                            "educationOrganizationCategories": [
                            {
                                "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#Educator Preparation Provider"
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

        @API-121
        Scenario: 02 Ensure clients can get information when filtering by limit and offset greater than the total
             When a GET request is made to "/ed-fi/schools?offset=6&limit=5"
             Then it should respond with 200
              And the response body is
                  """
                  []
                  """

        @API-122
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
                                "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#Educator Preparation Provider"
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

        @API-123
        Scenario: 04 Ensure clients can GET information when filtering with limits and properties
             When a GET request is made to "/ed-fi/schools?nameOfInstitution=School+5&limit=2"
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
                                "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#Educator Preparation Provider"
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
