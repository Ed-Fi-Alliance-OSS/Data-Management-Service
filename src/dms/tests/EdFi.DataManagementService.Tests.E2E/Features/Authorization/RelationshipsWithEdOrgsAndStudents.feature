Feature: RelationshipsWithEdOrgsAndPeople Authorization

Background:
Given the claimSet "EdFiAPIPublisherWriter" is authorized with educationOrganizationIds "255901001, 244901"
             And the system has these "schoolYearTypes"
                | schoolYear | currentSchoolYear | schoolYearDescription |
                | 2023       |     true              | "year 2023"       |
             And the system has these descriptors
                | descriptorValue                                                       |
                | uri://ed-fi.org/CourseAttemptResultDescriptor#Pass                    |
                | uri://ed-fi.org/TermDescriptor#Semester                               |
                | uri://ed-fi.org/CourseIdentificationSystemDescriptor#CSSC course code |
                | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school        |
                | uri://ed-fi.org/ExitWithdrawTypeDescriptor#Student withdrew           |

 Rule: Resource respect RelationshipsWithEdOrgsAndPeople authorization

        Background:
          Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901001, 244901"

              And the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                       |
                  | 255901001 | ACC-test       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName | lastSurname | birthDate   |
                  | "98989898"        | student-fn  | student-ln| 2008-01-01|
                  | "98989899"        | student-fn  | student-ln| 2008-01-01|
              And the system has these "courses"
                  | courseCode       | identificationCodes                                                                                                                                   | educationOrganizationReference          | courseTitle        | numberOfParts |
                  | ACC-test-Course1 | [ {"courseIdentificationSystemDescriptor": "uri://ed-fi.org/CourseIdentificationSystemDescriptor#CSSC course code", "identificationCode": "Id-123"} ] | {"educationOrganizationId": 255901001 } | "ACC-test-Course1" | 8             |
                  | ACC-test-Course2 | [ {"courseIdentificationSystemDescriptor": "uri://ed-fi.org/CourseIdentificationSystemDescriptor#CSSC course code", "identificationCode": "Id-123"} ] | {"educationOrganizationId": 255901001 } | "ACC-test-Course2" | 8             |
                  | ACC-test-Course3 | [ {"courseIdentificationSystemDescriptor": "uri://ed-fi.org/CourseIdentificationSystemDescriptor#CSSC course code", "identificationCode": "Id-123"} ] | {"educationOrganizationId": 255901001 } | "ACC-test-Course3" | 8             |
              Given the system has these "studentschoolassociations"
                  | studentReference                | schoolReference           | entryGradeLevelDescriptor                            | entryDate  | exitGradeLevel                                       | exitWithdrawTypeDescriptor                                    |
                  | { "studentUniqueId": "98989898" } | { "schoolId": 255901001 } | "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary" | 2023-08-01 | "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary" | "uri://ed-fi.org/ExitWithdrawTypeDescriptor#Student withdrew" |

             And the system has these "studentacademicrecords"
                  | studentReference                | educationOrganizationReference           | schoolYearTypeReference  | termDescriptor                            |
                  | { "studentUniqueId": "98989898" } | { "educationOrganizationId": 255901001 } | {"schoolYear": 2023}     | "uri://ed-fi.org/TermDescriptor#Semester" |
            

 Scenario: 01 Ensure client can create a courseTranscripts with edorg id:255901001 and student id:98989898
             When a POST request is made to "/ed-fi/courseTranscripts" with
                  """
                  {
                      "courseAttemptResultDescriptor": "uri://ed-fi.org/CourseAttemptResultDescriptor#Pass",
                      "courseReference": {
                        "courseCode": "ACC-test-Course1",
                        "educationOrganizationId": 255901001
                      }, 
                      "studentAcademicRecordReference": {
                        "educationOrganizationId": 255901001,
                        "schoolYear": 2023,
                        "studentUniqueId": "98989898",
                        "termDescriptor": "uri://ed-fi.org/TermDescriptor#Semester"
                      }
                    }
                  """
             Then it should respond with 201 or 200
             When a GET request is made to "/ed-fi/courseTranscripts/{id}"
             Then it should respond with 200
             And the response body is
             """
                 {
                 "id":"{id}",
                 "courseAttemptResultDescriptor": "uri://ed-fi.org/CourseAttemptResultDescriptor#Pass",
                 "courseReference": {
                   "courseCode": "ACC-test-Course1",
                   "educationOrganizationId": 255901001
                 }, 
                 "studentAcademicRecordReference": {
                   "educationOrganizationId": 255901001,
                   "schoolYear": 2023,
                   "studentUniqueId": "98989898",
                   "termDescriptor": "uri://ed-fi.org/TermDescriptor#Semester"
                 }
               }
             """

 Scenario: 02 Ensure client can update a courseTranscripts with edorg id:255901001 and student id:98989898
             When a POST request is made to "/ed-fi/courseTranscripts" with
                  """
                  {
                      "courseAttemptResultDescriptor": "uri://ed-fi.org/CourseAttemptResultDescriptor#Pass",
                      "courseReference": {
                        "courseCode": "ACC-test-Course2",
                        "educationOrganizationId": 255901001
                      }, 
                      "studentAcademicRecordReference": {
                        "educationOrganizationId": 255901001,
                        "schoolYear": 2023,
                        "studentUniqueId": "98989898",
                        "termDescriptor": "uri://ed-fi.org/TermDescriptor#Semester"
                      }
                    }
                  """
             Then it should respond with 201 or 200
             When a PUT request is made to "/ed-fi/courseTranscripts/{id}" with
                  """
                  {
                      "id":"{id}",
                      "courseAttemptResultDescriptor": "uri://ed-fi.org/CourseAttemptResultDescriptor#Pass",
                      "courseReference": {
                        "courseCode": "ACC-test-Course2",
                        "educationOrganizationId": 255901001
                      },
                      "courseTitle":"ACC-test-Course2-title",
                      "studentAcademicRecordReference": {
                        "educationOrganizationId": 255901001,
                        "schoolYear": 2023,
                        "studentUniqueId": "98989898",
                        "termDescriptor": "uri://ed-fi.org/TermDescriptor#Semester"
                      }
                    }
                  """
             Then it should respond with 204
             When a GET request is made to "/ed-fi/courseTranscripts/{id}"
             Then it should respond with 200
             And the response body is
             """
                 {
                 "id":"{id}",
                 "courseAttemptResultDescriptor": "uri://ed-fi.org/CourseAttemptResultDescriptor#Pass",
                 "courseReference": {
                   "courseCode": "ACC-test-Course2",
                   "educationOrganizationId": 255901001
                 },
                 "courseTitle":"ACC-test-Course2-title",
                 "studentAcademicRecordReference": {
                   "educationOrganizationId": 255901001,
                   "schoolYear": 2023,
                   "studentUniqueId": "98989898",
                   "termDescriptor": "uri://ed-fi.org/TermDescriptor#Semester"
                 }
               }
             """

 Scenario: 03 Ensure client can delete a courseTranscripts with edorg id:255901001 and student id:98989898
             When a POST request is made to "/ed-fi/courseTranscripts" with
                  """
                  {
                      "courseAttemptResultDescriptor": "uri://ed-fi.org/CourseAttemptResultDescriptor#Pass",
                      "courseReference": {
                        "courseCode": "ACC-test-Course3",
                        "educationOrganizationId": 255901001
                      }, 
                      "studentAcademicRecordReference": {
                        "educationOrganizationId": 255901001,
                        "schoolYear": 2023,
                        "studentUniqueId": "98989898",
                        "termDescriptor": "uri://ed-fi.org/TermDescriptor#Semester"
                      }
                    }
                  """
             Then it should respond with 201 or 200
             When a GET request is made to "/ed-fi/courseTranscripts/{id}"
             Then it should respond with 200
             And the response body is
             """
               {
                  "id": "{id}",
                  "courseReference": {
                    "courseCode": "ACC-test-Course3",
                    "educationOrganizationId": 255901001
                  },
                  "courseAttemptResultDescriptor": "uri://ed-fi.org/CourseAttemptResultDescriptor#Pass",
                  "studentAcademicRecordReference": {
                    "schoolYear": 2023,
                    "termDescriptor": "uri://ed-fi.org/TermDescriptor#Semester",
                    "studentUniqueId": "98989898",
                    "educationOrganizationId": 255901001
                  }
                }
             """
            When a DELETE request is made to "/ed-fi/courseTranscripts/{id}"
            Then it should respond with 204

 Scenario: 04 Ensure client can not create a courseTranscripts with out student school association
    When a POST request is made to "/ed-fi/courseTranscripts" with
        """
        {
            "courseAttemptResultDescriptor": "uri://ed-fi.org/CourseAttemptResultDescriptor#Pass",
            "courseReference": {
              "courseCode": "ACC-test-Course1",
              "educationOrganizationId": 255901001
            }, 
            "studentAcademicRecordReference": {
              "educationOrganizationId": 255901001,
              "schoolYear": 2023,
              "studentUniqueId": "98989899",
              "termDescriptor": "uri://ed-fi.org/TermDescriptor#Semester"
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
                    "No relationships have been established between the caller's education organization id claims ('255901001', '244901') and one or more of the following properties of the resource item: 'EducationOrganizationId', 'StudentUniqueId'."
                  ]
                }
                  """

 Scenario: 05 Ensure client can not update a courseTranscripts with out student school association
    When a POST request is made to "/ed-fi/courseTranscripts" with
        """
        {
            "courseAttemptResultDescriptor": "uri://ed-fi.org/CourseAttemptResultDescriptor#Pass",
            "courseReference": {
              "courseCode": "ACC-test-Course1",
              "educationOrganizationId": 255901001
            }, 
            "studentAcademicRecordReference": {
              "educationOrganizationId": 255901001,
              "schoolYear": 2023,
              "studentUniqueId": "98989898",
              "termDescriptor": "uri://ed-fi.org/TermDescriptor#Semester"
            }
          }
        """
    Then it should respond with 201 or 200
    When a PUT request is made to "/ed-fi/courseTranscripts/{id}" with
                  """
                  {
                      "id":"{id}",
                      "courseAttemptResultDescriptor": "uri://ed-fi.org/CourseAttemptResultDescriptor#Pass",
                       "courseReference": {
                       "courseCode": "ACC-test-Course1",
                       "educationOrganizationId": 255901001
                     }, 
                    "studentAcademicRecordReference": {
                      "educationOrganizationId": 255901001,
                      "schoolYear": 2023,
                      "studentUniqueId": "98989899",
                      "termDescriptor": "uri://ed-fi.org/TermDescriptor#Semester"
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
                "No relationships have been established between the caller's education organization id claims ('255901001', '244901') and one or more of the following properties of the resource item: 'EducationOrganizationId', 'StudentUniqueId'."
                ]
            }
            """

 Rule: DELETE or GET resource fails with a 403 forbidden error with no student school association
 Background:
     Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "3, 301"
       And the system has these descriptors
                  | descriptorValue                                                          |
                  | uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#State       |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#District    |
                  | uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade                         |
                  | uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC               |
     And the system has these "stateEducationAgencies"
                  | stateEducationAgencyId | nameOfInstitution | categories                                                                                                            |
                  | 3                      | Test state        | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#State" }] |
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | stateEducationAgencyReference   | categories                                                                                                               | localEducationAgencyCategoryDescriptor                       |
                  | 301                    | Test LEA          | { "stateEducationAgencyId": 3 } | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#District" }] | "uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC" |
              And the system has these "schools"
                  | schoolId     | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                        | localEducationAgencyReference    |
                  | 30101 | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] | { "localEducationAgencyId": 301} |
              And the system has these "students"
                  | studentUniqueId | firstName | lastSurname | birthDate   |
                  | "11111"        | student-fn  | student-ln| 2008-01-01|
              And the system has these "studentSchoolAssociations"
                  | studentReference               | schoolReference              | entryGradeLevelDescriptor                          | entryDate  | exitGradeLevel                                     | exitWithdrawTypeDescriptor                                    |
                  | { "studentUniqueId": "11111" } | { "schoolId": 30101 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | 2023-08-01 | "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade" | "uri://ed-fi.org/ExitWithdrawTypeDescriptor#Student withdrew" |
@addrelationships
Scenario: 06 Ensure client can not delete or get a PostSecondaryEvent with out student school association

     When a POST request is made to "/ed-fi/PostSecondaryEvents" with
            """
            {
                "eventDate": "2023-09-15",
                "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                "studentReference": {
                  "studentUniqueId": "11111"
                }
              }
            """
        Then it should respond with 201 or 200
        When a GET request is made to "/ed-fi/PostSecondaryEvents/{id}"
        Then it should respond with 200
        And the response body is
            """
            {
                "id": "{id}",
                "eventDate": "2023-09-15",
                "postSecondaryEventCategoryDescriptor": "uri://ed-fi.org/PostSecondaryEventCategoryDescriptor#College Application",
                "studentReference": {
                  "studentUniqueId": "11111"
                }
              }
            """
        When a relationship with "studentSchoolAssociations" is deleted
        Then it should respond with 204
        When a GET request is made to "/ed-fi/PostSecondaryEvents/{id}"
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
                            "No relationships have been established between the caller's education organization id claims ('3', '301') and one or more of the following properties of the resource item: 'EducationOrganizationId', 'StudentUniqueId'."
                        ]
                      }
                """
        When a DELETE request is made to "/ed-fi/PostSecondaryEvents/{id}"
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
                        "No relationships have been established between the caller's education organization id claims ('3', '301') and one or more of the following properties of the resource item: 'EducationOrganizationId', 'StudentUniqueId'."
                      ]
                    }
                """
