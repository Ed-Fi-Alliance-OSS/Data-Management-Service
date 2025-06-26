Feature: RelationshipsWithEdOrgsAndStaff Authorization

        Background:
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901,25590100100000"
              And the system has these descriptors
                  | descriptorValue                                               |
                  | uri://ed-fi.org/ProgramAssignmentDescriptor#Regular Education |

              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | categories                                                                                                                         | localEducationAgencyCategoryDescriptor                         | nameOfInstitution |
                  | 255901                 | [ { "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Local Education Agency"} ] | uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Charter | LEA-100001        |
              And the system has these "schools"
                  | schoolId       | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                   | localEducationAgencyReference       |
                  | 25590100100000 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] | { "localEducationAgencyId": 255901} |
              And the system has these "people"
                  | personId | sourceSystemDescriptor                      |
                  | p001     | uri://ed-fi.org/SourceSystemDescriptor#Pass |
              And the system has these "Staffs"
                  | staffUniqueId | firstName | lastSurname |
                  | s0001         | peterson  | Buck        |
                  | s0002         | Steve     | Buck        |
                  | s0003         | Tim       | Buck        |
                  | s0004         | Adam      | Buck        |
                  | s0005         | Francis   | Buck        |
              And the system has these "staffEducationOrganizationAssignmentAssociations"
                  | beginDate  | staffClassificationDescriptor                         | educationOrganizationReference                | staffReference                 |
                  | 10/10/2020 | uri://ed-fi.org/StaffClassificationDescriptor#Teacher | { "educationOrganizationId": 25590100100000 } | {  "staffUniqueId": "s0001"  } |
              And the system has these "staffEducationOrganizationEmploymentAssociations"
                  | hireDate   | employmentStatusDescriptor                         | educationOrganizationReference                | staffReference                 |
                  | 10/10/2020 | uri://ed-fi.org/employmentStatusDescriptor#Teacher | { "educationOrganizationId": 25590100100000 } | {  "staffUniqueId": "s0004"  } |

    Rule: staffEducationOrganizationAssignmentAssociations CRUD is properly authorized

        Scenario: 01 Ensure client can authorize create a staffSchoolAssociations when the staff is assigned to the school using staffEducationOrganizationAssignmentAssociations

             When a POST request is made to "/ed-fi/staffSchoolAssociations" with
                  """
                     {
                        "schoolReference": {
                            "schoolId": 25590100100000
                        },
                        "staffReference": {
                            "staffUniqueId": "s0001"
                        },
                        "programAssignmentDescriptor": "uri://ed-fi.org/ProgramAssignmentDescriptor#Regular Education"
                    }
                  """
             Then it should respond with 201


        Scenario: 02 Ensure client cannot  authorize create a staffSchoolAssociations when the staff is not assigned to the school school using staffEducationOrganizationAssignmentAssociations
             When a POST request is made to "/ed-fi/staffSchoolAssociations" with
                  """
                     {
                        "schoolReference": {
                            "schoolId": 25590100100000
                        },
                        "staffReference": {
                            "staffUniqueId": "s0002"
                        },
                        "programAssignmentDescriptor": "uri://ed-fi.org/ProgramAssignmentDescriptor#Regular Education"
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
                      "correlationId": "0HNCJPIJKHR7A:00000019",
                      "validationErrors": {},
                      "errors": [
                        "No relationships have been established between the caller's education organization id claims ('255901', '25590100100000') and the resource item's StaffUniqueId value."
                      ]
                    }
                  """


        Scenario: 03 Ensure client cannot  authorize update a staffSchoolAssociations when the staff is not assigned to the school  using staffEducationOrganizationAssignmentAssociations

             When a POST request is made to "/ed-fi/staffSchoolAssociations" with
                  """
                      {
                      "schoolReference": {
                          "schoolId": 25590100100000
                      },
                      "staffReference": {
                          "staffUniqueId": "s0001"
                      },
                      "programAssignmentDescriptor": "uri://ed-fi.org/ProgramAssignmentDescriptor#Regular Education"
                  }
                  """
             Then it should respond with 201 or 200

             When a PUT request is made to "/ed-fi/staffSchoolAssociations/{id}" with
                  """
                      {
                      "id":"{id}",
                      "schoolReference": {
                          "schoolId": 25590100100000
                      },
                      "staffReference": {
                          "staffUniqueId": "s0002"
                      },
                      "programAssignmentDescriptor": "uri://ed-fi.org/ProgramAssignmentDescriptor#Regular Education"
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
                          "correlationId": "0HNCJPIJKHR7K:0000001A",
                          "validationErrors": {},
                          "errors": [
                          "No relationships have been established between the caller's education organization id claims ('255901', '25590100100000') and the resource item's StaffUniqueId value."
                          ]
                      }
                  """

        Scenario: 04 Ensure client can Search staffEducationOrganizationAssignmentAssociations

             When a GET request is made to "/ed-fi/staffEducationOrganizationAssignmentAssociations"
             Then it should respond with 200
              And the response body is
                  """
                    [
                      {
                        "beginDate": "2020-10-10",
                        "educationOrganizationReference": {
                          "educationOrganizationId": 25590100100000
                        },
                        "staffReference": {
                          "staffUniqueId": "s0001"
                        },
                        "staffClassificationDescriptor": "uri://ed-fi.org/StaffClassificationDescriptor#Teacher",
                        "id": "{id}"
                      }
                    ]
                  """

        Scenario: 05 Ensure client can POST staffEducationOrganizationAssignmentAssociations

             When a POST request is made to "/ed-fi/staffEducationOrganizationAssignmentAssociations" with
                  """
                    {
                      "staffClassificationDescriptor": "uri://ed-fi.org/StaffClassificationDescriptor#Teacher",
                      "educationOrganizationReference": { "educationOrganizationId": 25590100100000 },
                      "staffReference": {  "staffUniqueId": "s0002"  },
                      "beginDate": "2018-08-20",
                      "positionTitle": "Math Teacher"
                    }
                  """
             Then it should respond with 201



        Scenario: 06 Ensure client can Get staffEducationOrganizationAssignmentAssociations

             When a POST request is made to "/ed-fi/staffEducationOrganizationAssignmentAssociations" with
                  """
                    {
                      "staffClassificationDescriptor": "uri://ed-fi.org/StaffClassificationDescriptor#Teacher",
                      "educationOrganizationReference": { "educationOrganizationId": 25590100100000 },
                      "staffReference": {  "staffUniqueId": "s0001"  },
                      "beginDate": "2018-08-20",
                      "positionTitle": "Math Teacher"
                    }
                  """
             Then it should respond with 201
             When a GET request is made to "/ed-fi/staffEducationOrganizationAssignmentAssociations/{id}"
             Then it should respond with 200
              And the response body is
                  """
                      {
                        "id": "{id}",
                        "beginDate": "2018-08-20",
                        "positionTitle": "Math Teacher",
                        "staffReference": {
                          "staffUniqueId": "s0001"
                        },
                        "staffClassificationDescriptor": "uri://ed-fi.org/StaffClassificationDescriptor#Teacher",
                        "educationOrganizationReference": {
                          "educationOrganizationId": 25590100100000
                        }
                      }
                  """



        Scenario: 07 Ensure client can PUT staffEducationOrganizationAssignmentAssociations

             When a POST request is made to "/ed-fi/staffEducationOrganizationAssignmentAssociations" with
                  """
                    {
                      "staffClassificationDescriptor": "uri://ed-fi.org/StaffClassificationDescriptor#Teacher",
                      "educationOrganizationReference": { "educationOrganizationId": 25590100100000 },
                      "staffReference": {  "staffUniqueId": "s0001"  },
                      "beginDate": "2018-08-20",
                      "positionTitle": "Math Teacher"
                    }
                  """
             Then it should respond with 201 or 200
             When a PUT request is made to "/ed-fi/staffEducationOrganizationAssignmentAssociations/{id}" with
                  """
                    {
                       "id":"{id}",
                      "staffClassificationDescriptor": "uri://ed-fi.org/StaffClassificationDescriptor#Teacher",
                      "educationOrganizationReference": { "educationOrganizationId": 25590100100000 },
                      "staffReference": {  "staffUniqueId": "s0001"  },
                      "beginDate": "2018-08-20",
                      "positionTitle": "Science Teacher"
                    }
                  """
             Then it should respond with 204

        Scenario: 08 Ensure client can DELETE staffEducationOrganizationAssignmentAssociations

             When a POST request is made to "/ed-fi/staffEducationOrganizationAssignmentAssociations" with
                  """
                    {
                      "staffClassificationDescriptor": "uri://ed-fi.org/StaffClassificationDescriptor#Teacher",
                      "educationOrganizationReference": { "educationOrganizationId": 25590100100000 },
                      "staffReference": {  "staffUniqueId": "s0001"  },
                      "beginDate": "2018-08-20",
                      "positionTitle": "Math Teacher"
                    }
                  """
             Then it should respond with 201 or 200
             When a DELETE request is made to "/ed-fi/staffEducationOrganizationAssignmentAssociations/{id}"
             Then it should respond with 204

        Scenario: 09 Ensure client cannot  Create staffEducationOrganizationAssignmentAssociations with client does not have access it to educationOrganizationId
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901903"
             When a POST request is made to "/ed-fi/staffEducationOrganizationAssignmentAssociations" with
                  """
                    {
                      "staffClassificationDescriptor": "uri://ed-fi.org/StaffClassificationDescriptor#Teacher",
                      "educationOrganizationReference": { "educationOrganizationId": 25590100100000 },
                      "staffReference": {  "staffUniqueId": "s0002"  },
                      "beginDate": "2018-08-20",
                      "positionTitle": "Math Teacher"
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
                      "correlationId": "0HNCJPIJKHR9A:0000001A",
                      "validationErrors": {},
                      "errors": [
                        "No relationships have been established between the caller's education organization id claims ('255901903') and the resource item's EducationOrganizationId value."
                      ]
                    }
                  """

        Scenario: 10 Ensure client cannot get staffEducationOrganizationAssignmentAssociations with client does not have access it to educationOrganizationId
             When a POST request is made to "/ed-fi/staffEducationOrganizationAssignmentAssociations" with
                  """
                    {
                      "staffClassificationDescriptor": "uri://ed-fi.org/StaffClassificationDescriptor#Teacher",
                      "educationOrganizationReference": { "educationOrganizationId": 25590100100000 },
                      "staffReference": {  "staffUniqueId": "s0002"  },
                      "beginDate": "2018-08-20",
                      "positionTitle": "Math Teacher"
                    }
                  """
             Then it should respond with 201 or 200
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901903"
             When a GET request is made to "/ed-fi/staffEducationOrganizationAssignmentAssociations/{id}"
             Then it should respond with 403
              And the response body is
                  """
                    {
                      "detail": "Access to the resource could not be authorized. Hint: You may need to create a corresponding 'StaffSchoolAssociation' item.",
                      "type": "urn:ed-fi:api:security:authorization:",
                      "title": "Authorization Denied",
                      "status": 403,
                      "validationErrors": {},
                      "errors": [
                        "No relationships have been established between the caller's education organization id claims ('255901903') and one or more of the following properties of the resource item: 'EducationOrganizationId', 'StaffUniqueId'."
                      ]
                    }
                  """

        Scenario: 11  Ensure client cannot search staffEducationOrganizationAssignmentAssociations with client does not have access it to educationOrganizationId
             When a POST request is made to "/ed-fi/staffEducationOrganizationAssignmentAssociations" with
                  """
                    {
                      "staffClassificationDescriptor": "uri://ed-fi.org/StaffClassificationDescriptor#Teacher",
                      "educationOrganizationReference": { "educationOrganizationId": 25590100100000 },
                      "staffReference": {  "staffUniqueId": "s0002"  },
                      "beginDate": "2018-08-20",
                      "positionTitle": "Math Teacher"
                    }
                  """
             Then it should respond with 201 or 200
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901903"
             When a GET request is made to "/ed-fi/staffEducationOrganizationAssignmentAssociations/"
             Then it should respond with 200
              And the response body is
                  """
                     []
                  """

        Scenario: 12  Ensure client cannot update staffEducationOrganizationAssignmentAssociations with client does not have access it to educationOrganizationId
             When a POST request is made to "/ed-fi/staffEducationOrganizationAssignmentAssociations" with
                  """
                    {
                      "staffClassificationDescriptor": "uri://ed-fi.org/StaffClassificationDescriptor#Teacher",
                      "educationOrganizationReference": { "educationOrganizationId": 25590100100000 },
                      "staffReference": {  "staffUniqueId": "s0002"  },
                      "beginDate": "2018-08-20",
                      "positionTitle": "Math Teacher"
                    }
                  """
             Then it should respond with 201 or 200
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901903"
             When a PUT request is made to "/ed-fi/staffEducationOrganizationAssignmentAssociations/{id}" with
                  """
                    {
                       "id":"{id}",
                      "staffClassificationDescriptor": "uri://ed-fi.org/StaffClassificationDescriptor#Teacher",
                      "educationOrganizationReference": { "educationOrganizationId": 25590100100000 },
                      "staffReference": {  "staffUniqueId": "s0002"  },
                      "beginDate": "2018-08-20",
                      "positionTitle": "Science Teacher"
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
                      "No relationships have been established between the caller's education organization id claims ('255901903') and one or more of the following properties of the resource item: 'EducationOrganizationId', 'StaffUniqueId'."
                    ]
                  }
                  """

        Scenario: 13  Ensure client cannot delete staffEducationOrganizationAssignmentAssociations with client does not have access it to educationOrganizationId
             When a POST request is made to "/ed-fi/staffEducationOrganizationAssignmentAssociations" with
                  """
                    {
                      "staffClassificationDescriptor": "uri://ed-fi.org/StaffClassificationDescriptor#Teacher",
                      "educationOrganizationReference": { "educationOrganizationId": 25590100100000 },
                      "staffReference": {  "staffUniqueId": "s0002"  },
                      "beginDate": "2018-08-20",
                      "positionTitle": "Math Teacher"
                    }
                  """
             Then it should respond with 201 or 200
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901903"
             When a DELETE request is made to "/ed-fi/staffEducationOrganizationAssignmentAssociations/{id}"
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
                      "No relationships have been established between the caller's education organization id claims ('255901903') and one or more of the following properties of the resource item: 'EducationOrganizationId', 'StaffUniqueId'."
                    ]
                  }
                  """

    Rule: StaffEducationOrganizationEmploymentAssociation CRUD is properly authorized

        Scenario: 14 Ensure client can authorize create a staffSchoolAssociations when the staff is assigned to the school using StaffEducationOrganizationEmploymentAssociation

             When a POST request is made to "/ed-fi/staffSchoolAssociations" with
                  """
                     {
                        "schoolReference": {
                            "schoolId": 25590100100000
                        },
                        "staffReference": {
                            "staffUniqueId": "s0004"
                        },
                        "programAssignmentDescriptor": "uri://ed-fi.org/ProgramAssignmentDescriptor#Regular Education"
                    }
                  """
             Then it should respond with 201

        Scenario: 15 Ensure client cannot  authorize update a staffSchoolAssociations when the staff is not assigned to the school  using StaffEducationOrganizationEmploymentAssociation

             When a POST request is made to "/ed-fi/staffSchoolAssociations" with
                  """
                      {
                      "schoolReference": {
                          "schoolId": 25590100100000
                      },
                      "staffReference": {
                          "staffUniqueId": "s0004"
                      },
                      "programAssignmentDescriptor": "uri://ed-fi.org/ProgramAssignmentDescriptor#Regular Education"
                  }
                  """
             Then it should respond with 201 or 200

             When a PUT request is made to "/ed-fi/staffSchoolAssociations/{id}" with
                  """
                      {
                      "id":"{id}",
                      "schoolReference": {
                          "schoolId": 25590100100000
                      },
                      "staffReference": {
                          "staffUniqueId": "s0005"
                      },
                      "programAssignmentDescriptor": "uri://ed-fi.org/ProgramAssignmentDescriptor#Regular Education"
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
                          "correlationId": "0HNCJPIJKHR7K:0000001A",
                          "validationErrors": {},
                          "errors": [
                          "No relationships have been established between the caller's education organization id claims ('255901', '25590100100000') and the resource item's StaffUniqueId value."
                          ]
                      }
                  """

        Scenario: 16 Ensure client can GET staffEducationOrganizationEmploymentAssociations

             When a GET request is made to "/ed-fi/staffEducationOrganizationEmploymentAssociations/"
             Then it should respond with 200
              And the response body is
                  """
                    [
                      {
                        "employmentStatusDescriptor": "uri://ed-fi.org/employmentStatusDescriptor#Teacher",
                        "hireDate": "2020-10-10",
                        "educationOrganizationReference": {
                          "educationOrganizationId": 25590100100000
                        },
                        "staffReference": {
                          "staffUniqueId": "s0004"
                        },
                        "id": "{id}"
                      }
                    ]
                  """

        Scenario: 17 Ensure client can POST staffEducationOrganizationEmploymentAssociations

             When a POST request is made to "/ed-fi/staffEducationOrganizationEmploymentAssociations" with
                  """
                    {
                      "employmentStatusDescriptor": "uri://ed-fi.org/employmentStatusDescriptor#Teacher",
                      "educationOrganizationReference": { "educationOrganizationId": 25590100100000 },
                      "staffReference": {  "staffUniqueId": "s0005"  },
                      "hireDate": "2018-08-20",
                      "positionTitle": "Math Teacher"
                    }
                  """
             Then it should respond with 201

        Scenario: 18 Ensure client can GET staffEducationOrganizationEmploymentAssociations

             When a POST request is made to "/ed-fi/staffEducationOrganizationEmploymentAssociations" with
                  """
                    {
                      "employmentStatusDescriptor": "uri://ed-fi.org/employmentStatusDescriptor#Teacher",
                      "educationOrganizationReference": { "educationOrganizationId": 25590100100000 },
                      "staffReference": {  "staffUniqueId": "s0005"  },
                      "hireDate": "2018-08-20",
                      "positionTitle": "Math Teacher"
                    }
                  """
             Then it should respond with 201 or 200
             When a GET request is made to "/ed-fi/staffEducationOrganizationEmploymentAssociations/{id}"
             Then it should respond with 200
              And the response body is
                  """
                        {
                      "id": "{id}",
                      "hireDate": "2018-08-20",
                      "staffReference": {
                        "staffUniqueId": "s0005"
                      },
                      "employmentStatusDescriptor": "uri://ed-fi.org/employmentStatusDescriptor#Teacher",
                      "educationOrganizationReference": {
                        "educationOrganizationId": 25590100100000
                      }
                    }
                  """

        Scenario: 19 Ensure client can PUT staffEducationOrganizationEmploymentAssociations

             When a POST request is made to "/ed-fi/staffEducationOrganizationEmploymentAssociations" with
                  """
                    {
                      "employmentStatusDescriptor": "uri://ed-fi.org/employmentStatusDescriptor#Teacher",
                      "educationOrganizationReference": { "educationOrganizationId": 25590100100000 },
                      "staffReference": {  "staffUniqueId": "s0005"  },
                      "hireDate": "2018-08-20",
                      "positionTitle": "Math Teacher"
                    }
                  """
             Then it should respond with 201 or 200
             When a PUT request is made to "/ed-fi/staffEducationOrganizationEmploymentAssociations/{id}" with
                  """
                    {
                       "id":"{id}",
                      "employmentStatusDescriptor": "uri://ed-fi.org/employmentStatusDescriptor#Teacher",
                      "educationOrganizationReference": { "educationOrganizationId": 25590100100000 },
                      "staffReference": {  "staffUniqueId": "s0005"  },
                      "hireDate": "2018-08-20",
                      "positionTitle": "Science Teacher"
                    }
                  """
             Then it should respond with 204

        Scenario: 20 Ensure client can DELETE staffEducationOrganizationEmploymentAssociations

             When a POST request is made to "/ed-fi/staffEducationOrganizationEmploymentAssociations" with
                  """
                    {
                      "employmentStatusDescriptor": "uri://ed-fi.org/employmentStatusDescriptor#Teacher",
                      "educationOrganizationReference": { "educationOrganizationId": 25590100100000 },
                      "staffReference": {  "staffUniqueId": "s0005"  },
                      "hireDate": "2018-08-20",
                      "positionTitle": "Math Teacher"
                    }
                  """
             Then it should respond with 201 or 200
             When a DELETE request is made to "/ed-fi/staffEducationOrganizationEmploymentAssociations/{id}"
             Then it should respond with 204

        Scenario: 21 Ensure client cannot  Create staffEducationOrganizationEmploymentAssociations with client does not have access it to educationOrganizationId
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901903"
             When a POST request is made to "/ed-fi/staffEducationOrganizationEmploymentAssociations" with
                  """
                    {
                      "employmentStatusDescriptor": "uri://ed-fi.org/employmentStatusDescriptor#Teacher",
                      "educationOrganizationReference": { "educationOrganizationId": 25590100100000 },
                      "staffReference": {  "staffUniqueId": "s0005"  },
                      "hireDate": "2018-08-20",
                      "positionTitle": "Math Teacher"
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
                      "correlationId": "0HNCJPIJKHR9A:0000001A",
                      "validationErrors": {},
                      "errors": [
                        "No relationships have been established between the caller's education organization id claims ('255901903') and the resource item's EducationOrganizationId value."
                      ]
                    }
                  """

        Scenario: 22 Ensure client cannot  get staffEducationOrganizationEmploymentAssociations with client does not have access it to educationOrganizationId
             When a POST request is made to "/ed-fi/staffEducationOrganizationEmploymentAssociations" with
                  """
                    {
                      "employmentStatusDescriptor": "uri://ed-fi.org/employmentStatusDescriptor#Teacher",
                      "educationOrganizationReference": { "educationOrganizationId": 25590100100000 },
                      "staffReference": {  "staffUniqueId": "s0005"  },
                      "hireDate": "2018-08-20",
                      "positionTitle": "Math Teacher"
                    }
                  """
             Then it should respond with 201 or 200
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901903"
             When a GET request is made to "/ed-fi/staffEducationOrganizationEmploymentAssociations/{id}"
             Then it should respond with 403
              And the response body is
                  """
                    {
                      "detail": "Access to the resource could not be authorized. Hint: You may need to create a corresponding 'StaffSchoolAssociation' item.",
                      "type": "urn:ed-fi:api:security:authorization:",
                      "title": "Authorization Denied",
                      "status": 403,
                      "validationErrors": {},
                      "errors": [
                        "No relationships have been established between the caller's education organization id claims ('255901903') and one or more of the following properties of the resource item: 'EducationOrganizationId', 'StaffUniqueId'."
                      ]
                    }
                  """

        Scenario: 23 Ensure client cannot  search staffEducationOrganizationEmploymentAssociations with client does not have access it to educationOrganizationId
             When a POST request is made to "/ed-fi/staffEducationOrganizationEmploymentAssociations" with
                  """
                    {
                      "employmentStatusDescriptor": "uri://ed-fi.org/employmentStatusDescriptor#Teacher",
                      "educationOrganizationReference": { "educationOrganizationId": 25590100100000 },
                      "staffReference": {  "staffUniqueId": "s0005"  },
                      "hireDate": "2018-08-20",
                      "positionTitle": "Math Teacher"
                    }
                  """
             Then it should respond with 201 or 200
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901903"
             When a GET request is made to "/ed-fi/staffEducationOrganizationEmploymentAssociations/"
             Then it should respond with 200
              And the response body is
                  """
                     []
                  """

        Scenario: 24 Ensure client cannot  update staffEducationOrganizationEmploymentAssociations with client does not have access it to educationOrganizationId
             When a POST request is made to "/ed-fi/staffEducationOrganizationEmploymentAssociations" with
                  """
                    {
                      "employmentStatusDescriptor": "uri://ed-fi.org/employmentStatusDescriptor#Teacher",
                      "educationOrganizationReference": { "educationOrganizationId": 25590100100000 },
                      "staffReference": {  "staffUniqueId": "s0005"  },
                      "hireDate": "2018-08-20",
                      "positionTitle": "Math Teacher"
                    }
                  """
             Then it should respond with 201 or 200
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901903"
             When a PUT request is made to "/ed-fi/staffEducationOrganizationEmploymentAssociations/{id}" with
                  """
                    {
                       "id":"{id}",
                      "employmentStatusDescriptor": "uri://ed-fi.org/employmentStatusDescriptor#Teacher",
                      "educationOrganizationReference": { "educationOrganizationId": 25590100100000 },
                      "staffReference": {  "staffUniqueId": "s0005"  },
                      "hireDate": "2018-08-20",
                      "positionTitle": "Science Teacher"
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
                      "No relationships have been established between the caller's education organization id claims ('255901903') and one or more of the following properties of the resource item: 'EducationOrganizationId', 'StaffUniqueId'."
                    ]
                  }
                  """

        Scenario: 25 Ensure client cannot  delete staffEducationOrganizationEmploymentAssociations with client does not have access it to educationOrganizationId
             When a POST request is made to "/ed-fi/staffEducationOrganizationEmploymentAssociations" with
                  """
                    {
                      "employmentStatusDescriptor": "uri://ed-fi.org/employmentStatusDescriptor#Teacher",
                      "educationOrganizationReference": { "educationOrganizationId": 25590100100000 },
                      "staffReference": {  "staffUniqueId": "s0005"  },
                      "hireDate": "2018-08-20",
                      "positionTitle": "Math Teacher"
                    }
                  """
             Then it should respond with 201 or 200
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901903"
             When a DELETE request is made to "/ed-fi/staffEducationOrganizationEmploymentAssociations/{id}"
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
                      "No relationships have been established between the caller's education organization id claims ('255901903') and one or more of the following properties of the resource item: 'EducationOrganizationId', 'StaffUniqueId'."
                    ]
                  }
                  """
