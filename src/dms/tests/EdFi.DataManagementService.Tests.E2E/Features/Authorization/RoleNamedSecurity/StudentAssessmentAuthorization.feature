Feature: StudentAssessment Authorization

        Background:
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901"
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | categories                                                                                                          | localEducationAgencyCategoryDescriptor                     |
                  | 255901                 | Test LEA          | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#District" }] | uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC |
              And the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                   | localEducationAgencyReference      |
                  | 255901001 | School 001        | [{ "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" }] | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school" }] | {"localEducationAgencyId": 255901} |
                  | 255901044 | School 044        | [{ "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" }] | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school" }] | {"localEducationAgencyId": 255901} |
              And the system has these "students"
                  | studentUniqueId | firstName  | lastSurname | birthDate  |
                  | "61"            | student-fn | student-ln  | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | studentReference          | schoolReference         | entryGradeLevelDescriptor                          | entryDate  |
                  | {"studentUniqueId": "61"} | {"schoolId": 255901001} | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 |
              And the system has these "assessments"
                  | assessmentIdentifier | namespace                                 | assessmentTitle                           | academicSubjects                                                                                     |
                  | Test Assessment      | uri://ed-fi.org/Assessment/Assessment.xml | 3rd Grade Reading 1st Six Weeks 2021-2022 | [{ "academicSubjectDescriptor": "uri://ed-fi.org/AcademicSubjectDescriptor#English Language Arts" }] |
              And the system has these "studentAssessments"
                  | _storeResultingIdInVariable | studentAssessmentIdentifier | reportedSchoolReference   | studentReference          | assessmentReference                                                                                  |
                  | studentAssessmentId         | Test Student Assessment     | {"schoolId": "255901001"} | {"studentUniqueId": "61"} | {"assessmentIdentifier": "Test Assessment","namespace": "uri://ed-fi.org/Assessment/Assessment.xml"} |

    # StudentAssessments have NamespaceBased authorization by default. Tests must be run after overriding authorization to RelationshipsWithEdOrgsOnly.
    @ignore
    Rule: When the client is authorized
        Background:
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901001"

        Scenario: 01 Ensure authorized client can create a StudentAssessment
             When a POST request is made to "/ed-fi/studentAssessments" with
                  """
                  {
                    "reportedSchoolReference": {
                        "schoolId": 255901001
                    },
                    "studentAssessmentIdentifier": "New Test Student Assessment",
                    "studentReference": {
                        "studentUniqueId": "61"
                    },
                    "assessmentReference": {
                        "assessmentIdentifier": "Test Assessment","namespace": "uri://ed-fi.org/Assessment/Assessment.xml"
                    }
                  }
                  """
             Then it should respond with 201

        Scenario: 02 Ensure authorized client can get a StudentAssessment
             When a GET request is made to "/ed-fi/studentAssessments/{studentAssessmentId}"
             Then it should respond with 200

        Scenario: 03 Ensure authorized client can update a StudentAssessment
             When a PUT request is made to "/ed-fi/studentAssessments/{studentAssessmentId}" with
                  """
                  {
                    "id": "{studentAssessmentId}",
                    "reportedSchoolReference": {
                        "schoolId": 255901001
                    },
                    "studentAssessmentIdentifier": "Test Student Assessment",
                    "studentReference": {
                        "studentUniqueId": "61"
                    },
                    "assessmentReference": {
                        "assessmentIdentifier": "Test Assessment","namespace": "uri://ed-fi.org/Assessment/Assessment.xml"
                    }
                  }
                  """
             Then it should respond with 204

        Scenario: 04 Ensure authorized client can delete a StudentAssessment
             When a DELETE request is made to "/ed-fi/studentAssessments/{studentAssessmentId}"
             Then it should respond with 204

    # StudentAssessments have NamespaceBased authorization by default. Tests must be run after overriding authorization to RelationshipsWithEdOrgsOnly.
    @ignore
    Rule: When the client is unauthorized
        Background:
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901044"

        Scenario: 05 Ensure unauthorized client can not create a StudentAssessment
             When a POST request is made to "/ed-fi/studentAssessments" with
                  """
                  {
                    "reportedSchoolReference": {
                        "schoolId": 255901001
                    },
                    "studentAssessmentIdentifier": "New Test Student Assessment",
                    "studentReference": {
                        "studentUniqueId": "61"
                    },
                    "assessmentReference": {
                        "assessmentIdentifier": "Test Assessment","namespace": "uri://ed-fi.org/Assessment/Assessment.xml"
                    }
                  }
                  """
             Then it should respond with 403
              And the response body is
                  """
                  {
                     "detail": "Access to the resource could not be authorized.",
                     "type": "urn:ed-fi:api:security:authorization:",
                     "title": "Authorization Denied",
                     "status": 403,
                     "validationErrors": {},
                     "errors": [
                        "No relationships have been established between the caller's education organization id claims ('255901044') and the resource item's EducationOrganizationId value."
                     ]
                  }
                  """

        Scenario: 06 Ensure unauthorized client can not get a StudentAssessment
             When a GET request is made to "/ed-fi/studentAssessments/{studentAssessmentId}"
             Then it should respond with 403
              And the response body is
                  """
                  {
                     "detail": "Access to the resource could not be authorized.",
                     "type": "urn:ed-fi:api:security:authorization:",
                     "title": "Authorization Denied",
                     "status": 403,
                     "validationErrors": {},
                     "errors": [
                        "No relationships have been established between the caller's education organization id claims ('255901044') and the resource item's EducationOrganizationId value."
                     ]
                  }
                  """

        Scenario: 07 Ensure unauthorized client can not update a StudentAssessment
             When a PUT request is made to "/ed-fi/studentAssessments/{studentAssessmentId}" with
                  """
                  {
                    "id": "{studentAssessmentId}",
                    "reportedSchoolReference": {
                        "schoolId": 255901001
                    },
                    "studentAssessmentIdentifier": "Test Student Assessment",
                    "studentReference": {
                        "studentUniqueId": "61"
                    },
                    "assessmentReference": {
                        "assessmentIdentifier": "Test Assessment","namespace": "uri://ed-fi.org/Assessment/Assessment.xml"
                    }
                  }
                  """
             Then it should respond with 403
              And the response body is
                  """
                  {
                     "detail": "Access to the resource could not be authorized.",
                     "type": "urn:ed-fi:api:security:authorization:",
                     "title": "Authorization Denied",
                     "status": 403,
                     "validationErrors": {},
                     "errors": [
                        "No relationships have been established between the caller's education organization id claims ('255901044') and the resource item's EducationOrganizationId value."
                     ]
                  }
                  """

        Scenario: 08 Ensure unauthorized client can not delete a StudentAssessment
             When a DELETE request is made to "/ed-fi/studentAssessments/{studentAssessmentId}"
             Then it should respond with 403
              And the response body is
                  """
                  {
                     "detail": "Access to the resource could not be authorized.",
                     "type": "urn:ed-fi:api:security:authorization:",
                     "title": "Authorization Denied",
                     "status": 403,
                     "validationErrors": {},
                     "errors": [
                        "No relationships have been established between the caller's education organization id claims ('255901044') and the resource item's EducationOrganizationId value."
                     ]
                  }
                  """
