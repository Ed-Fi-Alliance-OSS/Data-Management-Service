@reset-data-before-scenario
Feature: Validation of the structure of the URLs

        Background:
            Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"
              And the system has these descriptors
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

        @API-067
        @e2e-ci-shard-4
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

        @API-068
        @e2e-ci-shard-4
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

        @API-069
        @e2e-ci-shard-4
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

        @API-070
        @e2e-ci-shard-4
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

        @API-071
        @e2e-ci-shard-4
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
        @e2e-ci-shard-4
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
        @e2e-ci-shard-4
        Scenario: 07 Ensure clients cannot update a resource when endpoint does not end in plural
             When a PUT request is made to "/ed-fi/school/00000000-0000-4000-a000-000000000000" with
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
        @e2e-ci-shard-4
        Scenario: 08 Ensure clients cannot delete a resource when endpoint does not end in plural
             When a DELETE request is made to "/ed-fi/school/00000000-0000-4000-a000-000000000000"
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

        @API-075
        @e2e-ci-shard-4
        Scenario: 09 Ensure clients cannot create a resource adding an ID as a path variable
             When a POST request is made to "/ed-fi/schools/00000000-0000-4000-a000-000000000000" with
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
                      "validationErrors": {},
                      "errors": [
                          "Resource items can only be updated using PUT. To 'upsert' an item in the resource collection using POST, remove the 'id' from the route."
                      ]
                  }
                  """
              And the response headers include
                  """
                    {
                        "Allow": "GET, PUT, DELETE",
                        "Content-Type": "application/json; charset=utf-8"
                    }
                  """

        @API-077
        @e2e-ci-shard-4
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
                      "validationErrors": {},
                      "errors": [
                         "Resource collections cannot be replaced. To 'upsert' an item in the collection, use POST. To update a specific item, use PUT and include the 'id' in the route."
                      ]
                  }
                  """
              And the response headers include
                  """
                    {
                        "Allow": "GET, POST",
                        "Content-Type": "application/json; charset=utf-8"
                    }
                  """

        @API-078
        @e2e-ci-shard-4
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
                      "validationErrors": {},
                      "errors": [
                         "Resource collections cannot be deleted. To delete a specific item, use DELETE and include the 'id' in the route."
                      ]
                  }
                  """
              And the response headers include
                  """
                    {
                        "Allow": "GET, POST",
                        "Content-Type": "application/json; charset=utf-8"
                    }
                  """

        @API-250
        @e2e-ci-shard-4
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
        @e2e-ci-shard-4
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

        @API-252
        @e2e-ci-shard-4
        # DMS-397
        Scenario: 15 Ensure client can retrieve information through case insensitive LIMIT parameter
             When a GET request is made to "/ed-fi/schools?liMIt=2"
             Then it should respond with 200
              And the response body is
                  """
                  [
                    {
                      "id": "{id}",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Sixth grade"
                          }
                      ],
                      "nameOfInstitution": "Grand Bend Middle School",
                      "schoolId": 255901044
                    }
                  ]
                  """
              And the response headers include
                  """
                    {
                        "Content-Type": "application/json; charset=utf-8"
                    }
                  """

        @API-253
        @e2e-ci-shard-4
        # DMS-397
        Scenario: 16 Ensure client can retrieve information through case insensitive OFFSET parameter
             # There is only one item, and offset=1 skips that one item.
             When a GET request is made to "/ed-fi/SCHOOLS?OfFSeT=1"
             Then it should respond with 200
              And the response body is
                  """
                  []
                  """

        @API-254
        @e2e-ci-shard-4
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

        @DMS-816
        @e2e-ci-shard-4
        Scenario: 18 Ensure clients get 404 for completely invalid path with prefix before data model
             When a POST request is made to "/ed-fi/notExisting" with
                  """
                  {
                      "namespace": "uri://ed-fi.org/GradeLevelDescriptor",
                      "codeValue": "Test",
                      "shortDescription": "Test",
                      "description": "Test"
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

        @DMS-816
        @e2e-ci-shard-4
        Scenario: 19 Ensure clients get 404 for misspelled resource endpoint
             When a POST request is made to "/ed-fi/gradeLevelDescriptorz" with
                  """
                  {
                      "namespace": "uri://ed-fi.org/GradeLevelDescriptor",
                      "codeValue": "Test",
                      "shortDescription": "Test",
                      "description": "Test"
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

        @DMS-816
        @e2e-ci-shard-4
        Scenario: 20 Ensure clients get 404 for arbitrary non-existent path
             When a GET request is made to "/foo/bar"
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

        @DMS-816
        @e2e-ci-shard-4
        Scenario: 21 Ensure clients get 404 for PUT on non-existent path with ID
             When a PUT request is made to "/invalid/path/00000000-0000-4000-a000-000000000000" with
                  """
                  {
                      "id": "00000000-0000-4000-a000-000000000000",
                      "test": "value"
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

        @DMS-993
        @e2e-ci-shard-4
        Scenario: 22 Ensure clients get 404 for PUT on unknown Ed-Fi resource collection
             When a PUT request is made to "/ed-fi/unknownResource" with
                  """
                  {
                      "test": "value"
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

        @DMS-1281
        @e2e-ci-shard-4
        Scenario: 23 Ensure clients get 405 for an unsupported method on a resource collection
             When an "PATCH" request is made to "/ed-fi/schools" with headers
                  | Key    | Value |
                  | Accept | */*   |
             Then it should respond with 405
              And the response body is
                  """
                  {
                      "detail": "The request construction was invalid.",
                      "type": "urn:ed-fi:api:method-not-allowed",
                      "title": "Method Not Allowed",
                      "status": 405,
                      "correlationId": null,
                      "validationErrors": {},
                      "errors": [
                          "The endpoint of the request does not support the 'PATCH' method."
                      ]
                  }
                  """
              And the response headers include
                  """
                    {
                        "Allow": "GET, POST",
                        "Content-Type": "application/json; charset=utf-8"
                    }
                  """

        @DMS-1281
        @e2e-ci-shard-4
        Scenario: 24 Ensure clients get 405 for an unsupported method on a resource item
             When an "PATCH" request is made to "/ed-fi/schools/00000000-0000-4000-a000-000000000000" with headers
                  | Key    | Value |
                  | Accept | */*   |
             Then it should respond with 405
              And the response body is
                  """
                  {
                      "detail": "The request construction was invalid.",
                      "type": "urn:ed-fi:api:method-not-allowed",
                      "title": "Method Not Allowed",
                      "status": 405,
                      "correlationId": null,
                      "validationErrors": {},
                      "errors": [
                          "The endpoint of the request does not support the 'PATCH' method."
                      ]
                  }
                  """
              And the response headers include
                  """
                    {
                        "Allow": "GET, PUT, DELETE",
                        "Content-Type": "application/json; charset=utf-8"
                    }
                  """

        # ODS/API parity: an unsupported method on a resource that does not exist is a 404, not a
        # 405, because existence is resolved before the method is rejected.
        @DMS-1281
        @e2e-ci-shard-4
        Scenario: 25 Ensure clients get 404 for an unsupported method on an unknown Ed-Fi resource
             When an "PATCH" request is made to "/ed-fi/notaresource" with headers
                  | Key    | Value |
                  | Accept | */*   |
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

        # An unsupported method is routed into the DMS core rather than answered at the routing
        # layer, so authentication runs ahead of the method check. Without a token the response is
        # 401, never the 405 below. This is the ODS/API ordering: authentication, then existence,
        # then method.
        @DMS-1281
        @e2e-ci-shard-4
        Scenario: 26 Ensure an unsupported method without a token is rejected as unauthorized
             When an unauthenticated "PATCH" request is made to "/ed-fi/schools"
             Then it should respond with 401

        @DMS-1281
        @e2e-ci-shard-4
        Scenario: 27 Ensure clients get 405 for an unsupported method on a deletes route
             When an "PATCH" request is made to "/ed-fi/schools/deletes" with headers
                  | Key    | Value |
                  | Accept | */*   |
             Then it should respond with 405
              And the response body is
                  """
                  {
                      "detail": "The request construction was invalid.",
                      "type": "urn:ed-fi:api:method-not-allowed",
                      "title": "Method Not Allowed",
                      "status": 405,
                      "correlationId": null,
                      "validationErrors": {},
                      "errors": [
                          "The endpoint of the request does not support the 'PATCH' method."
                      ]
                  }
                  """
              And the response headers include
                  """
                    {
                        "Allow": "GET",
                        "Content-Type": "application/json; charset=utf-8"
                    }
                  """

        @DMS-1281
        @e2e-ci-shard-4
        Scenario: 28 Ensure clients get 405 for an unsupported method on a keyChanges route
             When an "PATCH" request is made to "/ed-fi/schools/keyChanges" with headers
                  | Key    | Value |
                  | Accept | */*   |
             Then it should respond with 405
              And the response headers include
                  """
                    {
                        "Allow": "GET",
                        "Content-Type": "application/json; charset=utf-8"
                    }
                  """

        # The same existence-before-method ordering as scenario 25, on a tracked-change route.
        # These routes resolve the resource in the core pipeline, so an unknown resource is a 404.
        @DMS-1281
        @e2e-ci-shard-4
        Scenario: 29 Ensure clients get 404 for an unsupported method on an unknown resource's deletes route
             When an "PATCH" request is made to "/ed-fi/notaresource/deletes" with headers
                  | Key    | Value |
                  | Accept | */*   |
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

        # Tracked-change routes are answered by the same core pipeline as the data routes, so they
        # inherit the same authentication ordering as scenario 26.
        @DMS-1281
        @e2e-ci-shard-4
        Scenario: 30 Ensure an unsupported method on a tracked-change route without a token is rejected as unauthorized
             When an unauthenticated "PATCH" request is made to "/ed-fi/schools/deletes"
             Then it should respond with 401

        # ODS/API parity: HEAD is not a supported method on a data route, so it is rejected like any
        # other unsupported verb rather than answered by the GET endpoint. A HEAD response carries no
        # body, so only the status and the Allow header are asserted.
        @DMS-1281
        @e2e-ci-shard-4
        Scenario: 31 Ensure clients get 405 for a HEAD request on a resource collection
             When an "HEAD" request is made to "/ed-fi/schools" with headers
                  | Key    | Value |
                  | Accept | */*   |
             Then it should respond with 405
              And the response headers include
                  """
                    {
                        "Allow": "GET, POST"
                    }
                  """

        # The unknown-project-namespace branch of endpoint validation, the sibling of scenario 25's
        # unknown-resource branch.
        @DMS-1281
        @e2e-ci-shard-4
        Scenario: 32 Ensure clients get 404 for an unsupported method on an unknown project namespace
             When an "PATCH" request is made to "/notaschema/notaresource" with headers
                  | Key    | Value |
                  | Accept | */*   |
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

        # ODS/API parity: OPTIONS is not a supported method on a data route either, so it earns the
        # same problem-details 405 as any other unsupported verb. A CORS preflight is different - it
        # carries Origin and Access-Control-Request-Method and is answered by the CORS middleware
        # before routing, which OwaspCriticalPaths scenario 12 covers.
        @DMS-1281
        @e2e-ci-shard-4
        Scenario: 33 Ensure clients get 405 for an OPTIONS request on a resource collection
             When an "OPTIONS" request is made to "/ed-fi/schools" with headers
                  | Key    | Value |
                  | Accept | */*   |
             Then it should respond with 405
              And the response body is
                  """
                  {
                      "detail": "The request construction was invalid.",
                      "type": "urn:ed-fi:api:method-not-allowed",
                      "title": "Method Not Allowed",
                      "status": 405,
                      "correlationId": null,
                      "validationErrors": {},
                      "errors": [
                          "The endpoint of the request does not support the 'OPTIONS' method."
                      ]
                  }
                  """
              And the response headers include
                  """
                    {
                        "Allow": "GET, POST",
                        "Content-Type": "application/json; charset=utf-8"
                    }
                  """
