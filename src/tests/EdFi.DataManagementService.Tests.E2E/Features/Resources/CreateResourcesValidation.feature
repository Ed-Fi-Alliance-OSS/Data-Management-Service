Feature: Resources "Create" Operation validations

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

    Rule: Resources

        @API-152 @POST
        Scenario: 01 Post an empty request object (Resource)
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

        @API-153 @POST
        Scenario: 02 Post using an empty JSON body (Resource)
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
                      "type": "urn:ed-fi:api:bad-request:data-validation-failed",
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

        @API-154 @POST
        Scenario: 03 Create a document with spaces in identity fields (Resource)
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
                     "type": "urn:ed-fi:api:bad-request:data-validation-failed",
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

        @API-155 @POST
        Scenario: 04 Create a document with leading spaces in identity fields (Resource)
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
                     "type": "urn:ed-fi:api:bad-request:data-validation-failed",
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

        @API-156 @POST
        Scenario: 05 Create a document with trailing spaces in identity fields (Resource)
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
                     "type": "urn:ed-fi:api:bad-request:data-validation-failed",
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

        @API-157 @POST
        Scenario: 07 Create a document with a required, non-identity, property's value containing leading and trailing white spaces (Resource)
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

        @API-158 @POST
        Scenario: 08 Create a document with optional property's value containing only white spaces (Resource)
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

        @API-159 @POST
        Scenario: 09 Create a document with id property (Resource)
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
                        "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                        "title": "Data Validation Failed",
                        "status": 400,
                        "correlationId": null,
                        "errors": [
                            "Resource identifiers cannot be assigned by the client. The 'id' property should not be included in the request body."
                        ],
                        "validationErrors": {}
                    }
                  """

        @API-160 @POST
        Scenario: 10 Create a document with an extra property (overpost) (Resource)
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

        @API-161 @POST
        Scenario: 11 Create a document with an null optional property (Resource)
             When a POST request is made to "/ed-fi/educationContents" with
                  """
                  {
                    "contentIdentifier": "Testing1",
                    "namespace": "Testing1",
                    "shortDescription": "Testing1",
                    "contentClassDescriptor": "uri://ed-fi.org/ContentClassDescriptor#Testing",
                    "learningResourceMetadataURI": "Testing1",
                    "cost": null
                  }
                  """
             Then it should respond with 201
              And the record can be retrieved with a GET request
                  """
                  {
                    "id": "{id}",
                    "contentIdentifier": "Testing1",
                    "namespace": "Testing1",
                    "shortDescription": "Testing1",
                    "contentClassDescriptor": "uri://ed-fi.org/ContentClassDescriptor#Testing",
                    "learningResourceMetadataURI": "Testing1"
                  }
                  """

        @API-162 @POST
        Scenario: 12 Post an numeric and boolean fields as strings are coerced (Resource)
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

        @API-163 @POST
        Scenario: 13 Post a request with a value that is too short (Resource)
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
                      "type": "urn:ed-fi:api:bad-request:data-validation-failed",
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

        @API-164 @POST
        Scenario: 14 Post a request with a value that is too long (Resource)
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
                    "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                    "title": "Data Validation Failed",
                    "status": 400,
                    "correlationId": null
                  }
                  """

        @API-165 @POST
        Scenario: 15 Create a document that is missing multiple required properties (Resource)
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
                        "type": "urn:ed-fi:api:bad-request:data-validation-failed",
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

        @API-166 @POST
        Scenario: 16 Post a new document (Resource)
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

        @API-167 @POST
        Scenario: 17 Post a request with a duplicated value (Resource)
             When a POST request is made to "/ed-fi/educationContents" with
                  """
                  {
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

        @API-168 @POST
        Scenario: 18 Create a document with empty value in identity fields (Resource)
             When a POST request is made to "/ed-fi/students" with
                  """
                   {
                        "studentUniqueId":"",
                        "birthDate": "2016-08-07",
                        "firstName": "firstName    ",
                        "lastSurname": "lastSurname"
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
                         "$.studentUniqueId": [
                             "studentUniqueId is required and should not be left empty."
                         ]
                     },
                     "errors": []
                    }
                  """

        @API-169 @POST
        Scenario: 19 Create a document with leading and trailing spaces in required fields (Resource)
             When a POST request is made to "/ed-fi/students" with
                  """
                   {
                        "studentUniqueId":"87878787",
                        "birthDate": "2016-08-07",
                        "firstName": "    firstName    ",
                        "lastSurname": "lastSurname"
                   }
                  """
             Then it should respond with 201 or 200

        @API-170 @POST
        Scenario: 20 Create a document with just spaces in required fields (Resource)
             When a POST request is made to "/ed-fi/students" with
                  """
                   {
                        "studentUniqueId":"878787383",
                        "birthDate": "2016-08-07",
                        "firstName": "    ",
                        "lastSurname": "lastSurname"
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
                         "$.firstName": [
                             "firstName cannot contain leading or trailing spaces."
                         ]
                     },
                     "errors": []
                    }
                  """

        @API-171 @POST
        Scenario: 21 Create a document with empty required fields (Resource)
             When a POST request is made to "/ed-fi/students" with
                  """
                   {
                        "studentUniqueId":"878787383",
                        "birthDate": "2016-08-07",
                        "firstName": "",
                        "lastSurname": "lastSurname"
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
                         "$.firstName": [
                             "firstName is required and should not be left empty."
                         ]
                     },
                     "errors": []
                    }
                  """

        @API-172 @APIConventions @POST
        Scenario: 24 Verify user can send a POST using extra fields
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "54721642126",
                      "birthDate": "2007-08-13",
                      "firstName": "John",
                      "lastSurname": "Doe",
                      "newField": "Doe",
                      "newField2": "Doe"
                  }
                  """
             Then it should respond with 201
              And the response headers includes
                  """
                    {
                        "location": "/ed-fi/students/{id}"
                    }
                  """

        @API-173 @ignore @APIConventions @POST
        Scenario: 25 Verify clients cannot POST a resource without permissions
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "54721642123",
                      "birthDate": "2007-08-13",
                      "firstName": "John",
                      "lastSurname": "Doe"
                  }
                  """
             Then it should respond with 401
              And the response body is
                  """
                  {
                      "detail": "The caller could not be authenticated.",
                      "type": "urn:ed-fi:api:security:authentication",
                      "title": "Authentication Failed",
                      "status": 401,
                      "correlationId": "4ebd1a6d-5ab2-40c8-a54b-fb8a5103c18b",
                      "errors": [
                          "Authorization header is missing."
                      ]
                  }
                  """
              And the response header contains
                  """
                  content-type: application/problem+json
                  """
            Given the user is authenticated
              And the token is expired
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "54721642123",
                      "birthDate": "2007-08-13",
                      "firstName": "John",
                      "lastSurname": "Doe"
                  }
                  """
             Then it should respond with 401
              And the response body is
                  """
                  {
                      "detail": "The caller could not be authenticated.",
                      "type": "urn:ed-fi:api:security:authentication",
                      "title": "Authentication Failed",
                      "status": 401,
                      "correlationId": "4ebd1a6d-5ab2-40c8-a54b-fb8a5103c18b",
                      "errors": [
                          "Authorization header is missing."
                      ]
                  }
                  """
              And the response header contains
                  """
                  content-type: application/problem+json
                  """

        @API-174 @APIConventions @POST
        Scenario: 26 Validate special characters values during POST action
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "54721642124",
                      "birthDate": "2007-08-13",
                      "firstName": "~!@:;?/.!{}@$:(_+#%^&*=+[>'])|FirstName\\",
                      "lastSurname": "~!@:;?/.!{}@$:(_+#%^&*=+[>'])|LastName\\"
                  }
                  """
             Then it should respond with 201
              And the response headers includes
                  """
                    {
                        "location": "/ed-fi/students/{id}"
                    }
                  """

        @API-175 @POST
        Scenario: 06 Post an invalid document missing a comma (Resource)
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
                    "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                    "title": "Data Validation Failed",
                    "status": 400,
                    "correlationId": null,
                    "validationErrors": {
                        "$.": [
                            "'\"' is invalid after a value. Expected either ',', '}', or ']'. LineNumber: 5 | BytePositionInLine: 2."
                        ]
                    },
                    "errors": []
                  }
                  """
