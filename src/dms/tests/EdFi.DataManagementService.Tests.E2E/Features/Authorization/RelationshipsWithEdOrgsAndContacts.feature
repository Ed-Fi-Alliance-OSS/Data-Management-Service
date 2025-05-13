Feature: RelationshipsWithEdOrgsAndContacts Authorization

        Background:
            Given the claimSet "EdFiAPIPublisherWriter" is authorized with educationOrganizationIds "255901001"
              And the system has these "schoolYearTypes"
                  | schoolYear | currentSchoolYear | schoolYearDescription |
                  | 2023       | true              | "year 2023"           |
              And the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade               |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school |
                  | uri://ed-fi.org/SexDescriptor#Female                           |

    Rule: StudentContactAssociation CRUD is properly authorized
        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with educationOrganizationIds "255901901, 255901902"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution   | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 255901901 | Authorized school   | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
                  | 255901902 | Authorized school 2 | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName            | lastSurname | birthDate  |
                  | "S91111"        | Authorized student   | student-ln  | 2008-01-01 |
                  | "S91112"        | Unauthorized student | student-ln  | 2008-01-01 |
              And the system has these "contacts"
                  | contactUniqueId | firstName          | lastSurname |
                  | "C91111"        | Authorized contact | contact-ln  |
                  | "C91112"        | Authorized contact | contact-ln  |
              And the system has these "studentSchoolAssociations"
                  | schoolReference           | studentReference                | entryGradeLevelDescriptor                          | entryDate  |
                  | { "schoolId": 255901901 } | { "studentUniqueId": "S91111" } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
                  | { "schoolId": 255901902 } | { "studentUniqueId": "S91112" } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |

        Scenario: 01 Ensure client can create a StudentContactAssociation
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with educationOrganizationIds "255901901"
            When a POST request is made to "/ed-fi/students" with
                """
                {
                    "studentUniqueId": "S91113",
                    "firstName": "David",
                    "lastSurname": "Smith",
                    "birthDate": "2008-01-01"
                }
                """
            Then it should respond with 201 or 200

            When a POST request is made to "/ed-fi/StudentSchoolAssociations" with
                """
                {
                      "studentReference": {
                          "studentUniqueId": "S91113"
                      },
                      "schoolReference": {
                          "schoolId": 255901901
                      },
                      "entryDate":"2018-01-01",
                      "entryGradeLevelDescriptor":"uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                }
                """
            Then it should respond with 201 or 200

             When a POST request is made to "/ed-fi/contacts" with
                  """
                  {
                      "contactUniqueId": "C81113",
                      "firstName": "John",
                      "lastSurname": "Doe",
                      "sexDescriptor": "uri://ed-fi.org/SexDescriptor#Female",
                      "_ext": {
                        "Sample": {
                          "teacherConference": {
                            "dayOfWeek": "Monday",
                            "endTime": "12:00:00",
                            "startTime": "12:00:00"
                          },
                          "authors": [],
                          "favoriteBookTitles": [
                            {
                              "favoriteBookTitle": "Green Eggs and Ham"
                            }
                          ],
                          "isSportsFan": false
                        }
                      }
                   }
                  """
             Then it should respond with 201

             When a POST request is made to "/ed-fi/studentContactAssociations" with
                  """
                  {
                      "contactReference": {
                          "contactUniqueId": "C81113"
                      },
                      "studentReference": {
                          "studentUniqueId": "S91113"
                      },
                     "emergencyContactStatus": true,
                      "_ext": {
                        "Sample": {
                          "bedtimeReader": true,
                          "favoriteBookTitles": [
                            {
                              "favoriteBookTitle": "Green Eggs and Ham"
                            }
                          ]
                        }
                      }
                  }
                  """
             Then it should respond with 201


        Scenario: 02 Ensure client can retrieve a StudentContactAssociation
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with educationOrganizationIds "255901901"
            Given a POST request is made to "/ed-fi/studentContactAssociations" with
                  """
                  {
                      "contactReference": {
                          "contactUniqueId": "C91111"
                      },
                      "studentReference": {
                          "studentUniqueId": "S91111"
                      },
                      "emergencyContactStatus": true
                  }
                  """
             Then it should respond with 201 or 200
             When a GET request is made to "/ed-fi/studentContactAssociations/{id}"
             Then it should respond with 200

        Scenario: 03 Ensure client can update a StudentContactAssociation
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with educationOrganizationIds "255901901"
            When a POST request is made to "/ed-fi/students" with
                """
                {
                    "studentUniqueId": "S91114",
                    "firstName": "David",
                    "lastSurname": "Smith",
                    "birthDate": "2008-01-01"
                }
                """
            Then it should respond with 201 or 200

            When a POST request is made to "/ed-fi/StudentSchoolAssociations" with
                """
                {
                      "studentReference": {
                          "studentUniqueId": "S91114"
                      },
                      "schoolReference": {
                          "schoolId": 255901901
                      },
                      "entryDate":"2018-01-01",
                      "entryGradeLevelDescriptor":"uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                }
                """
            Then it should respond with 201 or 200

             When a POST request is made to "/ed-fi/contacts" with
                  """
                  {
                      "contactUniqueId": "C81114",
                      "firstName": "John",
                      "lastSurname": "Doe",
                      "sexDescriptor": "uri://ed-fi.org/SexDescriptor#Female",
                      "_ext": {
                        "Sample": {
                          "teacherConference": {
                            "dayOfWeek": "Monday",
                            "endTime": "12:00:00",
                            "startTime": "12:00:00"
                          },
                          "authors": [],
                          "favoriteBookTitles": [
                            {
                              "favoriteBookTitle": "Green Eggs and Ham"
                            }
                          ],
                          "isSportsFan": false
                        }
                      }
                   }
                  """
             Then it should respond with 201

             When a POST request is made to "/ed-fi/studentContactAssociations" with
                  """
                  {
                      "contactReference": {
                          "contactUniqueId": "C81114"
                      },
                      "studentReference": {
                          "studentUniqueId": "S91114"
                      },
                     "emergencyContactStatus": true,
                      "_ext": {
                        "Sample": {
                          "bedtimeReader": true,
                          "favoriteBookTitles": [
                            {
                              "favoriteBookTitle": "Green Eggs and Ham"
                            }
                          ]
                        }
                      }
                  }
                  """
             Then it should respond with 201

             When a PUT request is made to "/ed-fi/studentContactAssociations/{id}" with
                  """
                  {
                      "id":"{id}",
                      "contactReference": {
                          "contactUniqueId": "C81114"
                      },
                      "studentReference": {
                          "studentUniqueId": "S91114"
                      },
                      "emergencyContactStatus": false
                  }
                  """
             Then it should respond with 204

        Scenario: 04 Ensure client can delete a StudentContactAssociation
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with educationOrganizationIds "255901902"
            Given  a POST request is made to "/ed-fi/studentContactAssociations" with
                  """
                  {
                      "contactReference": {
                          "contactUniqueId": "C91112"
                      },
                      "studentReference": {
                          "studentUniqueId": "S91112"
                      },
                      "emergencyContactStatus": true
                  }
                  """
             Then it should respond with 201 or 200

             When a DELETE request is made to "/ed-fi/studentContactAssociations/{id}"
             Then it should respond with 204

        Scenario: 05 Ensure client get the required validation error when studentContactAssociations is created with empty contactReference
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with educationOrganizationIds "255901902"
             When a POST request is made to "/ed-fi/studentContactAssociations" with
                  """
                  {
                      "studentReference": {
                          "studentUniqueId": "S91111"
                      },
                      "emergencyContactStatus": true
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
                      "correlationId": "0HNCHAN3J69H5:0000000F",
                      "validationErrors": {
                        "$.contactReference": [
                          "contactReference is required."
                        ]
                      },
                      "errors": []
                    }
                  """
        Scenario: 06 Ensure invalid claimSet  can not get a studentContactAssociations
            Given the claimSet "E2E-NameSpaceBasedClaimSet" is authorized with educationOrganizationIds "255901902"
             When a GET request is made to "/ed-fi/studentContactAssociations/{id}"
             Then it should respond with 403
              And the response body is
                  """
                     {
                      "detail": "Access to the resource could not be authorized.",
                      "type": "urn:ed-fi:api:security:authorization:",
                      "title": "Authorization Denied",
                      "status": 403,
                      "validationErrors": {},
                      "errors": []
                     }
                  """

    Rule: Contact CRUD is properly authorized

        Scenario: 07 Ensure client can create a Contact
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with educationOrganizationIds "255901901"
             When a POST request is made to "/ed-fi/contacts" with
                  """
                  {
                      "contactUniqueId": "C81111",
                      "firstName": "John",
                      "lastSurname": "Doe",
                       "_ext": {
                        "Sample": {
                          "teacherConference": {
                            "dayOfWeek": "Monday",
                            "endTime": "12:00:00",
                            "startTime": "12:00:00"
                          },
                          "authors": [],
                          "favoriteBookTitles": [
                            {
                              "favoriteBookTitle": "Green Eggs and Ham"
                            }
                          ],
                          "isSportsFan": false
                        }
                      }
                  }
                  """
             Then it should respond with 201

        Scenario: 08 Ensure client can retrieve a contact
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with educationOrganizationIds "255901901"
             When a POST request is made to "/ed-fi/contacts" with
                  """
                  {
                      "contactUniqueId": "C81111",
                      "firstName": "John",
                      "lastSurname": "Doe"
                  }
                  """
             Then it should respond with 201 or 200
             When a GET request is made to "/ed-fi/contacts/{id}"
             Then it should respond with 200

        Scenario: 09 Ensure client can update a contact
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with educationOrganizationIds "255901901"
            Given a POST request is made to "/ed-fi/contacts/" with
                  """
                  {
                    "contactUniqueId": "C81111",
                    "firstName": "Peter",
                    "lastSurname": "Doe"
                  }
                  """
             Then it should respond with 201 or 200
             When a PUT request is made to "/ed-fi/contacts/{id}" with
                  """
                  {
                    "id": "{id}",
                    "contactUniqueId": "C81111",
                    "firstName": "Peter",
                    "lastSurname": "Doe",
                    "_ext": {
                        "Sample": {
                          "teacherConference": {
                            "dayOfWeek": "Monday",
                            "endTime": "12:00:00",
                            "startTime": "12:00:00"
                          },
                          "authors": [],
                          "favoriteBookTitles": [
                            {
                              "favoriteBookTitle": "Green Eggs and Ham"
                            }
                          ],
                          "isSportsFan": false
                        }
                      }
                  }
                  """
             Then it should respond with 204

        Scenario: 10 Ensure client can delete a contact when it's unused and should return 204 nocontent
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with educationOrganizationIds "255901901"
             When a POST request is made to "/ed-fi/contacts" with
                  """
                  {
                    "contactUniqueId": "C81111",
                    "firstName": "Peter",
                    "lastSurname": "Doe"
                  }
                  """
             Then it should respond with 201 or 200
             When a GET request is made to "/ed-fi/contacts/{id}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                    "id": "{id}",
                    "contactUniqueId": "C81111",
                    "firstName": "Peter",
                    "lastSurname": "Doe"
                  }
                  """
             When a DELETE request is made to "/ed-fi/contacts/{id}"
             Then it should respond with 204
        Scenario: 11  Ensure client get the required validation error when contact is created with empty firstName
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with educationOrganizationIds "255901901"
             When a POST request is made to "/ed-fi/contacts" with
                  """
                  {
                    "contactUniqueId": "C81111",
                    "firstName": "",
                    "lastSurname": "Doe"
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
                      "correlationId": "0HNCHAN3J69GV:00000004",
                      "validationErrors": {
                        "$.firstName": [
                          "firstName is required and should not be left empty."
                        ]
                      },
                      "errors": []
                    }
                  """
        Scenario: 12 Ensure invalid claimSet cannot get a contacts
            Given the claimSet "E2E-NameSpaceBasedClaimSet" is authorized with educationOrganizationIds "255901902"
             When a GET request is made to "/ed-fi/contacts{id}"
             Then it should respond with 403
              And the response body is
                  """
                     {
                      "detail": "Access to the resource could not be authorized.",
                      "type": "urn:ed-fi:api:security:authorization:",
                      "title": "Authorization Denied",
                      "status": 403,
                      "validationErrors": {},
                      "errors": []
                     }
                  """
        Scenario: 13 Ensure client get 409 conflict error when deleting a contact 
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with educationOrganizationIds "255901901"
            When a POST request is made to "/ed-fi/students" with
                """
                {
                    "studentUniqueId": "S91115",
                    "firstName": "David",
                    "lastSurname": "Smith",
                    "birthDate": "2008-01-01"
                }
                """
            Then it should respond with 201 or 200

            When a POST request is made to "/ed-fi/StudentSchoolAssociations" with
                """
                {
                      "studentReference": {
                          "studentUniqueId": "S91115"
                      },
                      "schoolReference": {
                          "schoolId": 255901901
                      },
                      "entryDate":"2018-01-01",
                      "entryGradeLevelDescriptor":"uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                }
                """
            Then it should respond with 201 or 200

             When a POST request is made to "/ed-fi/contacts" with
                  """
                  {
                      "contactUniqueId": "C81115",
                      "firstName": "John",
                      "lastSurname": "Doe",
                      "sexDescriptor": "uri://ed-fi.org/SexDescriptor#Female",
                      "_ext": {
                        "Sample": {
                          "teacherConference": {
                            "dayOfWeek": "Monday",
                            "endTime": "12:00:00",
                            "startTime": "12:00:00"
                          },
                          "authors": [],
                          "favoriteBookTitles": [
                            {
                              "favoriteBookTitle": "Green Eggs and Ham"
                            }
                          ],
                          "isSportsFan": false
                        }
                      }
                   }
                  """
             Then it should respond with 201 or 200

             When a POST request is made to "/ed-fi/studentContactAssociations" with
                  """
                  {
                      "contactReference": {
                          "contactUniqueId": "C81115"
                      },
                      "studentReference": {
                          "studentUniqueId": "S91115"
                      },
                     "emergencyContactStatus": true,
                      "_ext": {
                        "Sample": {
                          "bedtimeReader": true,
                          "favoriteBookTitles": [
                            {
                              "favoriteBookTitle": "Green Eggs and Ham"
                            }
                          ]
                        }
                      }
                  }
                  """
             Then it should respond with 201 or 200
             When a DELETE request is made to "/ed-fi/contacts/{id}"
             Then it should respond with 404

      Scenario: 14 Ensure client can update a contact When it's unassociated contact should fail 403 forbidden
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with educationOrganizationIds "255901901"
            Given a POST request is made to "/ed-fi/contacts/" with
                  """
                  {
                    "contactUniqueId": "C81124",
                    "firstName": "Smith",
                    "lastSurname": "Johnson"
                  }
                  """
             Then it should respond with 201 or 200
             When a PUT request is made to "/ed-fi/contacts/{id}" with
                  """
                  {
                    "id": "{id}",
                    "contactUniqueId": "C81125",
                    "firstName": "Smith",
                    "lastSurname": "Johnson",
                    "_ext": {
                        "Sample": {
                          "teacherConference": {
                            "dayOfWeek": "Monday",
                            "endTime": "12:00:00",
                            "startTime": "12:00:00"
                          },
                          "authors": [],
                          "favoriteBookTitles": [
                            {
                              "favoriteBookTitle": "Green Eggs and Ham"
                            }
                          ],
                          "isSportsFan": false
                        }
                      }
                  }
                  """
             Then it should respond with 400
              And the response body is
                  """
                    {
                      "detail": "Identifying values for the Contact resource cannot be changed. Delete and recreate the resource item instead.",
                      "type": "urn:ed-fi:api:bad-request:data-validation-failed:key-change-not-supported",
                      "title": "Key Change Not Supported",
                      "status": 400,
                      "correlationId": "0HNCHMB4NOEHU:00000006",
                      "validationErrors": {},
                      "errors": []
                    }
                  """

       Scenario: 15 Ensure client should get 409  When associating an unexisting student
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with educationOrganizationIds "255901901"
             When a POST request is made to "/ed-fi/contacts" with
                  """
                  {
                      "contactUniqueId": "C81126",
                      "firstName": "John",
                      "lastSurname": "Doe",
                      "sexDescriptor": "uri://ed-fi.org/SexDescriptor#Female"
                   }
                  """
             Then it should respond with 201

             When a POST request is made to "/ed-fi/studentContactAssociations" with
                  """
                  {
                      "contactReference": {
                          "contactUniqueId": "C81126"
                      },
                      "studentReference": {
                          "studentUniqueId": "S91127"
                      },
                     "emergencyContactStatus": true
                  }
                  """
             Then it should respond with 409    
              And the response body is
                  """
                    {
                  "detail": "The referenced Student item(s) do not exist.",
                  "type": "urn:ed-fi:api:data-conflict:unresolved-reference",
                  "title": "Unresolved Reference",
                  "status": 409,
                  "validationErrors": {},
                  "errors": []
                   }
                  """
