Feature: Profiles endpoints

        Background:
            Given valid credentials
              And token received

        @MssqlRepresentative
        Scenario: 01 Ensure clients can POST and GET profile
             When a POST request is made to "/v3/profiles" with
                  """
                    {
                        "name": "Profile_{scenarioRunId}",
                        "definition": "<Profile name=\"Profile_{scenarioRunId}\"><Resource name=\"School\"></Resource></Profile>"
                    }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                    {
                        "location": "/v3/profiles/{profileId}"
                    }
                  """
             When a GET request is made to "/v3/profiles/{profileId}"
             Then it should respond with 200
              And the response body is
                  """
                    {
                        "id": {profileId},
                        "name": "Profile_{scenarioRunId}",
                        "definition": "<Profile name=\"Profile_{scenarioRunId}\"><Resource name=\"School\"></Resource></Profile>"
                    }
                  """
