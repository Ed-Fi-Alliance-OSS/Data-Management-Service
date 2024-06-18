Feature: School Year Reference Validation
  Reference validation on School Years

        Background:

            Given the Data Management Service must receive a token issued by "http://localhost"
              And user is already authorized

            Given the system has these "descriptors"
                  | descriptorname                           | codeValue         | description       | namespace                                               | shortDescription  |
                  | educationOrganizationCategoryDescriptors | School            | School            | uri://ed-fi.org/EducationOrganizationCategoryDescriptor | School            |
                  | calendarTypeDescriptors                  | Student Specific  | Student Specific  | uri://ed-fi.org/CalendarTypeDescriptor                  | Student Specific  |
                  | gradeLevelDescriptors                    | Tenth grade       | Tenth grade       | uri://ed-fi.org/GradeLevelDescriptor                    | Tenth grade       |
                  | calendarEventDescriptors                 | Instructional day | Instructional day | uri://ed-fi.org/CalendarEventDescriptor                 | Instructional day |

            Given the system has these "schools"
                  | schoolId | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories |
                  | 535      | Test school       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth Grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://tpdm.ed-fi.org/EducationOrganizationCategoryDescriptor#school"} ] |

            Given the system has these "schoolYearTypes"
                  | schoolYear | currentSchoolYear | schoolYearDescription |
                  | 2024       | true              | School Year 2024      |
                  | 2029       | false             | School Year 2029      |

            Given the system has these "calendars"
                  | calendarCode | schoolReference     | schoolYearTypeReference | calendarTypeDescriptor                                  | gradeLevels |
                  | "451"        | { "schoolId": 535 } | { "schoolYear": 2029 }  | uri://ed-fi.org/CalendarTypeDescriptor#Student Specific | []          |

        Scenario: Try creating a resource using a valid school year
             When a POST request is made to "/ed-fi/calendars" with
                  """
                  {
                      "calendarCode": "321",
                      "schoolReference": {
                        "schoolId": 535
                      },
                      "schoolYearTypeReference": {
                        "schoolYear": 2024
                      },
                      "calendarTypeDescriptor": "uri://ed-fi.org/CalendarTypeDescriptor#Student Specific",
                      "gradeLevels": []
                  }
                  """
             Then it should respond with 201

        @Ignore
        Scenario: Try creating a resource using an invalid school year
             When a POST request is made to "/ed-fi/calendars" with
                   """
                  {
                      "calendarCode": "325",
                      "schoolReference": {
                        "schoolId": 535
                      },
                      "schoolYearTypeReference": {
                        "schoolYear": 2099
                      },
                      "calendarTypeDescriptor": "uri://ed-fi.org/CalendarTypeDescriptor#Student Specific",
                      "gradeLevels": []
                  }
                  """
             Then it should respond with 409
              And the response body is
                  """
                     {
                      "detail": "The referenced 'SchoolYearType' item does not exist.",
                      "type": "urn:ed-fi:api:data-conflict:unresolved-reference",
                      "title": "Unresolved Reference",
                      "status": 409,
                      "correlationId": null
                     }
                  """

        #CalendarDates / School Year
        @Ignore
        Scenario: Try creating a CalendarDate using a valid Calendar reference
             When a POST request is made to "/ed-fi/calendarDates" with
                  """
                     {
                        "calendarReference": {
                          "calendarCode": "451",
                          "schoolId": 535,
                          "schoolYear": 2029
                        },
                        "date": "2029-06-23",
                        "calendarEvents": [
                          {
                            "calendarEventDescriptor": "uri://ed-fi.org/CalendarEventDescriptor#Instructional day"
                          }
                        ]
                    }
                  """
             Then it should respond with 201

        @Ignore
        Scenario: Try creating a CalendarDate using an invalid Calendar reference with an invalid School year
             When a POST request is made to "/ed-fi/calendarDates" with
                  """
                     {
                        "calendarReference": {
                          "calendarCode": "321",
                          "schoolId": 535,
                          "schoolYear": 2059
                        },
                        "date": "2024-06-23",
                        "calendarEvents": [
                          {
                            "calendarEventDescriptor": "uri://ed-fi.org/CalendarEventDescriptor#Instructional day"
                          }
                        ]
                    }
                  """
             Then it should respond with 409
              And the response body is
                  """
                      {
                          "detail": "The referenced 'Calendar' item does not exist.",
                          "type": "urn:ed-fi:api:data-conflict:unresolved-reference",
                          "title": "Unresolved Reference",
                          "status": 409,
                          "correlationId": null
                      }
                  """
