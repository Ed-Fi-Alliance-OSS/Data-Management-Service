Feature: Resources "Update" Operation validations

    Rule: Descriptors

        Background:
            Given the Data Management Service must receive a token issued by "http://localhost"
              And user is already authorized
              And a POST request is made to "ed-fi/absenceEventCategoryDescriptors" with
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
             When a PUT request is made to "ed-fi/absenceEventCategoryDescriptors/{id}" with
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
             When a PUT request is made to "ed-fi/absenceEventCategoryDescriptors/{id}" with
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

        Scenario: 03 Put an existing document with an extra property (overpost) (Descriptor)
            # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "ed-fi/absenceEventCategoryDescriptors/{id}" with
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

        Scenario: 04 Put a document that does not exist (Descriptor)
             # The id value should be replaced with a non existing resource
             When a PUT request is made to "ed-fi/absenceEventCategoryDescriptors/00000000-0000-4000-a000-000000000000" with
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
                      "validationErrors": null,
                      "errors": null
                  }
                  """

        Scenario: 05 Put a document with modification of an identity field (Descriptor)
            # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "ed-fi/absenceEventCategoryDescriptors/{id}" with
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
                    "validationErrors": null,
                    "errors": null
                    }
                  """

        Scenario: 06  Put an empty request object (Descriptor)
             # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "ed-fi/absenceEventCategoryDescriptors/{id}" with
                  """
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
                        ],
                        "$.id": [
                        "id is required."
                        ]
                    },
                    "errors": []
                  }
                  """

        Scenario: 07 Put an empty JSON body (Descriptor)
             # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "ed-fi/absenceEventCategoryDescriptors/{id}" with
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
                        ],
                        "$.id": [
                        "id is required."
                        ]
                    },
                    "errors": []
                  }
                  """

        Scenario: 08 Put a document with mismatch between URL and id (Descriptor)
             # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "ed-fi/absenceEventCategoryDescriptors/{id}" with
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
                    "validationErrors": null,
                    "errors": [
                        "Request body id must match the id in the url."
                    ]
                  }
                  """

        Scenario: 09 Put a document with a blank id (Descriptor)
            # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "ed-fi/absenceEventCategoryDescriptors/{id}" with
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
                     "validationErrors": null,
                     "errors": [
                         "Request body id must match the id in the url."
                     ]
                   }
                  """

        Scenario: 10 Put a document with an invalid id format (Descriptor)
             # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "ed-fi/absenceEventCategoryDescriptors/{id}" with
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
                    "validationErrors": null,
                    "errors": [
                        "Request body id must match the id in the url."
                    ]
                  }
                  """

    Rule: Resources

        Background:
            Given the Data Management Service must receive a token issued by "http://localhost"
              And user is already authorized
              And a POST request is made to "ed-fi/educationContents" with
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
             When a PUT request is made to "ed-fi/educationContents/{id}" with
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
             When a PUT request is made to "ed-fi/educationContents/{id}" with
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
             When a PUT request is made to "ed-fi/educationContents/{id}" with
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

        Scenario: 14 Put a document that does not exist (Resource)
             # The id value should be replaced with a non existing resource
             When a PUT request is made to "ed-fi/educationContents/00000000-0000-4000-a000-000000000000" with
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
                      "validationErrors": null,
                      "errors": null
                  }
                  """

        Scenario: 15 Put a document with modification of an identity field (Resource)
            # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "ed-fi/educationContents/{id}" with
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
                    "detail": "Identifying values for the EducationContents resource cannot be changed. Delete and recreate the resource item instead.",
                    "type": "urn:ed-fi:api:bad-request:data-validation-failed:key-change-not-supported",
                    "title": "Key Change Not Supported",
                    "status": 400,
                    "correlationId": null,
                    "validationErrors": null,
                    "errors": null
                    }
                  """

        Scenario: 16  Put an empty request object (Resource)
             # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "ed-fi/educationContents/{id}" with
                  """
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
                        "$.contentIdentifier": [
                            "contentIdentifier is required."
                        ],
                        "$.namespace": [
                            "namespace is required."
                        ]
                    },
                    "errors": []
                  }
                  """

        Scenario: 17 Put an empty JSON body (Resource)
             # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "ed-fi/educationContents/{id}" with
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
                        "$.contentIdentifier": [
                            "contentIdentifier is required."
                        ],
                        "$.namespace": [
                            "namespace is required."
                        ]
                    },
                    "errors": []
                  }
                  """

        Scenario: 18 Put a document with mismatch between URL and id (Resource)
             # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "ed-fi/educationContents/{id}" with
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
                    "validationErrors": null,
                    "errors": [
                        "Request body id must match the id in the url."
                    ]
                  }
                  """

        Scenario: 19 Put a document with a blank id (Resource)
            # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "ed-fi/educationContents/{id}" with
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
                     "validationErrors": null,
                     "errors": [
                         "Request body id must match the id in the url."
                     ]
                   }
                  """

        Scenario: 20 Put a document with an invalid id format (Resource)
             # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "ed-fi/educationContents/{id}" with
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
                    "validationErrors": null,
                    "errors": [
                        "Request body id must match the id in the url."
                    ]
                  }
                  """
