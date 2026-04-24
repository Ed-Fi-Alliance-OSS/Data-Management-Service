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

        # FK-violation delete scenarios live in DeleteResourcesReferenceValidation.feature so they
        # can carry the relational-backend tag without conflicting with the "Scenario: 00 Background"
        # seed guardrail in this file. Scenario 04 below is the one exception: it asserts multi-
        # resource enumeration ("StudentContactAssociation, StudentSchoolAssociation"), which the
        # relational backend cannot deliver today — Postgres surfaces only the first-violated FK
        # per DELETE, and multi-resource reporting is deferred to DMS-1012 ("Who References Me?"
        # Diagnostics). Move this scenario into DeleteResourcesReferenceValidation.feature once
        # DMS-1012 lands.

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
