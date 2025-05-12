Feature: RelationshipsWithEdOrgsAndContacts Authorization

        Background:
            Given the claimSet "EdFiAPIPublisherWriter" is authorized with educationOrganizationIds "255901001, 244901"
              And the system has these "schoolYearTypes"
                  | schoolYear | currentSchoolYear | schoolYearDescription |
                  | 2023       | true              | "year 2023"           |
              And the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade               |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school |

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

             When a POST request is made to "/ed-fi/studentContactAssociations" with
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
             Then it should respond with 201
        Scenario: 02 Ensure client can retrieve a StudentContactAssociation
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

        Scenario: 04 Ensure client can delete a StudentContactAssociation
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
        Background:
            Given the claimSet "E2E-NoFurtherAuthRequiredClaimSet" is authorized with educationOrganizationIds "255901901"

        Scenario: 07 Ensure client can create a Contact
             When a POST request is made to "/ed-fi/contacts" with
                  """
                  {
                      "contactUniqueId": "C81111",
                      "firstName": "John",
                      "lastSurname": "Doe"
                  }
                  """
             Then it should respond with 201

        Scenario: 08 Ensure client can retrieve a contact

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
                    "lastSurname": "Doe"
                  }
                  """
             Then it should respond with 204

        Scenario: 10 Ensure client can delete a contact
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
        Scenario: 12 Ensure invalid claimSet can not get a contacts
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
