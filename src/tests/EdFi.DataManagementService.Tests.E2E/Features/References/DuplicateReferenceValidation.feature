Feature: Validate the duplicate references

        @API-095
        Scenario: 01 Verify clients can create a studentEducationOrganizationAssociation resource with combined unique descriptors
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
                        "$.performanceLevels[*].assessmentReportingMethodDescriptor": [
                            "The 2nd item of the performanceLevels has the same identifying values as another item earlier in the list."
                        ],
                        "$.performanceLevels[*].performanceLevelDescriptor": [
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
                          },
                          {
                              "classPeriodReference": {
                                  "classPeriodName": "02 - Traditional",
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
                            "$.ClassPeriod": [
                                "The 4th item of the ClassPeriod has the same identifying values as another item earlier in the list.",
                                "The 5th item of the ClassPeriod has the same identifying values as another item earlier in the list."
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
                      "schoolId":255901001,
                      "nameOfInstitution":"School Test",
                      "gradeLevels": [
                          {
                          "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Sixth grade"
                          },
                          {
                          "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Seven grade"
                          },
                          {
                          "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Seven grade"
                          },
                          {
                          "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Sixth grade"
                          }
                      ],
                      "educationOrganizationCategories":[
                          {
                              "educationOrganizationCategoryDescriptor":"uri://ed-fi.org/educationOrganizationCategoryDescriptor#School"
                          }
                      ]
                  }
                  """
             Then it should respond with 400
              And the response body is
                  """
                  {
                        "validationErrors": {
                            "$.gradeLevels[*].gradeLevelDescriptor": [
                                "The 3rd item of the gradeLevels has the same identifying values as another item earlier in the list.",
                                "The 4th item of the gradeLevels has the same identifying values as another item earlier in the list."
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


