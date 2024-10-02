Feature: Validation of the structure of the URLs

        ## The resolution of this ticket will solve the execution error: https://edfi.atlassian.net/browse/DMS-352
        @API-067 @ignore
        Scenario: 01 Ensure clients cannot retrieve information if part of the endpoint is missing
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
              And the response headers includes
                  """
                  Content-Type: application/problem+json
                  """

        ## The resolution of this ticket will solve the execution error: https://edfi.atlassian.net/browse/DMS-352
        @API-068 @ignore
        Scenario: 02 Ensure clients cannot create a resource if part of the endpoint is missing
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
              And the response headers includes
                  """
                  Content-Type: application/problem+json
                  """

        ## The resolution of this ticket will solve the execution error: https://edfi.atlassian.net/browse/DMS-352
        @API-069 @ignore
        Scenario: 03 Ensure clients cannot update a resource if part of the endpoint is missing
            Given the system has these "schools"
                  | schoolId  | nameOfInstitution        | gradeLevelDescriptor                             | educationOrganizationCategoryDescriptor                        |
                  | 255901044 | Grand Bend Middle School | uri://ed-fi.org/GradeLevelDescriptor#Sixth grade | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
             When a PUT request is made to "/schools/{id}" with
                  """
                  {
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
              And the response headers includes
                  """
                  Content-Type: application/problem+json
                  """

        ## The resolution of this ticket will solve the execution error: https://edfi.atlassian.net/browse/DMS-352
        @API-070 @ignore
        Scenario: 04 Ensure clients cannot delete a resource if part of the endpoint is missing
            Given the system has these "schools"
                  | schoolId  | nameOfInstitution        | gradeLevelDescriptor                             | educationOrganizationCategoryDescriptor                        |
                  | 255901044 | Grand Bend Middle School | uri://ed-fi.org/GradeLevelDescriptor#Sixth grade | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
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
              And the headers contain
                  """
                  Content-Type: application/problem+json
                  """

        ## The resolution of this ticket will solve the execution error: https://edfi.atlassian.net/browse/DMS-351
        @API-071 @ignore
        Scenario: 05 Ensure clients cannot retrieve a resource when endpoint does not end in plural
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
              And the response headers includes
                  """
                  Content-Type: application/problem+json
                  """

        ## The resolution of this ticket will solve the execution error: https://edfi.atlassian.net/browse/DMS-351
        @API-072 @ignore
        Scenario: 06 Ensure clients cannot create a resource when endpoint does not end in plural
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
                      "correlationId": null
                  }
                  """
              And the response headers includes
                  """
                  Content-Type: application/problem+json
                  """

        ## The resolution of this ticket will solve the execution error: https://edfi.atlassian.net/browse/DMS-351
        @API-073 @ignore
        Scenario: 07 Ensure clients cannot update a resource when endpoint does not end in plural
            Given the system has these "schools"
                  | schoolId  | nameOfInstitution        | gradeLevelDescriptor                             | educationOrganizationCategoryDescriptor                        |
                  | 255901044 | Grand Bend Middle School | uri://ed-fi.org/GradeLevelDescriptor#Sixth grade | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
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
                      "correlationId": null
                  }
                  """
              And the response headers includes
                  """
                  Content-Type: application/problem+json
                  """

        ## The resolution of this ticket will solve the execution error: https://edfi.atlassian.net/browse/DMS-351
        @API-074 @ignore
        Scenario: 08 Ensure clients cannot delete a resource when endpoint does not end in plural
            Given the system has these "schools"
                  | schoolId  | nameOfInstitution        | gradeLevelDescriptor                             | educationOrganizationCategoryDescriptor                        |
                  | 255901044 | Grand Bend Middle School | uri://ed-fi.org/GradeLevelDescriptor#Sixth grade | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
             When a DELETE request is made to "/ed-fi/school/{id}"
             Then it should respond with 404
              And the respond body is
                  """
                  {
                      "detail": "The specified data could not be found.",
                      "type": "urn:ed-fi:api:not-found",
                      "title": "Not Found",
                      "status": 404,
                      "correlationId": null
                  }
                  """
              And the response headers includes
                  """
                  Content-Type: application/problem+json
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
              And the response headers includes
                  """
                  Content-Type: application/json; charset=utf-8
                  """

        ## The resolution of this ticket will solve the execution error: https://edfi.atlassian.net/browse/DMS-353
        @API-076 @ignore
        Scenario: 10 Ensure clients validate required identifier on POST requests
            Given the system has these "schools"
                  | schoolId  | nameOfInstitution        | gradeLevelDescriptor                             | educationOrganizationCategoryDescriptor                        |
                  | 255901044 | Grand Bend Middle School | uri://ed-fi.org/GradeLevelDescriptor#Sixth grade | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
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
              And the response headers includes
                  """
                  Content-Type: application/json; charset=utf-8
                  """

        ## The resolution of this ticket will solve the execution error: https://edfi.atlassian.net/browse/DMS-353
        @API-077 @ignore
        Scenario: 11 Ensure clients validate required identifier on PUT requests
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
              And the response headers includes
                  """
                  Content-Type: application/json; charset=utf-8
                  """

        ## The resolution of this ticket will solve the execution error: https://edfi.atlassian.net/browse/DMS-354
        @API-078 @ignore
        Scenario: 12 Ensure clients validate required identifier on DELETE requests
             When a GET request is made to "/ed-fi/SCHOOLS?OfFSeT=1&LImiT=2&totalCount=TRue"
             Then it should respond with 200
              And the response body is
                  """
                  [
                      {
                          "id": "49fac44c76ad417a9511101df379718f",
                          "schoolId": 5,
                          "nameOfInstitution": "UT Austin College of Education Graduate",
                          "addresses": [
                              {
                                  "addressTypeDescriptor": "uri://ed-fi.org/AddressTypeDescriptor#Physical",
                                  "city": "Austin",
                                  "postalCode": "78712",
                                  "stateAbbreviationDescriptor": "uri://ed-fi.org/StateAbbreviationDescriptor#TX",
                                  "streetNumberName": "1912 Speedway Stop D5000",
                                  "nameOfCounty": "Travis",
                                  "periods": []
                              }
                          ],
                          "educationOrganizationCategories": [
                              {
                                  "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#Educator Preparation Provider"
                              }
                          ],
                          "identificationCodes": [],
                          "indicators": [],
                          "institutionTelephones": [],
                          "internationalAddresses": [],
                          "_ext": {
                              "tpdm": {}
                          },
                          "schoolCategories": [],
                          "gradeLevels": [
                              {
                                  "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"
                              }
                          ],
                          "_etag": "5250218145643362917",
                          "_lastModifiedDate": "2024-06-05T19:32:01.5975013Z"
                      }
                  ]
                  """
              And the response headers includes
                  """
                  Content-Type: application/json; charset=utf-8
                  Total-Count: 10
                  """

