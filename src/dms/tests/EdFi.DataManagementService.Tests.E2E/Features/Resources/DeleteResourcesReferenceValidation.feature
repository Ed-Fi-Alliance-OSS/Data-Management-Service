@reset-data-before-scenario
Feature: Resources "Delete" Reference Conflict validations

        Background:
            Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"

        @API-176 @relational-backend
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

        @API-177 @relational-backend
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

        @API-178 @relational-backend
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

