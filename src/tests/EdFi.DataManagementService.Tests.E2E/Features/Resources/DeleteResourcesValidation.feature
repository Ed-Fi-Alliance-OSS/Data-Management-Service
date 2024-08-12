# This is a rough draft feature for future use.

Feature: Resources "Delete" Operation validations

        Background:
            Given the Data Management Service must receive a token issued by "http://localhost"
              And user is already authorized
             When a POST request is made to "/ed-fi/absenceEventCategoryDescriptors" with
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

        Scenario: 01 Verify response when deleting a referenced school
            Given the system has these "schools" references
                  | schoolId | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 4003     | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |

            Given the system has these "schoolYearTypes"
                  | schoolYear | currentSchoolYear | schoolYearDescription |
                  | 2022       | false             | School Year 2022      |

            Given the system has these "gradingPeriods"
                  | schoolReference    | schoolYearTypeReference | gradingPeriodDescriptor                                 | gradingPeriodName              | beginDate  | endDate    | periodSequence | totalInstructionalDays |
                  | {"schoolId": 4003} | {"schoolYear": 2022}    | uri://ed-fi.org/GradingPeriodDescriptor#First Six Weeks | 2021-2022 Fall Semester Exam 1 | 2021-08-23 | 2021-10-03 | 1              | 29                     |

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

        Scenario: 02 Verify response when deleting a referenced schoolyeartype
            Given the system has these "schools"
                  | schoolId | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 4003     | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |

            Given the system has these "schoolYearTypes" references
                  | schoolYear | currentSchoolYear | schoolYearDescription |
                  | 2022       | false             | School Year 2022      |

            Given the system has these "gradingPeriods"
                  | schoolReference    | schoolYearTypeReference | gradingPeriodDescriptor                                 | gradingPeriodName              | beginDate  | endDate    | periodSequence | totalInstructionalDays |
                  | {"schoolId": 4003} | {"schoolYear": 2022}    | uri://ed-fi.org/GradingPeriodDescriptor#First Six Weeks | 2021-2022 Fall Semester Exam 1 | 2021-08-23 | 2021-10-03 | 1              | 29                     |

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

        Scenario: 03 Verify response when deleting a referenced student
            Given the system has these "schools"
                  | schoolId | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 4005     | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#First Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |

            Given the system has these "students" references
                  | studentUniqueId | birthDate  | firstName   | lastSurname |
                  | "987"           | 2017-08-23 | "firstname" | "lastname"  |

            Given the system has these "studentSchoolAssociations"
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

