Feature: Check extra functionalities for GET requests

        Background:
            Given a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "604824",
                      "birthDate": "2010-01-13",
                      "firstName": "Traci",
                      "lastSurname": "Mathews"
                  }
                  """
              And a POST request is made to "/ed-fi/students" with
                  """
                  {
                      "studentUniqueId": "604829",
                      "birthDate": "2015-08-17",
                      "firstName": "April",
                      "lastSurname": "Shelton"
                  }
                  """
              And the system has these descriptors
                  | descriptorValue                                                                            |
                  | uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#Educator Preparation Provider |
                  | uri://ed-fi.org/GradeLevelDescriptor#Postsecondary                                         |
              And a POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 4,
                      "nameOfInstitution": "UT Austin College of Education Graduate",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#Educator Preparation Provider"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"
                          }
                      ]
                  }
                  """
              And a POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 6,
                      "nameOfInstitution": "UT Austin College of Education Under Graduate",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#Educator Preparation Provider"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"
                          }
                      ]
                  }
                  """
              And a POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": 5,
                      "nameOfInstitution": "UT Austin College of Education Graduate",
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#Educator Preparation Provider"
                          }
                      ],
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"
                          }
                      ]
                  }
                  """


        @ignore
        Scenario: 01 Ensure that a resource can be retrieved by name
             When a GET request is made to "/ed-fi/students?firstName=April"
             Then it should respond with 200
              And the response body is
                  """
                  [
                      {
                          "studentUniqueId": "604829",
                          "birthDate": "2015-08-17",
                          "firstName": "April",
                          "lastSurname": "Shelton",
                          "_lastModifiedDate": "2024-05-10T20:57:00.9107017Z"
                      }
                  ]
                  """

        @ignore
        Scenario: 02 Ensure that a resource can be retrieved bt lastModifiedDate property
             When a GET request is made to "/ed-fi/students?_lastModifiedDate=2024-07-09T18:46:39.5385642Z"
             Then it should respond with 200
              And the response body is
                  """
                  [
                      {
                          "studentUniqueId": "604824",
                          "birthDate": "2010-01-13",
                          "firstName": "Traci",
                          "lastSurname": "Mathews",
                          "_lastModifiedDate": "2024-07-09T18:46:39.5385642Z"
                      }
                  ]
                  """

        @ignore
        Scenario: 03 Ensure that resources can be ordered by attribute ascending or descending
             When a POST request is made to "/ed-fi/schools?orderBy=schoolId&direction=desc"
             Then it should respond with 200
              And the response body is
                  """
                  [
                      {
                          "schoolId": 6,
                          "nameOfInstitution": "UT Austin College of Education Under Graduate",
                          "educationOrganizationCategories": [
                              {
                                  "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#Educator Preparation Provider"
                              }
                          ],
                          "gradeLevels": [
                              {
                                  "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"
                              }
                          ],
                          "_lastModifiedDate": "2024-05-10T21:16:55.435633Z"
                      },
                      {
                          "schoolId": 5,
                          "nameOfInstitution": "UT Austin College of Education Graduate",
                          "educationOrganizationCategories": [
                              {
                                  "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#Educator Preparation Provider"
                              }
                          ],
                          "gradeLevels": [
                              {
                                  "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"
                              }
                          ],
                          "_lastModifiedDate": "2024-06-05T19:32:01.5975013Z"
                      },
                      {
                          "schoolId": 4,
                          "nameOfInstitution": "UT Austin College of Education Graduate",
                          "educationOrganizationCategories": [
                              {
                                  "educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#Educator Preparation Provider"
                              }
                          ],
                          "gradeLevels": [
                              {
                                "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Postsecondary"
                              }
                        ],
                        "_lastModifiedDate": "2024-06-17T19:27:09.9480071Z"
                      }
                  ]
                  """




