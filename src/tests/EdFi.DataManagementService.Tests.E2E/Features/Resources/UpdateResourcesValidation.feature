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

        @API-184
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

        @API-185
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

        @API-186
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

        @API-187
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

        @API-188
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

        @API-189
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

        @API-190
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

        @API-191
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

        @API-192
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

        @API-193
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

        @API-194
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

        @API-195
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

        @API-196
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

        @API-197
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
                      "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                      "title": "Data Validation Failed",
                      "status": 400,
                      "correlationId": null,
                      "validationErrors": {
                        "$.learningResourceMetadataURI": [
                          "learningResourceMetadataURI Value should be at least 5 characters"
                        ],
                        "$.publisher": [
                          "publisher Value should be at most 50 characters"
                        ]
                      },
                      "errors": []
                    }
                  """

        @API-198
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

        @API-199
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

        @API-200
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

        @API-201
        Scenario: 18 Verify clients can update the identity of resources that allow identity updates
            Given the system has these "schools"
                  | schoolId | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 4003     | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |

             When a POST request is made to "/ed-fi/classPeriods/" with
                  """
                    {
                        "classPeriodName": "first period",
                        "schoolReference": {
                            "schoolId": 4003
                        }
                    }
                  """
             Then it should respond with 201
              And the record can be retrieved with a GET request
                  """
                  {
                    "id": "{id}",
                    "classPeriodName": "first period",
                    "schoolReference": {
                        "schoolId": 4003
                    }
                  }
                  """
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

        @API-202
        Scenario: 19 Verify cascading updates on non reference values
            Given the system has these "schools"
                  | schoolId | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 4003     | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
            Given the system has these "schoolYearTypes"
                  | schoolYear | schoolYearDescription | currentSchoolYear |
                  | 2025       | "2025"                | false             |
            Given the system has these descriptors
                  | descriptorValue                                          |
                  | uri://ed-fi.org/CourseIdentificationSystemDescriptor#LEA |
                  | uri://ed-fi.org/TermDescriptor#Quarter                   |
            Given the system has these "courses"
                  | educationOrganizationReference    | courseCode | courseTitle    | numberOfParts | identificationCodes                                                                                                                       |
                  | {"educationOrganizationId": 4003} | "ART-01"   | "Art, Grade 1" | 1             | [ {"courseIdentificationSystemDescriptor": "uri://ed-fi.org/CourseIdentificationSystemDescriptor#LEA", "identificationCode": "ART-01" } ] |
             When a POST request is made for dependent resource "/ed-fi/sessions/" with
                  """
                  {
                    "endDate": "2025-03-31",
                    "beginDate": "2025-01-01",
                    "sessionName": "Third Quarter",
                    "termDescriptor": "uri://ed-fi.org/TermDescriptor#Quarter",
                    "schoolReference": {
                        "schoolId": 4003
                    },
                    "totalInstructionalDays": 45,
                    "schoolYearTypeReference": {
                        "schoolYear": 2025
                    }
                  }
                  """
             Then it should respond with 200 or 201
              And the record can be retrieved with a GET request
                  """
                  {
                    "id": "{dependentId}",
                    "endDate": "2025-03-31",
                    "beginDate": "2025-01-01",
                    "sessionName": "Third Quarter",
                    "termDescriptor": "uri://ed-fi.org/TermDescriptor#Quarter",
                    "schoolReference": {
                        "schoolId": 4003
                    },
                    "totalInstructionalDays": 45,
                    "schoolYearTypeReference": {
                        "schoolYear": 2025
                    }
                  }
                  """
             When a POST request is made to "/ed-fi/courseOfferings/" with
                  """
                  {
                    "localCourseCode": "abc",
                    "schoolReference": {
                        "schoolId": 4003
                    },
                    "sessionReference": {
                        "schoolYear": 2025,
                        "sessionName": "Third Quarter",
                        "schoolId": 4003
                    },
                    "courseReference": {
                        "courseCode": "ART-01",
                        "educationOrganizationId": 4003
                    }
                  }
                  """
             Then it should respond with 200 or 201
             # Change the sessionName
             When a PUT request is made to "/ed-fi/sessions/{dependentId}" with
                  """
                  {
                    "id": "{dependentId}",
                    "endDate": "2025-03-31",
                    "beginDate": "2025-01-01",
                    "sessionName": "Fourth Quarter",
                    "termDescriptor": "uri://ed-fi.org/TermDescriptor#Quarter",
                    "schoolReference": {
                        "schoolId": 4003
                    },
                    "totalInstructionalDays": 45,
                    "schoolYearTypeReference": {
                        "schoolYear": 2025
                    }
                  }
                  """
             Then it should respond with 204
             When a GET request is made to "/ed-fi/courseOfferings/{id}"
             Then it should respond with 200
             # The new sessionName should cascade to this entity
              And the response body is
                  """
                  {
                    "id": "{id}",
                    "courseReference": {
                        "courseCode": "ART-01",
                        "educationOrganizationId": 4003
                    },
                    "localCourseCode": "abc",
                    "schoolReference": {
                        "schoolId": 4003
                    },
                    "sessionReference": {
                        "schoolId": 4003,
                        "schoolYear": 2025,
                        "sessionName": "Fourth Quarter"
                    }
                  }
                  """

        @API-203
        Scenario: 20 Verify recursive cascading updates on non reference values
            Given the system has these "schools"
                  | schoolId | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 4003     | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
            Given the system has these "schoolYearTypes"
                  | schoolYear | schoolYearDescription | currentSchoolYear |
                  | 2025       | "2025"                | false             |
            Given the system has these descriptors
                  | descriptorValue                                          |
                  | uri://ed-fi.org/CourseIdentificationSystemDescriptor#LEA |
                  | uri://ed-fi.org/TermDescriptor#Quarter                   |
            Given the system has these "courses"
                  | educationOrganizationReference    | courseCode | courseTitle    | numberOfParts | identificationCodes                                                                                                                       |
                  | {"educationOrganizationId": 4003} | "ART-01"   | "Art, Grade 1" | 1             | [ {"courseIdentificationSystemDescriptor": "uri://ed-fi.org/CourseIdentificationSystemDescriptor#LEA", "identificationCode": "ART-01" } ] |
             When a POST request is made for dependent resource "/ed-fi/sessions/" with
                  """
                  {
                    "endDate": "2025-03-31",
                    "beginDate": "2025-01-01",
                    "sessionName": "Q1",
                    "termDescriptor": "uri://ed-fi.org/TermDescriptor#Quarter",
                    "schoolReference": {
                        "schoolId": 4003
                    },
                    "totalInstructionalDays": 45,
                    "schoolYearTypeReference": {
                        "schoolYear": 2025
                    }
                  }
                  """
             Then it should respond with 200 or 201
              And the record can be retrieved with a GET request
                  """
                  {
                    "id": "{dependentId}",
                    "endDate": "2025-03-31",
                    "beginDate": "2025-01-01",
                    "sessionName": "Q1",
                    "termDescriptor": "uri://ed-fi.org/TermDescriptor#Quarter",
                    "schoolReference": {
                        "schoolId": 4003
                    },
                    "totalInstructionalDays": 45,
                    "schoolYearTypeReference": {
                        "schoolYear": 2025
                    }
                  }
                  """
             When a POST request is made to "/ed-fi/courseOfferings/" with
                  """
                  {
                    "localCourseCode": "abc",
                    "schoolReference": {
                        "schoolId": 4003
                    },
                    "sessionReference": {
                        "schoolYear": 2025,
                        "sessionName": "Q1",
                        "schoolId": 4003
                    },
                    "courseReference": {
                        "courseCode": "ART-01",
                        "educationOrganizationId": 4003
                    }
                  }
                  """
             Then it should respond with 200 or 201
             When a POST request is made to "/ed-fi/sections/" with
                  """
                  {
                    "sectionIdentifier": "SECTION ABC",
                    "courseOfferingReference": {
                        "localCourseCode": "abc",
                        "schoolId": 4003,
                        "schoolYear": 2025,
                        "sessionName": "Q1"
                    }
                  }
                  """
             Then it should respond with 201
             # Change the sessionName
             When a PUT request is made to "/ed-fi/sessions/{dependentId}" with
                  """
                  {
                    "id": "{dependentId}",
                    "endDate": "2025-03-31",
                    "beginDate": "2025-01-01",
                    "sessionName": "Q2",
                    "termDescriptor": "uri://ed-fi.org/TermDescriptor#Quarter",
                    "schoolReference": {
                        "schoolId": 4003
                    },
                    "totalInstructionalDays": 45,
                    "schoolYearTypeReference": {
                        "schoolYear": 2025
                    }
                  }
                  """
             Then it should respond with 204
             When a GET request is made to "/ed-fi/sections/{id}"
             Then it should respond with 200
             # The new sessionName should cascade to this entity which is 2 levels away from session
             # session -> courseOffering -> section
              And the response body is
                  """
                  {
                    "id": "{id}",
                    "sectionIdentifier": "SECTION ABC",
                    "courseOfferingReference": {
                        "localCourseCode": "abc",
                        "schoolId": 4003,
                        "schoolYear": 2025,
                        "sessionName": "Q2"
                    }
                  }
                  """

        @API-204 @ignore
        Scenario: 21 Verify cascading updates on dependent resources in lists
            Given the system has these "schools"
                  | schoolId | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 4003     | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
             When a POST request is made for dependent resource "/ed-fi/classPeriods/" with
                  """
                  {
                     "classPeriodName": "Third Period",
                     "schoolReference": {
                         "schoolId": 4003
                     }
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/bellSchedules/" with
                  """
                  {
                    "bellScheduleName": "Schedule 1",
                    "classPeriods": [
                        {
                            "classPeriodReference": {
                                "classPeriodName": "Third Period",
                                "schoolId": 4003
                            }
                        }
                    ],
                    "schoolReference": {
                        "schoolId": 4003
                    }
                  }
                  """
             Then it should respond with 201
             # Change classPeriodName
             When a PUT request is made to "/ed-fi/classPeriods/{dependentId}" with
                  """
                  {
                    "id": "{dependentId}",
                     "classPeriodName": "Fourth Period",
                     "schoolReference": {
                         "schoolId": 4003
                     }
                  }
                  """
             Then it should respond with 204
              And the record can be retrieved with a GET request
                  """
                  {
                      "id": "{dependentId}",
                      "classPeriodName": "Fourth Period",
                      "schoolReference": {
                          "schoolId": 4003
                      }
                  }
                  """
             When a GET request is made to "/ed-fi/bellSchedules/{id}"
             Then it should respond with 200
             # The new classPeriodName should cascade to the array element
              And the response body is
                  """
                  {
                    "id": "{id}",
                    "bellScheduleName": "Schedule 1",
                    "classPeriods": [
                        {
                            "classPeriodReference": {
                                "classPeriodName": "Fourth Period",
                                "schoolId": 4003
                            }
                        }
                    ],
                    "schoolReference": {
                        "schoolId": 4003
                    }
                  }
                  """




