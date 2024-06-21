# This is a rough draft feature for future use.
Feature: Resources "Create" Operation validations

        Background:
            Given the Data Management Service must receive a token issued by "http://localhost"
              And user is already authorized

        Scenario: Verify new resource can be created successfully
             When a POST request is made to "ed-fi/absenceEventCategoryDescriptors" with
                  """
                  {
                      "codeValue": "Sick Leave",
                      "description": "Sick Leave",
                      "effectiveBeginDate": "2024-05-14",
                      "effectiveEndDate": "2024-05-14",
                      "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                      "shortDescription": "Sick Leave"
                  }
                  """
             Then it should respond with 201 or 200
              And the response headers includes
                  """
                    {
                        "location": "/ed-fi/absenceEventCategoryDescriptors/{id}"
                    }
                  """
              And the record can be retrieved with a GET request
                  """
                  {
                      "id": "{id}",
                      "codeValue": "Sick Leave",
                      "description": "Sick Leave",
                      "effectiveBeginDate": "2024-05-14",
                      "effectiveEndDate": "2024-05-14",
                      "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                      "shortDescription": "Sick Leave"
                  }
                  """

        Scenario: Verify error handling with POST using invalid data
             When a POST request is made to "ed-fi/absenceEventCategoryDescriptors" with
                  """
                    {
                        "description": "Wrong Value",
                        "effectiveBeginDate": "2024-05-14",
                        "effectiveEndDate": "2024-05-14",
                        "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                        "shortDescription": "Wrong Value"
                    }
                  """
             Then it should respond with 400
              And the response body is
                  """
                  {
                      "detail": "Data validation failed. See 'validationErrors' for details.",
                      "type": "urn:ed-fi:api:bad-request:data",
                      "title": "Data Validation Failed",
                      "status": 400,
                      "correlationId": null,
                      "validationErrors": {
                        "$.codeValue": [
                          "codeValue is required."
                        ]
                      },
                      "errors": []
                    }
                  """

        @ignore
        Scenario: Verify error handling with POST using invalid data Forbidden
             When a POST request is made to "ed-fi/absenceEventCategoryDescriptors" with
                  """
                    {
                        "codeValue": "xxxx",
                        "description": "Wrong Value",
                        "namespace": "uri://.org/wrong",
                        "shortDescription": "Wrong Value"
                    }
                  """
             Then it should respond with 403
              And the response body is
                  """
                    {
                        "detail": "Access to the resource could not be authorized. The 'Namespace' value of the resource does not start with any of the caller's associated namespace prefixes ('uri://ed-fi.org', 'uri://gbisd.org', 'uri://tpdm.ed-fi.org').",
                        "type": "urn:ed-fi:api:security:authorization:namespace:access-denied:namespace-mismatch",
                        "title": "Authorization Denied",
                        "status": 403,
                        "correlationId": null
                    }
                  """

        Scenario: Verify error handling with POST using empty body
             When a POST request is made to "ed-fi/absenceEventCategoryDescriptors" with
                  """
                    {
                    }
                  """
             Then it should respond with 400
              And the response body is
                  """
                    {
                      "detail": "Data validation failed. See 'validationErrors' for details.",
                      "type": "urn:ed-fi:api:bad-request:data",
                      "title": "Data Validation Failed",
                      "status": 400,
                      "correlationId": null,
                      "validationErrors": {
                        "$.namespace": [
                          "namespace is required."
                        ],
                        "$.codeValue": [
                          "codeValue is required."
                        ],
                        "$.shortDescription": [
                          "shortDescription is required."
                        ]
                      },
                      "errors": []
                    }
                  """

        @ignore
        Scenario: Verify error handling with POST using blank spaces in the required fields
             When a POST request is made to "ed-fi/absenceEventCategoryDescriptors" with
                  """
                    {
                        "codeValue": "                      ",
                        "description": "                    ",
                        "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                        "shortDescription": "                    "
                    }
                  """
             Then it should respond with 400
             #The error message should be confirmed once we have this validation in the code,
             #Currently the API is allowing to POST records with empty spaces
              And the response body is
                  """
                    {
                        "detail": "Data validation failed. See 'validationErrors' for details.",
                        "type": "urn:ed-fi:api:bad-request:data",
                        "title": "Data Validation Failed",
                        "status": 400,
                        "correlationId": null,
                        "validationErrors": {
                            "$.codeValue": [
                                "CodeValue is required."
                                ],
                            "$.namespace": [
                                "Namespace is required."
                                ],
                            "$.shortDescription": [
                                "ShortDescription is required."
                                ]
                        }
                    }
                  """

        @ignore
        Scenario: Verify POST of existing record without changes
            Given a POST request is made to "ed-fi/absenceEventCategoryDescriptors" with
                  """
                    {
                        "codeValue": "Sick Lave",
                        "description": "Sick Leave",
                        "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                        "shortDescription": "Sick Leave"
                    }
                  """
             When a POST request is made to "ed-fi/absenceEventCategoryDescriptors" with
                  """
                    {
                        "codeValue": "Sick Lave",
                        "description": "Sick Leave",
                        "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                        "shortDescription": "Sick Leave"
                    }
                  """
             Then it should respond with 200
              And the record can be retrieved with a GET request
                  """
                        {
                            "codeValue": "Sick Lave",
                            "description": "Sick Leave",
                            "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                            "shortDescription": "Sick Leave"
                        }
                  """

        @ignore
        Scenario: Verify POST of existing record (change non-key field) works
             When a POST request is made to "ed-fi/absenceEventCategoryDescriptors" with
                  """
                    {
                        "codeValue": "Sick Lave",
                        "description": "Sick Leave Edit",
                        "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                        "shortDescription": "Sick Leave"
                    }
                  """
             Then it should respond with 200

        @ignore
        Scenario: Verify error handling when resource ID is included in body on POST
            # The id value should be replaced with the resource created in the Background section
             When a POST request is made to "ed-fi/absenceEventCategoryDescriptors/" with
                  """
                    {
                        "id": "{id}",
                        "codeValue": "Sick Leave",
                        "description": "Sick Leave Edited",
                        "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                        "shortDescription": "Sick Leave"
                    }
                  """
             Then it should respond with 400
              And the response body is
                  """
                    {
                        "detail": "The request data was constructed incorrectly.",
                        "type": "urn:ed-fi:api:bad-request:data",
                        "title": "Data Validation Failed",
                        "status": 400,
                        "correlationId": null,
                        "errors": [
                            "Resource identifiers cannot be assigned by the client. The 'id' property should not be included in the request body."
                        ]
                    }
                  """

        Scenario: Verify Post when adding a overposting object
            When a POST request is made to "ed-fi/educationContents" with
                """
                {
                  "contentIdentifier": "Testing",
                   "namespace": "Testing",
                  "learningStandardReference": {
                    "learningStandardId": "Testing"
                  },
                   "objectOverpost": {
                     "x": 1
                  }
                }
                """
            Then it should respond with 201 or 200
            And the record can be retrieved with a GET request
            """
            {
                "id": "{id}",
                "contentIdentifier": "Testing",
                "namespace": "Testing",
                "learningStandardReference": {
                    "learningStandardId": "Testing"
                }
            }
            """

        Scenario: Verify POST of numeric and boolean fields as strings are coerced
            # In this example schoolId is numeric and doNotPublishIndicator are boolean, yet posted in quotes as strings
            # In the GET request you can see they are coerced to their proper types
             When a POST request is made to "ed-fi/schools/" with
                  """
                  {
                      "schoolId": "99",
                      "nameOfInstitution": "UT Austin College of Education Graduate",
                      "addresses": [
                          {
                          "addressTypeDescriptor": "uri://ed-fi.org/AddressTypeDescriptor#Physical",
                          "city": "Austin",
                          "postalCode": "78712",
                          "stateAbbreviationDescriptor": "uri://ed-fi.org/StateAbbreviationDescriptor#TX",
                          "streetNumberName": "1912 Speedway Stop D5000",
                          "nameOfCounty": "Travis",
                          "doNotPublishIndicator": "true"
                          }
                      ],
                      "educationOrganizationCategories": [
                          {
                          "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#Educator Preparation Provider"
                          }
                      ],
                      "schoolCategories": [],
                      "gradeLevels": [
                          {
                          "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"
                          }
                      ]
                    }
                  """
             Then it should respond with 201
              And the response headers includes
                  """
                    {
                        "location": "/ed-fi/schools/{id}"
                    }
                  """
              And the record can be retrieved with a GET request
                  """
                  {
                    "id": "{id}",
                    "schoolId": 99,
                    "addresses": [
                        {
                            "city": "Austin",
                            "postalCode": "78712",
                            "nameOfCounty": "Travis",
                            "streetNumberName": "1912 Speedway Stop D5000",
                            "addressTypeDescriptor": "uri://ed-fi.org/AddressTypeDescriptor#Physical",
                            "doNotPublishIndicator": true,
                            "stateAbbreviationDescriptor": "uri://ed-fi.org/StateAbbreviationDescriptor#TX"
                        }
                    ],
                    "gradeLevels": [
                        {
                            "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"
                        }
                    ],
                    "schoolCategories": [],
                    "nameOfInstitution": "UT Austin College of Education Graduate",
                    "educationOrganizationCategories": [
                        {
                            "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#Educator Preparation Provider"
                        }
                    ]
                  }
                  """
