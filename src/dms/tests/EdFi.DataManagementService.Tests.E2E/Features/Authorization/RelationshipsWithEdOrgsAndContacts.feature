Feature: RelationshipsWithEdOrgsAndContacts Authorization

    Background:
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901901, 255901902"
              And the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade               |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school |
                  | uri://ed-fi.org/SexDescriptor#Female                           |
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

Rule: StudentContactAssociation CRUD is properly authorized

        Scenario: 01 Ensure client can create a StudentContactAssociation
            When a POST request is made to "/ed-fi/studentContactAssociations" with
                  """
                  {
                      "contactReference": {
                          "contactUniqueId": "C91111"
                      },
                      "studentReference": {
                          "studentUniqueId": "S91111"
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
            Given a POST request is made to "/ed-fi/studentContactAssociations" with
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
             When a GET request is made to "/ed-fi/studentContactAssociations/{id}"
             Then it should respond with 200

        Scenario: 03 Ensure client can not create a StudentContactAssociation with wrong educationOrganizationId
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901903"
            When a POST request is made to "/ed-fi/studentContactAssociations" with
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
                          "No relationships have been established between the caller's education organization id claims ('255901903') and the resource item's StudentUniqueId value."
                        ]
                  }
                """

        Scenario: 04 Ensure client can not get StudentContactAssociation with wrong educationOrganizationId
            When a POST request is made to "/ed-fi/studentContactAssociations" with
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
             Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901903"
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
                    "errors": [
                          "No relationships have been established between the caller's education organization id claims ('255901903') and the resource item's StudentUniqueId value."
                        ]
                   }
                """

        Scenario: 05 Ensure client can not search StudentContactAssociation with wrong educationOrganizationId
            When a POST request is made to "/ed-fi/studentContactAssociations" with
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
             Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901903"
             When a GET request is made to "/ed-fi/studentContactAssociations"
             Then it should respond with 200
             And the response body is
                """
                   []
                """

        Scenario: 06 Ensure client can update a StudentContactAssociation
             When a POST request is made to "/ed-fi/studentContactAssociations" with
                  """
                  {
                      "contactReference": {
                          "contactUniqueId": "C91112"
                      },
                      "studentReference": {
                          "studentUniqueId": "S91112"
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
             When a PUT request is made to "/ed-fi/studentContactAssociations/{id}" with
                  """
                  {
                      "id":"{id}",
                      "contactReference": {
                          "contactUniqueId": "C91112"
                      },
                      "studentReference": {
                          "studentUniqueId": "S91112"
                      },
                      "emergencyContactStatus": false
                  }
                  """
             Then it should respond with 204

        Scenario: 07 Ensure client can not update a StudentContactAssociation with wrong educationOrganizationId
             When a POST request is made to "/ed-fi/studentContactAssociations" with
                  """
                  {
                      "contactReference": {
                          "contactUniqueId": "C91112"
                      },
                      "studentReference": {
                          "studentUniqueId": "S91112"
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
             Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901903"
             When a PUT request is made to "/ed-fi/studentContactAssociations/{id}" with
                  """
                  {
                      "id":"{id}",
                      "contactReference": {
                          "contactUniqueId": "C91112"
                      },
                      "studentReference": {
                          "studentUniqueId": "S91112"
                      },
                      "emergencyContactStatus": false
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
                          "No relationships have been established between the caller's education organization id claims ('255901903') and the resource item's StudentUniqueId value."
                        ]
                   }
                """

        Scenario: 08 Ensure client can delete a StudentContactAssociation
            When a POST request is made to "/ed-fi/studentContactAssociations" with
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

        Scenario: 09 Ensure client can not delete a StudentContactAssociation with wrong educationOrganizationId
            When a POST request is made to "/ed-fi/studentContactAssociations" with
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
             Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901903"
             When a DELETE request is made to "/ed-fi/studentContactAssociations/{id}"
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
                          "No relationships have been established between the caller's education organization id claims ('255901903') and the resource item's StudentUniqueId value."
                        ]
                  }
                """

        Scenario: 10 Ensure client get the required validation error when studentContactAssociations is created with empty contactReference
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901902"
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

Rule: Contact CRUD is properly authorized

        Scenario: 11 Ensure client can create a Contact
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

        Scenario: 12 Ensure client can not retrieve a contact with out student contact association
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
                              "No relationships have been established between the caller's education organization id claims ('255901901', '255901902') and the resource item's ContactUniqueId value."
                            ]
                       }
                    """

        Scenario: 13 Ensure client can not update a contact when it's unassociated
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
                          "No relationships have been established between the caller's education organization id claims ('255901901', '255901902') and the resource item's ContactUniqueId value."
                        ]
                  }
                """

        Scenario: 14 Ensure client can delete a contact when it's unused and should return 204 nocontent
             When a POST request is made to "/ed-fi/contacts" with
                  """
                  {
                    "contactUniqueId": "C81106",
                    "firstName": "Peter",
                    "lastSurname": "Doe"
                  }
                  """
             Then it should respond with 201 or 200
             When a DELETE request is made to "/ed-fi/contacts/{id}"
             Then it should respond with 204

        Scenario: 15 Ensure client can not delete a contact when it's associated with a student
            When a POST request is made to "/ed-fi/contacts" with
                """
                {
                  "contactUniqueId": "C81111",
                  "firstName": "Peter",
                  "lastSurname": "Doe"
                }
                """
            And the resulting id is stored in the "AssociatedContactId" variable
            Then it should respond with 201 or 200
            When a POST request is made to "/ed-fi/studentContactAssociations" with
                """
                {
                    "contactReference": {
                        "contactUniqueId": "C81111"
                    },
                    "studentReference": {
                        "studentUniqueId": "S91111"
                    },
                   "emergencyContactStatus": true
                }
                """
            Then it should respond with 201 or 200
            When a DELETE request is made to "/ed-fi/contacts/{AssociatedContactId}"
            Then it should respond with 409
            And the response body is
                """
                  {
                   "detail": "The requested action cannot be performed because this item is referenced by existing StudentContactAssociation item(s).",
                  "type": "urn:ed-fi:api:data-conflict:dependent-item-exists",
                  "title": "Dependent Item Exists",
                  "status": 409,
                  "validationErrors": {},
                  "errors": []
                  }
                """

        Scenario: 16 Ensure client can retrieve a contact with student contact association
             When a POST request is made to "/ed-fi/contacts" with
                  """
                  {
                      "contactUniqueId": "C81111",
                      "firstName": "John",
                      "lastSurname": "Doe"
                  }
                  """
             And the resulting id is stored in the "AssociatedContactId" variable
             Then it should respond with 201 or 200
             When a POST request is made to "/ed-fi/studentContactAssociations" with
                """
                {
                    "contactReference": {
                        "contactUniqueId": "C81111"
                    },
                    "studentReference": {
                        "studentUniqueId": "S91111"
                    },
                   "emergencyContactStatus": true
                }
                """
             Then it should respond with 201 or 200
             When a GET request is made to "/ed-fi/contacts/{AssociatedContactId}"
             Then it should respond with 200
             And the response body is
                    """
                    {
                      "id": "{id}",
                      "contactUniqueId": "C81111",
                      "firstName": "John",
                      "lastSurname": "Doe"
                    }
                    """

        Scenario: 17 Ensure client can not update a contact When it's unassociated
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
                          "No relationships have been established between the caller's education organization id claims ('255901901', '255901902') and the resource item's ContactUniqueId value."
                        ]
                  }
                """


        Scenario: 18 Ensure client can update a contact When it's associated
            Given a POST request is made to "/ed-fi/contacts/" with
                  """
                  {
                    "contactUniqueId": "C81124",
                    "firstName": "Smith",
                    "lastSurname": "Johnson"
                  }
                  """
             And the resulting id is stored in the "AssociatedContactId" variable
             Then it should respond with 201 or 200
             When a POST request is made to "/ed-fi/studentContactAssociations" with
                """
                {
                    "contactReference": {
                        "contactUniqueId": "C81124"
                    },
                    "studentReference": {
                        "studentUniqueId": "S91111"
                    },
                   "emergencyContactStatus": true
                }
                """
             Then it should respond with 201 or 200
             When a PUT request is made to "/ed-fi/contacts/{AssociatedContactId}" with
                  """
                  {
                    "id": "{AssociatedContactId}",
                    "contactUniqueId": "C81124",
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
             Then it should respond with 204

        Scenario: 19 Ensure client can not update a contact with wrong educationOrganizationId
             Given a POST request is made to "/ed-fi/contacts/" with
                  """
                  {
                    "contactUniqueId": "C81124",
                    "firstName": "Smith",
                    "lastSurname": "Johnson"
                  }
                  """
             And the resulting id is stored in the "AssociatedContactId" variable
             Then it should respond with 201 or 200
             When a POST request is made to "/ed-fi/studentContactAssociations" with
                """
                {
                    "contactReference": {
                        "contactUniqueId": "C81124"
                    },
                    "studentReference": {
                        "studentUniqueId": "S91111"
                    },
                   "emergencyContactStatus": true
                }
                """
             Then it should respond with 201 or 200
             Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901903"
             When a PUT request is made to "/ed-fi/contacts/{AssociatedContactId}" with
                  """
                  {
                    "id": "{AssociatedContactId}",
                    "contactUniqueId": "C81124",
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
                          "No relationships have been established between the caller's education organization id claims ('255901903') and the resource item's ContactUniqueId value."
                        ]
                  }
                """

        Scenario: 20 Ensure client should get 403 When associating a nonexistent student
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
                          "No relationships have been established between the caller's education organization id claims ('255901901', '255901902') and the resource item's StudentUniqueId value."
                        ]
                  }
                """

Rule: Associate more than one students with a contact

        Scenario: 21 Ensure client can retrieve the contact using any associated student's edorg id when associated with multiple students

            When a POST request is made to "/ed-fi/contacts" with
                """
                {
                    "contactUniqueId": "C81127",
                    "firstName": "John",
                    "lastSurname": "Doe"
                 }
                """
            And the resulting id is stored in the "AssociatedContactId" variable
            Then it should respond with 201
            When a POST request is made to "/ed-fi/studentContactAssociations" with
                """
                {
                    "contactReference": {
                        "contactUniqueId": "C81127"
                    },
                    "studentReference": {
                        "studentUniqueId": "S91111"
                    },
                   "emergencyContactStatus": true
                }
                """
            Then it should respond with 201 or 200
            When a POST request is made to "/ed-fi/studentContactAssociations" with
                """
                {
                    "contactReference": {
                        "contactUniqueId": "C81127"
                    },
                    "studentReference": {
                        "studentUniqueId": "S91112"
                    },
                   "emergencyContactStatus": true
                }
                """
            Then it should respond with 201 or 200
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901902"
            When a GET request is made to "/ed-fi/contacts?contactUniqueId=C81127"
            Then it should respond with 200
            And the response body is
                """
                [{
                  "id": "{AssociatedContactId}",
                  "contactUniqueId": "C81127",
                  "firstName": "John",
                  "lastSurname": "Doe"
                }]
                """
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901901"
            When a GET request is made to "/ed-fi/contacts?contactUniqueId=C81127"
            Then it should respond with 200
            And the response body is
                """
                [{
                  "id": "{AssociatedContactId}",
                  "contactUniqueId": "C81127",
                  "firstName": "John",
                  "lastSurname": "Doe"
                }]
                """

Rule: Associate more than one contacts with a student

Background:
          Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901904"
              And the system has these "schools"
                  | schoolId  | nameOfInstitution   | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 255901904 | Authorized school   | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |

              And the system has these "students"
                  | studentUniqueId | firstName            | lastSurname | birthDate  |
                  | "S91114"        | Unauthorized student | student-ln  | 2008-01-01 |

              And the system has these "studentSchoolAssociations"
                  | schoolReference           | studentReference                | entryGradeLevelDescriptor                          | entryDate  |
                  | { "schoolId": 255901904 } | { "studentUniqueId": "S91114" } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
              
        Scenario: 22 Ensure client can retrieve only the associated contacts using student's edorg id
            When a POST request is made to "/ed-fi/contacts" with
                """
                {
                    "contactUniqueId": "C81130",
                    "firstName": "Jim",
                    "lastSurname": "Doe"
                 }
                """
            And the resulting id is stored in the "AssociatedContactId1" variable
            Then it should respond with 201

            When a POST request is made to "/ed-fi/contacts" with
                """
                {
                    "contactUniqueId": "C81131",
                    "firstName": "John",
                    "lastSurname": "Doe"
                 }
                """
            And the resulting id is stored in the "AssociatedContactId2" variable
            Then it should respond with 201

            When a POST request is made to "/ed-fi/contacts" with
                """
                {
                    "contactUniqueId": "C81132",
                    "firstName": "NotAssociatedFN",
                    "lastSurname": "NotAssociatedLN"
                 }
                """
            Then it should respond with 201

            When a POST request is made to "/ed-fi/studentContactAssociations" with
                """
                {
                    "contactReference": {
                        "contactUniqueId": "C81130"
                    },
                    "studentReference": {
                        "studentUniqueId": "S91114"
                    },
                   "emergencyContactStatus": true
                }
                """
            Then it should respond with 201 or 200
            When a POST request is made to "/ed-fi/studentContactAssociations" with
                """
                {
                    "contactReference": {
                        "contactUniqueId": "C81131"
                    },
                    "studentReference": {
                        "studentUniqueId": "S91114"
                    },
                   "emergencyContactStatus": true
                }
                """
            Then it should respond with 201 or 200
            When a GET request is made to "/ed-fi/contacts"
            Then it should respond with 200
            And the response body is
                """
                [{
                  "id": "{AssociatedContactId1}",
                  "contactUniqueId": "C81130",
                  "firstName": "Jim",
                  "lastSurname": "Doe"
                },
                {
                  "id": "{AssociatedContactId2}",
                  "contactUniqueId": "C81131",
                  "firstName": "John",
                  "lastSurname": "Doe"
                }
                ]
                """
