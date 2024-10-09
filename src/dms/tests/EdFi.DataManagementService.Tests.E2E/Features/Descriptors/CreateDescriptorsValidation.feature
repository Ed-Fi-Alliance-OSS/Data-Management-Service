Feature: Create a Descriptor

        Background:
            Given the Data Management Service must receive a token issued by "http://localhost"
              And user is already authorized

        @API-006
        Scenario: 01 Ensure clients can create a descriptor
             When a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
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
             Then it should respond with 201
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

        @API-007
        Scenario: 02 Ensure clients cannot create a descriptor using a value that is too long
             When a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  """
                  {
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
                    "detail": "Data validation failed. See 'validationErrors' for details.",
                    "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                    "title": "Data Validation Failed",
                    "status": 400,
                    "correlationId": null,
                    "validationErrors": {
                        "$.codeValue": [
                        "codeValue Value should be at most 50 characters"
                        ]
                    },
                    "errors": []
                    }
                  """

        @API-008
        Scenario: 03 Ensure clients cannot create a descriptor omitting any of the required values
             When a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  """
                    {
                        "__codeValue": "will be ignored",
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
                      "type": "urn:ed-fi:api:bad-request:data-validation-failed",
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

        # Ignored because we do not have namespace security for descriptors yet. DMS-81
        @API-009 @ignore
        Scenario: 04 Post a Descriptor using an invalid namespace
             When a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
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
                        "detail": "Access to the resource could not be authorized. The 'Namespace' value of the resource does not start with any of the caller's associated namespace prefixes ('uri://ed-fi.org').",
                        "type": "urn:ed-fi:api:security:authorization:namespace:access-denied:namespace-mismatch",
                        "title": "Authorization Denied",
                        "status": 403,
                        "correlationId": null
                    }
                  """

        @API-010
        Scenario: 05 Post using an empty JSON body
             When a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
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
                        ]
                      },
                      "errors": []
                    }
                  """

        @API-011
        Scenario: 06 Ensure clients cannot create a descriptor only using spaces for the required attributes
             When a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  """
                    {
                        "codeValue": "                      ",
                        "namespace": "  ",
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

        @API-012
        Scenario: 07 Ensure clients cannot create a descriptor with leading spaces in required attributes
             When a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  """
                    {
                        "codeValue": "                      a",
                        "description": "                    a",
                        "namespace": "   uri://ed-fi.org/AbsenceEventCategoryDescriptor",
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

        @API-013
        Scenario: 08 Ensure clients cannot create a descriptor with trailing spaces in required attributes
             When a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  """
                    {
                        "codeValue": "a                      ",
                        "description": "a                    ",
                        "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor   ",
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

        @API-014
        Scenario: 09 Post a new descriptor with an extra property (overpost)
            Given a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  """
                    {
                        "codeValue": "Sick Leave2",
                        "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                        "shortDescription": "Sick Leave2",
                        "effectiveBeginDate": "2024-07-22",
                        "effectiveEndDate": "2024-07-22",
                        "objectOverpost": {
                            "x": 1
                        }
                    }
                  """
             Then it should respond with 201
              And the record can be retrieved with a GET request
                  """
                        {
                            "id": "{id}",
                            "codeValue": "Sick Leave2",
                            "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                            "shortDescription": "Sick Leave2",
                            "effectiveBeginDate": "2024-07-22",
                            "effectiveEndDate": "2024-07-22"
                        }
                  """

        @API-015
        Scenario: 10 Post a new descriptor with invalid JSON (trailing comma)
            Given a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  """
                    {
                        "codeValue": "Sick Leave",
                        "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                        "shortDescription": "Sick Leave",
                        "effectiveBeginDate": "2024-07-22",
                        "effectiveEndDate": "2024-07-22",
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
                        "$.": [
                        "The JSON object contains a trailing comma at the end which is not supported in this mode. Change the reader options. LineNumber: 6 | BytePositionInLine: 2."
                        ]
                    },
                    "errors": []
                  }
                  """

        @API-016
        Scenario: 11 Create a descriptor with forbidden id property
            # The ID used does not need to exist: any ID is invalid here
             When a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  """
                    {
                        "id": "3a93cdce-157d-4cfe-b6f8-d5caa88c986b",
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
                    "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                    "title": "Data Validation Failed",
                    "status": 400,
                    "correlationId": null,
                    "validationErrors":{},
                    "errors": [
                      "Resource identifiers cannot be assigned by the client. The 'id' property should not be included in the request body."
                    ]
                  }
                  """

        @API-017
        Scenario: 12 Post a new descriptor with required attributes only
            Given a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  """
                    {
                        "codeValue": "SL",
                        "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                        "shortDescription": "Sick Leave"
                    }
                  """
             Then it should respond with 201
              And the record can be retrieved with a GET request
                  """
                        {
                            "id": "{id}",
                            "codeValue": "SL",
                            "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                            "shortDescription": "Sick Leave"
                        }
                  """

        @API-018
        Scenario: 13 Create a descriptor with a required, non-identity, property's value containing leading and trailing white spaces
             When a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  """
                    {
                        "codeValue": "SL2",
                        "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                        "shortDescription": "Sick Leave2",
                        "description": "  aa  "
                    }
                  """
             Then it should respond with 201

        @API-019
        Scenario: 14 Create a descriptor with optional property's value containing only white spaces
             When a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  """
                    {
                        "codeValue": "SL",
                        "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                        "shortDescription": "Sick Leave",
                        "description": "    "
                    }
                  """
             # 200 because this is updating a document created above.
             Then it should respond with 200 or 201
              And the record can be retrieved with a GET request
                  """
                    {
                        "id": "{id}",
                        "codeValue": "SL",
                        "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                        "shortDescription": "Sick Leave",
                        "description": "    "
                    }
                  """

        @API-020
        Scenario: 15 Post an existing descriptor without changes
            Given a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  """
                    {
                        "codeValue": "Sick Leave",
                        "description": "Sick Leave",
                        "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                        "shortDescription": "Sick Leave"
                    }
                  """
             When a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  """
                    {
                        "codeValue": "Sick Leave",
                        "description": "Sick Leave",
                        "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                        "shortDescription": "Sick Leave"
                    }
                  """
             Then it should respond with 200
              And the record can be retrieved with a GET request
                  """
                        {
                            "id": "{id}",
                            "codeValue": "Sick Leave",
                            "description": "Sick Leave",
                            "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                            "shortDescription": "Sick Leave"
                        }
                  """

        @API-021
        Scenario: 16 Create a descriptor with duplicate properties
             # The id value should be replaced with the resource created in the Background section
             When a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  """
                  {
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
                    "validationErrors": {
                      "$.shortDescription": [
                        "An item with the same key has already been added."
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
