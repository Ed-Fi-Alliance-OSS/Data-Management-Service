Feature: Resources "Create" Operation validations

        Background:
            Given the Data Management Service must receive a token issued by "http://localhost"
              And user is already authorized

    Rule: Descriptors

        Scenario: 01 Post a valid document (Descriptor)
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

        # Descriptors are not validating properly. DMS-295
        @ignore
        Scenario: 02 Create a document with a string that is too long (Descriptor)
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
                    "validationErrors": {
                        "$.codeValue": [
                            "codeValue Value should be at most 50 characters"
                        ]
                    },
                    "errors": [],
                    "detail": "Data validation failed. See 'validationErrors' for details.",
                    "type": "urn:ed-fi:api:bad-request:data",
                    "title": "Data Validation Failed",
                    "status": 400,
                    "correlationId": null
                  }
                  """

        Scenario: 03 Create a document that is missing a required property (Descriptor)
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

        # Ignored because we do not have namespace security for descriptors yet. DMS-81
        @ignore
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
                        "detail": "Access to the resource could not be authorized. The 'Namespace' value of the resource does not start with any of the caller's associated namespace prefixes ('uri://ed-fi.org', 'uri://gbisd.org', 'uri://tpdm.ed-fi.org').",
                        "type": "urn:ed-fi:api:security:authorization:namespace:access-denied:namespace-mismatch",
                        "title": "Authorization Denied",
                        "status": 403,
                        "correlationId": null
                    }
                  """

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

        # Descriptors are not validating properly. DMS-295
        @ignore
        Scenario: 06 Create a document with spaces in required fields (Descriptor)
             When a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  """
                    {
                        "codeValue": "                      ",
                        "description": "                    ",
                        "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                        "shortDescription": "                    "
                    }
                  """
             Then it should respond with 400
              And the response body is
                  """{
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
                    "type": "urn:ed-fi:api:bad-request:data",
                    "title": "Data Validation Failed",
                    "status": 400,
                    "correlationId": null
                  }
                  """

        # Descriptors are not validating properly. DMS-295
        @ignore
        Scenario: 07 Create a document with leading spaces in required fields (Descriptor)
             When a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  """
                    {
                        "codeValue": "                      a",
                        "description": "                    a",
                        "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                        "shortDescription": "                   a"
                    }
                  """
             Then it should respond with 400
              And the response body is
                  """{
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
                    "type": "urn:ed-fi:api:bad-request:data",
                    "title": "Data Validation Failed",
                    "status": 400,
                    "correlationId": null
                  }
                  """

        # Descriptors are not validating the whitespace yet. DMS-295
        @ignore
        Scenario: 08 Create a document with trailing spaces in required fields (Descriptor)
             When a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  """
                    {
                        "codeValue": "a                      ",
                        "description": "a                    ",
                        "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                        "shortDescription": "a                   "
                    }
                  """
             Then it should respond with 400
              And the response body is
                  """{
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
                    "type": "urn:ed-fi:api:bad-request:data",
                    "title": "Data Validation Failed",
                    "status": 400,
                    "correlationId": null
                  }
                  """

        Scenario: 09 Post a new document with optional fields (Descriptor)
            Given a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  """
                    {
                        "codeValue": "Sick Leave",
                        "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                        "shortDescription": "Sick Leave",
                        "effectiveBeginDate": "2024-07-22",
                        "effectiveEndDate": "2024-07-22"
                    }
                  """
             Then it should respond with 200
              And the record can be retrieved with a GET request
                  """
                        {
                            "id": "{id}",
                            "codeValue": "Sick Leave",
                            "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                            "shortDescription": "Sick Leave",
                            "effectiveBeginDate": "2024-07-22",
                            "effectiveEndDate": "2024-07-22"
                        }
                  """

        Scenario: 10 Post a new document with an extra property (overpost) (Descriptor)
            Given a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  """
                    {
                        "codeValue": "Sick Leave",
                        "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                        "shortDescription": "Sick Leave",
                        "effectiveBeginDate": "2024-07-22",
                        "effectiveEndDate": "2024-07-22",
                        "objectOverpost": {
                            "x": 1
                        }
                    }
                  """
             Then it should respond with 200
              And the record can be retrieved with a GET request
                  """
                        {
                            "id": "{id}",
                            "codeValue": "Sick Leave",
                            "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                            "shortDescription": "Sick Leave",
                            "effectiveBeginDate": "2024-07-22",
                            "effectiveEndDate": "2024-07-22"
                        }
                  """

        Scenario: 11 Post a new document with trailing comma (Descriptor)
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
              And the record can be retrieved with a GET request
                  """
                  {
                    "validationErrors": {
                        "$.": [
                        "The JSON object contains a trailing comma at the end which is not supported in this mode. Change the reader options. LineNumber: 6 | BytePositionInLine: 20."
                        ]
                    },
                    "errors": [],
                    "detail": "Data validation failed. See 'validationErrors' for details.",
                    "type": "urn:ed-fi:api:bad-request:data",
                    "title": "Data Validation Failed",
                    "status": 400,
                    "correlationId": null
                  }
                  """

        # Descriptors are not validating properly. DMS-295
        @ignore
        Scenario: 12 Create a document with id property (Descriptor)
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
                        "type": "urn:ed-fi:api:bad-request:data",
                        "title": "Data Validation Failed",
                        "status": 400,
                        "correlationId": null,
                        "errors": [
                            "Resource identifiers cannot be assigned by the client. The 'id' property should not be included in the request body."
                        ]
                    }
                  """

        Scenario: 13 Post a new document with required fields only (Descriptor)
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

        Scenario: 14 Create a document with a required, non-identity, property's value containing leading and trailing white spaces (Descriptor)
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

        Scenario: 15 Create a document with optional property's value containing only white spaces (Descriptor)
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
             Then it should respond with 200
              And the record can be retrieved with a GET request
                  """
                    {
                        "id": "{id}",
                        "codeValueSSSSS": "SL",
                        "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                        "shortDescription": "Sick Leave",
                        "description": "    "
                    }
                  """

        Scenario: 16 Post an existing document without changes (Descriptor)
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

        # DMS-297
        # Not sure yet what the expected response should be. Depends on what the
        # JSON schema can do.
        @ignore
        Scenario: 16.1 Create a document with duplicate properties (Descriptor)
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
                    "detail": "The request could not be processed. See 'errors' for details.",
                    "type": "urn:ed-fi:api:bad-request",
                    "title": "Bad Request",
                    "status": 400,
                    "correlationId": null,
                    "validationErrors": {
                       "$.shortDescription": [
                         "shortDescription value occurs twice"
                       ]
                     },
                     "errors": []
                  }
                  """

    Rule: Resources

        Scenario: 17 Post an empty request object (Resource)
             When a POST request is made to "/ed-fi/schools" with
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
                    "validationErrors": {},
                    "errors": ["A non-empty request body is required."]
                  }
                  """

        Scenario: 18 Post using an empty JSON body (Resource)
             When a POST request is made to "/ed-fi/academicWeeks" with
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
                        "$.weekIdentifier": [
                          "weekIdentifier is required."
                        ],
                        "$.schoolReference": [
                          "schoolReference is required."
                        ],
                        "$.beginDate": [
                          "beginDate is required."
                        ],
                        "$.endDate": [
                          "endDate is required."
                        ],
                        "$.totalInstructionalDays": [
                          "totalInstructionalDays is required."
                        ]
                      },
                      "errors": []
                    }
                  """

        Scenario: 19 Create a document with spaces in required fields (Resource)
             When a POST request is made to "/ed-fi/academicWeeks" with
                  """
                    {
                        "weekIdentifier": "             ",
                        "schoolReference": {
                            "schoolId": 255901
                        },
                        "beginDate": "2024-07-10",
                        "endDate": "2024-07-30",
                        "totalInstructionalDays": 20
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
                         "$.weekIdentifier": [
                             "weekIdentifier cannot contain leading or trailing spaces."
                         ]
                     },
                     "errors": []
                    }
                  """

        Scenario: 20 Create a document with leading spaces in required fields (Resource)
             When a POST request is made to "/ed-fi/academicWeeks" with
                  """
                    {
                        "weekIdentifier": "             a",
                        "schoolReference": {
                        "schoolId": 255901
                        },
                        "beginDate": "2024-07-10",
                        "endDate": "2024-07-30",
                        "totalInstructionalDays": 20
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
                         "$.weekIdentifier": [
                             "weekIdentifier cannot contain leading or trailing spaces."
                         ]
                     },
                     "errors": []
                    }
                  """

        Scenario: 21 Create a document with trailing spaces in required fields (Resource)
             When a POST request is made to "/ed-fi/academicWeeks" with
                  """
                    {
                        "weekIdentifier": "a             ",
                        "schoolReference": {
                        "schoolId": 255901
                        },
                        "beginDate": "2024-07-10",
                        "endDate": "2024-07-30",
                        "totalInstructionalDays": 20
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
                         "$.weekIdentifier": [
                             "weekIdentifier cannot contain leading or trailing spaces."
                         ]
                     },
                     "errors": []
                    }
                  """


        Scenario: 22 Post an invalid document missing a comma (Resource)
             When a POST request is made to "/ed-fi/academicWeeks" with
                  """
                  {
                    "weekIdentifier": "abcdef",
                    "schoolReference": {
                        "schoolId": 255901001
                    }
                    "beginDate": "2024-04-04",
                    "endDate": "2024-04-04",
                    "totalInstructionalDays": 300
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
                        "$.": [
                            "'\"' is invalid after a value. Expected either ',', '}', or ']'. LineNumber: 8 | BytePositionInLine: 13."
                        ]
                    },
                    "errors": []
                  }
                  """

        Scenario: 23 Create a document with a required, non-identity, property's value containing leading and trailing white spaces (Resource)
             When a POST request is made to "/ed-fi/students" with
                  """
                    {
                        "studentUniqueId":"8989",
                        "birthDate":  "2017-08-23",
                        "firstName": "first name",
                        "lastSurname": "      last name            "
                    }
                  """
             Then it should respond with 201

        Scenario: 24 Create a document with optional property's value containing only white spaces (Resource)
             When a POST request is made to "/ed-fi/students" with
                  """
                    {
                        "studentUniqueId":"8989",
                        "birthDate":  "2017-08-23",
                        "firstName": "first name",
                        "lastSurname": "last name",
                        "middleName": "                        "
                    }
                  """
             # 200 because this is updating the document stored with the scenario above.
             Then it should respond with 200

        # We're treating `id` as an overpost, when it should be rejected. DMS-300
        @ignore
        Scenario: 25 Create a document with id property (Resource)
            # The ID used does not need to exist: any ID is invalid here
             When a POST request is made to "/ed-fi/academicWeeks" with
                  """
                  {
                    "id": "3a93cdce-157d-4cfe-b6f8-d5caa88c986b",
                    "weekIdentifier": "abcdef",
                    "schoolReference": {
                        "schoolId": 255901001
                    },
                    "beginDate": "2024-04-04",
                    "endDate": "2024-04-04",
                    "totalInstructionalDays": 300
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

        Scenario: 26 Create a document with an extra property (overpost) (Resource)
             When a POST request is made to "/ed-fi/educationContents" with
                  """
                  {
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
             Then it should respond with 201
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

        Scenario: 27 Post an numeric and boolean fields as strings are coerced (Resource)
                  # In this example schoolId is numeric and doNotPublishIndicator are boolean, yet posted in quotes as strings
                  # In the GET request you can see they are coerced to their proper types
             When a POST request is made to "/ed-fi/schools/" with
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

        Scenario: 28 Post a request with a value that is too short (Resource)
             When a POST request is made to "/ed-fi/academicWeeks" with
                  """
                  {
                   "weekIdentifier": "one",
                   "schoolReference": {
                     "schoolId": 17012391
                   },
                   "beginDate": "2023-09-11",
                   "endDate": "2023-09-11",
                   "totalInstructionalDays": 300
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
                        "$.weekIdentifier": [
                          "weekIdentifier Value should be at least 5 characters"
                        ]
                      },
                      "errors": []
                    }
                  """

        Scenario: 29 Post a request with a value that is too long (Resource)
             When a POST request is made to "/ed-fi/academicWeeks" with
                  """
                  {
                   "weekIdentifier": "oneoneoneoneoneoneoneoneoneoneoneoneoneoneoneoneoneoneoneoneoneoneoneoneoneoneoneoneoneoneoneone",
                   "schoolReference": {
                     "schoolId": 17012391
                   },
                   "beginDate": "2023-09-11",
                   "endDate": "2023-09-11",
                   "totalInstructionalDays": 300
                  }
                  """
             Then it should respond with 400
              And the response body is
                  """
                  {
                    "validationErrors": {
                        "$.weekIdentifier": [
                        "weekIdentifier Value should be at most 80 characters"
                        ]
                    },
                    "errors": [],
                    "detail": "Data validation failed. See 'validationErrors' for details.",
                    "type": "urn:ed-fi:api:bad-request:data",
                    "title": "Data Validation Failed",
                    "status": 400,
                    "correlationId": null
                  }
                  """


        Scenario: 30 Create a document that is missing multiple required properties (Resource)
             When a POST request is made to "/ed-fi/academicWeeks" with
                  """
                  {
                    "weekIdentifier": "seven",
                    "schoolReference": {
                        "__schoolId": 999
                    },
                    "beginDate": "2023-09-11",
                    "endDate": "2023-09-11",
                    "__totalInstructionalDays": 10
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
                        "$.totalInstructionalDays": [
                            "totalInstructionalDays is required."
                        ],
                        "$.schoolReference.schoolId": [
                            "schoolId is required."
                        ]
                        },
                        "errors": []
                    }
                  """

        Scenario: 31 Post a new document (Resource)
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
             # Was already created somewhere above
             Then it should respond with 200


        # DMS-297
        # Not sure yet what the expected response should be. Depends on what the
        # JSON schema can do.
        @ignore
        Scenario: 24 Post a request with a duplicated value (Resource)
             When a POST request is made to "/ed-fi/educationContents" with
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
                      "type": "urn:ed-fi:api:bad-request:data",
                      "title": "Data Validation Failed",
                      "status": 400,
                      "correlationId": null,
                      "validationErrors": {
                        "$.learningResourceMetadataURI": [
                          "learningResourceMetadataURI value occurs twice"
                        ]
                      },
                      "errors": []
                    }
                  """
