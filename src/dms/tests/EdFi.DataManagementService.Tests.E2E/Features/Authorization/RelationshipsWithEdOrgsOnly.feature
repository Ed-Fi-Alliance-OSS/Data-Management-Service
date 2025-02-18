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
                      ],
                      "dates": [],
                      "gradeLevels": []
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
