Feature: Check extra functionalities for GET requests

        Background:
            Given the system has these descriptors
                  | descriptorValue                                                                            |
                  | uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#Educator Preparation Provider |
                  | uri://ed-fi.org/GradeLevelDescriptor#Postsecondary                                         |
            Given the system has these "schools"
                  | schoolId  | nameOfInstitution            | gradeLevels                                                                         | educationOrganizationCategories                                                                                   |
                  | 255901044 | Grand Bend Middle School     | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Sixth grade"} ]    | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 255901107 | Grand Bend Elementary School | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#First grade"} ]    | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
                  | 255901634 | Grand Bend High School       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Eleventh grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |
            Given the system has these "students"
                  | studentUniqueId | birthDate  | firstName | lastSurname |
                  | 604829          | 2010-01-13 | Traci     | Mathews     |
                  | 604829          | 2015-08-17 | April     | Shelton     |

        @API-257 @ignore
        Scenario: 01 Ensure that clients can retrieve a resource by lastModifiedDate
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

        @API-258 @ignore
        Scenario: 03 Ensure that clients can order by attribute ascending or descending
             When a GET request is made to "/ed-fi/schools?orderBy=schoolId&direction=desc"
             Then it should respond with 200
              And the response body is
                  """
                  [
                      {
                          "schoolId": 255901634,
                          "nameOfInstitution": "Grand Bend High School",
                          "educationOrganizationCategories": [
                              {
                                  "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                              }
                          ],
                          "gradeLevels": [
                              {
                                  "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Eleventh grade"
                              }
                        ],
                        "_lastModifiedDate": "2024-09-20T18:15:46.8229446Z"
                      },
                      {
                          "schoolId": 255901107,
                          "nameOfInstitution": "Grand Bend Elementary School",
                          "educationOrganizationCategories": [
                              {
                                  "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                              }
                          ],
                          "gradeLevels": [
                              {
                                  "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#First grade"
                              }
                          ],
                        "_lastModifiedDate": "2024-05-10T20:56:29.7546249Z"
                      },
                      {
                          "id": "ead36072b993441db409fc7f8c4ec31e",
                          "schoolId": 255901044,
                          "nameOfInstitution": "Grand Bend Middle School",
                          "educationOrganizationCategories": [
                              {
                                  "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                              }
                          ],
                          "gradeLevels": [
                              {
                                  "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Sixth grade"
                              }
                          ],
                          "_lastModifiedDate": "2024-05-10T20:56:29.7546249Z"
                      }
                  ]
                  """




