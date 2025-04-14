Feature: RelationshipsWithEdOrgsAndPeople Authorization

    Rule: Resource respect RelationshipsWithEdOrgsAndPeople authorization

        Background:
            Given the claimSet "EdFiAPIPublisherWriter" is authorized with educationOrganizationIds "255901001, 244901"
             And the system has these "schoolYearTypes"
                | schoolYear | currentSchoolYear | schoolYearDescription |
                | 2023       |     true              | "year 2023"       |

            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901001, 244901"
             And the system has these descriptors
                  | descriptorValue                                                       |
                  | uri://ed-fi.org/CourseAttemptResultDescriptor#Pass                    |
                  | uri://ed-fi.org/TermDescriptor#Semester                               |
                  | uri://ed-fi.org/CourseIdentificationSystemDescriptor#CSSC course code |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school        |
                  | uri://ed-fi.org/ExitWithdrawTypeDescriptor#Student withdrew           |

              And the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                       |
                  | 255901001 | ACC-test       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "students"
                  | studentUniqueId | firstName | lastSurname | birthDate |
                  | "98989898"        | student-fn  | student-ln| 2008-01-01|
              And the system has these "courses"
                  | courseCode       | identificationCodes                                                                                                                                   | educationOrganizationReference          | courseTitle        | numberOfParts |
                  | ACC-test-Course1 | [ {"courseIdentificationSystemDescriptor": "uri://ed-fi.org/CourseIdentificationSystemDescriptor#CSSC course code", "identificationCode": "Id-123"} ] | {"educationOrganizationId": 255901001 } | "ACC-test-Course1" | 8             |
              And the system has these "studentschoolassociations"
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
