Feature: RelationshipsWithEdOrgsAndStaff Authorization


        Background:
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901001"
              And the system has these descriptors
                  | descriptorValue                                                          |
                  | uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application |
                  | uri://ed-fi.org/SexDescriptor#Female                                     |
                  | uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade                         |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school           |
                  | uri://ed-fi.org/ProgramAssignmentDescriptor#Regular Education            |
                  | uri://ed-fi.org/SourceSystemDescriptor#Pass                              |
                  | uri://ed-fi.org/SourceSystemDescriptor#Fail                              |
                  | uri://ed-fi.org/EmploymentStatusDescriptor#Tenured or permanent          |
              And the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 255901001 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
                  | 255901001911 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |


        Scenario: 01 Create Person With "District" sourceSystemDescriptor and associate with Staff
            
             When a POST request is made to "/ed-fi/people" with
                  """
                    {
                      "personId": "p001",
                      "sourceSystemDescriptor": "uri://ed-fi.org/SourceSystemDescriptor#Pass"
                    }
                  """
             Then it should respond with 201

             When a POST request is made to "/ed-fi/Staffs" with
                  """
                    {
                      "staffUniqueId": "s0001",
                      "personReference": {
                        "personId": "p001",
                        "sourceSystemDescriptor": "uri://ed-fi.org/SourceSystemDescriptor#Pass"
                      },
                      "birthDate": "1969-09-13",
                      "firstName": "Steve",
                      "lastSurname": "Buck"
                    }
                  """
             Then it should respond with 201

             When a POST request is made to "/ed-fi/StaffEducationOrganizationEmploymentAssociations" with
                  """
                    {
                        "educationOrganizationReference":{
                          "educationOrganizationId":"255901001"
                       },
                       "staffReference":{
                          "staffUniqueId":"s0001"
                       },
                       "employmentStatusDescriptor":"uri://ed-fi.org/EmploymentStatusDescriptor#Tenured or permanent",
                       "hireDate":"2010-09-13"
                    }
                  """
             Then it should respond with 201

             When a GET request is made to "/ed-fi/Staffs"
             Then it should respond with 200
              And the response body is
                  """
                    [
                      {
                        "firstName": "Steve",
                        "id": "{id}",
                        "staffUniqueId": "s0001",
                        "personReference": {
                          "sourceSystemDescriptor": "uri://ed-fi.org/SourceSystemDescriptor#Pass",
                          "personId": "p001"
                        },
                        "birthDate": "1969-09-13",
                        "lastSurname": "Buck"
                      }
                    ]
                  """

        Scenario: 02 Create same Person With  different  sourceSystemDescriptor and associate with Staff

             When a POST request is made to "/ed-fi/people" with
                  """
                    {
                      "personId": "p002",
                      "sourceSystemDescriptor": "uri://ed-fi.org/SourceSystemDescriptor#Pass"
                    }
                  """
             Then it should respond with 201

             When a POST request is made to "/ed-fi/people" with
                  """
                    {
                      "personId": "p002",
                      "sourceSystemDescriptor": "uri://ed-fi.org/SourceSystemDescriptor#Fail"
                    }
                  """
             Then it should respond with 201


             When a GET request is made to "/ed-fi/people?personId=p002"
             Then it should respond with 200
              And the response body is
                  """
                  [
                   {
                     "sourceSystemDescriptor": "uri://ed-fi.org/SourceSystemDescriptor#Pass",
                     "personId": "p002",
                     "id": "{id}"
                   },
                   {
                     "sourceSystemDescriptor": "uri://ed-fi.org/SourceSystemDescriptor#Fail",
                     "personId": "p002",
                     "id": "{id}"
                   }
                  ]
                  """

             When a POST request is made to "/ed-fi/Staffs" with
                  """
                    {
                      "staffUniqueId": "s0002",
                      "personReference": {
                        "personId": "p002",
                        "sourceSystemDescriptor": "uri://ed-fi.org/SourceSystemDescriptor#Pass"
                      },
                      "birthDate": "1969-09-13",
                      "firstName": "Steve",
                      "lastSurname": "Buck"
                    }
                  """
             Then it should respond with 201

             When a POST request is made to "/ed-fi/StaffEducationOrganizationEmploymentAssociations" with
                  """
                    {
                        "educationOrganizationReference":{
                          "educationOrganizationId":"255901001"
                       },
                       "staffReference":{
                          "staffUniqueId":"s0002"
                       },
                       "employmentStatusDescriptor":"uri://ed-fi.org/EmploymentStatusDescriptor#Tenured or permanent",
                       "hireDate":"2010-09-13"
                    }
                  """
             Then it should respond with 201

             When a GET request is made to "/ed-fi/Staffs?staffUniqueId=s0002&personId=p002"
             Then it should respond with 200
              And the response body is
                  """
                    [
                      {
                        "firstName": "Steve",
                        "id": "{id}",
                        "staffUniqueId": "s0002",
                        "personReference": {
                          "sourceSystemDescriptor": "uri://ed-fi.org/SourceSystemDescriptor#Pass",
                          "personId": "p002"
                        },
                        "birthDate": "1969-09-13",
                        "lastSurname": "Buck"
                      }
                    ]
                  """

        Scenario: 03 Data Validation with Staff when lastSurname has leading or trailing spaces

             When a POST request is made to "/ed-fi/staffs" with
                  """
                      {
                        "staffUniqueId": "TEST-StringLength",
                        "birthDate": "1976-08-19",
                        "firstName": "Barry",
                        "hispanicLatinoEthnicity": false,
                        "lastSurname": "       "
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
                      "correlationId": "0HNCIHNFRNK0C:00000002",
                      "validationErrors": {
                        "$.lastSurname": [
                          "lastSurname cannot contain leading or trailing spaces."
                        ]
                      },
                      "errors": []
                    }
                  """

        # Ignored because Data Validation with Staff when petName has leading or trailing spaces DMS-696-Defect
        @ignore
        Scenario: 04 Data Validation with Staff when petName has leading or trailing spaces

             When a POST request is made to "/ed-fi/staffs" with
                  """
                      {
                        "staffUniqueId": "TEST-StringLength",
                        "birthDate": "1976-08-19",
                        "firstName": "Barry",
                        "hispanicLatinoEthnicity": false,
                        "lastSurname": "Peterson",
                        "_ext": {
                          "Sample": {
                            "pets": [
                              {
                                "petName": "         ",
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
                      "correlationId": "0HNCIHNFRNK0C:00000002",
                      "validationErrors": {
                        "$.petName": [
                          "petName cannot contain leading or trailing spaces."
                        ]
                      },
                      "errors": []
                    }
                  """

        # Ignored because Data Validation with Staff when Pet name has too short value @DMS-697Defect
        @ignore
        Scenario: 05 Data Validation with Staff when Pet name has too short value

             When a POST request is made to "/ed-fi/staffs" with
                  """
                      {
                        "staffUniqueId": "TEST-StringLength",
                        "birthDate": "1976-08-19",
                        "firstName": "Barry",
                        "hispanicLatinoEthnicity": false,
                        "lastSurname": "Peterson",
                        "_ext": {
                          "Sample": {
                            "pets": [
                              {
                                "petName": "it",
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
                      "correlationId": "0HNCIHNFRNK0C:00000002",
                      "validationErrors": {
                        "$.petName": [
                          "petName must be between 3 and 20 characters in length."
                        ]
                      },
                      "errors": []
                    }
                  """

        # Ignored because Data Validation with Staff when Pet name has too long value @DMS-697Defect
        @ignore

        Scenario: 06 Data Validation with Staff when Pet name has too long value

             When a POST request is made to "/ed-fi/staffs" with
                  """
                      {
                        "staffUniqueId": "TEST-StringLength",
                        "birthDate": "1976-08-19",
                        "firstName": "Barry",
                        "hispanicLatinoEthnicity": false,
                        "lastSurname": "Peterson",
                        "_ext": {
                          "Sample": {
                            "pets": [
                              {
                                "petName": "John Jacob Jingleheimer Schmidt",
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
                      "correlationId": "0HNCIHNFRNK0C:00000002",
                      "validationErrors": {
                        "$.petName": [
                          "PetName must be between 3 and 20 characters in length."
                        ]
                      },
                      "errors": []
                    }
                  """

        Scenario: 07 Create Staff with Pet with name valid

             When a POST request is made to "/ed-fi/staffs" with
                  """
                      {
                        "staffUniqueId": "TEST-StringLength",
                        "birthDate": "1976-08-19",
                        "firstName": "Barry",
                        "hispanicLatinoEthnicity": false,
                        "lastSurname": "Peterson",
                        "_ext": {
                          "Sample": {
                            "pets": [
                              {
                                "petName": "John",
                                "isFixed": true
                              }
                            ]
                          }
                        }
                      }
                  """
             Then it should respond with 201

        Scenario: 08 Include total count in Get request without search condition
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901001911"
              And the system has these "staffs"
                  | staffUniqueId | firstName | lastSurname |
                  | S01           | David     | Peterson    |
                  | S02           | Geoff     | Peterson    |
                  | S03           | Adam      | Peterson    |
             When a GET request is made to "/ed-fi/staffs?totalCount=true"
             Then it should respond with 200
              And the response headers include
                  """
                    {
                        "total-count": 3
                    }
                  """

        Scenario: 09 Include total count in Get request ,Set limit to 2
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901001911"
              And the system has these "staffs"
                  | staffUniqueId | firstName | lastSurname |
                  | S01           | David     | Peterson    |
                  | S02           | Geoff     | Peterson    |
                  | S03           | Adam      | Peterson    |
                  | S04           | Franics   | Peterson    |
                  | S05           | Johnson   | Peterson    |
             When a GET request is made to "/ed-fi/staffs?limit=2"
             Then it should respond with 200
              And total of records should be 2
              And the response headers include
                  """
                    {
                        "total-count": 2
                    }
                  """

        Scenario: 10 Create staff with duplicate extension items

             When a POST request is made to "/ed-fi/staffs" with
                  """
                      {
                        "staffUniqueId": "TEST-StringLength-004",
                        "birthDate": "1976-08-19",
                        "firstName": "Barry",
                        "hispanicLatinoEthnicity": false,
                        "lastSurname": "Peterson",
                        "_ext": {
                            "Sample": {
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
                                  "isFixed": false
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
                      "correlationId": "0HNCIHNFRNK0C:00000002",
                    "validationErrors": {
                        "$._ext.sample.pets": [
                            "The 4th item of the StaffPets has the same identifying values as another item earlier in the list."
                        ]
                    },
                      "errors": []
                    }
                  """

        Scenario: 11 Create staff without duplicate extension items

             When a POST request is made to "/ed-fi/staffs" with
                  """
                      {
                        "staffUniqueId": "TEST-StringLength-005",
                        "birthDate": "1976-08-19",
                        "firstName": "Barry",
                        "hispanicLatinoEthnicity": false,
                        "lastSurname": "Peterson",
                         "_ext": {
                            "Sample": {
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
                                }
                              ]
                            }
                          }
                      }
                  """
             Then it should respond with 201

        Scenario: 12 Update staff with duplicate extension items

             When a POST request is made to "/ed-fi/staffs" with
                  """
                      {
                        "staffUniqueId": "TEST-DUPLICATE-TEST2",
                        "birthDate": "1976-08-19",
                        "firstName": "Barry",
                        "hispanicLatinoEthnicity": false,
                        "lastSurname": "Peterson",
                         "_ext": {
                            "Sample": {
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
                                }
                              ]
                            }
                          }
                      }
                  """
             Then it should respond with 201

             When a POST request is made to "/ed-fi/staffs" with
                  """
                      {
                        "staffUniqueId": "TEST-DUPLICATE-TEST2",
                        "birthDate": "1976-08-19",
                        "firstName": "Barry",
                        "hispanicLatinoEthnicity": false,
                        "lastSurname": "Peterson",
                         "_ext": {
                            "Sample": {
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
                                  "isFixed": false
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
                      "correlationId": "0HNCIHNFRNK0C:00000002",
                     "validationErrors": {
                            "$._ext.sample.pets": [
                                "The 4th item of the StaffPets has the same identifying values as another item earlier in the list."
                            ]
                        },
                      "errors": []
                    }
                  """

        Scenario: 13 Ensure client can not create a StaffEducationOrganizationEmploymentAssociations with wrong educationOrganizationId
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901001"
             When a POST request is made to "/ed-fi/staffs" with
                  """
                      {
                      "staffUniqueId": "staff10",
                      "birthDate": "1976-08-19",
                      "firstName": "Barry",
                      "lastSurname": "Peterson",
                      "sexDescriptor":"uri://ed-fi.org/SexDescriptor#Female"
                      }
                  """
             Then it should respond with 201

             When a POST request is made to "/ed-fi/EducationOrganizationCategoryDescriptors" with
                  """
                  {
                      "codeValue": "school",
                      "description": "school",
                      "namespace": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor",
                      "shortDescription": "school"
                  }
                  """
             Then it should respond with 201 or 200

             When a POST request is made to "/ed-fi/EmploymentStatusDescriptors" with
                  """
                  {
                      "codeValue": "Tenured or permanent",
                      "description": "Tenured or permanent",
                      "namespace": "uri://ed-fi.org/EmploymentStatusDescriptor",
                      "shortDescription": "Tenured or permanent"
                  }
                  """
             Then it should respond with 201 or 200

             When a POST request is made to "/ed-fi/schools" with
                  """
                    {
                        "schoolId": "255901001",
                        "nameOfInstitution": "UT Austin College of Education Graduate",
                        "educationOrganizationCategories": [
                            {
                                "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"
                            }
                        ],
                        "gradeLevels": [
                            {
                                "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                            }
                        ]
                    }
                  """
             Then it should respond with 201 or 200

             When a POST request is made to "/ed-fi/StaffEducationOrganizationEmploymentAssociations" with
                  """
                      {
                          "educationOrganizationReference":{
                              "educationOrganizationId":"255901002"
                          },
                          "staffReference":{
                              "staffUniqueId":"staff10"
                          },
                          "employmentStatusDescriptor":"uri://ed-fi.org/EmploymentStatusDescriptor#Tenured or permanent",
                          "hireDate":"2023-08-19"
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
                        "correlationId": "0HNCJ8H1N6HM5:00000009",
                        "validationErrors": {},
                        "errors": [
                            "No relationships have been established between the caller's education organization id claims ('255901001') and properties of the resource item."
                        ]
                    }
                  """

        Scenario: 14 Ensure client create a StaffEducationOrganizationEmploymentAssociations with vaild educationOrganizationId
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901005"
             When a POST request is made to "/ed-fi/staffs" with
                  """
                      {
                      "staffUniqueId": "staff019",
                      "birthDate": "1976-08-19",
                      "firstName": "Barry",
                      "lastSurname": "Peterson",
                      "sexDescriptor":"uri://ed-fi.org/SexDescriptor#Female"
                      }
                  """
             Then it should respond with 201

             When a POST request is made to "/ed-fi/EducationOrganizationCategoryDescriptors" with
                  """
                  {
                      "codeValue": "school",
                      "description": "school",
                      "namespace": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor",
                      "shortDescription": "school"
                  }
                  """
             Then it should respond with 201 or 200

             When a POST request is made to "/ed-fi/schools" with
                  """
                    {
                        "schoolId": "255901005",
                        "nameOfInstitution": "UT Austin College of Education Graduate",
                        "educationOrganizationCategories": [
                            {
                                "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"
                            }
                        ],
                        "gradeLevels": [
                            {
                                "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"
                            }
                        ]
                    }
                  """
             Then it should respond with 201

             When a POST request is made to "/ed-fi/EmploymentStatusDescriptors" with
                  """
                  {
                      "codeValue": "Tenured or permanent",
                      "description": "Tenured or permanent",
                      "namespace": "uri://ed-fi.org/EmploymentStatusDescriptor",
                      "shortDescription": "Tenured or permanent"
                  }
                  """
             Then it should respond with 201 or 200

             When a POST request is made to "/ed-fi/StaffEducationOrganizationEmploymentAssociations" with
                  """
                      {
                          "educationOrganizationReference":{
                              "educationOrganizationId":"255901005"
                          },
                          "staffReference":{
                              "staffUniqueId":"staff019"
                          },
                          "employmentStatusDescriptor":"uri://ed-fi.org/EmploymentStatusDescriptor#Tenured or permanent",
                          "hireDate":"2023-08-19"
                       }
                  """
             Then it should respond with 201

        Scenario: 15 When deleting a staff in use should return 409 conflict
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901006"
              And the system has these descriptors
                  | descriptorValue                                                          |
                  | uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application |
                  | uri://ed-fi.org/SexDescriptor#Female                                     |
                  | uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade                         |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school           |
                  | uri://ed-fi.org/EmploymentStatusDescriptor#Tenured or permanent          |
              And the system has these "schools"
                  | schoolId  | nameOfInstitution                       | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 255901006 | UT Austin College of Education Graduate | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "staffs"
                  | _storeResultingIdInVariable | staffUniqueId | birthDate  | firstName | lastSurname | sexDescriptor                        |
                  | staffUniqueId               | staff020      | 2023-09-15 | Barry     | Peterson    | uri://ed-fi.org/SexDescriptor#Female |
              And the system has these "StaffEducationOrganizationEmploymentAssociations"
                  | educationOrganizationReference            | staffReference                  | employmentStatusDescriptor                                        | hireDate   |
                  | { "educationOrganizationId":"255901006" } | { "staffUniqueId":"staff020"  } | "uri://ed-fi.org/EmploymentStatusDescriptor#Tenured or permanent" | 2023-08-19 |

             When a DELETE request is made to "/ed-fi/staffs/{staffUniqueId}"
             Then it should respond with 409

        Scenario: 16 When deleting an unused staff should return 204 nocontent
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901007"
              And the system has these descriptors
                  | descriptorValue                                                          |
                  | uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application |
                  | uri://ed-fi.org/SexDescriptor#Female                                     |
                  | uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade                         |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school           |
                  | uri://ed-fi.org/EmploymentStatusDescriptor#Tenured or permanent          |
              And the system has these "schools"
                  | schoolId  | nameOfInstitution                       | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 255901007 | UT Austin College of Education Graduate | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "staffs"
                  | _storeResultingIdInVariable | staffUniqueId | birthDate  | firstName | lastSurname | sexDescriptor                        |
                  | staffUniqueId               | staff021      | 2023-09-15 | Barry     | Peterson    | uri://ed-fi.org/SexDescriptor#Female |

             When a DELETE request is made to "/ed-fi/staffs/{staffUniqueId}"
             Then it should respond with 204

        Scenario: 17 When updating an associated staff should succeed
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901007"
              And the system has these descriptors
                  | descriptorValue                                                          |
                  | uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application |
                  | uri://ed-fi.org/SexDescriptor#Female                                     |
                  | uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade                         |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school           |
                  | uri://ed-fi.org/EmploymentStatusDescriptor#Tenured or permanent          |
              And the system has these "schools"
                  | schoolId  | nameOfInstitution                       | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 255901007 | UT Austin College of Education Graduate | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "staffs"
                  | _storeResultingIdInVariable | staffUniqueId | birthDate  | firstName | lastSurname | sexDescriptor                        |
                  | staffUniqueId               | staff022      | 2023-09-15 | Barry     | Peterson    | uri://ed-fi.org/SexDescriptor#Female |
              And the system has these "StaffEducationOrganizationEmploymentAssociations"
                  | educationOrganizationReference            | staffReference                  | employmentStatusDescriptor                                        | hireDate   |
                  | { "educationOrganizationId":"255901007" } | { "staffUniqueId":"staff022"  } | "uri://ed-fi.org/EmploymentStatusDescriptor#Tenured or permanent" | 2023-08-19 |

             When a PUT request is made to "/ed-fi/staffs/{staffUniqueId}" with
                  """
                    {
                    "id": "{staffUniqueId}",
                    "staffUniqueId": "staff022",
                    "birthDate": "1976-08-19",
                    "firstName": "David",
                    "lastSurname": "Steven",
                    "sexDescriptor":"uri://ed-fi.org/SexDescriptor#Female"
                    }
                  """
             Then it should respond with 204

        Scenario: 18 When posting a resource with decimal overflow
             When a POST request is made to "/ed-fi/staffs" with
                  """
                      {
                      "staffUniqueId": "staff11",
                      "birthDate": "1976-08-19",
                      "firstName": "Barry",
                      "lastSurname": "Peterson",
                      "yearsOfPriorProfessionalExperience": 1000,
                      "sexDescriptor":"uri://ed-fi.org/SexDescriptor#Female"
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
                        "correlationId": "0HNCIHNFRNK0C:00000002",
                        "validationErrors": {
                            "$.yearsOfPriorProfessionalExperience": [
                                "YearsOfPriorProfessionalExperience must be between -999.99 and 999.99.."
                            ]
                        },
                        "errors": []
                    }
                  """

        Scenario: 19 when posting a resource with partially formed optional reference
             When a POST request is made to "/ed-fi/staffs" with
                  """
                      {
                      "staffUniqueId": "staff21",
                      "birthDate": "1976-08-19",
                      "firstName": "Barry",
                      "lastSurname": "Peterson",
                      "sexDescriptor":"uri://ed-fi.org/SexDescriptor#Female"
                      }
                  """
             Then it should respond with 201

             When a POST request is made to "/ed-fi/staffSchoolAssociations" with
                  """
                      {
                        "calendarReference": {
        	                "calendarCode": "Ignored Due To Missing SchoolId",
        	                "schoolYear": 2024
                        },
                        "schoolReference": {
                          "schoolId": "255901001"
                        },
                        "staffReference": {
                          "staffUniqueId": "staff21"
                        },
                        "programAssignmentDescriptor": "uri://ed-fi.org/ProgramAssignmentDescriptor#Regular Education"
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
                      "correlationId": "0HNCJ8H1N6HPV:0000000A",
                      "validationErrors": {
                        "$.calendarReference.schoolId": [
                          "schoolId is required."
                        ]
                      },
                      "errors": []
                    }
                  """
        Scenario: 20 when posting a resource specifying a snapshot

             When a POST request is made to "/ed-fi/staffSchoolAssociations" with header "Use-Snapshot" value "true"
                  """
                      {  }
                  """
             Then it should respond with 405
              And the response body is
                  """
                     {
                        "detail": "An attempt was made to modify data in a Snapshot, but this data is read-only.",
                        "type": "urn:ed-fi:api:snapshots:method-not-allowed",
                        "title": "Method Not Allowed with Snapshots",
                        "status": 405,
                        "correlationId": "dd89ea591795"
                    }
                  """
        Scenario: 21 when posting an association where resource does not exist

             When a POST request is made to "/ed-fi/staffLeaves" with
                  """
                      {
                          "staffReference": {
                            "staffUniqueId": "999999"
                          },
                          "beginDate": "2022-08-26",
                          "staffLeaveEventCategoryDescriptor": "uri://ed-fi.org/StaffLeaveEventCategoryDescriptor#Personal"
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
                        "No relationships have been established between the caller's education organization id claims ('255901001') and one or more of the following properties of the resource item: 'EducationOrganizationId', 'StaffUniqueId'."
                      ]
                    }
                  """
        Scenario: 22 when putting a resource specifying a snapshot

             When a PUT request is made to "/ed-fi/staffSchoolAssociations" with header "Use-Snapshot" value "true"
                  """
                      {  }
                  """
             Then it should respond with 405
              And the response body is
                  """
                    {
                        "detail": "An attempt was made to modify data in a Snapshot, but this data is read-only.",
                        "type": "urn:ed-fi:api:snapshots:method-not-allowed",
                        "title": "Method Not Allowed with Snapshots",
                        "status": 405,
                        "correlationId": "6265078f530e"
                    }
                  """

        Scenario: 23 Create a DisciplineIncident (associated with the SchoolId)

            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901008"
              And the system has these descriptors
                  | descriptorValue                                                          |
                  | uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application |
                  | uri://ed-fi.org/SexDescriptor#Female                                     |
                  | uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade                         |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school           |
                  | uri://ed-fi.org/EmploymentStatusDescriptor#Tenured or permanent          |
                  | uri://ed-fi.org/IncidentLocationDescriptor#Auditorium                    |
              And the system has these "schools"
                  | schoolId  | nameOfInstitution                       | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 255901008 | UT Austin College of Education Graduate | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "staffs"
                  | _storeResultingIdInVariable | staffUniqueId | birthDate  | firstName | lastSurname | sexDescriptor                        |
                  | staffUniqueId               | staff023      | 2023-09-15 | Barry     | Peterson    | uri://ed-fi.org/SexDescriptor#Female |
              And the system has these "StaffEducationOrganizationEmploymentAssociations"
                  | educationOrganizationReference            | staffReference                  | employmentStatusDescriptor                                        | hireDate   |
                  | { "educationOrganizationId":"255901008" } | { "staffUniqueId":"staff023"  } | "uri://ed-fi.org/EmploymentStatusDescriptor#Tenured or permanent" | 2023-08-19 |

             When a POST request is made to "/ed-fi/disciplineIncidents" with
                  """
                  {
                      "schoolReference": {
                          "schoolId": "255901008"
                      },
                      "staffReference": {
                          "staffUniqueId": "staff023"
                      },
                      "incidentIdentifier": "1",
                      "incidentDate": "2011-02-09",
                      "incidentLocationDescriptor": "uri://ed-fi.org/IncidentLocationDescriptor#Auditorium"
                      }
                  """
             Then it should respond with 201
             When a GET request is made to "/ed-fi/disciplineIncidents/{id}"
             Then it should respond with 200
              And the response body is
                  """
                    {
                      "id": "{id}",
                      "incidentDate": "2011-02-09",
                      "schoolReference": {
                        "schoolId": 255901008
                      },
                      "incidentIdentifier": "1",
                      "incidentLocationDescriptor": "uri://ed-fi.org/IncidentLocationDescriptor#Auditorium"
                    }
                  """
