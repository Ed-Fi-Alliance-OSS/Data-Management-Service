Feature: Tenants endpoints

        Background:
            Given valid credentials
              And token received

        @MssqlMultitenantRepresentative
        Scenario: 01 Ensure clients can POST and GET tenant
             When a POST request is made to "/v3/tenants" with
                  """
                    {
                        "name": "Tenant_{scenarioRunId}"
                    }
                  """
             Then it should respond with 201
              And the response headers include
                  """
                    {
                        "location": "/v3/tenants/{tenantId}"
                    }
                  """
             When a GET request is made to "/v3/tenants/{tenantId}"
             Then it should respond with 200
              And the response body is
                  """
                    {
                        "id": {id},
                        "name": "Tenant_{scenarioRunId}"
                    }
                  """
