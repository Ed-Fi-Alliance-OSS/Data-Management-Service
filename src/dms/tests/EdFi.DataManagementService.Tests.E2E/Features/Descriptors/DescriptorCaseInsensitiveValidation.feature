Feature: Descriptor CaseInsensitive Validation

        Background:
            Given the claimSet "EdFiSandbox" is authorized with namespacePrefixes "uri://ed-fi.org"
              And a POST request is made to "/ed-fi/assessmentReportingMethodDescriptors" with
                  """
                    {
                    "codeValue": "Scale score",
                    "description": "Scale score",
                    "effectiveBeginDate": "2024-05-14",
                    "effectiveEndDate": "2025-05-14",
                    "namespace": "uri://ed-fi.org/AssessmentReportingMethodDescriptor",
                    "shortDescription": "Scale score"
                    }
                  """
              And a POST request is made to "/ed-fi/performanceLevelDescriptors" with
                  """
                    {
                    "codeValue": "Met standard",
                    "description": "Met standard",
                    "effectiveBeginDate": "2024-05-14",
                    "effectiveEndDate": "2025-05-14",
                    "namespace": "uri://ed-fi.org/PerformanceLevelDescriptor",
                    "shortDescription": "Met standard"
                    }
                  """
              And a POST request is made to "/ed-fi/AssessmentCategoryDescriptors" with
                  """
                    {
                        "codeValue": "College entrance exam",
                        "description": "College entrance exam",
                        "effectiveBeginDate": "2024-05-14",
                        "effectiveEndDate": "2025-05-14",
                        "namespace": "uri://ed-fi.org/AssessmentCategoryDescriptor",
                        "shortDescription": "College entrance exam"
                    }
                  """
              And a POST request is made to "/ed-fi/AcademicSubjectDescriptors" with
                  """
                    {
                        "codeValue": "Reading",
                        "description": "Reading",
                        "effectiveBeginDate": "2024-05-14",
                        "effectiveEndDate": "2025-05-14",
                        "namespace": "uri://ed-fi.org/AcademicSubjectDescriptor",
                        "shortDescription": "Reading"
                    }
                  """
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
