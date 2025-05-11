Feature: RelationshipsWithEdOrgsOnly Authorization

    Rule: Resource respect RelationshipsWithEdOrgsOnly authorization

        Background:
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901001, 244901"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 255901001 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "classPeriods"
                  | classPeriodName  | schoolReference           |
                  | 01 - Traditional | { "schoolId": 255901001 } |
                  | 02 - Traditional | { "schoolId": 255901001 } |

        Scenario: 01 Ensure client can create a bellschedule with 255901001
             When a POST request is made to "/ed-fi/bellschedules" with
                  """
                  {
                      "schoolReference": {
                          "schoolId": 255901001
                      },
                      "bellScheduleName": "Test Schedule",
                      "totalInstructionalTime": 325,
                      "classPeriods": [
                          {
                              "classPeriodReference": {
                                  "classPeriodName": "01 - Traditional",
                                  "schoolId": 255901001
                              }
                          },
                          {
                              "classPeriodReference": {
                                  "classPeriodName": "02 - Traditional",
                                  "schoolId": 255901001
                              }
                          }
                      ],
                      "dates": [],
                      "gradeLevels": []
                  }
                  """
             Then it should respond with 201 or 200

        Scenario: 02 Ensure client can update a bellschedule with 255901001
             When a POST request is made to "/ed-fi/bellschedules" with
                  """
                  {
                      "schoolReference": {
                          "schoolId": 255901001
                      },
                      "bellScheduleName": "Test Schedule",
                      "totalInstructionalTime": 325,
                      "classPeriods": [
                          {
                              "classPeriodReference": {
                                  "classPeriodName": "01 - Traditional",
                                  "schoolId": 255901001
                              }
                          }
                      ],
                      "dates": [],
                      "gradeLevels": []
                  }
                  """
             Then it should respond with 201 or 200
             When a PUT request is made to "/ed-fi/bellschedules/{id}" with
                  """
                  {
                      "id": "{id}",
                      "schoolReference": {
                          "schoolId": 255901001
                      },
                      "bellScheduleName": "Test Schedule",
                      "totalInstructionalTime": 325,
                      "classPeriods": [
                          {
                              "classPeriodReference": {
                                  "classPeriodName": "01 - Traditional",
                                  "schoolId": 255901001
                              }
                          },
                          {
                              "classPeriodReference": {
                                  "classPeriodName": "02 - Traditional",
                                  "schoolId": 255901001
                              }
                          }
                      ],
                      "dates": [],
                      "gradeLevels": []
                  }
                  """
             Then it should respond with 204
              And the record can be retrieved with a GET request
                  """
                  {
                           "id": "{id}",
                           "schoolReference": {
                               "schoolId": 255901001
                           },
                           "bellScheduleName": "Test Schedule",
                           "totalInstructionalTime": 325,
                           "classPeriods": [
                               {
                                   "classPeriodReference": {
                                       "classPeriodName": "01 - Traditional",
                                       "schoolId": 255901001
                                   }
                               },
                               {
                                   "classPeriodReference": {
                                       "classPeriodName": "02 - Traditional",
                                       "schoolId": 255901001
                                   }
                               }
                           ]
                       }
                  """

    Rule: POST resource fails with a 403 forbidden error with no education organization ids claim
        Background:
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds ""
              And the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 255901001 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "classPeriods"
                  | classPeriodName  | schoolReference           |
                  | 01 - Traditional | { "schoolId": 255901001 } |
                  | 02 - Traditional | { "schoolId": 255901001 } |

        Scenario: 03 Ensure client can not create a bellschedule with 255901001
             When a POST request is made to "/ed-fi/bellschedules" with
                  """
                  {
                      "schoolReference": {
                          "schoolId": 255901001
                      },
                      "bellScheduleName": "Test Schedule",
                      "totalInstructionalTime": 325,
                      "classPeriods": [
                          {
                              "classPeriodReference": {
                                  "classPeriodName": "01 - Traditional",
                                  "schoolId": 255901001
                              }
                          },
                          {
                              "classPeriodReference": {
                                  "classPeriodName": "02 - Traditional",
                                  "schoolId": 255901001
                              }
                          }
                      ],
                      "dates": [],
                      "gradeLevels": []
                  }
                  """
             Then it should respond with 403
              And the response body is
                  """
                  {
                      "detail": "Access to the resource could not be authorized.",
                      "type": "urn:ed-fi:api:security:authorization:",
                      "title": "Authorization Denied",
                      "status": 403,
                      "validationErrors": {},
                      "errors": [
                        "The API client has been given permissions on a resource that uses the 'RelationshipsWithEdOrgsOnly' authorization strategy but the client doesn't have any education organizations assigned."
                      ]
                  }
                  """

    Rule: PUT resource fails with a 403 forbidden error with no education organization ids claim
        Background:
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901001"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 255901001 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "classPeriods"
                  | classPeriodName  | schoolReference           |
                  | 01 - Traditional | { "schoolId": 255901001 } |
                  | 02 - Traditional | { "schoolId": 255901001 } |

        Scenario: 04 Ensure client can not update a bellschedule with 255901001
             When a POST request is made to "/ed-fi/bellschedules" with
                  """
                  {
                      "schoolReference": {
                          "schoolId": 255901001
                      },
                      "bellScheduleName": "Test Schedule",
                      "totalInstructionalTime": 325,
                      "classPeriods": [
                          {
                              "classPeriodReference": {
                                  "classPeriodName": "01 - Traditional",
                                  "schoolId": 255901001
                              }
                          }
                      ],
                      "dates": [],
                      "gradeLevels": []
                  }
                  """

             Then it should respond with 201 or 200
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds ""
             When a PUT request is made to "/ed-fi/bellschedules/{id}" with
                  """
                  {
                      "id": "{id}",
                      "schoolReference": {
                          "schoolId": 255901001
                      },
                      "bellScheduleName": "Test Schedule",
                      "totalInstructionalTime": 325,
                      "classPeriods": [
                          {
                              "classPeriodReference": {
                                  "classPeriodName": "01 - Traditional",
                                  "schoolId": 255901001
                              }
                          },
                          {
                              "classPeriodReference": {
                                  "classPeriodName": "02 - Traditional",
                                  "schoolId": 255901001
                              }
                          }
                      ],
                      "dates": [],
                      "gradeLevels": []
                  }
                  """
             Then it should respond with 403
              And the response body is
                  """
                  {
                      "detail": "Access to the resource could not be authorized.",
                      "type": "urn:ed-fi:api:security:authorization:",
                      "title": "Authorization Denied",
                      "status": 403,
                      "validationErrors": {},
                      "errors": [
                        "The API client has been given permissions on a resource that uses the 'RelationshipsWithEdOrgsOnly' authorization strategy but the client doesn't have any education organizations assigned."
                      ]
                  }
                  """

    Rule: Create or update resource fails with a 403 forbidden error with no matching education organization ids claim
        Background:
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901001"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 255901001 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "classPeriods"
                  | classPeriodName  | schoolReference           |
                  | 01 - Traditional | { "schoolId": 255901001 } |
                  | 02 - Traditional | { "schoolId": 255901001 } |

        Scenario: 05 Ensure client can not create a bellschedule with 255901002
             When a POST request is made to "/ed-fi/bellschedules" with
                  """
                  {
                      "schoolReference": {
                          "schoolId": 255901002
                      },
                      "bellScheduleName": "Test Schedule",
                      "totalInstructionalTime": 325,
                      "classPeriods": [
                          {
                              "classPeriodReference": {
                                  "classPeriodName": "01 - Traditional",
                                  "schoolId": 255901002
                              }
                          },
                          {
                              "classPeriodReference": {
                                  "classPeriodName": "02 - Traditional",
                                  "schoolId": 255901002
                              }
                          }
                      ],
                      "dates": [],
                      "gradeLevels": []
                  }
                  """
             Then it should respond with 403
              And the response body is
                  """
                  {
                      "detail": "Access to the resource could not be authorized.",
                      "type": "urn:ed-fi:api:security:authorization:",
                      "title": "Authorization Denied",
                      "status": 403,
                      "validationErrors": {},
                      "errors": [
                        "No relationships have been established between the caller's education organization id claims ('255901001') and properties of the resource item."
                      ]
                    }
                  """

        Scenario: 06 Ensure client can not update a bellschedule with 255901002
             When a POST request is made to "/ed-fi/bellschedules" with
                  """
                  {
                      "schoolReference": {
                          "schoolId": 255901001
                      },
                      "bellScheduleName": "Test Schedule 06",
                      "totalInstructionalTime": 325,
                      "classPeriods": [
                          {
                              "classPeriodReference": {
                                  "classPeriodName": "01 - Traditional",
                                  "schoolId": 255901001
                              }
                          }
                      ],
                      "dates": [],
                      "gradeLevels": []
                  }
                  """
             Then it should respond with 201 or 200
             When a PUT request is made to "/ed-fi/bellschedules/{id}" with
                  """
                  {
                      "id": "{id}",
                      "schoolReference": {
                          "schoolId": 255901002
                      },
                      "bellScheduleName": "Test Schedule 06",
                      "totalInstructionalTime": 325,
                      "classPeriods": [
                          {
                              "classPeriodReference": {
                                  "classPeriodName": "01 - Traditional",
                                  "schoolId": 255901002
                              }
                          },
                          {
                              "classPeriodReference": {
                                  "classPeriodName": "02 - Traditional",
                                  "schoolId": 255901002
                              }
                          }
                      ],
                      "dates": [],
                      "gradeLevels": []
                  }
                  """
             Then it should respond with 403
              And the response body is
                  """
                  {
                      "detail": "Access to the resource could not be authorized.",
                      "type": "urn:ed-fi:api:security:authorization:",
                      "title": "Authorization Denied",
                      "status": 403,
                      "validationErrors": {},
                      "errors": [
                        "No relationships have been established between the caller's education organization id claims ('255901001') and properties of the resource item."
                      ]
                  }
                  """

    Rule: GetById or Delete the resource with RelationshipsWithEdOrgsOnly authorization
        Background:
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901001, 244901"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 255901001 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "classPeriods"
                  | classPeriodName  | schoolReference           |
                  | 01 - Traditional | { "schoolId": 255901001 } |
                  | 02 - Traditional | { "schoolId": 255901001 } |

        Scenario: 07 Ensure client can get the bellschedule with 255901001
             When a POST request is made to "/ed-fi/bellschedules" with
                  """
                  {
                      "schoolReference": {
                          "schoolId": 255901001
                      },
                      "bellScheduleName": "Test Schedule",
                      "totalInstructionalTime": 325,
                      "classPeriods": [
                          {
                              "classPeriodReference": {
                                  "classPeriodName": "01 - Traditional",
                                  "schoolId": 255901001
                              }
                          },
                          {
                              "classPeriodReference": {
                                  "classPeriodName": "02 - Traditional",
                                  "schoolId": 255901001
                              }
                          }
                      ],
                      "dates": [],
                      "gradeLevels": []
                  }
                  """
             Then it should respond with 201 or 200
             When a GET request is made to "/ed-fi/bellschedules/{id}"
             Then it should respond with 200
              And the record can be retrieved with a GET request
                  """
                  {
                           "id": "{id}",
                          "schoolReference": {
                               "schoolId": 255901001
                           },
                           "bellScheduleName": "Test Schedule",
                           "totalInstructionalTime": 325,
                           "classPeriods": [
                               {
                                   "classPeriodReference": {
                                       "classPeriodName": "01 - Traditional",
                                       "schoolId": 255901001
                                   }
                               },
                               {
                                   "classPeriodReference": {
                                       "classPeriodName": "02 - Traditional",
                                       "schoolId": 255901001
                                   }
                               }
                           ]
                       }
                  """

        Scenario: 08 Ensure client can delete a bellschedule with 255901001
             When a POST request is made to "/ed-fi/bellschedules" with
                  """
                  {
                      "schoolReference": {
                          "schoolId": 255901001
                      },
                      "bellScheduleName": "Test Schedule",
                      "totalInstructionalTime": 325,
                      "classPeriods": [
                          {
                              "classPeriodReference": {
                                  "classPeriodName": "01 - Traditional",
                                  "schoolId": 255901001
                              }
                          }
                      ],
                      "dates": [],
                      "gradeLevels": []
                  }
                  """
             Then it should respond with 201 or 200
             When a DELETE request is made to "/ed-fi/bellschedules/{id}"
             Then it should respond with 204
             When a GET request is made to "/ed-fi/bellschedules/{id}"
             Then it should respond with 404

    Rule: GetById or Delete the resource fails with a 403 forbidden error with no matching education organization ids claim
        Background:
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901001"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 255901001 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "classPeriods"
                  | classPeriodName  | schoolReference           |
                  | 01 - Traditional | { "schoolId": 255901001 } |
                  | 02 - Traditional | { "schoolId": 255901001 } |

        Scenario: 09 Ensure client can not get the bellschedule with 255901001
             When a POST request is made to "/ed-fi/bellschedules" with
                  """
                  {
                      "schoolReference": {
                          "schoolId": 255901001
                      },
                      "bellScheduleName": "Test Schedule",
                      "totalInstructionalTime": 325,
                      "classPeriods": [
                          {
                              "classPeriodReference": {
                                  "classPeriodName": "01 - Traditional",
                                  "schoolId": 255901001
                              }
                          },
                          {
                              "classPeriodReference": {
                                  "classPeriodName": "02 - Traditional",
                                  "schoolId": 255901001
                              }
                          }
                      ],
                      "dates": [],
                      "gradeLevels": []
                  }
                  """
             Then it should respond with 201 or 200
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901222"
             When a GET request is made to "/ed-fi/bellschedules/{id}"
             Then it should respond with 403
              And the response body is
                  """
                  {
                   "detail": "Access to the resource could not be authorized.",
                   "type": "urn:ed-fi:api:security:authorization:",
                   "title": "Authorization Denied",
                   "status": 403,
                   "validationErrors": {},
                   "errors": [
                        "Access to the resource item could not be authorized based on the caller's EducationOrganizationIds claims: '255901222'."
                    ]
                  }
                  """

        Scenario: 10 Ensure client can not delete a bellschedule with 255901001
             When a POST request is made to "/ed-fi/bellschedules" with
                  """
                  {
                      "schoolReference": {
                          "schoolId": 255901001
                      },
                      "bellScheduleName": "Test Schedule",
                      "totalInstructionalTime": 325,
                      "classPeriods": [
                          {
                              "classPeriodReference": {
                                  "classPeriodName": "01 - Traditional",
                                  "schoolId": 255901001
                              }
                          }
                      ],
                      "dates": [],
                      "gradeLevels": []
                  }
                  """
             Then it should respond with 201 or 200
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901223"
             When a DELETE request is made to "/ed-fi/bellschedules/{id}"
             Then it should respond with 403
              And the response body is
                  """
                  {
                   "detail": "Access to the resource could not be authorized.",
                   "type": "urn:ed-fi:api:security:authorization:",
                   "title": "Authorization Denied",
                   "status": 403,
                   "validationErrors": {},
                   "errors": [
                        "Access to the resource item could not be authorized based on the caller's EducationOrganizationIds claims: '255901223'."
                    ]
                  }
                  """

    Rule: Search for a resource with RelationshipsWithEdOrgsOnly authorization
        Background:
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901001, 244901"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 255901001 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "academicWeeks"
                  | weekIdentifier | schoolReference           | beginDate  | endDate    | totalInstructionalDays |
                  | week 1         | { "schoolId": 255901001 } | 2023-08-01 | 2023-08-07 | 5                      |

        @addwait
        Scenario: 11 Ensure client with access to school 255901001 gets query results for classPeriods
             When a GET request is made to "/ed-fi/academicWeeks"
             Then it should respond with 200
              And the response body is
                  """
                  [
                   {
                    "beginDate": "2023-08-01",
                    "endDate": "2023-08-07",
                    "totalInstructionalDays": 5,
                    "id": "{id}",
                    "weekIdentifier": "week 1",
                    "schoolReference": {
                        "schoolId": 255901001
                     }
                    }
                  ]
                  """

        @addwait
        Scenario: 12 Ensure client with access to school 255901222 does not get query results for classPeriods
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901222"
             When a GET request is made to "/ed-fi/academicWeeks"
             Then it should respond with 200
              And the response body is
                  """
                  []
                  """

    Rule: Access a resource in the EducationOrganizationHierarchy with RelationshipsWithEdOrgsOnly authorization
        Background:
            # Build a hierarchy
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "2, 201, 20101"
              And the system has these "stateEducationAgencies"
                  | stateEducationAgencyId | nameOfInstitution | categories                                                                                                            |
                  | 2                      | Test state        | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#State" }] |
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | stateEducationAgencyReference   | categories                                                                                                               | localEducationAgencyCategoryDescriptor                       |
                  | 201                    | Test LEA          | { "stateEducationAgencyId": 2 } | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#District" }] | "uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC" |
              And the system has these "schools"
                  | schoolId | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        | localEducationAgencyReference    |
                  | 20101    | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] | { "localEducationAgencyId": 201} |
              And a POST request is made to "/ed-fi/academicWeeks" with
                  """
                  {
                      "schoolReference": {
                          "schoolId": 20101
                      },
                      "beginDate": "2023-08-01",
                      "endDate": "2023-08-07",
                      "totalInstructionalDays": 5,
                      "weekIdentifier": "week 1"
                  }
                  """

        Scenario: 13.1 Ensure client with access to state education agency 2 can post and put classPeriods for school 20201
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "2"
             When a POST request is made to "/ed-fi/academicWeeks" with
                  """
                  {
                      "schoolReference": {
                          "schoolId": 20101
                      },
                      "beginDate": "2023-08-08",
                      "endDate": "2023-08-14",
                      "totalInstructionalDays": 5,
                      "weekIdentifier": "week 2"
                  }
                  """
             Then it should respond with 201 or 200
             When a PUT request is made to "/ed-fi/academicWeeks/{id}" with
                  """
                  {
                      "id": "{id}",
                      "schoolReference": {
                          "schoolId": 20101
                      },
                      "beginDate": "2023-08-08",
                      "endDate": "2023-08-14",
                      "totalInstructionalDays": 6,
                      "weekIdentifier": "week 2"
                  }
                  """
             Then it should respond with 204

        Scenario: 13.2 Ensure client with access to state education agency 3 can not post or put classPeriods for school 20201
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "3"
             When a POST request is made to "/ed-fi/academicWeeks" with
                  """
                  {
                      "schoolReference": {
                          "schoolId": 20101
                      },
                      "beginDate": "2023-08-08",
                      "endDate": "2023-08-14",
                      "totalInstructionalDays": 5,
                      "weekIdentifier": "week 2"
                  }
                  """
             Then it should respond with 403
              And the response body is
                  """
                  {
                      "detail": "Access to the resource could not be authorized.",
                      "type": "urn:ed-fi:api:security:authorization:",
                      "title": "Authorization Denied",
                      "status": 403,
                      "validationErrors": {},
                      "errors": [
                          "No relationships have been established between the caller's education organization id claims ('3') and properties of the resource item."
                       ]
                      }
                  """

        Scenario: 13.3 Ensure client with access to state education agency 3 can not put classPeriods for school 20201
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "3"
             When a PUT request is made to "/ed-fi/academicWeeks/{id}" with
                  """
                  {
                      "id": "{id}",
                      "schoolReference": {
                          "schoolId": 20101
                      },
                      "beginDate": "2023-08-08",
                      "endDate": "2023-08-14",
                      "totalInstructionalDays": 6,
                      "weekIdentifier": "week 2"
                  }
                  """
             Then it should respond with 403

        @addwait
        Scenario: 13.4 Ensure client with access to state education agency 2 gets query results for school level classPeriods
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "2"
             When a GET request is made to "/ed-fi/academicWeeks?weekIdentifier=week 1"
             Then it should respond with 200
              And the response body is
                  """
                  [
                   {
                    "beginDate": "2023-08-01",
                    "endDate": "2023-08-07",
                    "totalInstructionalDays": 5,
                    "id": "{id}",
                    "weekIdentifier": "week 1",
                    "schoolReference": {
                        "schoolId": 20101
                     }
                    }
                  ]
                  """

        Scenario: 13.5 Ensure client with access to state education agency 2 can get by id school level classPeriods
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "2"
             When a GET request is made to "/ed-fi/academicWeeks/{id}"
             Then it should respond with 200
              And the response body is
                  """
                   {
                    "beginDate": "2023-08-01",
                    "endDate": "2023-08-07",
                    "totalInstructionalDays": 5,
                    "id": "{id}",
                    "weekIdentifier": "week 1",
                    "schoolReference": {
                        "schoolId": 20101
                     }
                    }
                  """

        Scenario: 13.6 Ensure client with access to state education agency 2 can delete school level classPeriods
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "2"
             When a DELETE request is made to "/ed-fi/academicWeeks/{id}"
             Then it should respond with 204

    Rule: Search for a resource up the EducationOrganizationHierarchy with RelationshipsWithEdOrgsOnly authorization
        Background:
                  # Build a hierarchy
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "2, 201, 301, 20101"
              And the system has these "stateEducationAgencies"
                  | stateEducationAgencyId | nameOfInstitution | categories                                                                                                            |
                  | 2                      | Test state        | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#State" }] |
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | stateEducationAgencyReference   | categories                                                                                                               | localEducationAgencyCategoryDescriptor                       |
                  | 201                    | Test LEA          | { "stateEducationAgencyId": 2 } | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#District" }] | "uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC" |
              And the system has these "schools"
                  | schoolId | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        | localEducationAgencyReference    |
                  | 20101    | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] | { "localEducationAgencyId": 201} |
              And the system has these "academicWeeks"
                  | weekIdentifier | schoolReference       | beginDate  | endDate    | totalInstructionalDays |
                  | week 1         | { "schoolId": 20101 } | 2023-08-01 | 2023-08-07 | 5                      |
              And a POST request is made to "/ed-fi/localEducationAgencies" with
                  """
                  {
                      "localEducationAgencyId": 301,
                      "nameOfInstitution": "Test LEA 301",
                      "stateEducationAgencyReference": {
                          "stateEducationAgencyId": 2
                      },
                      "categories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#District"
                          }
                      ],
                      "localEducationAgencyCategoryDescriptor": "uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC"
                  }
                  """

        @addwait
        Scenario: 14.1 Ensure client with access to school 20101 does not gets query results for LEA because it is up the hierarchy
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "20101"
             When a GET request is made to "/ed-fi/localEducationAgencies"
             Then it should respond with 200
              And the response body is
                  """
                  [
                  ]
                  """
        Scenario: 14.2 Ensure client with access to school 20101 cannot get by id LEA because it is up the hierarchy
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "20101"
             When a GET request is made to "/ed-fi/localEducationAgencies/{id}"
             Then it should respond with 403
              And the response body is
                  """
                  {
                    "detail": "Access to the resource could not be authorized.",
                    "type": "urn:ed-fi:api:security:authorization:",
                    "title": "Authorization Denied",
                    "status": 403,
                    "correlationId": "0HNB05S3Q7LS5:00000084",
                    "validationErrors": {},
                    "errors": [
                      "Access to the resource item could not be authorized based on the caller's EducationOrganizationIds claims: '20101'."
                    ]
                  }
                  """
        Scenario: 14.3 Ensure client with access to school 20101 cannot delete by id LEA because it is up the hierarchy
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "20101"
             When a DELETE request is made to "/ed-fi/localEducationAgencies/{id}"
             Then it should respond with 403
              And the response body is
                  """
                  {
                    "detail": "Access to the resource could not be authorized.",
                    "type": "urn:ed-fi:api:security:authorization:",
                    "title": "Authorization Denied",
                    "status": 403,
                    "correlationId": "0HNB05S3Q7LS5:00000084",
                    "validationErrors": {},
                    "errors": [
                      "Access to the resource item could not be authorized based on the caller's EducationOrganizationIds claims: '20101'."
                    ]
                  }
                  """
        Scenario: 14.4 Ensure client with access to school 20202 cannot PUT LEA because it is up the hierarchy
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "20101"
             When a PUT request is made to "/ed-fi/localEducationAgencies/{id}" with
                  """
                  {
                      "id": "{id}",
                      "localEducationAgencyId": 301,
                      "nameOfInstitution": "Test LEA 301",
                      "stateEducationAgencyReference": {
                          "stateEducationAgencyId": 2
                      },
                      "categories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#District"
                          }
                      ],
                      "localEducationAgencyCategoryDescriptor": "uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC"
                  }
                  """
             Then it should respond with 403
              And the response body is
                  """
                  {
                    "detail": "Access to the resource could not be authorized.",
                    "type": "urn:ed-fi:api:security:authorization:",
                    "title": "Authorization Denied",
                    "status": 403,
                    "correlationId": "0HNB05S3Q7LS5:00000084",
                    "validationErrors": {},
                    "errors": [
                      "No relationships have been established between the caller's education organization id claims ('20101') and properties of the resource item."
                    ]
                  }
                  """

    Rule: Search for a resource in the EducationOrganizationHierarchy with RelationshipsWithEdOrgsOnly authorization and LONG schoolId
        Background:
                      # Build a hierarchy
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "3, 301, 30101999999"
              And the system has these "stateEducationAgencies"
                  | stateEducationAgencyId | nameOfInstitution | categories                                                                                                            |
                  | 3                      | Test state        | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#State" }] |
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | stateEducationAgencyReference   | categories                                                                                                               | localEducationAgencyCategoryDescriptor                       |
                  | 301                    | Test LEA          | { "stateEducationAgencyId": 3 } | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#District" }] | "uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC" |
              And the system has these "schools"
                  | schoolId    | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        | localEducationAgencyReference    |
                  | 30101999999 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] | { "localEducationAgencyId": 301} |
              And the system has these "academicWeeks"
                  | weekIdentifier | schoolReference             | beginDate  | endDate    | totalInstructionalDays |
                  | week 1         | { "schoolId": 30101999999 } | 2023-08-01 | 2023-08-07 | 5                      |


        @addwait
        Scenario: 19 Ensure client with access to state education agency 3 gets query results for school level classPeriods
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "3"
             When a GET request is made to "/ed-fi/academicWeeks"
             Then it should respond with 200
              And the response body is
                  """
                  [
                   {
                    "beginDate": "2023-08-01",
                    "endDate": "2023-08-07",
                    "totalInstructionalDays": 5,
                    "id": "{id}",
                    "weekIdentifier": "week 1",
                    "schoolReference": {
                        "schoolId": 30101999999
                     }
                    }
                  ]
                  """

    Rule: LEA CRUD is properly authorized
        Background:
            Given the claimSet "E2E-RelationshipsWithEdOrgsOnlyClaimSet" is authorized with educationOrganizationIds "255901"
              And the system has these descriptors
                  | descriptorValue                                                                       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency        |
                  | uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Regular public school district |

        Scenario: 20 Ensure client can create an LEA
             When a POST request is made to "/ed-fi/localEducationAgencies" with
                  """
                  {
                      "localEducationAgencyId": 255901,
                      "nameOfInstitution": "Grand Bend SD",
                      "categories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency"
                          }
                      ],
                      "localEducationAgencyCategoryDescriptor": "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Regular public school district"
                  }
                  """
             Then it should respond with 201
                  
        Scenario: 21 Ensure client can retrieve an LEA
            Given a POST request is made to "/ed-fi/localEducationAgencies" with
                  """
                  {
                      "localEducationAgencyId": 255901,
                      "nameOfInstitution": "Grand Bend SD",
                      "categories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency"
                          }
                      ],
                      "localEducationAgencyCategoryDescriptor": "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Regular public school district"
                  }
                  """
              And the resulting id is stored in the "localEducationAgencyId" variable
             Then it should respond with 201 or 200

             When a GET request is made to "/ed-fi/localEducationAgencies/{localEducationAgencyId}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                      "id": "{localEducationAgencyId}",
                      "localEducationAgencyId": 255901,
                      "nameOfInstitution": "Grand Bend SD",
                      "categories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency"
                          }
                      ],
                      "localEducationAgencyCategoryDescriptor": "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Regular public school district"
                  }
                  """

        @addwait
        Scenario: 22 Ensure client can only query authorized LEAs
            Given a POST request is made to "/ed-fi/localEducationAgencies" with
                  """
                  {
                      "localEducationAgencyId": 255901,
                      "nameOfInstitution": "Grand Bend SD",
                      "categories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency"
                          }
                      ],
                      "localEducationAgencyCategoryDescriptor": "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Regular public school district"
                  }
                  """
              And the resulting id is stored in the "localEducationAgencyId" variable
             Then it should respond with 201 or 200

             When a GET request is made to "/ed-fi/localEducationAgencies"
             Then it should respond with 200
              And the response body is
                  """
                  [
                      {
                          "id": "{localEducationAgencyId}",
                          "localEducationAgencyId": 255901,
                          "nameOfInstitution": "Grand Bend SD",
                          "categories": [
                              {
                                  "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency"
                              }
                          ],
                          "localEducationAgencyCategoryDescriptor": "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Regular public school district"
                      }
                  ]
                  """

        Scenario: 23 Ensure client can update an LEA
            Given a POST request is made to "/ed-fi/localEducationAgencies" with
                  """
                  {
                      "localEducationAgencyId": 255901,
                      "nameOfInstitution": "Grand Bend SD",
                      "categories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency"
                          }
                      ],
                      "localEducationAgencyCategoryDescriptor": "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Regular public school district"
                  }
                  """
              And the resulting id is stored in the "localEducationAgencyId" variable
             Then it should respond with 201 or 200

             When a PUT request is made to "/ed-fi/localEducationAgencies/{localEducationAgencyId}" with
                  """
                  {
                     "id": "{localEducationAgencyId}",
                     "localEducationAgencyId": 255901,
                     "nameOfInstitution": "Grand Bend SD - Updated",
                     "categories": [
                         {
                             "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency"
                         }
                     ],
                     "localEducationAgencyCategoryDescriptor": "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Regular public school district"
                  }
                  """
             Then it should respond with 204

        Scenario: 24 Ensure client can delete an LEA
            Given a POST request is made to "/ed-fi/localEducationAgencies" with
                  """
                  {
                      "localEducationAgencyId": 255901,
                      "nameOfInstitution": "Grand Bend SD",
                      "categories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency"
                          }
                      ],
                      "localEducationAgencyCategoryDescriptor": "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Regular public school district"
                  }
                  """
              And the resulting id is stored in the "localEducationAgencyId" variable
             Then it should respond with 201 or 200

             When a DELETE request is made to "/ed-fi/localEducationAgencies/{localEducationAgencyId}"
             Then it should respond with 204
