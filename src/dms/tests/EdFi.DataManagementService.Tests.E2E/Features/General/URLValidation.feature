Feature: Validation of the structure of the URLs

        @addwait
        Scenario: 00 Background
            Given the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/GradeLevelDescriptor#Sixth grade               |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
              And the system has these "schools"
                  | schoolId  | nameOfInstitution        | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 255901044 | Grand Bend Middle School | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Sixth grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
              And a POST request is made to "/ed-fi/classPeriods" with
                  """
                  {
                      "schoolReference": {
                          "schoolId": 255901044
                      },
                      "classPeriodName": "Class Period Test",
                      "officialAttendancePeriod": true
                  }
                  """

        ## The resolution of this ticket will solve the execution error: https://edfi.atlassian.net/browse/DMS-352
        @API-067 @ignore
        Scenario: 01 Ensure clients cannot retrieve information when the data model name is missing
             When a GET request is made to "/schools"
             Then it should respond with 404
              And the response body is
                  """
                  {
                      "detail": "The specified data could not be found.",
                      "type": "urn:ed-fi:api:not-found",
                      "title": "Not Found",
                      "status": 404,
                      "correlationId": null,
                      "validationErrors": {},
                      "errors": []
                  }
                  """
              And the response headers include
                  """
                    {
                        "Content-Type": "application/problem+json"
                    }
                  """

        ## The resolution of this ticket will solve the execution error: https://edfi.atlassian.net/browse/DMS-352
        @API-068 @ignore
        Scenario: 02 Ensure clients cannot create a resource when the data model name is missing
             When a POST request is made to "/schools" with
                  """
                  {
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Transitional Kindergarten"
                          }
                      ],
                      "schoolId": 2244668800,
                      "nameOfInstitution": "Institution Test"
                  }
                  """
             Then it should respond with 404
              And the response body is
                  """
                  {
                      "detail": "The specified data could not be found.",
                      "type": "urn:ed-fi:api:not-found",
                      "title": "Not Found",
                      "status": 404,
                      "correlationId": null
                  }
                  """
              And the response headers include
                  """
                  Content-Type: application/problem+json
                  """

        ## The resolution of this ticket will solve the execution error: https://edfi.atlassian.net/browse/DMS-352
        @API-069 @ignore
        Scenario: 03 Ensure clients cannot update a resource when the data model name is missing
             When a PUT request is made to "/schools/{id}" with
                  """
                  {
                      "id": "{id}",
                      "schoolId": 255901044,
                      "nameOfInstitution": "Grand Bend Middle School",
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Sixth grade"
                          }
                      ],
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ]
                  }
                  """
             Then it should respond with 404
              And the response body is
                  """
                  {
                      "detail": "The specified data could not be found.",
                      "type": "urn:ed-fi:api:not-found",
                      "title": "Not Found",
                      "status": 404,
                      "correlationId": null
                  }
                  """
              And the response headers include
                  """
                    {
                        "Content-Type": "application/problem+json"
                    }
                  """

        ## The resolution of this ticket will solve the execution error: https://edfi.atlassian.net/browse/DMS-352
        @API-070 @ignore
        Scenario: 04 Ensure clients cannot delete a resource when the data model name is missing
             When a DELETE request is made to "/schools/{id}"
             Then it should respond with 404
              And the response body is
                  """
                  {
                      "detail": "The specified data could not be found.",
                      "type": "urn:ed-fi:api:not-found",
                      "title": "Not Found",
                      "status": 404,
                      "correlationId": null
                  }
                  """
              And the response headers include
                  """
                    {
                        "Content-Type": "application/problem+json"
                    }
                  """

        @API-071
        Scenario: 05 Ensure clients cannot retrieve a resource when endpoint is not pluralized
             When a GET request is made to "/ed-fi/school"
             Then it should respond with 404
              And the response body is
                  """
                  {
                      "detail": "The specified data could not be found.",
                      "type": "urn:ed-fi:api:not-found",
                      "title": "Not Found",
                      "status": 404,
                      "correlationId": null,
                      "validationErrors": {},
                      "errors": []
                  }
                  """
              And the response headers include
                  """
                    {
                        "Content-Type": "application/problem+json"
                    }
                  """

        @API-072
        Scenario: 06 Ensure clients cannot create a resource when endpoint is not pluralized
             When a POST request is made to "/ed-fi/school" with
                  """
                  {
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Transitional Kindergarten"
                          }
                      ],
                      "schoolId": 2244668800,
                      "nameOfInstitution": "Institution Test"
                  }
                  """
             Then it should respond with 404
              And the response body is
                  """
                  {
                      "detail": "The specified data could not be found.",
                      "type": "urn:ed-fi:api:not-found",
                      "title": "Not Found",
                      "status": 404,
                      "correlationId": null,
                      "validationErrors": {},
                      "errors": []
                  }
                  """
              And the response headers include
                  """
                    {
                        "Content-Type": "application/problem+json"
                    }
                  """

        @API-073
        Scenario: 07 Ensure clients cannot update a resource when endpoint does not end in plural
             When a PUT request is made to "/ed-fi/school/{id}" with
                  """
                  {
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Transitional Kindergarten"
                          }
                      ],
                      "schoolId": 2244668800,
                      "nameOfInstitution": "Institution Test"
                  }
                  """
             Then it should respond with 404
              And the response body is
                  """
                  {
                      "detail": "The specified data could not be found.",
                      "type": "urn:ed-fi:api:not-found",
                      "title": "Not Found",
                      "status": 404,
                      "correlationId": null,
                      "validationErrors": {},
                      "errors": []
                  }
                  """
              And the response headers include
                  """
                    {
                        "Content-Type": "application/problem+json"
                    }
                  """

        @API-074
        Scenario: 08 Ensure clients cannot delete a resource when endpoint does not end in plural
             When a DELETE request is made to "/ed-fi/school/{id}"
             Then it should respond with 404
              And the response body is
                  """
                  {
                      "detail": "The specified data could not be found.",
                      "type": "urn:ed-fi:api:not-found",
                      "title": "Not Found",
                      "status": 404,
                      "correlationId": null,
                      "validationErrors": {},
                      "errors": []
                  }
                  """
              And the response headers include
                  """
                    {
                        "Content-Type": "application/problem+json"
                    }
                  """

        ## The resolution of this ticket will solve the execution error: https://edfi.atlassian.net/browse/DMS-353
        @API-075 @ignore
        Scenario: 09 Ensure clients cannot create a resource adding an ID as a path variable
             When a POST request is made to "/ed-fi/schools/0123456789" with
                  """
                  {
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Transitional Kindergarten"
                          }
                      ],
                      "schoolId": 2244668800,
                      "nameOfInstitution": "Institution Test"
                  }
                  """
             Then it should respond with 405
              And the response body is
                  """
                  {
                      "detail": "The request construction was invalid.",
                      "type": "urn:ed-fi:api:method-not-allowed",
                      "title": "Method Not Allowed",
                      "status": 405,
                      "correlationId": null,
                      "errors": [
                          "Resource items can only be updated using PUT. To \\"upsert\\" an item in the resource collection using POST, remove the \\"id\\" from the route."
                      ]
                  }
                  """
              And the response headers include
                  """
                    {
                        "Content-Type": "application/json; charset=utf-8"
                    }
                  """

        ## The resolution of this ticket will solve the execution error: https://edfi.atlassian.net/browse/DMS-353
        @API-077 @ignore
        Scenario: 10 Ensure PUT requests require an Id value
             When a PUT request is made to "/ed-fi/schools/" with
                  """
                  {
                      "schoolId": 4,
                      "nameOfInstitution": "UT Austin College of Education Graduate",
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
                  """
             Then it should respond with 405
              And the response body is
                  """
                  {
                      "detail": "The request construction was invalid.",
                      "type": "urn:ed-fi:api:method-not-allowed",
                      "title": "Method Not Allowed",
                      "status": 405,
                      "correlationId": null,
                      "errors": [
                         "Resource collections cannot be replaced. To \\"upsert\\" an item in the collection, use POST. To update a specific item, use PUT and include the \\"id\\" in the route."
                      ]
                  }
                  """
              And the response headers include
                  """
                    {
                        "Content-Type": "application/json; charset=utf-8"
                    }
                  """

        ## The resolution of this ticket will solve the execution error: https://edfi.atlassian.net/browse/DMS-353
        @API-078 @ignore
        Scenario: 11 Ensure DELETE requests require an Id value
             When a DELETE request is made to "/ed-fi/schools/"
             Then it should respond with 405
              And the response body is
                  """
                  {
                      "detail": "The request construction was invalid.",
                      "type": "urn:ed-fi:api:method-not-allowed",
                      "title": "Method Not Allowed",
                      "status": 405,
                      "correlationId": null,
                      "errors": [
                         "Resource collections cannot be deleted. To delete a specific item, use DELETE and include the \\"id\\" in the route."
                      ]
                  }
                  """
              And the response headers include
                  """
                    {
                        "Content-Type": "application/json; charset=utf-8"
                    }
                  """

        @API-235
        Scenario: 12 Ensure client can retrieve information through a case insensitive query
             When a GET request is made to "/ed-fi/classPeriods?classPeriodName=CLASS+pERIOD+test"
             Then it should respond with 200
              And the response body is
                  """
                  [
                      {
                          "id": "{id}",
                          "schoolReference": {
                              "schoolId": 255901044
                          },
                          "classPeriodName": "Class Period Test",
                          "officialAttendancePeriod": true
                      }
                  ]
                  """

        @API-250
        Scenario: 13 Ensure client can retrieve information through a case insensitive query parameter
             When a GET request is made to "/ed-fi/classPeriods?CLaSSperIODName=Class+Period+Test"
             Then it should respond with 200
              And the response body is
                  """
                  [
                      {
                          "id": "{id}",
                          "schoolReference": {
                              "schoolId": 255901044
                          },
                          "classPeriodName": "Class Period Test",
                          "officialAttendancePeriod": true
                      }
                  ]
                  """

        @API-251
        Scenario: 14 Ensure clients validate identifier on GET requests
             When a GET request is made to "/ed-fi/schools/ffc0a272"
             Then it should respond with 400
              And the response body is
                  """
                  {
                      "detail": "Data validation failed. See 'validationErrors' for details.",
                      "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                      "title": "Data Validation Failed",
                      "status": 400,
                      "correlationId": null,
                      "validationErrors": {
                        "$.id": [
                            "The value 'ffc0a272' is not valid."
                        ]
                      },
                      "errors": []
                  }
                  """

        @API-252 @ignore
        # DMS-397
        Scenario: 15 Ensure client can retrieve information through case insensitive LIMIT parameter
             When a GET request is made to "/ed-fi/schools?lImIt=1"
             Then it should respond with 200
              And the response body is
                  """
                  [
                    {
                      "id": "{id}",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                          }
                      ],
                      "nameOfInstitution": "Middle School Test",
                      "schoolId": 745672453832456000
                    }
                  ]
                  """
              And the response headers include
                  """
                    {
                        "Content-Type": "application/json; charset=utf-8"
                    }
                  """

        @API-253 @ignore
        # DMS-397
        Scenario: 16 Ensure client can retrieve information through case insensitive OFFSET parameter
             # There is only one item, and offset=1 skips that one item.
             When a GET request is made to "/ed-fi/SCHOOLS?OfFSeT=1"
             Then it should respond with 200
              And the response body is
                  """
                  []
                  """

        @API-254 @ignore
        # DMS-397
        Scenario: 17 Ensure client can retrieve information through case insensitive TOTALCOUNT parameter
             When a GET request is made to "/ed-fi/SCHOOLS?tOtAlCoUnT=trUE"
             Then it should respond with 200
              And the response headers include
                  """
                    {
                        "Content-Type": "application/json; charset=utf-8",
                        "Total-Count": 1
                    }
                  """
