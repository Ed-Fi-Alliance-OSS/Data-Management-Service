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

         Scenario: 33 Create a document with empty value in identity fields (Resource)
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

          Scenario: 34 Create a document with leading and trailing spaces in required fields (Resource)
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

         Scenario: 35 Create a document with just spaces in required fields (Resource)
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

        Scenario: 36 Create a document with empty required fields (Resource)
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

        Scenario: 37 Verify clients cannot create a resource with a duplicate descriptor
            When a POST request is made to "/ed-fi/schools" with
            """
            {
                "schoolId":255901001,
                "nameOfInstitution":"School Test",
                "gradeLevels": [
                    {
                    "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Sixth grade"
                    },
                    {
                    "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Seven grade"
                    },
                    {
                    "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Seven grade"
                    },
                    {
                    "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Sixth grade"
                    }
                ],  
                "educationOrganizationCategories":[
                    {
                        "educationOrganizationCategoryDescriptor":"uri://ed-fi.org/educationOrganizationCategoryDescriptor#School"
                    }
                ]
            }
            """
            Then it should respond with 400
            And the response body is
                  """
                  {
                        "validationErrors": {
                            "$.gradeLevels[*].gradeLevelDescriptor": [
                                "The 3rd item of the gradeLevels has the same identifying values as another item earlier in the list.",
                                "The 4th item of the gradeLevels has the same identifying values as another item earlier in the list."
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

        Scenario: 38 Verify clients cannot create a resource with a duplicate resource reference
            When a POST request is made to "/ed-fi/bellschedules" with
            """
            {
                "schoolReference": {
                    "schoolId": 1
                },
                "bellScheduleName": "Test Schedule",    
                "totalInstructionalTime": 325,
                "classPeriods": [
                    {
                        "classPeriodReference": {
                            "classPeriodName": "01 - Traditional",
                            "schoolId": 1
                        }
                    },
                    {
                        "classPeriodReference": {
                            "classPeriodName": "02 - Traditional",
                            "schoolId": 1
                        }
                    },
                    {
                        "classPeriodReference": {
                            "classPeriodName": "03 - Traditional",
                            "schoolId": 1
                        }
                    },
                    {
                        "classPeriodReference": {
                            "classPeriodName": "01 - Traditional",
                            "schoolId": 1
                        }
                    },
                    {
                        "classPeriodReference": {
                            "classPeriodName": "02 - Traditional",
                            "schoolId": 1
                        }
                    }

                ],
                "dates": [],
                "gradeLevels": []
            }
            """
            Then it should respond with 400
            And the response body is
                  """
                  {
                        "validationErrors": {
                            "$.ClassPeriod": [
                                "The 4th item of the ClassPeriod has the same identifying values as another item earlier in the list.",
                                "The 5th item of the ClassPeriod has the same identifying values as another item earlier in the list."
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
