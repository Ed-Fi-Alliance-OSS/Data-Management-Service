Feature: Validate the duplicate references

        Background:
            Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"

        @API-095
        Scenario: 01 Can create addresses on studentEducationOrganizationAssociations differing only in address type
            Given the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
                  | uri://ed-fi.org/AddressTypeDescriptor#Mailing                  |
                  | uri://ed-fi.org/AddressTypeDescriptor#Home                     |
                  | uri://ed-fi.org/StateAbbreviationDescriptor#TX                 |

              And the system has these "students"
                  | studentUniqueId | birthDate  | firstName | lastSurname |
                  | "604824"        | 2010-01-13 | Traci     | Mathews     |
              And the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        |
                  | 255901001 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
             When a POST request is made to "/ed-fi/studentEducationOrganizationAssociations" with
                  """
                  {
                      "educationOrganizationReference": {
                          "educationOrganizationId": 255901001
                      },
                      "studentReference": {
                          "studentUniqueId": "604824"
                      },
                      "addresses": [
                          {
                              "addressTypeDescriptor": "uri://ed-fi.org/AddressTypeDescriptor#Mailing",
                              "city": "Grand Bend",
                              "postalCode": "78834",
                              "stateAbbreviationDescriptor": "uri://ed-fi.org/StateAbbreviationDescriptor#TX",
                              "streetNumberName": "980 Green New Boulevard",
                              "nameOfCounty": "WILLISTON",
                              "periods": []
                          },
                          {
                              "addressTypeDescriptor": "uri://ed-fi.org/AddressTypeDescriptor#Home",
                              "city": "Grand Bend",
                              "postalCode": "78834",
                              "stateAbbreviationDescriptor": "uri://ed-fi.org/StateAbbreviationDescriptor#TX",
                              "streetNumberName": "980 Green New Boulevard",
                              "nameOfCounty": "WILLISTON",
                              "periods": []
                          }
                      ]
                  }
                  """
             Then it should respond with 201 or 200

    Rule: Duplicate References on assessment
        Background:
            Given the system has these descriptors
                  | descriptorValue                                                 |
                  | uri://ed-fi.org/AcademicSubjectDescriptor#English Language Arts |
                  | uri://ed-fi.org/PerformanceLevelDescriptor#Advanced             |
                  | uri://ed-fi.org/PerformanceLevelDescriptor#Below Basic          |
                  | uri://ed-fi.org/AssessmentReportingMethodDescriptor#Scale score |
                  | uri://ed-fi.org/AssessmentReportingMethodDescriptor#Raw score   |
                  | uri://ed-fi.org/ResultDatatypeTypeDescriptor#Integer            |

        @API-096
        Scenario: 02 Verify clients can create a assessment resource with combined unique descriptors

             When a POST request is made to "/ed-fi/assessments" with
                  """
                  {
                      "assessmentIdentifier": "01774fa3-06f1-47fe-8801-c8b1e65057f2",
                      "namespace": "uri://ed-fi.org/Assessment/Assessment.xml",
                      "assessmentTitle": "3rd Grade Reading 1st Six Weeks 2021-2022",
                      "academicSubjects": [
                          {
                              "academicSubjectDescriptor": "uri://ed-fi.org/AcademicSubjectDescriptor#English Language Arts"
                          }
                      ],
                      "performanceLevels": [
                          {
                              "performanceLevelDescriptor": "uri://ed-fi.org/PerformanceLevelDescriptor#Advanced",
                              "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#Scale score",
                              "minimumScore": "23",
                              "maximumScore": "26"
                          },
                          {
                              "performanceLevelDescriptor": "uri://ed-fi.org/PerformanceLevelDescriptor#Below Basic",
                              "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#Scale score",
                              "minimumScore": "27",
                              "maximumScore": "30"
                          }
                      ],
                      "scores": [
                          {
                              "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#Raw score",
                              "maximumScore": "10",
                              "minimumScore": "0",
                              "resultDatatypeTypeDescriptor": "uri://ed-fi.org/ResultDatatypeTypeDescriptor#Integer"
                          }
                      ]
                  }
                  """
             Then it should respond with 201 or 200

        @API-097
        Scenario: 03 Verify clients can not create a assessment resource with combined duplicate descriptors

             When a POST request is made to "/ed-fi/assessments" with
                  """
                  {
                      "assessmentIdentifier": "01774fa3-06f1-47fe-8801-c8b1e65057f2",
                      "namespace": "uri://ed-fi.org/Assessment/Assessment.xml",
                      "assessmentTitle": "3rd Grade Reading 1st Six Weeks 2021-2022",
                      "academicSubjects": [
                          {
                              "academicSubjectDescriptor": "uri://ed-fi.org/AcademicSubjectDescriptor#English Language Arts"
                          }
                      ],
                      "performanceLevels": [
                          {
                              "performanceLevelDescriptor": "uri://ed-fi.org/PerformanceLevelDescriptor#Advanced",
                              "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#Scale score",
                              "minimumScore": "23",
                              "maximumScore": "26"
                          },
                          {
                              "performanceLevelDescriptor": "uri://ed-fi.org/PerformanceLevelDescriptor#Advanced",
                              "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#Scale score",
                              "minimumScore": "27",
                              "maximumScore": "30"
                          }
                      ],
                      "scores": [
                          {
                              "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#Raw score",
                              "maximumScore": "10",
                              "minimumScore": "0",
                              "resultDatatypeTypeDescriptor": "uri://ed-fi.org/ResultDatatypeTypeDescriptor#Integer"
                          }
                      ]
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
                      "correlationId": null,
                      "validationErrors": {
                          "$.performanceLevels": [
                              "The 2nd item of the performanceLevels has the same identifying values as another item earlier in the list."
                          ]
                      },
                      "errors": []
                  }
                  """

    Rule: Duplicate References
        @API-098
        Scenario: 04 Verify clients cannot create a resource with a duplicate resource reference
             When a POST request is made to "/ed-fi/bellschedules" with
                  """
                  {
                      "schoolReference": {
                          "schoolId": 1
                      },
                      "bellScheduleName": "Test Schedule",
                      "totalInstructionalTime": 325,
                      "classPeriods": [
                          {
                              "classPeriodReference": {
                                  "classPeriodName": "01 - Traditional",
                                  "schoolId": 1
                              }
                          },
                          {
                              "classPeriodReference": {
                                  "classPeriodName": "02 - Traditional",
                                  "schoolId": 1
                              }
                          },
                          {
                              "classPeriodReference": {
                                  "classPeriodName": "03 - Traditional",
                                  "schoolId": 1
                              }
                          },
                          {
                              "classPeriodReference": {
                                  "classPeriodName": "01 - Traditional",
                                  "schoolId": 1
                              }
                          }
                      ],
                      "dates": [],
                      "gradeLevels": []
                  }
                  """
             Then it should respond with 400
              And the response body is
                  """
                  {
                      "validationErrors": {
                          "$.classPeriods": [
                              "The 4th item of the classPeriods has the same identifying values as another item earlier in the list."
                          ]
                      },
                      "errors": [],
                      "detail": "Data validation failed. See 'validationErrors' for details.",
                      "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                      "title": "Data Validation Failed",
                      "status": 400,
                      "correlationId": null
                  }
                  """

        @API-099
        Scenario: 05 Verify clients cannot create a resource with a duplicate descriptor
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 255901001,
                      "nameOfInstitution": "School Test",
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Sixth grade"
                          },
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Seven grade"
                          },
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Seven grade"
                          }
                      ],
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/educationOrganizationCategoryDescriptor#School"
                          }
                      ]
                  }
                  """
             Then it should respond with 400
              And the response body is
                  """
                  {
                      "validationErrors": {
                          "$.gradeLevels": [
                              "The 3rd item of the gradeLevels has the same identifying values as another item earlier in the list."
                          ]
                      },
                      "errors": [],
                      "detail": "Data validation failed. See 'validationErrors' for details.",
                      "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                      "title": "Data Validation Failed",
                      "status": 400,
                      "correlationId": null
                  }
                  """

    Rule: Duplicate References on Student Assessments
        Background:
            Given the system has these descriptors
                  | descriptorValue                                                          |
                  | uri://ed-fi.org/GradeLevelDescriptor#Ninth grade                         |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School           |
                  | uri://ed-fi.org/LanguageDescriptor#eng                                   |
                  | uri://ed-fi.org/AdministrationEnvironmentDescriptor#Testing Center       |
                  | uri://ed-fi.org/RetestIndicatorDescriptor#Primary Administration         |
                  | uri://ed-fi.org/ResultDatatypeTypeDescriptor#Integer                     |
                  | uri://ed-fi.org/AssessmentReportingMethodDescriptor#Raw score            |
                  | uri://ed-fi.org/ResponseIndicatorDescriptor#Nonscorable response         |
                  | uri://ed-fi.org/AssessmentItemResultDescriptor#Correct                   |
                  | uri://ed-fi.org/AcademicSubjectDescriptor#Mathematics                    |
                  | uri://ed-fi.org/AssessmentIdentificationSystemDescriptor#Test Contractor |
                  | uri://ed-fi.org/AssessmentItemCategoryDescriptor#List Question           |
                  | uri://ed-fi.org/LearningStandardScopeDescriptor#State                    |
                  | uri://ed-fi.org/AssessmentCategoryDescriptor#Benchmark test              |
                  | uri://ed-fi.org/ImmunizationTypeDescriptor#MMR                           |
                  | uri://ed-fi.org/ImmunizationTypeDescriptor#IPV                           |
                  | uri://ed-fi.org/ImmunizationTypeDescriptor#DTaP                          |
                  | uri://ed-fi.org/ImmunizationTypeDescriptor#VAR                           |
                  | uri://ed-fi.org/ImmunizationTypeDescriptor#HepB                          |
              And the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 255901001 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
              And the system has these "students"
                  | studentUniqueId | birthDate  | firstName | lastSurname |
                  | "605475"        | 2010-01-13 | Traci     | Mathews     |
              And the system has these "schoolYearTypes"
                  | schoolYear | currentSchoolYear | schoolYearDescription |
                  | 2022       | true              | School Year 2022      |

              And a POST request is made to "/ed-fi/assessments" with
                  """
                  {
                      "assessmentIdentifier": "ae049cb3-33d0-431f-b0f3-a751df7217ef",
                      "namespace": "uri://ed-fi.org/Assessment/Assessment.xml",
                      "assessmentCategoryDescriptor": "uri://ed-fi.org/AssessmentCategoryDescriptor#Benchmark test",
                      "assessmentTitle": "3rd Grade Math 1st Six Weeks 2021-2022",
                      "assessmentVersion": 2021,
                      "maxRawScore": 10,
                      "revisionDate": "2021-09-25",
                      "contentStandard": {
                          "title": "State Essential Knowledge and Skills",
                          "authors": []
                      },
                      "academicSubjects": [
                          {
                              "academicSubjectDescriptor": "uri://ed-fi.org/AcademicSubjectDescriptor#Mathematics"
                          }
                      ],
                      "assessedGradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                          }
                      ],
                      "identificationCodes": [
                          {
                              "assessmentIdentificationSystemDescriptor": "uri://ed-fi.org/AssessmentIdentificationSystemDescriptor#Test Contractor",
                              "identificationCode": "ae049cb3-33d0-431f-b0f3-a751df7217ef"
                          }
                      ],
                      "languages": [
                          {
                              "languageDescriptor": "uri://ed-fi.org/LanguageDescriptor#eng"
                          }
                      ],
                      "performanceLevels": [],
                      "periods": [],
                      "platformTypes": [],
                      "programs": [],
                      "scores": [
                          {
                              "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#Raw score",
                              "maximumScore": "10",
                              "minimumScore": "0",
                              "resultDatatypeTypeDescriptor": "uri://ed-fi.org/ResultDatatypeTypeDescriptor#Integer"
                          }
                      ],
                      "sections": []
                  }
                  """

              And a POST request is made to "/ed-fi/learningStandards" with
                  """
                  {
                      "learningStandardId": "111.15.3.1.A",
                      "courseTitle": "Mathematics, Grade 3",
                      "description": "use place value to read, write (in symbols and words), and describe the value of whole numbers through 999,999.",
                      "learningStandardScopeDescriptor": "uri://ed-fi.org/LearningStandardScopeDescriptor#State",
                      "namespace": "uri://ed-fi.org/LearningStandard/LearningStandard.xml",
                      "contentStandard": {
                          "title": "State Standard",
                          "authors": []
                      },
                      "academicSubjects": [
                          {
                              "academicSubjectDescriptor": "uri://ed-fi.org/AcademicSubjectDescriptor#Mathematics"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"
                          }
                      ],
                      "identificationCodes": []
                  }
                  """
              And a POST request is made to "/ed-fi/assessmentItems" with
                  """
                  {
                      "assessmentReference": {
                          "assessmentIdentifier": "ae049cb3-33d0-431f-b0f3-a751df7217ef",
                          "namespace": "uri://ed-fi.org/Assessment/Assessment.xml"
                      },
                      "identificationCode": "9848478",
                      "assessmentItemCategoryDescriptor": "uri://ed-fi.org/AssessmentItemCategoryDescriptor#List Question",
                      "maxRawScore": 1,
                      "learningStandards": [
                          {
                              "learningStandardReference": {
                                  "learningStandardId": "111.15.3.1.A"
                              }
                          }
                      ],
                      "possibleResponses": [
                          {
                              "responseValue": "C",
                              "correctResponse": true
                          }
                      ]
                  }
                  """
              And a POST request is made to "/ed-fi/assessmentItems" with
                  """
                  {
                      "assessmentReference": {
                          "assessmentIdentifier": "ae049cb3-33d0-431f-b0f3-a751df7217ef",
                          "namespace": "uri://ed-fi.org/Assessment/Assessment.xml"
                      },
                      "identificationCode": "9848480",
                      "assessmentItemCategoryDescriptor": "uri://ed-fi.org/AssessmentItemCategoryDescriptor#List Question",
                      "maxRawScore": 1,
                      "learningStandards": [
                          {
                              "learningStandardReference": {
                                  "learningStandardId": "111.15.3.1.A"
                              }
                          }
                      ],
                      "possibleResponses": [
                          {
                              "responseValue": "C",
                              "correctResponse": true
                          }
                      ]
                  }
                  """
              And a POST request is made to "/ed-fi/assessmentItems" with
                  """
                  {
                      "assessmentReference": {
                          "assessmentIdentifier": "ae049cb3-33d0-431f-b0f3-a751df7217ef",
                          "namespace": "uri://ed-fi.org/Assessment/Assessment.xml"
                      },
                      "identificationCode": "9848481",
                      "assessmentItemCategoryDescriptor": "uri://ed-fi.org/AssessmentItemCategoryDescriptor#List Question",
                      "maxRawScore": 1,
                      "learningStandards": [
                          {
                              "learningStandardReference": {
                                  "learningStandardId": "111.15.3.1.A"
                              }
                          }
                      ],
                      "possibleResponses": [
                          {
                              "responseValue": "C",
                              "correctResponse": true
                          }
                      ]
                  }
                  """
              And a POST request is made to "/ed-fi/assessmentItems" with
                  """
                  {
                      "assessmentReference": {
                          "assessmentIdentifier": "ae049cb3-33d0-431f-b0f3-a751df7217ef",
                          "namespace": "uri://ed-fi.org/Assessment/Assessment.xml"
                      },
                      "identificationCode": "9848484",
                      "assessmentItemCategoryDescriptor": "uri://ed-fi.org/AssessmentItemCategoryDescriptor#List Question",
                      "maxRawScore": 1,
                      "learningStandards": [
                          {
                              "learningStandardReference": {
                                  "learningStandardId": "111.15.3.1.A"
                              }
                          }
                      ],
                      "possibleResponses": [
                          {
                              "responseValue": "C",
                              "correctResponse": true
                          }
                      ]
                  }
                  """

        Scenario: 06 Verify clients can create a a resource with multiple items not duplicated
             When a POST request is made to "/ed-fi/studentAssessments" with
                  """
                  {
                      "studentAssessmentIdentifier": "/OYwqot8L1KQOuf5mVud8r7GALJNLxkZKOjsRN71",
                      "administrationDate": "2021-09-28T15:00:00",
                      "administrationLanguageDescriptor": "uri://ed-fi.org/LanguageDescriptor#eng",
                      "administrationEnvironmentDescriptor": "uri://ed-fi.org/AdministrationEnvironmentDescriptor#Testing Center",
                      "retestIndicatorDescriptor": "uri://ed-fi.org/RetestIndicatorDescriptor#Primary Administration",
                      "scoreResults": [
                          {
                              "result": "4",
                              "resultDatatypeTypeDescriptor": "uri://ed-fi.org/ResultDatatypeTypeDescriptor#Integer",
                              "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#Raw score"
                          }
                      ],
                      "whenAssessedGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade",
                      "studentReference": {
                          "studentUniqueId": "605475"
                      },
                      "assessmentReference": {
                          "assessmentIdentifier": "ae049cb3-33d0-431f-b0f3-a751df7217ef",
                          "namespace": "uri://ed-fi.org/Assessment/Assessment.xml"
                      },
                      "items": [
                          {
                              "assessmentResponse": "G",
                              "responseIndicatorDescriptor": "uri://ed-fi.org/ResponseIndicatorDescriptor#Nonscorable response",
                              "assessmentItemResultDescriptor": "uri://ed-fi.org/AssessmentItemResultDescriptor#Correct",
                              "assessmentItemReference": {
                                  "identificationCode": "9848478",
                                  "assessmentIdentifier": "ae049cb3-33d0-431f-b0f3-a751df7217ef",
                                  "namespace": "uri://ed-fi.org/Assessment/Assessment.xml"
                              }
                          },
                          {
                              "assessmentResponse": "G",
                              "responseIndicatorDescriptor": "uri://ed-fi.org/ResponseIndicatorDescriptor#Nonscorable response",
                              "assessmentItemResultDescriptor": "uri://ed-fi.org/AssessmentItemResultDescriptor#Correct",
                              "assessmentItemReference": {
                                  "identificationCode": "9848480",
                                  "assessmentIdentifier": "ae049cb3-33d0-431f-b0f3-a751df7217ef",
                                  "namespace": "uri://ed-fi.org/Assessment/Assessment.xml"
                              }
                          },
                          {
                              "assessmentResponse": "G",
                              "responseIndicatorDescriptor": "uri://ed-fi.org/ResponseIndicatorDescriptor#Nonscorable response",
                              "assessmentItemResultDescriptor": "uri://ed-fi.org/AssessmentItemResultDescriptor#Correct",
                              "assessmentItemReference": {
                                  "identificationCode": "9848481",
                                  "assessmentIdentifier": "ae049cb3-33d0-431f-b0f3-a751df7217ef",
                                  "namespace": "uri://ed-fi.org/Assessment/Assessment.xml"
                              }
                          },
                          {
                              "assessmentResponse": "G",
                              "responseIndicatorDescriptor": "uri://ed-fi.org/ResponseIndicatorDescriptor#Nonscorable response",
                              "assessmentItemResultDescriptor": "uri://ed-fi.org/AssessmentItemResultDescriptor#Correct",
                              "assessmentItemReference": {
                                  "identificationCode": "9848484",
                                  "assessmentIdentifier": "ae049cb3-33d0-431f-b0f3-a751df7217ef",
                                  "namespace": "uri://ed-fi.org/Assessment/Assessment.xml"
                              }
                          }
                      ],
                      "schoolYearTypeReference": {
                          "schoolYear": 2022
                      }
                  }
                  """
             Then it should respond with 201 or 200

        Scenario: 07 Verify clients cannot create a a resource with duplicated references
             When a POST request is made to "/ed-fi/studentAssessments" with
                  """
                  {
                      "studentAssessmentIdentifier": "/OYwqot8L1KQOuf5mVud8r7GALJNLxkZKOjsRN71",
                      "administrationDate": "2021-09-28T15:00:00",
                      "administrationLanguageDescriptor": "uri://ed-fi.org/LanguageDescriptor#eng",
                      "administrationEnvironmentDescriptor": "uri://ed-fi.org/AdministrationEnvironmentDescriptor#Testing Center",
                      "retestIndicatorDescriptor": "uri://ed-fi.org/RetestIndicatorDescriptor#Primary Administration",
                      "scoreResults": [
                          {
                             "result": "4",
                             "resultDatatypeTypeDescriptor": "uri://ed-fi.org/ResultDatatypeTypeDescriptor#Integer",
                             "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#Raw score"
                          }
                      ],
                      "whenAssessedGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade",
                      "studentReference": {
                          "studentUniqueId": "605475"
                      },
                      "assessmentReference": {
                          "assessmentIdentifier": "ae049cb3-33d0-431f-b0f3-a751df7217ef",
                          "namespace": "uri://ed-fi.org/Assessment/Assessment.xml"
                      },
                      "items": [
                          {
                              "assessmentResponse": "G",
                              "responseIndicatorDescriptor": "uri://ed-fi.org/ResponseIndicatorDescriptor#Nonscorable response",
                              "assessmentItemResultDescriptor": "uri://ed-fi.org/AssessmentItemResultDescriptor#Correct",
                              "assessmentItemReference": {
                                  "identificationCode": "9848478",
                                  "assessmentIdentifier": "ae049cb3-33d0-431f-b0f3-a751df7217ef",
                                  "namespace": "uri://ed-fi.org/Assessment/Assessment.xml"
                              }
                          },
                          {
                              "assessmentResponse": "G",
                              "responseIndicatorDescriptor": "uri://ed-fi.org/ResponseIndicatorDescriptor#Nonscorable response",
                              "assessmentItemResultDescriptor": "uri://ed-fi.org/AssessmentItemResultDescriptor#Correct",
                              "assessmentItemReference": {
                                  "identificationCode": "9848478",
                                  "assessmentIdentifier": "ae049cb3-33d0-431f-b0f3-a751df7217ef",
                                  "namespace": "uri://ed-fi.org/Assessment/Assessment.xml"
                              }
                          },
                          {
                              "assessmentResponse": "G",
                              "responseIndicatorDescriptor": "uri://ed-fi.org/ResponseIndicatorDescriptor#Nonscorable response",
                              "assessmentItemResultDescriptor": "uri://ed-fi.org/AssessmentItemResultDescriptor#Correct",
                              "assessmentItemReference": {
                                  "identificationCode": "9848480",
                                  "assessmentIdentifier": "ae049cb3-33d0-431f-b0f3-a751df7217ef",
                                  "namespace": "uri://ed-fi.org/Assessment/Assessment.xml"
                              }
                          },
                          {
                              "assessmentResponse": "G",
                              "responseIndicatorDescriptor": "uri://ed-fi.org/ResponseIndicatorDescriptor#Nonscorable response",
                              "assessmentItemResultDescriptor": "uri://ed-fi.org/AssessmentItemResultDescriptor#Correct",
                              "assessmentItemReference": {
                                  "identificationCode": "9848481",
                                  "assessmentIdentifier": "ae049cb3-33d0-431f-b0f3-a751df7217ef",
                                  "namespace": "uri://ed-fi.org/Assessment/Assessment.xml"
                              }
                          },
                          {
                              "assessmentResponse": "G",
                              "responseIndicatorDescriptor": "uri://ed-fi.org/ResponseIndicatorDescriptor#Nonscorable response",
                              "assessmentItemResultDescriptor": "uri://ed-fi.org/AssessmentItemResultDescriptor#Correct",
                              "assessmentItemReference": {
                                  "identificationCode": "9848484",
                                  "assessmentIdentifier": "ae049cb3-33d0-431f-b0f3-a751df7217ef",
                                  "namespace": "uri://ed-fi.org/Assessment/Assessment.xml"
                              }
                          }
                      ],
                      "schoolYearTypeReference": {
                          "schoolYear": "2022"
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
                      "validationErrors": {
                        "$.items": [
                          "The 2nd item of the items has the same identifying values as another item earlier in the list."
                        ]
                      },
                      "errors": []
                  }
                  """

        Scenario: 08 Can create requiredImmunizations on studentHealths with same dates in different requiredImmunizations
             When a POST request is made to "/ed-fi/studentHealths" with
                  """
                  {
                     "asOfDate": "2021-08-23",
                     "educationOrganizationReference": {
                         "educationOrganizationId": "255901001"
                     },
                     "requiredImmunizations": [
                         {
                             "dates": [
                                 {
                                     "immunizationDate": "2007-07-01"
                                 }
                             ],
                             "immunizationTypeDescriptor": "uri://ed-fi.org/ImmunizationTypeDescriptor#MMR"
                         },
                         {
                             "dates": [
                                 {
                                     "immunizationDate": "2010-04-01"
                                 }
                             ],
                             "immunizationTypeDescriptor": "uri://ed-fi.org/ImmunizationTypeDescriptor#IPV"
                         },
                         {
                             "dates": [
                                 {
                                     "immunizationDate": "2010-04-01"
                                 }
                             ],
                             "immunizationTypeDescriptor": "uri://ed-fi.org/ImmunizationTypeDescriptor#DTaP"
                         },
                         {
                             "dates": [
                                 {
                                     "immunizationDate": "2010-12-01"
                                 }
                             ],
                             "immunizationTypeDescriptor": "uri://ed-fi.org/ImmunizationTypeDescriptor#VAR"
                         },
                         {
                             "immunizationTypeDescriptor": "uri://ed-fi.org/ImmunizationTypeDescriptor#HepB"
                         }
                     ],
                     "studentReference": {
                         "studentUniqueId": "605475"
                     }
                  }
                  """
             Then it should respond with 201
