Feature: Resources "Read" Operation validations

        Background:
            Given the Data Management Service must receive a token issued by "http://localhost"
              And user is already authorized
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

        Scenario: 01 Verify response code 404 when trying to get a school with an ID that corresponds to Course
            Given the system has these "Schools"
                  | schoolId | nameOfInstitution | educationOrganizationCategories                                                                                       | gradeLevels                                                                     |
                  | 100      | School Test       | [{ "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#School"}] | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Ninth grade"}] |
              And a POST request is made to "/ed-fi/courses" with
                  """
                  {
                       "courseCode": "ALG-2",
                       "identificationCodes": [
                         {
                           "courseIdentificationSystemDescriptor": "uri://ed-fi.org/CourseIdentificationSystemDescriptor#LEA course code",
                           "identificationCode": "ALG-2"
                         }
                       ],
                       "educationOrganizationReference": {
                         "educationOrganizationId": 100
                       },
                       "courseTitle": "Algebra 2",
                       "numberOfParts": 2
                   }
                  """
             When a GET request is made to "/ed-fi/schools/{id}"
             Then it should respond with 404

        Scenario: 02 Verify response code 200 when trying to get a student with a correct ID
            Given a POST request is made to "/ed-fi/students" with
                  """
                  {
                    "studentUniqueId": "604834",
  	                "birthDate": "2000-01-01",
  	                "firstName": "Thomas",
  	                "lastSurname": "Johnson"
                  }
                  """
             When a GET request is made to "/ed-fi/students/{id}"
             Then it should respond with 200
