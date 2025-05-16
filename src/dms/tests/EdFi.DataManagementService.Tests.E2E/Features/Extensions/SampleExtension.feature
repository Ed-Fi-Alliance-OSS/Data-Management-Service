Feature: Sample extension resources

     # Update busRoutes scenarios to remove busReference fields, as described in DMS-610
    Rule: busRoutes scenarios
        Background:
            Given the claimSet "EdFiSandbox" is authorized with namespacePrefixes "uri://ed-fi.org, uri://sample.ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                           |
                  | uri://ed-fi.org/TelephoneNumberTypeDescriptor#Emergency 1 |
              And a POST request is made to "/sample/buses" with
                  """
                  {
                      "busId": "111"
                  }
                  """

        Scenario: 01 Required Field Validation Errors for busRoutes Resource
             When a POST request is made to "/sample/busRoutes" with
                  """
                  {}
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
                              "$.busReference": [
                                  "busReference is required."
                              ],
                              "$.busRouteNumber": [
                                  "busRouteNumber is required."
                              ],
                              "$.busRouteDirection": [
                                  "busRouteDirection is required."
                              ],
                              "$.startTimes": [
                                  "startTimes is required."
                              ],
                              "$.operatingCost": [
                                  "operatingCost is required."
                              ],
                              "$.expectedTransitTime": [
                                  "expectedTransitTime is required."
                              ],
                              "$.hoursPerWeek": [
                                  "hoursPerWeek is required."
                              ],
                              "$.serviceAreaPostalCodes": [
                                  "serviceAreaPostalCodes is required."
                              ],
                              "$.telephones": [
                                  "telephones is required."
                              ]
                          },
                          "errors": []
                      }
                  """

        Scenario: 02 Creating New busRoutes Resource
             When a POST request is made to "/sample/busRoutes" with
                  """
                  {
                      "busId": "111",
                      "busRouteNumber": 101,
                      "busRouteDirection": "Northbound",
                      "startTimes": [
                          {
                              "startTime": "13:01:01"
                          }
                      ],
                      "operatingCost": 5,
                      "expectedTransitTime": "60",
                      "hoursPerWeek": 60,
                      "serviceAreaPostalCodes": [
                          {
                              "serviceAreaPostalCode": "78705"
                          }
                      ],
                      "telephones": [
                          {
                              "telephoneNumber": "555-123-4567",
                              "telephoneNumberTypeDescriptor": "uri://ed-fi.org/TelephoneNumberTypeDescriptor#Emergency 1"
                          }
                      ],
                      "busReference": {
                          "busId": "111"
                      }
                  }
                  """
             Then it should respond with 201

        Scenario: 03 Get by ID for busRoutes Resource
            Given a POST request is made to "/sample/busRoutes" with
                  """
                  {
                      "busId": "111",
                      "busRouteNumber": 102,
                      "busRouteDirection": "Southbound",
                      "startTimes": [
                          {
                              "startTime": "13:01:01"
                          }
                      ],
                      "operatingCost": 5,
                      "expectedTransitTime": "60",
                      "hoursPerWeek": 60,
                      "serviceAreaPostalCodes": [
                          {
                              "serviceAreaPostalCode": "78704"
                          }
                      ],
                      "telephones": [
                          {
                              "telephoneNumber": "555-123-4567",
                              "telephoneNumberTypeDescriptor": "uri://ed-fi.org/TelephoneNumberTypeDescriptor#Emergency 1"
                          }
                      ],
                      "busReference": {
                          "busId": "111"
                      }
                  }
                  """
             When a GET request is made to "/sample/busRoutes/{id}"
             Then it should respond with 200
              And the response body is
                  """
                        {
                            "id": "{id}",
                            "startTimes": [
                                {
                                    "startTime": "13:01:01"
                                }
                            ],
                            "telephones": [
                                {
                                    "telephoneNumber": "555-123-4567",
                                    "telephoneNumberTypeDescriptor": "uri://ed-fi.org/TelephoneNumberTypeDescriptor#Emergency 1"
                                }
                            ],
                            "busReference": {
                                "busId": "111"
                            },
                            "hoursPerWeek": 60,
                            "operatingCost": 5,
                            "busRouteNumber": 102,
                            "busRouteDirection": "Southbound",
                            "expectedTransitTime": 60,
                            "serviceAreaPostalCodes": [
                                {
                                    "serviceAreaPostalCode": "78704"
                                }
                            ]
                        }
                  """

        Scenario: 04 Delete by ID for busRoutes Resource
            Given a POST request is made to "/sample/busRoutes" with
                  """
                  {
                      "busId": "111",
                      "busRouteNumber": 999,
                      "busRouteDirection": "Southbound",
                      "startTimes": [
                          {
                              "startTime": "13:01:01"
                          }
                      ],
                      "operatingCost": 5,
                      "expectedTransitTime": "60",
                      "hoursPerWeek": 60,
                      "serviceAreaPostalCodes": [
                          {
                              "serviceAreaPostalCode": "78704"
                          }
                      ],
                      "telephones": [
                          {
                              "telephoneNumber": "555-123-4567",
                              "telephoneNumberTypeDescriptor": "uri://ed-fi.org/TelephoneNumberTypeDescriptor#Emergency 1"
                          }
                      ],
                      "busReference": {
                          "busId": "111"
                      }
                  }
                  """
             When a DELETE request is made to "/sample/busRoutes/{id}"
             Then it should respond with 204

    Rule: School scenarios
        Background:
            Given the claimSet "EdFiSandbox" is authorized with namespacePrefixes "uri://ed-fi.org, uri://sample.ed-fi.org"
              And the system has these descriptors
                  | descriptorValue                                                                    |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution |
                  | uri://ed-fi.org/GradeLevelDescriptor#Postsecondary                                 |
                  | uri://ed-fi.org/CTEProgramServiceDescriptor#Architecture and Construction          |

        Scenario: 05 Existing Core Entity School and Sample Extension with Missing CTEProgramServiceDescriptor Field
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": "8",
                    "nameOfInstitution": "Extension Test Community College",
                    "educationOrganizationCategories": [
                        {
                            "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution"
                        }
                    ],
                    "gradeLevels": [
                        {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"
                        }
                    ],
                    "_ext": {
                        "sample": {
                            "isExemplary": false,
                            "cteProgramService": {}
                        }
                    }
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
                        "correlationId": "0HNBIQF263QFC:00000020",
                        "validationErrors": {
                            "$.cteProgramService.cteProgramServiceDescriptor": [
                            "cteProgramServiceDescriptor is required."
                            ]
                        },
                        "errors": []
                      }
                  """

        Scenario: 06 Existing Core Entity School and Sample Extension with CTEProgramServiceDescriptor Field
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": "8",
                    "nameOfInstitution": "Extension Test Community College",
                    "educationOrganizationCategories": [
                        {
                            "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution"
                        }
                    ],
                    "gradeLevels": [
                        {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"
                        }
                    ],
                    "_ext": {
                        "sample": {
                            "isExemplary": false,
                            "cteProgramService": {
                                "cteProgramServiceDescriptor": "uri://ed-fi.org/CTEProgramServiceDescriptor#Architecture and Construction"
                            }
                        }
                    }
                  }
                  """
             Then it should respond with 201

        Scenario: 07 Get by ID for Core Entity School and Sample Extension with CTEProgramServiceDescriptor Field
            Given a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": "8",
                    "nameOfInstitution": "Extension Test Community College",
                    "educationOrganizationCategories": [
                        {
                            "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution"
                        }
                    ],
                    "gradeLevels": [
                        {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"
                        }
                    ],
                    "_ext": {
                        "sample": {
                            "isExemplary": false,
                            "cteProgramService": {
                                "cteProgramServiceDescriptor": "uri://ed-fi.org/CTEProgramServiceDescriptor#Architecture and Construction"
                            }
                        }
                    }
                  }
                  """
             When a GET request is made to "/ed-fi/schools/{id}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                    "id": "{id}",
                    "_ext": {
                      "sample": {
                        "isExemplary": false,
                        "cteProgramService": {
                          "cteProgramServiceDescriptor": "uri://ed-fi.org/CTEProgramServiceDescriptor#Architecture and Construction"
                        }
                      }
                    },
                    "schoolId": 8,
                    "gradeLevels": [
                      {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"
                      }
                    ],
                    "nameOfInstitution": "Extension Test Community College",
                    "educationOrganizationCategories": [
                      {
                        "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution"
                      }
                    ]
                  }
                  """

        Scenario: 08 Delete by ID for busRoutes Resource
            Given a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": "8",
                    "nameOfInstitution": "Extension Test Community College",
                    "educationOrganizationCategories": [
                        {
                            "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution"
                        }
                    ],
                    "gradeLevels": [
                        {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"
                        }
                    ],
                    "_ext": {
                        "sample": {
                            "isExemplary": false,
                            "cteProgramService": {
                                "cteProgramServiceDescriptor": "uri://ed-fi.org/CTEProgramServiceDescriptor#Architecture and Construction"
                            }
                        }
                    }
                  }
                  """
             When a DELETE request is made to "/ed-fi/schools/{id}"
             Then it should respond with 204

        Scenario: 09 Extension Values Should Become Null if the Sample Extension is Not Specified
            Given a POST request is made to "/sample/buses" with
                  """
                  {
                      "busId": "602"
                  }
                  """
              And a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": "5",
                    "nameOfInstitution": "Extension Test Community College",
                    "educationOrganizationCategories": [
                        {
                            "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution"
                        }
                    ],
                    "gradeLevels": [
                        {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"
                        }
                    ],
                    "_ext": {
                        "sample": {
                            "isExemplary": true,
                            "directlyOwnedBuses": [
                                {
                                    "directlyOwnedBusReference": {
                                        "busId": "602"
                                    }
                                }
                            ]
                        }
                    }
                  }
                  """
             When a GET request is made to "/ed-fi/schools/{id}"
             Then the response body is
                  """
                  {
                    "id": "{id}",
                    "_ext": {
                        "sample": {
                            "isExemplary": true,
                            "directlyOwnedBuses": [
                                {
                                    "directlyOwnedBusReference": {
                                        "busId": "602"
                                    }
                                }
                            ]
                        }
                    },
                    "schoolId": 5,
                    "gradeLevels": [
                      {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"
                      }
                    ],
                    "nameOfInstitution": "Extension Test Community College",
                    "educationOrganizationCategories": [
                      {
                        "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution"
                      }
                    ]
                  }
                  """
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                    "schoolId": "5",
                    "nameOfInstitution": "Extension Test Community College",
                    "educationOrganizationCategories": [
                        {
                            "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution"
                        }
                    ],
                    "gradeLevels": [
                        {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"
                        }
                    ],
                    "_ext": { }
                  }
                  """
             Then it should respond with 200
             When a GET request is made to "/ed-fi/schools/{id}"
             Then the response body is
                  """
                  {
                    "id": "{id}",
                    "_ext": {},
                    "schoolId": 5,
                    "gradeLevels": [
                      {
                        "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"
                      }
                    ],
                    "nameOfInstitution": "Extension Test Community College",
                    "educationOrganizationCategories": [
                      {
                        "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Post Secondary Institution"
                      }
                    ]
                  }
                  """

    Rule: busRoutes scenarios
        Scenario: 10 Create Staff with Duplicate Extension Items
            Given the claimSet "EdFiSandbox" is authorized with namespacePrefixes "uri://ed-fi.org, uri://sample.ed-fi.org"
             When a POST request is made to "/ed-fi/staffs" with
                  """
                  {
                    "staffUniqueId": "123",
                    "firstName": "John",
                    "lastSurname": "Doe",
                    "_ext": {
                        "sample": {
                            "pets": [
                                {
                                    "petName": "Sparky",
                                    "isFixed": true
                                },
                                {
                                    "petName": "Spot",
                                    "isFixed": true
                                },
                                {
                                    "petName": "Whiskers",
                                    "isFixed": true
                                },
                                {
                                    "petName": "Sparky",
                                    "isFixed": true
                                }
                            ]
                        }
                    }
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
                    "validationErrors": {
                        "$._ext.sample.pets": [
                            "The 4th item of the pets has the same identifying values as another item earlier in the list."
                        ]
                    },
                    "errors": []
                  }
                  """
