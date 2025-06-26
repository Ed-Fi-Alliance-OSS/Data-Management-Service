Feature: RelationshipsWithEdOrgsAndContacts Authorization

        Background:
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901901, 25590190200000"
              And the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade               |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school |
                  | uri://ed-fi.org/SexDescriptor#Female                           |
              And the system has these "schools"
                  | schoolId       | nameOfInstitution   | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 255901901      | Authorized school   | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
                  | 25590190200000 | Authorized school 2 | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName            | lastSurname | birthDate  |
                  | "S91111"        | Authorized student   | student-ln  | 2008-01-01 |
                  | "S91112"        | Unauthorized student | student-ln  | 2008-01-01 |
              And the system has these "contacts"
                  | contactUniqueId | firstName          | lastSurname |
                  | "C91111"        | Authorized contact | contact-ln  |
                  | "C91112"        | Authorized contact | contact-ln  |
              And the system has these "studentSchoolAssociations"
                  | schoolReference                | studentReference                | entryGradeLevelDescriptor                          | entryDate  |
                  | { "schoolId": 255901901 }      | { "studentUniqueId": "S91111" } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
                  | { "schoolId": 25590190200000 } | { "studentUniqueId": "S91112" } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |

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
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "25590190200000"
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
                      "detail": "Access to the resource could not be authorized. Hint: You may need to create a corresponding 'StudentContactAssociation' item.",
                      "type": "urn:ed-fi:api:security:authorization:",
                      "title": "Authorization Denied",
                      "status": 403,
                      "validationErrors": {},
                      "errors": [
                            "No relationships have been established between the caller's education organization id claims ('255901901', '25590190200000') and the resource item's ContactUniqueId value."
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
                            "No relationships have been established between the caller's education organization id claims ('255901901', '25590190200000') and the resource item's ContactUniqueId value."
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
                            "No relationships have been established between the caller's education organization id claims ('255901901', '25590190200000') and the resource item's ContactUniqueId value."
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
                            "No relationships have been established between the caller's education organization id claims ('255901901', '25590190200000') and the resource item's StudentUniqueId value."
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
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "25590190200000"
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
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 255901904 | Authorized school | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |

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

    Rule: Edge cases are properly authorized
        Scenario: 50 Ensure client can retrieve a Contact after an SSA has been created
            # Change to use long EdOrgIds when DMS-706 is done
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1255901001, 1255901002, 1255901003"
              And the resulting token is stored in the "EdFiSandbox_full_access" variable
              And the system has these "schools"
                  | schoolId   | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 1255901001 | Test school 1     | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
                  | 1255901002 | Test school 2     | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
                  | 1255901003 | Test school 3     | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName  | lastSurname | birthDate  |
                  | "121"           | student-fn | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | studentReference             | schoolReference            | entryGradeLevelDescriptor                          | entryDate  |
                  | { "studentUniqueId": "121" } | { "schoolId": 1255901001 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
              And the system has these "contacts"
                  | _storeResultingIdInVariable | contactUniqueId | firstName  | lastSurname |
                  | ContactId                   | "121"           | contact-fn | contact-ln  |
              And the system has these "studentContactAssociations"
                  | studentReference             | contactReference             |
                  | { "studentUniqueId": "121" } | { "contactUniqueId": "121" } |

            # Assert that token with '1255901001' access can retrieve a Contact
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1255901001"
             When a GET request is made to "/ed-fi/contacts/{ContactId}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/contacts?contactUniqueId=121"
             Then it should respond with 200
              And the response body is
                  """
                    [
                      {
                        "firstName": "contact-fn",
                        "contactUniqueId": "121",
                        "id": "{ContactId}",
                        "lastSurname": "contact-ln"
                      }
                    ]
                  """

            # Assert that token with '1255901002' access can not retrieve a Contact
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1255901002"
             When a GET request is made to "/ed-fi/contacts/{ContactId}"
             Then it should respond with 403

             When a GET request is made to "/ed-fi/contacts?contactUniqueId=121"
             Then it should respond with 200
              And the response body is
                  """
                  [
                  ]
                  """

            # Assert that token with '1255901003' access can not retrieve a Contact
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1255901003"
             When a GET request is made to "/ed-fi/contacts/{ContactId}"
             Then it should respond with 403

             When a GET request is made to "/ed-fi/contacts?contactUniqueId=121"
             Then it should respond with 200
              And the response body is
                  """
                  [
                  ]
                  """

            # Create SSA for School '1255901002'
            Given the token gets switched to the one in the "EdFiSandbox_full_access" variable

             When a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                          "schoolId": 1255901002
                      },
                      "studentReference": {
                          "studentUniqueId": "121"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 201

            # Assert that token with '1255901001' access continues to be able to retrieve a Contact
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1255901001"
             When a GET request is made to "/ed-fi/contacts/{ContactId}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/contacts?contactUniqueId=121"
             Then it should respond with 200
              And the response body is
                  """
                    [
                      {
                        "firstName": "contact-fn",
                        "contactUniqueId": "121",
                        "id": "{ContactId}",
                        "lastSurname": "contact-ln"
                      }
                    ]
                  """

            # Assert that token with '1255901002' access now is able able to retrieve a Contact
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1255901002"
             When a GET request is made to "/ed-fi/contacts/{ContactId}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/contacts?contactUniqueId=121"
             Then it should respond with 200
              And the response body is
                  """
                    [
                      {
                        "firstName": "contact-fn",
                        "contactUniqueId": "121",
                        "id": "{ContactId}",
                        "lastSurname": "contact-ln"
                      }
                    ]
                  """

            # Assert that token with '1255901003' access continues to not being able to retrieve a Contact
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1255901003"
             When a GET request is made to "/ed-fi/contacts/{ContactId}"
             Then it should respond with 403

             When a GET request is made to "/ed-fi/contacts?contactUniqueId=121"
             Then it should respond with 200
              And the response body is
                  """
                  [
                  ]
                  """

        Scenario: 51 Ensure client can no longer retrieve a Contact after the SSA has been deleted
            # Change to use long EdOrgIds when DMS-706 is done
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1355901001, 1355901002, 1355901003"
              And the resulting token is stored in the "EdFiSandbox_full_access" variable
              And the system has these "schools"
                  | schoolId   | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 1355901001 | Test school 1     | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
                  | 1355901002 | Test school 2     | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
                  | 1355901003 | Test school 3     | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName  | lastSurname | birthDate  |
                  | "131"           | student-fn | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | _storeResultingIdInVariable | studentReference             | schoolReference            | entryGradeLevelDescriptor                          | entryDate  |
                  |                             | { "studentUniqueId": "131" } | { "schoolId": 1355901001 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
                  | StudentSchoolAssociationId  | { "studentUniqueId": "131" } | { "schoolId": 1355901002 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
                  |                             | { "studentUniqueId": "131" } | { "schoolId": 1355901003 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
              And the system has these "contacts"
                  | _storeResultingIdInVariable | contactUniqueId | firstName  | lastSurname |
                  | ContactId                   | "131"           | contact-fn | contact-ln  |
              And the system has these "studentContactAssociations"
                  | studentReference             | contactReference             |
                  | { "studentUniqueId": "131" } | { "contactUniqueId": "131" } |

            # Assert that token with '1355901001' access can retrieve a Contact
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1355901001"
             When a GET request is made to "/ed-fi/contacts/{ContactId}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/contacts?contactUniqueId=131"
             Then it should respond with 200
              And the response body is
                  """
                    [
                      {
                        "firstName": "contact-fn",
                        "contactUniqueId": "131",
                        "id": "{ContactId}",
                        "lastSurname": "contact-ln"
                      }
                    ]
                  """

            # Assert that token with '1355901002' access can retrieve a Contact
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1355901002"
             When a GET request is made to "/ed-fi/contacts/{ContactId}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/contacts?contactUniqueId=131"
             Then it should respond with 200
              And the response body is
                  """
                    [
                      {
                        "firstName": "contact-fn",
                        "contactUniqueId": "131",
                        "id": "{ContactId}",
                        "lastSurname": "contact-ln"
                      }
                    ]
                  """

            # Assert that token with '1355901003' access can retrieve a Contact
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1355901003"
             When a GET request is made to "/ed-fi/contacts/{ContactId}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/contacts?contactUniqueId=131"
             Then it should respond with 200
              And the response body is
                  """
                    [
                      {
                        "firstName": "contact-fn",
                        "contactUniqueId": "131",
                        "id": "{ContactId}",
                        "lastSurname": "contact-ln"
                      }
                    ]
                  """

            # Delete SSA for School '1355901002'
            Given the token gets switched to the one in the "EdFiSandbox_full_access" variable
             When a DELETE request is made to "/ed-fi/studentSchoolAssociations/{StudentSchoolAssociationId}"
             Then it should respond with 204

            # Assert that token with '1355901001' access continues to be able to retrieve a Contact
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1355901001"
             When a GET request is made to "/ed-fi/contacts/{ContactId}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/contacts?contactUniqueId=131"
             Then it should respond with 200
              And the response body is
                  """
                    [
                      {
                        "firstName": "contact-fn",
                        "contactUniqueId": "131",
                        "id": "{ContactId}",
                        "lastSurname": "contact-ln"
                      }
                    ]
                  """

            # Assert that token with '1355901002' access can no longer retrieve a Contact
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1355901002"
             When a GET request is made to "/ed-fi/contacts/{ContactId}"
             Then it should respond with 403

             When a GET request is made to "/ed-fi/contacts?contactUniqueId=131"
             Then it should respond with 200
              And the response body is
                  """
                    [
                    ]
                  """

            # Assert that token with '1355901003' access continues to be able to retrieve a Contact
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1355901003"
             When a GET request is made to "/ed-fi/contacts/{ContactId}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/contacts?contactUniqueId=131"
             Then it should respond with 200
              And the response body is
                  """
                    [
                      {
                        "firstName": "contact-fn",
                        "contactUniqueId": "131",
                        "id": "{ContactId}",
                        "lastSurname": "contact-ln"
                      }
                    ]
                  """

        Scenario: 52 Ensure client can retrieve a Contact after the SSA has been recreated
            # Change to use long EdOrgIds when DMS-706 is done
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1455901001"
              And the resulting token is stored in the "EdFiSandbox_full_access" variable
              And the system has these "schools"
                  | schoolId   | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 1455901001 | Test school 1     | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName  | lastSurname | birthDate  |
                  | "141"           | student-fn | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | _storeResultingIdInVariable | studentReference             | schoolReference            | entryGradeLevelDescriptor                          | entryDate  |
                  | StudentSchoolAssociationId  | { "studentUniqueId": "141" } | { "schoolId": 1455901001 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
              And the system has these "contacts"
                  | _storeResultingIdInVariable | contactUniqueId | firstName  | lastSurname |
                  | ContactId                   | "141"           | contact-fn | contact-ln  |
              And the system has these "studentContactAssociations"
                  | studentReference             | contactReference             |
                  | { "studentUniqueId": "141" } | { "contactUniqueId": "141" } |

            # Assert that token with '1455901001' access can retrieve a Contact
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1455901001"
             When a GET request is made to "/ed-fi/contacts/{ContactId}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/contacts?contactUniqueId=141"
             Then it should respond with 200
              And the response body is
                  """
                    [
                      {
                        "firstName": "contact-fn",
                        "contactUniqueId": "141",
                        "id": "{ContactId}",
                        "lastSurname": "contact-ln"
                      }
                    ]
                  """

            # Delete SSA
            Given the token gets switched to the one in the "EdFiSandbox_full_access" variable
             When a DELETE request is made to "/ed-fi/studentSchoolAssociations/{StudentSchoolAssociationId}"
             Then it should respond with 204

             # Re-create SSA
             When a POST request is made to "/ed-fi/studentSchoolAssociations" with
                  """
                  {
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                          "schoolId": 1455901001
                      },
                      "studentReference": {
                          "studentUniqueId": "141"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 201

            # Assert that token with '1455901001' access continues to be able to retrieve a Contact
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1455901001"
             When a GET request is made to "/ed-fi/contacts/{ContactId}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/contacts?contactUniqueId=141"
             Then it should respond with 200
              And the response body is
                  """
                    [
                      {
                        "firstName": "contact-fn",
                        "contactUniqueId": "141",
                        "id": "{ContactId}",
                        "lastSurname": "contact-ln"
                      }
                    ]
                  """

        Scenario: 53 Ensure client can retrieve a Contact after the SSA has been updated to a new School
            # Change to use long EdOrgIds when DMS-706 is done
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1555901001, 1555901002, 1555901003"
              And the resulting token is stored in the "EdFiSandbox_full_access" variable
              And the system has these "schools"
                  | schoolId   | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 1555901001 | Test school 1     | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
                  | 1555901002 | Test school 2     | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
                  | 1555901003 | Test school 3     | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName  | lastSurname | birthDate  |
                  | "151"           | student-fn | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | _storeResultingIdInVariable | studentReference             | schoolReference            | entryGradeLevelDescriptor                          | entryDate  |
                  |                             | { "studentUniqueId": "151" } | { "schoolId": 1555901001 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
                  | StudentSchoolAssociationId  | { "studentUniqueId": "151" } | { "schoolId": 1555901002 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
              And the system has these "contacts"
                  | _storeResultingIdInVariable | contactUniqueId | firstName  | lastSurname |
                  | ContactId                   | "151"           | contact-fn | contact-ln  |
              And the system has these "studentContactAssociations"
                  | studentReference             | contactReference             |
                  | { "studentUniqueId": "151" } | { "contactUniqueId": "151" } |

            # Assert that token with '1555901001' access can retrieve a Contact
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1555901001"
             When a GET request is made to "/ed-fi/Contacts/{ContactId}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/Contacts?contactUniqueId=151"
             Then it should respond with 200
              And the response body is
                  """
                  [
                      {
                        "firstName": "contact-fn",
                        "contactUniqueId": "151",
                        "id": "{ContactId}",
                        "lastSurname": "contact-ln"
                      }
                  ]
                  """

            # Assert that token with '1555901002' access can retrieve a Contact
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1555901002"
             When a GET request is made to "/ed-fi/Contacts/{ContactId}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/Contacts?contactUniqueId=151"
             Then it should respond with 200
              And the response body is
                  """
                  [
                      {
                        "firstName": "contact-fn",
                        "contactUniqueId": "151",
                        "id": "{ContactId}",
                        "lastSurname": "contact-ln"
                      }
                  ]
                  """

            # Assert that token with '1555901003' access can not retrieve a Contact
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1555901003"
             When a GET request is made to "/ed-fi/Contacts/{ContactId}"
             Then it should respond with 403

             When a GET request is made to "/ed-fi/Contacts?contactUniqueId=151"
             Then it should respond with 200
              And the response body is
                  """
                  [
                  ]
                  """

            # Update SSA's School from '1555901002' to '1555901003'
            Given the token gets switched to the one in the "EdFiSandbox_full_access" variable

             When a PUT request is made to "/ed-fi/StudentSchoolAssociations/{StudentSchoolAssociationId}" with
                  """
                  {
                      "id":"{StudentSchoolAssociationId}",
                      "entryDate": "2023-08-01",
                      "schoolReference": {
                          "schoolId": 1555901003
                      },
                      "studentReference": {
                          "studentUniqueId": "151"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade",
                      "exitWithdrawDate": "2025-01-01"
                  }
                  """
             Then it should respond with 204

            # Assert that token with '1555901001' access continues to be able to retrieve a Contact
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1555901001"
             When a GET request is made to "/ed-fi/Contacts/{ContactId}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/Contacts?contactUniqueId=151"
             Then it should respond with 200
              And the response body is
                  """
                  [
                      {
                        "firstName": "contact-fn",
                        "contactUniqueId": "151",
                        "id": "{ContactId}",
                        "lastSurname": "contact-ln"
                      }
                  ]
                  """

            # Assert that token with '1555901002' access can no longer retrieve a Contact
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1555901002"
             When a GET request is made to "/ed-fi/Contacts/{ContactId}"
             Then it should respond with 403

             When a GET request is made to "/ed-fi/Contacts?contactUniqueId=151"
             Then it should respond with 200
              And the response body is
                  """
                  [
                  ]
                  """

            # Assert that token with '1555901003' access now can retrieve a Contact
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1555901003"
             When a GET request is made to "/ed-fi/Contacts/{ContactId}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/Contacts?contactUniqueId=151"
             Then it should respond with 200
              And the response body is
                  """
                  [
                      {
                        "firstName": "contact-fn",
                        "contactUniqueId": "151",
                        "id": "{ContactId}",
                        "lastSurname": "contact-ln"
                      }
                  ]
                  """

        Scenario: 54 Ensure client can retrieve a Contact after the SSA has been updated to a new Student
            # Change to use long EdOrgIds when DMS-706 is done
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1655901001"
              And the resulting token is stored in the "EdFiSandbox_full_access" variable
              And the system has these "schools"
                  | schoolId   | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 1655901001 | Test school 1     | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName  | lastSurname | birthDate  |
                  | "161"           | student-fn | student-ln  | 2008-01-01 |
                  | "162"           | student-fn | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | _storeResultingIdInVariable | studentReference             | schoolReference            | entryGradeLevelDescriptor                          | entryDate  |
                  | StudentSchoolAssociationId1 | { "studentUniqueId": "161" } | { "schoolId": 1655901001 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
                  | StudentSchoolAssociationId2 | { "studentUniqueId": "162" } | { "schoolId": 1655901001 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
              And the system has these "contacts"
                  | _storeResultingIdInVariable | contactUniqueId | firstName  | lastSurname |
                  | ContactId1                  | "161"           | contact-fn | contact-ln  |
                  | ContactId2                  | "162"           | contact-fn | contact-ln  |
              And the system has these "studentContactAssociations"
                  | studentReference             | contactReference             |
                  | { "studentUniqueId": "161" } | { "contactUniqueId": "161" } |
                  | { "studentUniqueId": "162" } | { "contactUniqueId": "162" } |

             # Assert that token can retrieve the Contact A
             When a GET request is made to "/ed-fi/Contacts/{ContactId1}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/Contacts?contactUniqueId=161"
             Then it should respond with 200
              And the response body is
                  """
                  [
                     {
                        "firstName": "contact-fn",
                        "contactUniqueId": "161",
                        "id": "{ContactId1}",
                        "lastSurname": "contact-ln"
                      }
                  ]
                  """

             # Assert that token can retrieve the Contact B
             When a GET request is made to "/ed-fi/Contacts/{ContactId2}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/Contacts?contactUniqueId=162"
             Then it should respond with 200
              And the response body is
                  """
                  [
                     {
                        "firstName": "contact-fn",
                        "contactUniqueId": "162",
                        "id": "{ContactId2}",
                        "lastSurname": "contact-ln"
                      }
                  ]
                  """

             # Update SSA's Student from A to B
             When a PUT request is made to "/ed-fi/StudentSchoolAssociations/{StudentSchoolAssociationId1}" with
                  """
                  {
                      "id":"{StudentSchoolAssociationId1}",
                      "entryDate": "2024-08-01",
                      "schoolReference": {
                          "schoolId": 1655901001
                      },
                      "studentReference": {
                          "studentUniqueId": "162"
                      },
                      "entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                  }
                  """
             Then it should respond with 204

             # Assert that token can no longer retrieve the Contact A
             When a GET request is made to "/ed-fi/Contacts/{ContactId1}"
             Then it should respond with 403

             When a GET request is made to "/ed-fi/Contacts?contactUniqueId=161"
             Then it should respond with 200
              And the response body is
                  """
                  [
                  ]
                  """

             # Assert that token continues to be able to retrieve the Contact B
             When a GET request is made to "/ed-fi/Contacts/{ContactId2}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/Contacts?contactUniqueId=162"
             Then it should respond with 200
              And the response body is
                  """
                  [
                     {
                        "firstName": "contact-fn",
                        "contactUniqueId": "162",
                        "id": "{ContactId2}",
                        "lastSurname": "contact-ln"
                      }
                  ]
                  """

             # Delete one of the SSAs
             When a DELETE request is made to "/ed-fi/StudentSchoolAssociations/{StudentSchoolAssociationId2}"
             Then it should respond with 204

             # Assert that token continues to not being able to retrieve the Contact A
             When a GET request is made to "/ed-fi/Contacts/{ContactId1}"
             Then it should respond with 403

             When a GET request is made to "/ed-fi/Contacts?contactUniqueId=161"
             Then it should respond with 200
              And the response body is
                  """
                  [
                  ]
                  """

             # Assert that token continues to be able to retrieve the Contact B
             When a GET request is made to "/ed-fi/Contacts/{ContactId2}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/Contacts?contactUniqueId=162"
             Then it should respond with 200
              And the response body is
                  """
                  [
                     {
                        "firstName": "contact-fn",
                        "contactUniqueId": "162",
                        "id": "{ContactId2}",
                        "lastSurname": "contact-ln"
                      }
                  ]
                  """

        Scenario: 55 Ensure client can retrieve a Contact after the SCA has been recreated
            # Change to use long EdOrgIds when DMS-706 is done
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1755901001, 1755901002, 1755901003"
              And the resulting token is stored in the "EdFiSandbox_full_access" variable
              And the system has these "schools"
                  | schoolId   | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 1755901001 | Test school 1     | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
                  | 1755901002 | Test school 2     | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
                  | 1755901003 | Test school 3     | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName  | lastSurname | birthDate  |
                  | "171"           | student-fn | student-ln  | 2008-01-01 |
                  | "172"           | student-fn | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | studentReference             | schoolReference            | entryGradeLevelDescriptor                          | entryDate  |
                  | { "studentUniqueId": "171" } | { "schoolId": 1755901001 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
                  | { "studentUniqueId": "171" } | { "schoolId": 1755901002 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
                  | { "studentUniqueId": "172" } | { "schoolId": 1755901003 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
              And the system has these "contacts"
                  | _storeResultingIdInVariable | contactUniqueId | firstName  | lastSurname |
                  | ContactId                   | "171"           | contact-fn | contact-ln  |
              And the system has these "studentContactAssociations"
                  | _storeResultingIdInVariable | studentReference             | contactReference             |
                  | StudentContactAssociationId | { "studentUniqueId": "171" } | { "contactUniqueId": "171" } |
                  |                             | { "studentUniqueId": "172" } | { "contactUniqueId": "171" } |

            # Assert that token with '1755901001' access can retrieve a Contact
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1755901001"
             When a GET request is made to "/ed-fi/contacts/{ContactId}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/contacts?contactUniqueId=171"
             Then it should respond with 200
              And the response body is
                  """
                    [
                      {
                        "firstName": "contact-fn",
                        "contactUniqueId": "171",
                        "id": "{ContactId}",
                        "lastSurname": "contact-ln"
                      }
                    ]
                  """

            # Assert that token with '1755901002' access can retrieve a Contact
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1755901002"
             When a GET request is made to "/ed-fi/contacts/{ContactId}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/contacts?contactUniqueId=171"
             Then it should respond with 200
              And the response body is
                  """
                    [
                      {
                        "firstName": "contact-fn",
                        "contactUniqueId": "171",
                        "id": "{ContactId}",
                        "lastSurname": "contact-ln"
                      }
                    ]
                  """

            # Assert that token with '1755901003' access can retrieve a Contact
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1755901003"
             When a GET request is made to "/ed-fi/contacts/{ContactId}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/contacts?contactUniqueId=171"
             Then it should respond with 200
              And the response body is
                  """
                    [
                      {
                        "firstName": "contact-fn",
                        "contactUniqueId": "171",
                        "id": "{ContactId}",
                        "lastSurname": "contact-ln"
                      }
                    ]
                  """

            # Delete SCA for School '1755901001', and '1755901002'
            Given the token gets switched to the one in the "EdFiSandbox_full_access" variable
             When a DELETE request is made to "/ed-fi/studentContactAssociations/{StudentContactAssociationId}"
             Then it should respond with 204

            # Assert that token with '1755901001' access can no longer retrieve a Contact
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1755901001"
             When a GET request is made to "/ed-fi/contacts/{ContactId}"
             Then it should respond with 403

             When a GET request is made to "/ed-fi/contacts?contactUniqueId=171"
             Then it should respond with 200
              And the response body is
                  """
                  [
                  ]
                  """

            # Assert that token with '1755901002' access can no longer retrieve a Contact
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1755901002"
             When a GET request is made to "/ed-fi/contacts/{ContactId}"
             Then it should respond with 403

             When a GET request is made to "/ed-fi/contacts?contactUniqueId=171"
             Then it should respond with 200
              And the response body is
                  """
                  [
                  ]
                  """

            # Assert that token with '1755901003' access continues to be able to retrieve a Contact
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1755901003"
             When a GET request is made to "/ed-fi/contacts/{ContactId}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/contacts?contactUniqueId=171"
             Then it should respond with 200
              And the response body is
                  """
                  [
                      {
                      "firstName": "contact-fn",
                      "contactUniqueId": "171",
                      "id": "{ContactId}",
                      "lastSurname": "contact-ln"
                      }
                  ]
                  """

            # Recreate SCA for School '1755901001', and '1755901002'
            Given the token gets switched to the one in the "EdFiSandbox_full_access" variable

             When a POST request is made to "/ed-fi/studentContactAssociations" with
                  """
                  {
                      "studentReference": {
                          "studentUniqueId": "171"
                      },
                      "contactReference": {
                          "contactUniqueId": "171"
                      }
                  }
                  """
             Then it should respond with 201

            # Assert that token with '1755901001' access can retrieve a Contact
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1755901001"
             When a GET request is made to "/ed-fi/contacts/{ContactId}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/contacts?contactUniqueId=171"
             Then it should respond with 200
              And the response body is
                  """
                    [
                      {
                        "firstName": "contact-fn",
                        "contactUniqueId": "171",
                        "id": "{ContactId}",
                        "lastSurname": "contact-ln"
                      }
                    ]
                  """

            # Assert that token with '1755901002' access can retrieve a Contact
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1755901002"
             When a GET request is made to "/ed-fi/contacts/{ContactId}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/contacts?contactUniqueId=171"
             Then it should respond with 200
              And the response body is
                  """
                    [
                      {
                        "firstName": "contact-fn",
                        "contactUniqueId": "171",
                        "id": "{ContactId}",
                        "lastSurname": "contact-ln"
                      }
                    ]
                  """

            # Assert that token with '1755901003' access continues to be able to retrieve a Contact
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "1755901003"
             When a GET request is made to "/ed-fi/contacts/{ContactId}"
             Then it should respond with 200

             When a GET request is made to "/ed-fi/contacts?contactUniqueId=171"
             Then it should respond with 200
              And the response body is
                  """
                    [
                      {
                        "firstName": "contact-fn",
                        "contactUniqueId": "171",
                        "id": "{ContactId}",
                        "lastSurname": "contact-ln"
                      }
                    ]
                  """
