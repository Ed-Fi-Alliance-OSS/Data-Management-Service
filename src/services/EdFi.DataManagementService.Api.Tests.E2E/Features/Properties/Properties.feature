Feature: Validate Extra Properties are being removed
    Tests that perform POST and PUT requests with extra properties,
    paired with a GET request that proves the extra properties have been removed

        Background:
             When a POST request is made to "ed-fi/academicWeeks" with
                  """
                  {
                  "weekIdentifier": "LastWeek",
                  "schoolReference": {
                  "schoolId": 255901001,
                  "link": {
                  "rel": "School",
                  "href": "/ed-fi/schools/20ec19e5070245128a30fdcc6925bb09"
                  }
                  },
                  "beginDate": "2024-05-30",
                  "endDate": "2024-05-30",
                  "totalInstructionalDays": 0,
                  "_etag": "5250168731208835753",
                  "_lastModifiedDate": "2024-05-30T22:30:57.509Z"
                  }
                  """
             Then it should respond with 201
              And the response headers includes
                  """
                    {
                        "location": "/ed-fi/academicWeeks/{id}"
                    }
                  """

        @properties
        Scenario: Validate extra properties are being removed on POST
             When a GET request is made to "ed-fi/academicWeeks/{id}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                    "weekIdentifier": "LastWeek",
                    "schoolReference": {
                      "schoolId": 255901001
                    },
                    "beginDate": "2024-05-30",
                    "endDate": "2024-05-30",
                    "totalInstructionalDays": 0
                  }
                  """

        @properties
        Scenario: Validate extra properties are being removed on PUT
             When a PUT request is made to "ed-fi/academicWeeks/{id}" with
                  """
                  {
                  "weekIdentifier": "LastWeek",
                  "schoolReference": {
                  "schoolId": 255901001,
                  "link": {
                  "rel": "School",
                  "href": "/ed-fi/schools/20ec19e5070245128a30fdcc6925bb09"
                  }
                  },
                  "beginDate": "2024-05-30",
                  "endDate": "2024-06-30",
                  "totalInstructionalDays": 0,
                  "_etag": "5250168731208835753",
                  "_lastModifiedDate": "2024-05-30T22:30:57.509Z"
                  }
                  """
             Then it should respond with 204
              And the response headers includes
                  """
                    {
                        "location": "/ed-fi/academicWeeks/{id}"
                    }
                  """
             When a GET request is made to "ed-fi/academicWeeks/{id}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                    "weekIdentifier": "LastWeek",
                    "schoolReference": {
                      "schoolId": 255901001
                    },
                    "beginDate": "2024-05-30",
                    "endDate": "2024-06-30",
                    "totalInstructionalDays": 0
                  }
                  """




