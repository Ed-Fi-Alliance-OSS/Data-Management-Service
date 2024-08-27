Feature: Update a Descriptor

    Rule: Descriptors

        Background:
            Given the Data Management Service must receive a token issued by "http://localhost"
              And user is already authorized
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

        Scenario: 01 Put an existing descriptor
            # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}" with
                  """
                  {
                    "id": "{id}",
                    "codeValue": "Sick Leave",
                    "description": "Sick Leave Edited",
                    "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                    "shortDescription": "Sick Leave Short Description",
                    "effectiveBeginDate": "2025-05-14",
                    "effectiveEndDate": "2027-05-14"
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
                    "shortDescription": "Sick Leave Short Description",
                    "effectiveBeginDate": "2025-05-14",
                    "effectiveEndDate": "2027-05-14"
                  }
                  """

        Scenario: 02 Put an existing descriptor with optional properties removed
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

        Scenario: 03 Update a descriptor with a string that is too long
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
        Scenario: 04 Put a descriptor using an invalid namespace
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
                        "detail": "Access to the resource could not be authorized. The 'Namespace' value of the resource does not start with any of the caller's associated namespace prefixes ('uri://ed-fi.org').",
                        "type": "urn:ed-fi:api:security:authorization:namespace:access-denied:namespace-mismatch",
                        "title": "Authorization Denied",
                        "status": 403,
                        "correlationId": null
                    }
                  """

        Scenario: 05 Update a descriptor with spaces in required fields
             When a PUT request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}" with
                  """
                    {
                        "id": "{id}",
                        "codeValue": "                      ",
                        "description": "                    ",
                        "namespace": "   ",
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

        Scenario: 06 Update a descriptor with leading spaces in required fields
             When a PUT request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}" with
                  """
                    {
                        "id": "{id}",
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

        Scenario: 07 Update a descriptor with trailing spaces in required fields
             When a PUT request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}" with
                  """
                    {
                        "id": "{id}",
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

        Scenario: 08 Put an existing descriptor with an extra property (overpost)
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

        Scenario: 09 Update a descriptor that does not exist
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

        Scenario: 10 Update a descriptor with modification of an identity field
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

        Scenario: 11  Put an empty request descriptor
             # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}" with
                  """
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
                    "validationErrors":{},
                    "errors": [
                        "A non-empty request body is required."
                    ]
                  }
                  """

        Scenario: 12 Put an empty JSON body
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

        Scenario: 13 Update a descriptor with mismatch between URL and id
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

        Scenario: 14 Update a descriptor with a blank id
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

        Scenario: 15 Update a descriptor with an invalid id format in the body
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

        Scenario: 16 Update a descriptor with duplicate properties
             # The id value should be replaced with the resource created in the Background section
             When a PUT request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}" with
                  """
                  {
                    "id": "invalid-id",
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

        Scenario: 17 Ensure clients cannot update a descriptor omitting any of the required values
             When a PUT request is made to "/ed-fi/disabilityDescriptors" with
                  """
                  {
                      "id": "{id}",
                      "namespace": "uri://ed-fi.org/DisabilityDescriptor",
                      "shortDescription": "Deaf-Blindness",
                      "description": "Deaf-Blindness"
                  }
                  """
             Then it should respond with 400
              And the response body is
                  """
                  {
                    "validationErrors": {
                      "$.codeValue": [
                        "codeValue is required."
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

        Scenario: 18 Ensure clients cannot update a codeValue
            Given a POST request is made to "/ed-fi/disabilityDescriptors" with
                  """
                  {
                    "codeValue": "Visual Impairment, including Blindness",
                    "namespace": "uri://ed-fi.org/DisabilityDescriptor",
                    "shortDescription": "Visual Impairment, including Blindness"
                  }
                  """
             When a PUT request is made to "/ed-fi/disabilityDescriptors/{id}" with
                  """
                  {
                      "id": "{id}",
                      "codeValue": "Visual Impairment, including Blindness Ed-Fi Test",
                      "description": "Visual Impairment, including Blindness",
                      "namespace": "uri://ed-fi.org/DisabilityDescriptor",
                      "shortDescription": "Visual Impairment, including Blindness"
                  }
                  """
             Then it should respond with 400
              And the response body is
                  """
                  {
                    "validationErrors": {},
                    "errors": [],
                    "detail": "Identifying values for the DisabilityDescriptor resource cannot be changed. Delete and recreate the resource item instead.",
                    "type": "urn:ed-fi:api:bad-request:data-validation-failed:key-change-not-supported",
                    "title": "Key Change Not Supported",
                    "status": 400,
                    "correlationId": null
                  }
                  """

        Scenario: 19 Ensure clients cannot update a namespace
            Given a POST request is made to "/ed-fi/disabilityDescriptors" with
                  """
                  {
                    "codeValue": "Visual Impairment, including Blindness",
                    "namespace": "uri://ed-fi.org/DisabilityDescriptor",
                    "shortDescription": "Visual Impairment, including Blindness"
                  }
                  """
             When a PUT request is made to "/ed-fi/disabilityDescriptors/{id}" with
                  """
                  {
                    "id": "{id}",
                    "codeValue": "Visual Impairment, including Blindness",
                    "namespace": "uri://ed-fi.org/DisabilityDescriptor__",
                    "shortDescription": "Visual Impairment, including Blindness"
                  }
                  """
             Then it should respond with 400
              And the response body is
                  """
                  {
                    "validationErrors": {},
                    "errors": [],
                    "detail": "Identifying values for the DisabilityDescriptor resource cannot be changed. Delete and recreate the resource item instead.",
                    "type": "urn:ed-fi:api:bad-request:data-validation-failed:key-change-not-supported",
                    "title": "Key Change Not Supported",
                    "status": 400,
                    "correlationId": null
                  }
                  """

        Scenario: 20 Verify response code 400 when ID is not valid
             When a PUT request is made to "/ed-fi/disabilityDescriptors/00112233445566" with
                  """
                  {
                    "id": "{id}",
                    "codeValue": "Visual Impairment, including Blindness",
                    "namespace": "uri://ed-fi.org/DisabilityDescriptor",
                    "shortDescription": "Visual Impairment, including Blindness"
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
                        "$.id": [
                            "The value '00112233445566' is not valid."
                        ]
                      },
                      "errors": []
                  }
                  """
