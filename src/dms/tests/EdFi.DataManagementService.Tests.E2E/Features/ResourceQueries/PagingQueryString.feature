Feature: Paging Support for GET requests for Ed-Fi Resources

        Background:
            Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"

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
             When a GET request is made to "/ed-fi/schools?offset=3&limit=2"
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
             When a GET request is made to "/ed-fi/schools?offset=600&limit=5"
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

        @API-147
        Scenario Outline: 12 Ensure clients can not GET information when filtering by limit and offset using invalid values
            # Some of these are "SQL Injection" style attacks
             When a GET request is made to "/ed-fi/schools?offset=<value>"
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
        Examples:
                  | value                    |
                  | -1                       |
                  | 'zero'                   |
                  | '5; select * from users' |
                  | '0)'                     |
                  | '1%27'                   |

        Scenario Outline: 13 Ensure clients can not GET information when filtering by out of tange limit values
             When a GET request is made to "/ed-fi/schools?offset=0&limit=<value>"
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
                            "Limit must be omitted or set to a numeric value between 0 and 500."
                        ]
                    }
                  """
        Examples:
                  | value |
                  | -1    |
                  | 900   |
