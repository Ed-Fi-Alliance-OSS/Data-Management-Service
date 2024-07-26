Feature: Validate the reference of descriptors when creating resources

        Background:
            Given the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
                  | uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Ind     |
                  | uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Other   |
                  | uri://ed-fi.org/ProgramTypeDescriptor#Bilingual                |

              And the system has these "students"
                  | studentUniqueId | birthDate  | firstName | lastSurname |
                  | "604824"        | 2010-01-13 | Traci     | Mathews     |
              And the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 255901001 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |

        Scenario: 01 User can not create a resource when descriptor doesn't exist
             When a POST request is made to "/ed-fi/localEducationAgencies" with
                  """
                  {
                        "localEducationAgencyId": 25590100,
                        "nameOfInstitution": "Grand+Institution+Test",
                        "categories": [
                            {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Fake"
                            }
                        ],
                        "localEducationAgencyCategoryDescriptor": "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Fake"
                  }
                  """
             Then it should respond with 400
              And the response body is
                  """
                  {
                    "validationErrors": {
                    "$.categories[*].educationOrganizationCategoryDescriptor": [
                        "EducationOrganizationCategoryDescriptor value 'uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Fake' does not exist."
                    ],
                    "$.localEducationAgencyCategoryDescriptor": [
                        "LocalEducationAgencyCategoryDescriptor value 'uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Fake' does not exist."
                    ]
                    },
                    "errors": [],
                    "detail": "Data validation failed. See 'validationErrors' for details.",
                    "type": "urn:ed-fi:api:bad-request",
                    "title": "Bad Request",
                    "status": 400,
                    "correlationId": null
                  }
                  """

        Scenario: 02 User can not upsert a resource using a descriptor that does not exist
             When a POST request is made to "/ed-fi/students" with
                  """
                  {
                    "studentUniqueId": "604824",
                    "birthDate": "2010-01-13",
                    "firstName": "Traci",
                    "lastSurname": "Mathews",
                    "citizenshipStatusDescriptor": "uri://ed-fi.org/CitizenshipStatusDescriptor#Fake"
                  }
                  """
             Then it should respond with 400
              And the response body is
                  """
                  {
                    "validationErrors": {
                        "$.citizenshipStatusDescriptor": [
                            "CitizenshipStatusDescriptor value 'uri://ed-fi.org/CitizenshipStatusDescriptor#Fake' does not exist."
                    ]
                    },
                    "errors": [],
                    "detail": "Data validation failed. See 'validationErrors' for details.",
                    "type": "urn:ed-fi:api:bad-request",
                    "title": "Bad Request",
                    "status": 400,
                    "correlationId": null
                  }
                  """

        Scenario: 03 User can not update a resource using a descriptor that does not exist
            Given a POST request is made to "/ed-fi/localEducationAgencies" with
                  """
                  {
                    "localEducationAgencyId": 25590100,
                    "nameOfInstitution": "Grand Institution Test",
                    "categories": [
                        {
                            "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                        }
                    ],
                    "localEducationAgencyCategoryDescriptor": "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Ind"
                  }
                  """
             When a PUT request is made to "/ed-fi/localEducationAgencies/{id}" with
                  """
                  {
                    "id": "{id}",
                    "localEducationAgencyId": 25590100,
                    "nameOfInstitution": "Grand Institution Test",
                    "categories": [
                        {
                            "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Fake"
                        }
                    ],
                    "localEducationAgencyCategoryDescriptor": "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Fake"
                  }
                  """
             Then it should respond with 400
              And the response body is
                  """
                  {
                    "validationErrors": {
                    "$.categories[*].educationOrganizationCategoryDescriptor": [
                        "EducationOrganizationCategoryDescriptor value 'uri://ed-fi.org/EducationOrganizationCategoryDescriptor#Fake' does not exist."
                    ],
                    "$.localEducationAgencyCategoryDescriptor": [
                        "LocalEducationAgencyCategoryDescriptor value 'uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Fake' does not exist."
                    ]
                    },
                    "errors": [],
                    "detail": "Data validation failed. See 'validationErrors' for details.",
                    "type": "urn:ed-fi:api:bad-request",
                    "title": "Bad Request",
                    "status": 400,
                    "correlationId": null
                  }
                  """

        Scenario: 04 User can create a resource when descriptor exists
             When a POST request is made to "/ed-fi/localEducationAgencies" with
                  """
                  {
                    "localEducationAgencyId": 25590100,
                    "nameOfInstitution": "Grand Institution Test",
                    "categories": [
                        {
                            "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                        }
                    ],
                    "localEducationAgencyCategoryDescriptor": "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Ind"
                  }
                  """
             Then it should respond with 201 or 200

        Scenario: 05 User can update a resource when descriptor exists
            Given a POST request is made to "/ed-fi/localEducationAgencies" with
                  """
                  {
                    "localEducationAgencyId": 25590100,
                    "nameOfInstitution": "Grand Institution Test",
                    "categories": [
                        {
                            "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                        }
                    ],
                    "localEducationAgencyCategoryDescriptor": "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Ind"
                  }
                  """
             When a PUT request is made to "/ed-fi/localEducationAgencies/{id}" with
                  """
                  {
                    "id": "{id}",
                    "localEducationAgencyId": 25590100,
                    "nameOfInstitution": "Grand Institution Test",
                    "categories": [
                        {
                            "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                        }
                    ],
                    "localEducationAgencyCategoryDescriptor": "uri://ed-fi.org/LocalEducationAgencyCategoryDescriptor#Other"
                  }
                  """
             Then it should respond with 204

        Scenario: 06 User receives 400 instead of 409 error when both descriptor and reference are invalid
             When a POST request is made to "/ed-fi/studentProgramAssociations" with
                  """
                  {
                      "educationOrganizationReference": {
                        "educationOrganizationId": 255901001
                      },
                      "programReference": {
                        "educationOrganizationId": 255901001,
                        "programName": "Bilingual",
                        "programTypeDescriptor": "uri://ed-fi.org/ProgramTypeDescriptor#Bilingual"
                      },
                      "studentReference": {
                        "studentUniqueId": "604824"
                      },
                      "beginDate": "2021-08-30",
                      "servedOutsideOfRegularSession": true,
                      "programParticipationStatuses": [
                        {
                          "participationStatusDescriptor": "uri://ed-fi.org/participationStatusDescriptor#Fake",
                          "statusBeginDate": "2024-06-26"
                        }
                      ]
                    }
                  """
             Then it should respond with 400
              And the response body is
                  """
                  {
                      "validationErrors": {
                          "$.programParticipationStatuses[*].participationStatusDescriptor": [
                              "ParticipationStatusDescriptor value 'uri://ed-fi.org/participationStatusDescriptor#Fake' does not exist."
                          ]
                      },
                      "errors": [],
                      "detail": "Data validation failed. See 'validationErrors' for details.",
                      "type": "urn:ed-fi:api:bad-request",
                      "title": "Bad Request",
                      "status": 400,
                      "correlationId": null
                  }
                  """

