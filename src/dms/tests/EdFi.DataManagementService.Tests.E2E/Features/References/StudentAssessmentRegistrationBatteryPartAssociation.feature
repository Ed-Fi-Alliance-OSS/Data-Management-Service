Feature: StudentAssessmentRegistrationBatteryPartAssociation References

        Background:
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901901, 255901902"
              And the system has these descriptors
                  | descriptorValue                                             |
                  | uri://ed-fi.org/educationOrganizationCategoryDescriptor#XYZ |
                  | uri://ed-fi.org/gradeLevelDescriptor#Tenth Grade            |
                  | uri://ed-fi.org/academicSubjectDescriptor#Math              |
              And the system has these "schools"
                  | schoolId  | nameOfInstitution    | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 255901901 | Bayside High School  | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#XYZ"} ] |
                  | 255901902 | Westside High School | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#XYZ"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName  | lastSurname | birthDate  |
                  | "604823"        | Lisa       | Woods       | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | schoolReference           | studentReference                | entryGradeLevelDescriptor                          | entryDate  |
                  | { "schoolId": 255901901 } | { "studentUniqueId": "604823" } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2021-08-23 |
                  | { "schoolId": 255901902 } | { "studentUniqueId": "604823" } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2021-08-23 |


Rule: Two StudentAssessmentRegistrationBatteryPartAssociation can be created only differing in AssigningEducationOrganizationId

        Scenario: 01 Create two StudentAssessmentRegistrationBatteryPartAssociation with only different AssigningEducationOrganizationIds

             When a POST request is made to "/ed-fi/studentEducationOrganizationAssociations" with
                """
                {
                    "educationOrganizationReference": {
                        "educationOrganizationId": 255901901
                    },
                    "studentReference": {
                        "studentUniqueId": "604823"
                    }
                }
                """
             Then it should respond with 201

             When a POST request is made to "/ed-fi/assessments" with
                """
                {
                    "assessmentIdentifier": "AssessmentId",
                    "namespace": "uri://ed-fi.org",
                    "assessmentTitle": "AssessmentTitle",
                    "academicSubjects": [
                        {
                            "academicSubjectDescriptor": "uri://ed-fi.org/academicSubjectDescriptor#Math"
                        }
                    ]
                }
                """
             Then it should respond with 201

             When a POST request is made to "/ed-fi/assessmentBatteryParts" with
                """
                {
                    "assessmentBatteryPartName": "BatteryPart",
                    "assessmentReference": {
                        "assessmentIdentifier": "AssessmentId",
                        "namespace": "uri://ed-fi.org"
                    }
                }
                """
             Then it should respond with 201

             When a POST request is made to "/ed-fi/assessmentAdministrations" with
                """
                {
                    "administrationIdentifier": "Admin1",
                    "assigningEducationOrganizationReference": {
                        "educationOrganizationId": 255901901
                    },
                    "assessmentReference": {
                    "assessmentIdentifier": "AssessmentId",
                        "namespace": "uri://ed-fi.org"
                    }
                }
                """
             Then it should respond with 201

             When a POST request is made to "/ed-fi/assessmentAdministrations" with
                """
                {
                    "administrationIdentifier": "Admin1",
                    "assigningEducationOrganizationReference": {
                        "educationOrganizationId": 255901902
                    },
                    "assessmentReference": {
                    "assessmentIdentifier": "AssessmentId",
                        "namespace": "uri://ed-fi.org"
                    }
                }
                """
             Then it should respond with 201

             When a POST request is made to "/ed-fi/studentAssessmentRegistrations" with
                """
                {
                    "assessmentAdministrationReference": {
                        "administrationIdentifier": "Admin1",
                        "assessmentIdentifier": "AssessmentId",
                        "assigningEducationOrganizationId": 255901901,
                        "namespace": "uri://ed-fi.org"
                    },
                    "studentEducationOrganizationAssociationReference": {
                        "educationOrganizationId": 255901901,
                        "studentUniqueId": "604823"
                    },
                    "studentSchoolAssociationReference": {
                        "entryDate": "2021-08-23",
                        "schoolId": 255901901,
                        "studentUniqueId": "604823"
                    }
                }
                """
             Then it should respond with 201

             When a POST request is made to "/ed-fi/studentAssessmentRegistrations" with
                """
                {
                    "assessmentAdministrationReference": {
                        "administrationIdentifier": "Admin1",
                        "assessmentIdentifier": "AssessmentId",
                        "assigningEducationOrganizationId": 255901902,
                        "namespace": "uri://ed-fi.org"
                    },
                    "studentEducationOrganizationAssociationReference": {
                        "educationOrganizationId": 255901901,
                        "studentUniqueId": "604823"
                    },
                    "studentSchoolAssociationReference": {
                        "entryDate": "2021-08-23",
                        "schoolId": 255901901,
                        "studentUniqueId": "604823"
                    }
                }
                """
             Then it should respond with 201

             When a POST request is made to "/ed-fi/studentAssessmentRegistrationBatteryPartAssociations" with
                """
                {
                    "assessmentBatteryPartReference": {
                        "assessmentBatteryPartName": "BatteryPart",
                        "assessmentIdentifier": "AssessmentId",
                        "namespace": "uri://ed-fi.org"
                    },
                    "studentAssessmentRegistrationReference": {
                        "administrationIdentifier": "Admin1",
                        "assessmentIdentifier": "AssessmentId",
                        "assigningEducationOrganizationId": 255901901,
                        "educationOrganizationId": 255901901,
                        "namespace": "uri://ed-fi.org",
                        "studentUniqueId": "604823"
                    }
                }
                """
             Then it should respond with 201

             When a POST request is made to "/ed-fi/studentAssessmentRegistrationBatteryPartAssociations" with
                """
                {
                    "assessmentBatteryPartReference": {
                        "assessmentBatteryPartName": "BatteryPart",
                        "assessmentIdentifier": "AssessmentId",
                        "namespace": "uri://ed-fi.org"
                    },
                    "studentAssessmentRegistrationReference": {
                        "administrationIdentifier": "Admin1",
                        "assessmentIdentifier": "AssessmentId",
                        "assigningEducationOrganizationId": 255901902,
                        "educationOrganizationId": 255901901,
                        "namespace": "uri://ed-fi.org",
                        "studentUniqueId": "604823"
                    }
                }
                """
             Then it should respond with 201
