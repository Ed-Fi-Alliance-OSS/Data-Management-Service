Feature: School Year Reference Validation
  Reference validation on School Years

        Background:
            Given the Data Management Service must receive a token issued by "http://localhost"
              And user is already authorized
             When a POST request is made to "/ed-fi/schoolYearTypes" with
                  """
                    {
                      "schoolYear": 2024,
                      "currentSchoolYear": true,
                      "schoolYearDescription": "Current School Year 2024"
                    }
                  """
             Then it should respond with 201 or 200
              When a POST request is made to "/ed-fi/schoolYearTypes" with
                  """
                    {
                      "schoolYear": 2029,
                      "currentSchoolYear": true,
                      "schoolYearDescription": "Current School Year 2029"
                    }
                  """
             Then it should respond with 201 or 200

        Scenario: Try creating a resource using a valid school year
             When a POST request is made to "/ed-fi/calendars" with
                  """
                  {
                      "calendarCode": "2010605675",
                      "schoolReference": {
                        "schoolId": 255901001
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
                      "calendarCode": "2010605675",
                      "schoolReference": {
                        "schoolId": 255901001
                      },
                      "schoolYearTypeReference": {
                        "schoolYear": 3029
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
                      "calendarCode": "2010605987",
                      "schoolReference": {
                        "schoolId": 255901001
                      },
                      "schoolYearTypeReference": {
                        "schoolYear": 2029
                      },
                      "calendarTypeDescriptor": "uri://ed-fi.org/CalendarTypeDescriptor#Student Specific",
                      "gradeLevels": []
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/ed-fi/calendarDates" with
                  """
                     {
                        "calendarReference": {
                          "calendarCode": "2010605987",
                          "schoolId": 255901001,
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
                          "calendarCode": "2010605675",
                          "schoolId": 255901001,
                          "schoolYear": 3024
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
