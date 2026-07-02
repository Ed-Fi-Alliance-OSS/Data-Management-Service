Feature: Tenants endpoints

        Background:
            Given valid credentials
              And token received

        # Tenant endpoints are only mapped when multi-tenancy is enabled; against a
        # single-tenant stack they respond 404, so this scenario is multitenant-only.
        @MssqlMultitenantRepresentative @MultitenantOnly
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
