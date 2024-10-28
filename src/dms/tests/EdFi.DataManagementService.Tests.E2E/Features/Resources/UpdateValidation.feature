Feature: Validates the functionality of the ETag

        Background:
            Given the system has these descriptors
                  | descriptorValue                                                |
                  | uri://ed-fi.org/GradeLevelDescriptor#Seventh grade             |
                  | uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School |
                  | uri://ed-fi.org/GradeLevelDescriptor#Sixth grade               |
            Given the system has these "students"
                  | studentUniqueId | birthDate  | firstName | lastSurname |
                  | 111111          | 2014-08-14 | Russella  | Mayers      |
            Given the system has these "schools"
                  | schoolId  | nameOfInstitution        | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 255901044 | Grand Bend Middle School | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Sixth grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
            Given the system has these "studentSchoolAssociations"
                  | entryDate  | schoolReference | studentReference | entryGradeLevelDescriptor                                                               |
                  | 2024-10-28 | 255901044       | 111111           | [ {"entryGradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Seventh grade"} ] |

        @API-260
        Scenario: 01 Ensure that clients can retrieve an ETag in the response header
             When a GET request is made to "/ed-fi/students/{id}"
             Then it should respond with 201
              And the response body is
                  """
                  {
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mayers"
                  }
                  """
              And the ETag is in the response header

        @API-261
        Scenario: 02 Ensure that clients can pass an ETag in the request header
             When a PUT request is made to "/ed-fi/students/{id}" with
                  """
                  {
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mayorga"
                  }
                  """
              And the ETag is in the request header
              And If-Match value matches ETag
             Then it should respond with 204

        @API-262
        Scenario: 03 Ensure that clients cannot pass a different ETag in the If-Match header
             When a PUT request is made to "/ed-fi/students/{id}" with
                  """
                  {
                      "studentUniqueId": "111111",
                      "birthDate": "2014-08-14",
                      "firstName": "Russella",
                      "lastSurname": "Mulligan"
                  }
                  """
              And the ETag is in the request header
              And If-Match value is Invalid-ETag
             Then it should respond with 412
              And the response body is
                  """
                  {
                      "detail": "The item has been modified by another user.",
                      "type": "urn:ed-fi:api:optimistic-lock-failed",
                      "title": "Optimistic Lock Failed",
                      "status": 412,
                      "correlationId": null,
                      "errors": [
                          "The resource item's etag value does not match what was specified in the 'If-Match' request header indicating that it has been modified by another client since it was last retrieved."
                      ]
                  }
                  """





