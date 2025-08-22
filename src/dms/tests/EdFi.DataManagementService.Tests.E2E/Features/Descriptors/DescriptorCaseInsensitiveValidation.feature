Feature: Descriptor CaseInsensitive Validation
        Background:
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901"
              And the system has these "schools"
                  | schoolId | nameOfInstitution            | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 255901   | Grand Bend Elementary School | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#First Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these descriptors
                  | descriptorValue                                                      |
                  | uri://ed-fi.org/AssessmentCategoryDescriptor#College entrance exam   |
                  | uri://ed-fi.org/AcademicSubjectDescriptor#Reading                    |
                  | uri://ed-fi.org/PerformanceLevelDescriptor#Met Standard              |
                  | uri://ed-fi.org/AssessmentReportingMethodDescriptor#Scale Score      |
                  | uri://ed-fi.org/CourseIdentificationSystemDescriptor#LEA course code |
                  | uri://ed-fi.org/CourseGpaApplicabilityDescriptor#Applicable          |
              
        Scenario: 1 Ensure clients can create objectiveAssessments with case-insensitive descriptor values
             When a POST request is made to "/ed-fi/assessments" with
                  """
                    {
                        "assessmentIdentifier": "ACT-ABW",
                        "namespace": "uri://ed-fi.org/Assessment",
                        "assessmentCategoryDescriptor": "uri://ed-fi.org/AssessmentCategoryDescriptor#College entrance exam",
                        "assessmentTitle": "The ACT with Writing",
                        "academicSubjects": [
                          {
                            "academicSubjectDescriptor": "uri://ed-fi.org/AcademicSubjectDescriptor#Reading"
                          }
                        ]
                    }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                    {
                        "location": "/ed-fi/assessments/{id}"
                    }
                  """
              And the record can be retrieved with a GET request
                  """
                  {
                    "id": "{id}",
                    "assessmentIdentifier": "ACT-ABW",
                    "namespace": "uri://ed-fi.org/Assessment",
                    "assessmentCategoryDescriptor": "uri://ed-fi.org/AssessmentCategoryDescriptor#College entrance exam",
                    "assessmentTitle": "The ACT with Writing",
                    "academicSubjects": [
                        {
                        "academicSubjectDescriptor": "uri://ed-fi.org/AcademicSubjectDescriptor#Reading"
                        }
                    ]
                  }
                  """
             When a POST request is made to "/ed-fi/objectiveAssessments" with
                  """
                    {
                        "assessmentReference": {
                          "assessmentIdentifier": "ACT-ABW",
                          "namespace": "uri://ed-fi.org/Assessment"
                        },
                        "identificationCode": "ACT-ABW-E",
                        "description": "The ACT with Writing - English",
                        "performanceLevels": [
                          {
                            "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#Scale Score",
                            "performanceLevelDescriptor": "uri://ed-fi.org/PerformanceLevelDescriptor#Met Standard"
                          }
                        ]
                      }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                    {
                        "location": "/ed-fi/objectiveAssessments/{id}"
                    }
                  """
              And the record can be retrieved with a GET request
                  """
                  {
                    "id": "{id}",
                    "description": "The ACT with Writing - English",
                    "performanceLevels": [
                        {
                            "performanceLevelDescriptor": "uri://ed-fi.org/PerformanceLevelDescriptor#Met Standard",
                            "assessmentReportingMethodDescriptor": "uri://ed-fi.org/AssessmentReportingMethodDescriptor#Scale Score"
                        }
                    ],
                    "identificationCode": "ACT-ABW-E",
                    "assessmentReference": {
                        "namespace": "uri://ed-fi.org/Assessment",
                        "assessmentIdentifier": "ACT-ABW"
                    }
                  }
                  """

        Scenario: 2 Ensure clients can create courses with mixed-case namespace descriptor URIs
             When a POST request is made to "/ed-fi/courses" with
                  """
                    {
                        "courseCode": "ALG-1",
                        "educationOrganizationReference": {
                            "educationOrganizationId": 255901
                        },
                        "courseTitle": "Algebra I",
                        "numberOfParts": 1,
                        "identificationCodes": [
                            {
                                "courseIdentificationSystemDescriptor": "uri://ed-fi.org/COURSEIdentificationSystemDescriptor#LEA course code",
                                "identificationCode": "ALG-1"
                            }
                        ],
                        "courseGPAApplicabilityDescriptor": "uri://ed-fi.org/COURSEGPAApplicabilityDescriptor#Applicable"
                    }
                  """
             Then it should respond with 201
              And the record can be retrieved with a GET request
                  """
                  {
                    "id": "{id}",
                    "courseCode": "ALG-1",
                    "educationOrganizationReference": {
                        "educationOrganizationId": 255901
                    },
                    "courseTitle": "Algebra I",
                    "numberOfParts": 1,
                    "identificationCodes": [
                        {
                            "courseIdentificationSystemDescriptor": "uri://ed-fi.org/COURSEIdentificationSystemDescriptor#LEA course code",
                            "identificationCode": "ALG-1"
                        }
                    ],
                    "courseGPAApplicabilityDescriptor": "uri://ed-fi.org/COURSEGPAApplicabilityDescriptor#Applicable"
                  }
                  """

        Scenario: 3 Ensure clients can create courses with all-uppercase namespace descriptor URIs
             When a POST request is made to "/ed-fi/courses" with
                  """
                    {
                        "courseCode": "MATH-2",
                        "educationOrganizationReference": {
                            "educationOrganizationId": 255901
                        },
                        "courseTitle": "Mathematics II",
                        "numberOfParts": 1,
                        "identificationCodes": [
                            {
                                "courseIdentificationSystemDescriptor": "uri://ed-fi.org/COURSEIDENTIFICATIONSYSTEMDESCRIPTOR#LEA course code",
                                "identificationCode": "MATH-2"
                            }
                        ],
                        "courseGPAApplicabilityDescriptor": "uri://ed-fi.org/COURSEGPAAPPLICABILITYDESCRIPTOR#Applicable"
                    }
                  """
             Then it should respond with 201
              And the record can be retrieved with a GET request
                  """
                  {
                    "id": "{id}",
                    "courseCode": "MATH-2",
                        "educationOrganizationReference": {
                            "educationOrganizationId": 255901
                        },
                        "courseTitle": "Mathematics II",
                        "numberOfParts": 1,
                        "identificationCodes": [
                            {
                                "courseIdentificationSystemDescriptor": "uri://ed-fi.org/COURSEIDENTIFICATIONSYSTEMDESCRIPTOR#LEA course code",
                                "identificationCode": "MATH-2"
                            }
                        ],
                        "courseGPAApplicabilityDescriptor": "uri://ed-fi.org/COURSEGPAAPPLICABILITYDESCRIPTOR#Applicable"
                  }
                  """

        Scenario: 4 Ensure clients can create courses with mixed upper/lower throughout descriptor URIs
             When a POST request is made to "/ed-fi/courses" with
                  """
                    {
                        "courseCode": "SCI-3",
                        "educationOrganizationReference": {
                            "educationOrganizationId": 255901
                        },
                        "courseTitle": "Science III",
                        "numberOfParts": 1,
                        "identificationCodes": [
                            {
                                "courseIdentificationSystemDescriptor": "uri://ed-fi.org/courseIDENTIFICATIONsystemDESCRIPTOR#lea COURSE code",
                                "identificationCode": "SCI-3"
                            }
                        ],
                        "courseGPAApplicabilityDescriptor": "uri://ed-fi.org/courseGPAapplicabilityDESCRIPTOR#APPLICABLE"
                    }
                  """
             Then it should respond with 201
              And the record can be retrieved with a GET request
                  """
                  {
                    "id": "{id}",
                    "courseCode": "SCI-3",
                        "educationOrganizationReference": {
                            "educationOrganizationId": 255901
                        },
                        "courseTitle": "Science III",
                        "numberOfParts": 1,
                        "identificationCodes": [
                            {
                                "courseIdentificationSystemDescriptor": "uri://ed-fi.org/courseIDENTIFICATIONsystemDESCRIPTOR#lea COURSE code",
                                "identificationCode": "SCI-3"
                            }
                        ],
                        "courseGPAApplicabilityDescriptor": "uri://ed-fi.org/courseGPAapplicabilityDESCRIPTOR#APPLICABLE"
                  }
                  """
