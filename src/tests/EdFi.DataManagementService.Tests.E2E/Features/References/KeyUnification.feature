Feature: Validation of Natural Key Unification

        Background:
            Given the system has these descriptors
                  | descriptorValue                                                      |
                  | uri://ed-fi.org/GradeLevelDescriptor#TenthGrade                      |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School       |
                  | uri://ed-fi.org/courseIdentificationSystemDescriptor#LEA course code |
                  | uri://ed-fi.org/TermDescriptor#Spring Semester                       |
              And the system has these "schoolYearTypes"
                  | schoolYear | currentSchoolYear | schoolYearDescription |
                  | 2025       | false             | School Year 2025      |
              And the system has these "Schools"
                  | schoolId | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 123      | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |
              And the system has these "Sessions"
                  | sessionName | schoolReference   | schoolYearTypeReference | beginDate  | endDate    | termDescriptor                                   | totalInstructionalDays |
                  | Session1    | {"schoolId": 123} | {"schoolYear": 2025}    | 2022-01-04 | 2022-05-27 | "uri://ed-fi.org/TermDescriptor#Spring Semester" | 88                     |
              And the system has these "Courses"
                  | courseCode | numberOfParts | identificationCode | schoolId | courseTitle  | educationOrganizationReference   | identificationCodes                                                                                                                                                                                                                                            |
                  | Course1    | 1             | ALG-1              | 123      | Course1Title | {"educationOrganizationId": 123} | [{"courseIdentificationSystemDescriptor": "uri://ed-fi.org/courseIdentificationSystemDescriptor#LEA course code", "assigningOrganizationIdentificationCode": "IdentificationCode1", "courseCatalogURL": "URL12", "identificationCode": "IdentificationCode1"}] |

        @API-100
        Scenario: 01 Verify clients can create a resource that contains multiple references with an overlapping natural key field
             When a POST request is made to "/ed-fi/courseOfferings" with
                  """
                  {
                      "localCourseCode": "CourseOffering1",
                      "schoolYearTypeReference": {
                           "schoolYear": 2025
                      },
                      "schoolReference": {
                           "schoolId": 123
                      },
                      "courseReference": {
                          "courseCode": "Course1",
                          "educationOrganizationId": 123
                      },
                      "sessionReference": {
                          "schoolId": 123,
                          "schoolYear": 2025,
                          "sessionName": "Session1"
                      }
                  }
                  """
             Then it should respond with 201 or 200

        @API-101
        Scenario: 02 Verify clients cannot create a resource that contains mismatched values on an overlapping natural key field
             When a POST request is made to "/ed-fi/courseOfferings" with
                  """
                  {
                      "localCourseCode": "CourseOffering1",
                      "schoolYearTypeReference": {
                           "schoolYear": 2025
                      },
                      "schoolReference": {
                           "schoolId": 123
                      },
                      "courseReference": {
                          "courseCode": "Course1",
                          "educationOrganizationId": 123
                      },
                      "sessionReference": {
                          "schoolId": 999,
                          "schoolYear": 2025,
                          "sessionName": "Session1"
                      }
                  }
                  """
             Then the response body is
                  """
                  {
                      "validationErrors":{
                        "$.schoolReference.schoolId": [
                          "All values supplied for 'schoolId' must match. Review all references (including those higher up in the resource's data) and align the following conflicting values: '123', '999'"
                        ],
                        "$.sessionReference.schoolId": [
                          "All values supplied for 'schoolId' must match. Review all references (including those higher up in the resource's data) and align the following conflicting values: '123', '999'"
                        ]
                      },
                      "errors": [],
                      "detail":"Data validation failed. See 'validationErrors' for details.",
                      "type":"urn:ed-fi:api:bad-request:data-validation-failed",
                      "title":"Data Validation Failed",
                      "status":400,
                      "correlationId":null
                  }
                  """
              And it should respond with 400

        @API-102
        Scenario: 03 Verify clients cannot update a resource that contains mismatched values on an overlapping natural key field
            Given the system has these "courseOfferings" references
                  | localCourseCode | courseReference                                         | schoolReference  | sessionReference                                                |
                  | ALG-1           | {"courseCode":"Course1", "educationOrganizationId":123} | {"schoolId":123} | {"schoolId":123, "schoolYear": 2025, "sessionName":"Session1" } |
             When a PUT request is made to referenced resource "/ed-fi/courseOfferings/{id}" with
                  """
                  {
                      "id": "{id}",
                      "localCourseCode": "ALG-1 TEST-101",
                      "courseReference": {
                          "courseCode": "ALG-1",
                          "educationOrganizationId": 255901001
                      },
                      "schoolReference": {
                          "schoolId": 725
                      },
                      "sessionReference": {
                          "schoolId": 999,
                          "schoolYear": 2022,
                          "sessionName": "2021-2022 Spring Semester"
                      }
                  }
                  """
             Then it should respond with 400
              And the response body is
                  """
                  {
                      "detail":"Data validation failed. See 'validationErrors' for details.",
                      "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                      "title": "Data Validation Failed",
                      "status": 400,
                      "correlationId": null,
                      "validationErrors":{
		                    "$.schoolReference.schoolId":[
			                    "All values supplied for 'schoolId' must match. Review all references (including those higher up in the resource's data) and align the following conflicting values: '725', '999'"
			                    ],
                        "$.sessionReference.schoolId":[
                          "All values supplied for 'schoolId' must match. Review all references (including those higher up in the resource's data) and align the following conflicting values: '725', '999'"
                          ]
		                    },
                      "errors": []
                  }
                  """

        @API-103
        Scenario: 04 Verify clients can create a resource with a reference to a resource with a complex identity (CourseOffering)
             When a POST request is made to "/ed-fi/sections" with
                  """
                  {
                      "sectionIdentifier": "Section1",
                      "courseOfferingReference": {
                          "localCourseCode": "CourseOffering1",
                          "schoolId": 123,
                          "schoolYear": 2025,
                          "sessionName": "Session1"
                      }
                  }
                  """
             Then it should respond with 201 or 200
