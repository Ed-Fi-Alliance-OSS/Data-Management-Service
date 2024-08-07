Feature: Resources "Update" Operation validations

    Rule: Descriptors

        Background:
            Given the Data Management Service must receive a token issued by "http://localhost"
              And user is already authorized
              And the system has these descriptors
                  | descriptorValue                                                                            |
                  | uri://ed-fi.org/ContentClassDescriptor#Testing                                             |
                  | uri://ed-fi.org/AddressTypeDescriptor#Physical                                             |
                  | uri://ed-fi.org/StateAbbreviationDescriptor#TX                                             |
                  | uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#Educator Preparation Provider |
                  | uri://ed-fi.org/GradeLevelDescriptor#Postsecondary                                         |
                  | uri://ed-fi.org/AbsenceEventCategoryDescriptor#Sick Leave                                  |
              And a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
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

        Scenario: 01 Put an existing document (Descriptor)
            # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}" with
                  """
                  {
                    "id": "{id}",
                    "codeValue": "Sick Leave",
                    "description": "Sick Leave Edited",
                    "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                    "shortDescription": "Sick Leave",
                    "effectiveBeginDate": "2024-05-14",
                    "effectiveEndDate": "2024-05-14"
                  }
                  """
             Then it should respond with 204
              And the record can be retrieved with a GET request
                  """
                  {
                    "id": "{id}",
                    "codeValue": "Sick Leave",
                    "description": "Sick Leave Edited",
                    "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                    "shortDescription": "Sick Leave",
                    "effectiveBeginDate": "2024-05-14",
                    "effectiveEndDate": "2024-05-14"
                  }
                  """

        Scenario: 02 Put an existing document with optional properties removed (Descriptor)
            # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}" with
                  """
                  {
                    "id": "{id}",
                    "codeValue": "Sick Leave",
                    "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                    "shortDescription": "Sick Leave"
                  }
                  """
             Then it should respond with 204
              And the record can be retrieved with a GET request
                  """
                  {
                    "id": "{id}",
                    "codeValue": "Sick Leave",
                    "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                    "shortDescription": "Sick Leave"
                  }
                  """

        Scenario: 03 Update a document with a string that is too long (Descriptor)
             When a PUT request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}" with
                  """
                  {
                      "id": "{id}",
                      "codeValue": "Sick LeaveSick LeaveSick LeaveSick LeaveSick LeaveSick LeaveSick LeaveSick Leave",
                      "description": "Sick Leave",
                      "effectiveBeginDate": "2024-05-14",
                      "effectiveEndDate": "2024-05-14",
                      "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                      "shortDescription": "Sick Leave"
                  }
                  """
             Then it should respond with 400
              And the response body is
                  """
                  {
                    "validationErrors": {
                    "$.codeValue": [
                        "codeValue Value should be at most 50 characters"
                    ]
                    },
                    "errors": [],
                    "detail": "Data validation failed. See 'validationErrors' for details.",
                    "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                    "title": "Data Validation Failed",
                    "status": 400,
                    "correlationId": null
                  }
                  """


        # Ignored because we do not have namespace security for descriptors yet. DMS-81
        @ignore
        Scenario: 04 Put a Descriptor using an invalid namespace
             When a PUT request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}" with
                  """
                    {
                        "id": "{id}",
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

        Scenario: 05 Update a document with spaces in required fields (Descriptor)
             When a PUT request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}" with
                  """
                    {
                        "id": "{id}",
                        "codeValue": "                      ",
                        "description": "                    ",
                        "namespace": "                      ",
                        "shortDescription": "                    "
                    }
                  """
             Then it should respond with 400
              And the response body is
                  """
                   {
                    "validationErrors": {
                        "$.codeValue": [
                            "codeValue cannot contain leading or trailing spaces."
                        ],
                        "$.namespace": [
                            "namespace cannot contain leading or trailing spaces."
                        ],
                        "$.shortDescription": [
                            "shortDescription cannot contain leading or trailing spaces."
                        ]
                    },
                    "errors": [],
                    "detail": "Data validation failed. See 'validationErrors' for details.",
                    "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                    "title": "Data Validation Failed",
                    "status": 400,
                    "correlationId": null
                  }
                  """

        Scenario: 06 Update a document with leading spaces in required fields (Descriptor)
             When a PUT request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}" with
                  """
                    {
                        "id": "{id}",
                        "codeValue": "                      a",
                        "description": "                    a",
                        "namespace": "     uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                        "shortDescription": "                   a"
                    }
                  """
             Then it should respond with 400
              And the response body is
                  """
                  {
                    "validationErrors": {
                        "$.codeValue": [
                            "codeValue cannot contain leading or trailing spaces."
                        ],
                        "$.namespace": [
                            "namespace cannot contain leading or trailing spaces."
                        ],
                        "$.shortDescription": [
                            "shortDescription cannot contain leading or trailing spaces."
                        ]
                    },
                    "errors": [],
                    "detail": "Data validation failed. See 'validationErrors' for details.",
                    "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                    "title": "Data Validation Failed",
                    "status": 400,
                    "correlationId": null
                  }
                  """

        Scenario: 07 Update a document with trailing spaces in required fields (Descriptor)
             When a PUT request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}" with
                  """
                    {
                        "id": "{id}",
                        "codeValue": "a                      ",
                        "description": "a                    ",
                        "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor     ",
                        "shortDescription": "a                   "
                    }
                  """
             Then it should respond with 400
              And the response body is
                  """
                  {
                    "validationErrors": {
                        "$.codeValue": [
                            "codeValue cannot contain leading or trailing spaces."
                        ],
                        "$.namespace": [
                            "namespace cannot contain leading or trailing spaces."
                        ],
                        "$.shortDescription": [
                            "shortDescription cannot contain leading or trailing spaces."
                        ]
                    },
                    "errors": [],
                    "detail": "Data validation failed. See 'validationErrors' for details.",
                    "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                    "title": "Data Validation Failed",
                    "status": 400,
                    "correlationId": null
                  }
                  """

        Scenario: 08 Put an existing document with an extra property (overpost) (Descriptor)
            # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}" with
                  """
                  {
                    "id": "{id}",
                    "codeValue": "Sick Leave",
                    "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                    "shortDescription": "Sick Leave",
                    "objectOverpost": {
                        "x": 1
                    }
                  }
                  """
             Then it should respond with 204
              And the record can be retrieved with a GET request
                  """
                  {
                    "id": "{id}",
                    "codeValue": "Sick Leave",
                    "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                    "shortDescription": "Sick Leave"
                  }
                  """

        Scenario: 09 Update a document that does not exist (Descriptor)
             # The id value should be replaced with a non existing resource
             When a PUT request is made to "/ed-fi/absenceEventCategoryDescriptors/00000000-0000-4000-a000-000000000000" with
                  """
                  {
                    "id": "00000000-0000-4000-a000-000000000000",
                    "codeValue": "Sick Leave",
                    "description": "Sick Leave Edited",
                    "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                    "shortDescription": "Sick Leave"
                  }
                  """
             Then it should respond with 404
              And the response body is
                  """
                  {
                      "detail": "Resource to update was not found",
                      "type": "urn:ed-fi:api:not-found",
                      "title": "Not Found",
                      "status": 404,
                      "correlationId": null,
                      "validationErrors": {},
                      "errors": []
                  }
                  """

        Scenario: 10 Update a document with modification of an identity field (Descriptor)
            # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}" with
                  """
                  {
                    "id": "{id}",
                    "codeValue": "Sick Leave",
                    "description": "Sick Leave Edited",
                    "namespace": "AbsenceEventCategoryDescriptor",
                    "shortDescription": "Sick Leave Edited"
                  }
                  """
             Then it should respond with 400
              And the response body is
                  """
                  {
                    "detail": "Identifying values for the AbsenceEventCategoryDescriptor resource cannot be changed. Delete and recreate the resource item instead.",
                    "type": "urn:ed-fi:api:bad-request:data-validation-failed:key-change-not-supported",
                    "title": "Key Change Not Supported",
                    "status": 400,
                    "correlationId": null,
                    "validationErrors": {},
                    "errors": []
                    }
                  """

        Scenario: 11  Put an empty request object (Descriptor)
             # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}" with
                  """
                  """
             Then it should respond with 400
              And the response body is
                  """
                  {
                    "validationErrors": {},
                    "errors": [
                        "A non-empty request body is required."
                    ],
                    "detail": "The request could not be processed. See 'errors' for details.",
                    "type": "urn:ed-fi:api:bad-request",
                    "title": "Bad Request",
                    "status": 400,
                    "correlationId": null
                  }
                  """

        Scenario: 12 Put an empty JSON body (Descriptor)
             # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}" with
                  """
                  {
                  }
                  """
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
                        "$.namespace": [
                        "namespace is required."
                        ],
                        "$.codeValue": [
                        "codeValue is required."
                        ],
                        "$.shortDescription": [
                        "shortDescription is required."
                        ],
                        "$.id": [
                        "id is required."
                        ]
                    },
                    "errors": []
                  }
                  """

        Scenario: 13 Update a document with mismatch between URL and id (Descriptor)
             # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}" with
                  """
                  {
                    "id": "00000000-0000-0000-0000-000000000000",
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
                    "detail": "The request could not be processed. See 'errors' for details.",
                    "type": "urn:ed-fi:api:bad-request",
                    "title": "Bad Request",
                    "status": 400,
                    "correlationId": null,
                    "validationErrors": {},
                    "errors": [
                        "Request body id must match the id in the url."
                    ]
                  }
                  """

        Scenario: 14 Update a document with a blank id (Descriptor)
            # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}" with
                  """
                  {
                    "id": "",
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
                     "detail": "The request could not be processed. See 'errors' for details.",
                     "type": "urn:ed-fi:api:bad-request",
                     "title": "Bad Request",
                     "status": 400,
                     "correlationId": null,
                     "validationErrors": {},
                     "errors": [
                         "Request body id must match the id in the url."
                     ]
                   }
                  """

        Scenario: 15 Update a document with an invalid id format (Descriptor)
             # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}" with
                  """
                  {
                    "id": "invalid-id",
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
                    "detail": "The request could not be processed. See 'errors' for details.",
                    "type": "urn:ed-fi:api:bad-request",
                    "title": "Bad Request",
                    "status": 400,
                    "correlationId": null,
                    "validationErrors": {},
                    "errors": [
                        "Request body id must match the id in the url."
                    ]
                  }
                  """

        Scenario: 15.1 Update a document with duplicate properties (Descriptor)
             # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}" with
                  """
                  {
                    "id": "{id}",
                    "codeValue": "Sick Leave",
                    "shortDescription": "Sick Leave Edited",
                    "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                    "shortDescription": "Sick Leave"
                  }
                  """
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
                       "$.shortDescription": [
                         "An item with the same key has already been added."
                       ]
                     },
                     "errors": []
                  }
                  """

    Scenario: 16 Put an existing document with a null optional property (Descriptor)
            # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}" with
                  """
                  {
                    "id": "{id}",
                    "codeValue": "Sick Leave",
                    "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                    "shortDescription": "Sick Leave",
                    "description": null
                  }
                  """
             Then it should respond with 204
              And the record can be retrieved with a GET request
                  """
                  {
                    "id": "{id}",
                    "codeValue": "Sick Leave",
                    "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                    "shortDescription": "Sick Leave"
                  }
                  """

    Rule: Resources

        Background:
            Given the Data Management Service must receive a token issued by "http://localhost"
              And user is already authorized
              And the system has these descriptors
                  | descriptorValue                                                                            |
                  | uri://ed-fi.org/ContentClassDescriptor#Testing                                             |

              And a POST request is made to "/ed-fi/educationContents" with
                  """
                  {
                    "contentIdentifier": "Testing",
                    "namespace": "Testing",
                    "shortDescription": "Testing",
                    "contentClassDescriptor": "uri://ed-fi.org/ContentClassDescriptor#Testing",
                    "learningResourceMetadataURI": "Testing"
                  }
                  """
             Then it should respond with 201 or 200

        Scenario: 11 Put an existing document (Resource)
            # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "/ed-fi/educationContents/{id}" with
                  """
                  {
                    "id": "{id}",
                    "contentIdentifier": "Testing",
                    "namespace": "Testing",
                    "shortDescription": "Testing",
                    "contentClassDescriptor": "uri://ed-fi.org/ContentClassDescriptor#Testing",
                    "learningResourceMetadataURI": "Testing"
                  }
                  """
             Then it should respond with 204
              And the record can be retrieved with a GET request
                  """
                  {
                    "id": "{id}",
                    "contentIdentifier": "Testing",
                    "namespace": "Testing",
                    "shortDescription": "Testing",
                    "contentClassDescriptor": "uri://ed-fi.org/ContentClassDescriptor#Testing",
                    "learningResourceMetadataURI": "Testing"
                  }
                  """

        Scenario: 12 Put an existing document with optional properties removed (Resource)
            # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "/ed-fi/educationContents/{id}" with
                  """
                  {
                    "id": "{id}",
                    "contentIdentifier": "Testing",
                    "namespace": "Testing"
                  }
                  """
             Then it should respond with 204
              And the record can be retrieved with a GET request
                  """
                  {
                    "id": "{id}",
                    "contentIdentifier": "Testing",
                    "namespace": "Testing"
                  }
                  """

        Scenario: 13 Put an existing document with an extra property (overpost) (Resource)
            # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "/ed-fi/educationContents/{id}" with
                  """
                  {
                    "id": "{id}",
                    "contentIdentifier": "Testing",
                    "namespace": "Testing",
                    "shortDescription": "Testing",
                    "contentClassDescriptor": "uri://ed-fi.org/ContentClassDescriptor#Testing",
                    "learningResourceMetadataURI": "Testing",
                    "objectOverpost": {
                        "x": 1
                    }
                  }
                  """
             Then it should respond with 204
              And the record can be retrieved with a GET request
                  """
                  {
                    "id": "{id}",
                    "contentIdentifier": "Testing",
                    "namespace": "Testing",
                    "shortDescription": "Testing",
                    "contentClassDescriptor": "uri://ed-fi.org/ContentClassDescriptor#Testing",
                    "learningResourceMetadataURI": "Testing"
                  }
                  """

        Scenario: 14 Update a document that does not exist (Resource)
             # The id value should be replaced with a non existing resource
             When a PUT request is made to "/ed-fi/educationContents/00000000-0000-4000-a000-000000000000" with
                  """
                  {
                    "id": "00000000-0000-4000-a000-000000000000",
                    "contentIdentifier": "Testing",
                    "namespace": "Testing"
                  }
                  """
             Then it should respond with 404
              And the response body is
                  """
                  {
                      "detail": "Resource to update was not found",
                      "type": "urn:ed-fi:api:not-found",
                      "title": "Not Found",
                      "status": 404,
                      "correlationId": null,
                      "validationErrors": {},
                      "errors": []
                  }
                  """

        Scenario: 15 Update a document with modification of an identity field (Resource)
            # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "/ed-fi/educationContents/{id}" with
                  """
                  {
                    "id": "{id}",
                    "contentIdentifier": "Testing should not be modified",
                    "namespace": "Testing"
                  }
                  """
             Then it should respond with 400
              And the response body is
                  """
                  {
                    "detail": "Identifying values for the EducationContent resource cannot be changed. Delete and recreate the resource item instead.",
                    "type": "urn:ed-fi:api:bad-request:data-validation-failed:key-change-not-supported",
                    "title": "Key Change Not Supported",
                    "status": 400,
                    "correlationId": null,
                    "validationErrors": {},
                    "errors": []
                    }
                  """

        Scenario: 16  Put an empty request object (Resource)
             # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "/ed-fi/educationContents/{id}" with
                  """
                  """
             Then it should respond with 400
              And the response body is
                  """
                  {
                    "validationErrors": {},
                    "errors": [
                        "A non-empty request body is required."
                    ],
                    "detail": "The request could not be processed. See 'errors' for details.",
                    "type": "urn:ed-fi:api:bad-request",
                    "title": "Bad Request",
                    "status": 400,
                    "correlationId": null
                  }
                  """

        Scenario: 17 Put an empty JSON body (Resource)
             # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "/ed-fi/educationContents/{id}" with
                  """
                  {
                  }
                  """
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
                        "$.contentIdentifier": [
                            "contentIdentifier is required."
                        ],
                        "$.namespace": [
                            "namespace is required."
                        ],
                        "$.id": [
                            "id is required."
                        ]
                    },
                    "errors": []
                  }
                  """

        Scenario: 18 Update a document with mismatch between URL and id (Resource)
             # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "/ed-fi/educationContents/{id}" with
                  """
                  {
                    "id": "00000000-0000-4000-a000-000000000000",
                    "contentIdentifier": "Testing should not be modified",
                    "namespace": "Testing"
                  }
                  """
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
                        "Request body id must match the id in the url."
                    ]
                  }
                  """

        Scenario: 19 Update a document with a blank id (Resource)
            # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "/ed-fi/educationContents/{id}" with
                  """
                  {
                    "id": "",
                    "contentIdentifier": "Testing should not be modified",
                    "namespace": "Testing"
                  }
                  """
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
                         "Request body id must match the id in the url."
                     ]
                   }
                  """

        Scenario: 20 Update a document with an invalid id format (Resource)
             # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "/ed-fi/educationContents/{id}" with
                  """
                  {
                    "id": "invalid-id",
                    "contentIdentifier": "Testing should not be modified",
                    "namespace": "Testing"
                  }
                  """
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
                        "Request body id must match the id in the url."
                    ]
                  }
                  """

        Scenario: 21 Put an existing document with string coercion to a numeric value (Resource)
            # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "/ed-fi/educationContents/{id}" with
                  """
                  {
                    "id": "{id}",
                    "contentIdentifier": "Testing",
                    "namespace": "Testing",
                    "shortDescription": "Testing",
                    "contentClassDescriptor": "uri://ed-fi.org/ContentClassDescriptor#Testing",
                    "learningResourceMetadataURI": "Testing",
                    "cost": "2.13"
                  }
                  """
             Then it should respond with 204
              And the record can be retrieved with a GET request
                  """
                  {
                    "id": "{id}",
                    "contentIdentifier": "Testing",
                    "namespace": "Testing",
                    "shortDescription": "Testing",
                    "contentClassDescriptor": "uri://ed-fi.org/ContentClassDescriptor#Testing",
                    "learningResourceMetadataURI": "Testing",
                    "cost": 2.13
                  }
                  """

        Scenario: 22 Put an existing document with null optional value (Resource)
            # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "/ed-fi/educationContents/{id}" with
                  """
                  {
                    "id": "{id}",
                    "contentIdentifier": "Testing",
                    "namespace": "Testing",
                    "shortDescription": "Testing",
                    "contentClassDescriptor": "uri://ed-fi.org/ContentClassDescriptor#Testing",
                    "learningResourceMetadataURI": "Testing",
                    "cost": null
                  }
                  """
             Then it should respond with 204
              And the record can be retrieved with a GET request
                  """
                  {
                    "id": "{id}",
                    "contentIdentifier": "Testing",
                    "namespace": "Testing",
                    "shortDescription": "Testing",
                    "contentClassDescriptor": "uri://ed-fi.org/ContentClassDescriptor#Testing",
                    "learningResourceMetadataURI": "Testing"
                  }
                  """

        Scenario: 23 Put an existing document with a string that is too long (Resource)
            # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "/ed-fi/educationContents/{id}" with
                  """
                  {
                    "id": "{id}",
                    "contentIdentifier": "Testing",
                    "namespace": "Testing",
                    "shortDescription": "Testing",
                    "contentClassDescriptor": "uri://ed-fi.org/ContentClassDescriptor#Testing",
                    "learningResourceMetadataURI": "Testing",
                    "publisher": "publisherpublisherpublisherpublisherpublisherpublisherpublisher"
                  }
                  """
             Then it should respond with 400
              And the response body is
                  """
                  {
                    "validationErrors": {
                        "$.publisher": [
                            "publisher Value should be at most 50 characters"
                        ]
                    },
                    "errors": [],
                    "detail": "Data validation failed. See 'validationErrors' for details.",
                    "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                    "title": "Data Validation Failed",
                    "status": 400,
                    "correlationId": null
                  }
                  """

        @ignore
        Scenario: 24 Update a document with a value that is too short (Resource)
             When a PUT request is made to "/ed-fi/educationContents/{id}" with
                  """
                  {
                    "id": "{id}",
                    "contentIdentifier": "Testing",
                    "namespace": "Testing",
                    "shortDescription": "Testing",
                    "contentClassDescriptor": "uri://ed-fi.org/ContentClassDescriptor#Testing",
                    "learningResourceMetadataURI": "uri",
                    "publisher": "publisherpublisherpublisherpublisherpublisherpublisherpublisher"
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
                        "$.learningResourceMetadataURI": [
                          "learningResourceMetadataURI Value should be at least 5 characters"
                        ]
                      },
                      "errors": []
                    }
                  """

        Scenario: 25 Update a document with a duplicated value (Resource)
             When a PUT request is made to "/ed-fi/educationContents/{id}" with
                  """
                  {
                    "id": "{id}",
                    "contentIdentifier": "Testing",
                    "namespace": "Testing",
                    "shortDescription": "Testing",
                    "contentClassDescriptor": "uri://ed-fi.org/ContentClassDescriptor#Testing",
                    "learningResourceMetadataURI": "Testing",
                    "publisher": "publisherpublisherpublisherpublisherpublisherpublisherpublisher",
                    "learningResourceMetadataURI": "uri"
                  }
                  """
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
                        "$.learningResourceMetadataURI": [
                          "An item with the same key has already been added."
                        ]
                      },
                      "errors": []
                    }
                  """
