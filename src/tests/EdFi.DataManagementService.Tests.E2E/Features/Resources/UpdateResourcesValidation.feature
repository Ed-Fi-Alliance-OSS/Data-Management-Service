Feature: Resources "Update" Operation validations

        Background:
            Given the Data Management Service must receive a token issued by "http://localhost"
              And user is already authorized
              And the system has these descriptors
                  | descriptorValue                                |
                  | uri://ed-fi.org/ContentClassDescriptor#Testing |

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

        Scenario: 01 Put an existing document (Resource)
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

        Scenario: 02 Put an existing document with optional properties removed (Resource)
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

        Scenario: 03 Put an existing document with an extra property (overpost) (Resource)
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

        Scenario: 04 Update a document that does not exist (Resource)
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

        Scenario: 05 Update a document with modification of an identity field (Resource)
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

        Scenario: 06  Put an empty request object (Resource)
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

        Scenario: 07 Put an empty JSON body (Resource)
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

        Scenario: 08 Update a document with mismatch between URL and id (Resource)
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

        Scenario: 09 Update a document with a blank id (Resource)
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

        Scenario: 10 Update a document with an invalid id format (Resource)
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

        Scenario: 11 Put an existing document with string coercion to a numeric value (Resource)
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

        Scenario: 12 Put an existing document with null optional value (Resource)
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

        Scenario: 13 Put an existing document with a string that is too long (Resource)
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
        Scenario: 14 Update a document with a value that is too short (Resource)
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

        Scenario: 15 Update a document with a duplicated value (Resource)
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

        Scenario: 16 Verify clients cannot update a resource with a duplicate descriptor
             When a PUT request is made to "/ed-fi/schools/{id}" with
                  """
                  {
                      "id": "{id}",
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

        Scenario: 17 Verify clients cannot upadate a resource with a duplicate resource reference
             When a PUT request is made to "/ed-fi/bellschedules/{id}" with
                  """
                  {
                      "id": "{id}",
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

        Scenario: 18 Verify clients can update the identity of resources that allow identity updates
            Given the system has these "schools"
                  | schoolId | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 4003     | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |

            Given the system has these "classPeriods"
                  | schoolReference    | classPeriodName |
                  | {"schoolId": 4003} | "first period"  |

            Given the system has these "bellSchedules"
                  | schoolReference    | classPeriods                                                                         | bellScheduleName    |
                  | {"schoolId": 4003} | [ { "classPeriodReference": {"classPeriodName": "first period", "schoolId": 155} } ] | "Saved By The Bell" |

             # classPeriodName is part of the identity of a classPeriod
             When a PUT request is made to "/ed-fi/classPeriods/{id}" with
                  """
                     {
                         "id": "{id}",
                         "classPeriodName": "second period",
                         "schoolReference": {
                             "schoolId": 4003
                         }
                     }
                  """
             Then it should respond with 204
              And the record can be retrieved with a GET request
                  """
                  {
                      "id": "{id}",
                      "classPeriodName": "second period",
                      "schoolReference": {
                          "schoolId": 4003
                      }
                  }
                  """
