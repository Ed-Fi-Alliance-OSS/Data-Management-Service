Feature: CorrleationId
    Validate the Correlation Id

     Scenario: 01 Ensure the response will contain provided correlation id
             When a POST request is made to "/ed-fi/academicWeeks" with header "correlationid" value "test-correlationId"
                  """
                  {
                   "weekIdentifier": "WeekIdentifier1",
                   "schoolReference": {
                     "schoolId": 9999
                   },
                   "beginDate": "2023-09-11",
                   "endDate": "2023-09-11",
                   "totalInstructionalDays": 300
                  }
                  """

             Then the response body should contain header value "test-correlationId"
                  """
                  {
                      "detail": "The referenced School item(s) do not exist.",
                      "type": "urn:ed-fi:api:data-conflict:unresolved-reference",
                      "title": "Unresolved Reference",
                      "status": 409,
                      "correlationId": "test-correlationId",
                      "validationErrors": {},
                      "errors": []
                  }
                  """
              And it should respond with 409
