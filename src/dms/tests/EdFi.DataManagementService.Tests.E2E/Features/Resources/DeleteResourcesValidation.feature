# This is a rough draft feature for future use.

Feature: Resources "Delete" Operation validations

        Background:
            Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"

        Scenario: 00 Background
              And a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  """
                    {
                        "codeValue": "Sick Leave",
                        "description": "Sick Leave",
                        "effectiveBeginDate": "2024-05-14",
                        "effectiveEndDate": "2024-05-14",
                        "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                        "shortDescription": "Sick Leave"
                    }
                  """

        @API-176
        Scenario: 01 Verify response when deleting a referenced school
            Given the system has these "schools" references
                  | schoolId | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 4003     | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |

              And the system has these "schoolYearTypes"
                  | schoolYear | currentSchoolYear | schoolYearDescription |
                  | 2019       | false             | School Year 2019      |

              And the system has these "gradingPeriods"
                  | schoolReference    | schoolYearTypeReference | gradingPeriodDescriptor                                 | gradingPeriodName              | beginDate  | endDate    | periodSequence | totalInstructionalDays |
                  | {"schoolId": 4003} | {"schoolYear": 2019}    | uri://ed-fi.org/GradingPeriodDescriptor#First Six Weeks | 2019-2020 Fall Semester Exam 1 | 2019-08-23 | 2019-10-03 | 1              | 29                     |

             When a DELETE request is made to referenced resource "/ed-fi/schools/{id}"
             Then it should respond with 409
              And the response body is
                  """
                    {
                        "detail": "The requested action cannot be performed because this item is referenced by existing GradingPeriod item(s).",
                        "type": "urn:ed-fi:api:data-conflict:dependent-item-exists",
                        "title": "Dependent Item Exists",
                        "status": 409,
                        "correlationId": null,
                        "validationErrors": {},
                        "errors": []
                    }
                  """

        @API-177
        Scenario: 02 Verify response when deleting a referenced schoolyeartype
            Given the system has these "schools"
                  | schoolId | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 4003     | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |

              And the system has these "schoolYearTypes" references
                  | schoolYear | currentSchoolYear | schoolYearDescription |
                  | 2020       | false             | School Year 2020      |

              And the system has these "gradingPeriods"
                  | schoolReference    | schoolYearTypeReference | gradingPeriodDescriptor                                 | gradingPeriodName              | beginDate  | endDate    | periodSequence | totalInstructionalDays |
                  | {"schoolId": 4003} | {"schoolYear": 2020}    | uri://ed-fi.org/GradingPeriodDescriptor#First Six Weeks | 2020-2021 Fall Semester Exam 1 | 2020-08-23 | 2020-10-03 | 1              | 29                     |

             When a DELETE request is made to referenced resource "/ed-fi/schoolYearTypes/{id}"
             Then it should respond with 409
              And the response body is
                  """
                    {
                        "detail": "The requested action cannot be performed because this item is referenced by existing GradingPeriod item(s).",
                        "type": "urn:ed-fi:api:data-conflict:dependent-item-exists",
                        "title": "Dependent Item Exists",
                        "status": 409,
                        "correlationId": null,
                        "validationErrors": {},
                        "errors": []
                    }
                  """

        @API-178
        Scenario: 03 Verify response when deleting a referenced student
            Given the system has these "schools"
                  | schoolId | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 4005     | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#First Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |

              And the system has these "students" references
                  | studentUniqueId | birthDate  | firstName   | lastSurname |
                  | "987"           | 2017-08-23 | "firstname" | "lastname"  |

              And the system has these "studentSchoolAssociations"
                  | entryDate  | schoolReference    | studentReference           | entryGradeLevelDescriptor                        |
                  | 2021-07-23 | {"schoolId": 4005} | {"studentUniqueId": "987"} | uri://ed-fi.org/GradeLevelDescriptor#First grade |

             When a DELETE request is made to referenced resource "/ed-fi/students/{id}"
             Then it should respond with 409
              And the response body is
                  """
                    {
                        "detail": "The requested action cannot be performed because this item is referenced by existing StudentSchoolAssociation item(s).",
                        "type": "urn:ed-fi:api:data-conflict:dependent-item-exists",
                        "title": "Dependent Item Exists",
                        "status": 409,
                        "correlationId": null,
                        "validationErrors": {},
                        "errors": []
                    }
                  """

        @API-179
        Scenario: 04 Verify response when deleting a student with more than one reference
            Given the system has these "schools"
                  | schoolId | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 4005     | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#First Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |

            Given the system has these "contacts"
                  | contactUniqueId | firstName          | lastSurname       |
                  | "123"           | "contactFirstname" | "contactLastname" |

            Given the system has these "students" references
                  | studentUniqueId | birthDate  | firstName   | lastSurname |
                  | "987"           | 2017-08-23 | "firstname" | "lastname"  |

            Given the system has these "studentSchoolAssociations"
                  | entryDate  | schoolReference    | studentReference           | entryGradeLevelDescriptor                        |
                  | 2021-07-23 | {"schoolId": 4005} | {"studentUniqueId": "987"} | uri://ed-fi.org/GradeLevelDescriptor#First grade |

            Given the system has these "studentContactAssociations"
                  | contactReference           | studentReference           |
                  | {"contactUniqueId": "123"} | {"studentUniqueId": "987"} |


             When a DELETE request is made to referenced resource "/ed-fi/students/{id}"
             Then it should respond with 409
              And the response body is
                  """
                    {
                        "detail": "The requested action cannot be performed because this item is referenced by existing StudentContactAssociation, StudentSchoolAssociation item(s).",
                        "type": "urn:ed-fi:api:data-conflict:dependent-item-exists",
                        "title": "Dependent Item Exists",
                        "status": 409,
                        "correlationId": null,
                        "validationErrors": {},
                        "errors": []
                    }
                  """

        @API-180
        Scenario: 05 Verify response when deleting
            Given a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
                  """
                    {
                        "codeValue": "abc",
                        "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                        "shortDescription": "abc"
                    }
                  """
             When a GET request is made to "/ed-fi/absenceEventCategoryDescriptors?codeValue=abc"
             Then it should respond with 200
              And the response body is
                  """
                  [
                  {
                    "id": "{id}",
                    "codeValue": "abc",
                    "namespace": "uri://ed-fi.org/AbsenceEventCategoryDescriptor",
                    "shortDescription": "abc"
                    }
                  ]
                  """
             When a DELETE request is made to "/ed-fi/absenceEventCategoryDescriptors/{id}"
             Then it should respond with 204
             When a GET request is made to "/ed-fi/absenceEventCategoryDescriptors?codeValue=abc"
             Then it should respond with 200
              And the response body is
                  """
                  []
                  """
