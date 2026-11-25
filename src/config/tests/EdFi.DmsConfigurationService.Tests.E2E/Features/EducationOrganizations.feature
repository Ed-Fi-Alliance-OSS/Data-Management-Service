Feature: Education organization refresh and tenant aggregate

        # The refresh routes and the tenant aggregate through the deployed stack. The stack runs in
        # bootstrap mode: job workers are off and no DMS is configured, so a queued refresh stays
        # Pending and no data store has published education organizations. Refresh execution is
        # covered by the API-level integration tests and the Instance E2E.

        @SingleTenantOnly @MssqlRepresentative
        Scenario: 01 Ensure a refresh of every data store is queued and its job polls Pending
            Given valid credentials
              And token received
             When a POST request is made to "/v3/dataStores/edOrgs/refresh" with no body
             Then the response queues a refresh job with message "Education organizations refresh has been queued for all instances"
             When a GET request is made to "/v3/jobs/{jobId}"
             Then the response reports the captured refresh job as "Pending" and unfinished

        @SingleTenantOnly @MssqlRepresentative
        Scenario: 02 Ensure a refresh of one data store is queued and its job polls Pending
            Given valid credentials
              And token received
             When a POST request is made to "/v3/dataStores" with
                  """
                  {
                      "dataStoreType": "SQL",
                      "name": "EdOrg Refresh {scenarioRunId}",
                      "connectionString": "Server=localhost;Database=EdOrgRefreshDb;"
                  }
                  """
             Then it should respond with 201
             When a POST request is made to "/v3/dataStores/{dataStoreId}/edOrgs/refresh" with no body
             Then the response queues a refresh job with message "Education organizations refresh has been queued for the specified instance"
             When a GET request is made to "/v3/jobs/{jobId}"
             Then the response reports the captured refresh job as "Pending" and unfinished

        @SingleTenantOnly
        Scenario: 03 Ensure a refresh of an absent data store responds 404
            Given valid credentials
              And token received
             When a POST request is made to "/v3/dataStores/2147483647/edOrgs/refresh" with no body
             Then it should respond with 404
              And the response header "Location" is not present
              And the response body is
                  """
                  {
                      "detail": "Data store not found.",
                      "type": "urn:ed-fi:management-api:not-found",
                      "title": "Not Found",
                      "status": 404,
                      "validationErrors": {},
                      "errors": []
                  }
                  """

        @SingleTenantOnly
        Scenario: 04 Ensure read only clients cannot queue a refresh
            Given client "CMSReadOnlyAccess" credentials with "edfi_admin_api/readonly_access" scope
              And token received
             When a POST request is made to "/v3/dataStores/edOrgs/refresh" with no body
             Then it should respond with 403
              And the response header "Location" is not present
             When a POST request is made to "/v3/dataStores/1/edOrgs/refresh" with no body
             Then it should respond with 403
              And the response header "Location" is not present

        @SingleTenantOnly
        Scenario: 05 Ensure anonymous clients cannot read a tenant aggregate
             When an unauthenticated GET request is made to "/v3/tenants/default/dataStores/edOrgs"
             Then it should respond with 401

        # The data stores of earlier scenarios in this feature are listed too, so the aggregate is
        # compared with the data store list the same stack returns.
        @SingleTenantOnly @MssqlRepresentative
        Scenario: 06 Ensure the single-tenant aggregate lists every data store under the default tenant
            Given valid credentials
              And token received
             When a POST request is made to "/v3/dataStores" with
                  """
                  {
                      "dataStoreType": "SQL",
                      "name": "EdOrg Aggregate {scenarioRunId}",
                      "connectionString": "Server=localhost;Database=EdOrgAggregateDb;"
                  }
                  """
             Then it should respond with 201
             When a GET request is made to "/v3/tenants/DEFAULT/dataStores/edOrgs"
             Then the response is the "default" tenant aggregate of every data store, including "EdOrg Aggregate {scenarioRunId}"
             When a GET request is made to "/v3/tenants/default/dataStores/edOrgs" with header "Tenant" value "AnyOtherTenant"
             Then the response is the "default" tenant aggregate of every data store, including "EdOrg Aggregate {scenarioRunId}"

        @SingleTenantOnly
        Scenario: 07 Ensure the single-tenant aggregate refuses every other tenant name
            Given valid credentials
              And token received
             When a GET request is made to "/v3/tenants/Tenant_{scenarioRunId}/dataStores/edOrgs"
             Then it should respond with 404
              And the response body is
                  """
                  {
                      "detail": "Tenant not found.",
                      "type": "urn:ed-fi:management-api:not-found",
                      "title": "Not Found",
                      "status": 404,
                      "validationErrors": {},
                      "errors": []
                  }
                  """

        @SingleTenantOnly @MssqlRepresentative
        Scenario: 08 Ensure the excluded education organization GETs respond as unknown routes
            Given valid credentials
              And token received
             When a GET request is made to "/v3/dataStores/edOrgs"
             Then it should respond with 404
              And the response body is
                  """
                  {
                      "detail": "The requested resource could not be found.",
                      "type": "urn:ed-fi:management-api:not-found",
                      "title": "Not Found",
                      "status": 404,
                      "validationErrors": {},
                      "errors": []
                  }
                  """
             When a GET request is made to "/v3/dataStores/1/edOrgs"
             Then it should respond with 404
              And the response body is
                  """
                  {
                      "detail": "The requested resource could not be found.",
                      "type": "urn:ed-fi:management-api:not-found",
                      "title": "Not Found",
                      "status": 404,
                      "validationErrors": {},
                      "errors": []
                  }
                  """

        # The multi-tenant scenarios run only in the SQL Server multi-tenant lane; every other lane
        # is single-tenant and skips them.
        @MssqlMultitenantRepresentative @MultitenantOnly
        Scenario: 09 Ensure the aggregate checks the tenant header before reading a tenant
            Given valid credentials
              And token received
             When a POST request is made to "/v3/tenants" with
                  """
                    {
                        "name": "EdOrgTenant_{scenarioRunId}"
                    }
                  """
             Then it should respond with 201
             When a GET request is made to "/v3/tenants/EdOrgTenant_{scenarioRunId}/dataStores/edOrgs"
             Then it should respond with 400
              And the response body is
                  """
                  {
                      "detail": "The 'Tenant' header is required when multi-tenancy is enabled",
                      "type": "urn:ed-fi:management-api:bad-request",
                      "title": "Bad Request",
                      "status": 400,
                      "validationErrors": {},
                      "errors": []
                  }
                  """
             When a GET request is made to "/v3/tenants/EdOrgTenant_{scenarioRunId}/dataStores/edOrgs" with header "Tenant" value "OtherTenant_{scenarioRunId}"
             Then it should respond with 400
              And the response body is
                  """
                  {
                      "detail": "The 'Tenant' header does not match the tenant name in the route.",
                      "type": "urn:ed-fi:management-api:bad-request",
                      "title": "Bad Request",
                      "status": 400,
                      "validationErrors": {},
                      "errors": []
                  }
                  """
             When a GET request is made to "/v3/tenants/Missing_{scenarioRunId}/dataStores/edOrgs" with header "Tenant" value "Missing_{scenarioRunId}"
             Then it should respond with 404
              And the response body is
                  """
                  {
                      "detail": "Tenant not found.",
                      "type": "urn:ed-fi:management-api:not-found",
                      "title": "Not Found",
                      "status": 404,
                      "validationErrors": {},
                      "errors": []
                  }
                  """
             When a GET request is made to "/v3/tenants/EDORGTENANT_{scenarioRunId}/dataStores/edOrgs" with header "Tenant" value "edorgtenant_{scenarioRunId}"
             Then it should respond with 200
              And the response body is
                  """
                  {
                      "id": "EdOrgTenant_{scenarioRunId}",
                      "name": "EdOrgTenant_{scenarioRunId}",
                      "dataStores": []
                  }
                  """

        @MssqlMultitenantRepresentative @MultitenantOnly
        Scenario: 10 Ensure a refresh is queued within the selected tenant only
            Given valid credentials
              And token received
             When a POST request is made to "/v3/tenants" with
                  """
                    {
                        "name": "EdOrgRefreshTenant_{scenarioRunId}"
                    }
                  """
             Then it should respond with 201
             When a POST request is made to "/v3/dataStores/edOrgs/refresh" with no body
             Then it should respond with 400
              And the response header "Location" is not present
             When a POST request is made to "/v3/dataStores/edOrgs/refresh" with header "Tenant" value "EdOrgRefreshTenant_{scenarioRunId}" and no body
             Then the response queues a refresh job with message "Education organizations refresh has been queued for all instances"
             When a GET request is made to "/v3/jobs/{jobId}" with header "Tenant" value "EdOrgRefreshTenant_{scenarioRunId}"
             Then the response reports the captured refresh job as "Pending" and unfinished
             When a POST request is made to "/v3/tenants" with
                  """
                    {
                        "name": "EdOrgOtherTenant_{scenarioRunId}"
                    }
                  """
             Then it should respond with 201
             When a GET request is made to "/v3/jobs/{jobId}" with header "Tenant" value "EdOrgOtherTenant_{scenarioRunId}"
             Then it should respond with 404

        @MssqlMultitenantRepresentative @MultitenantOnly
        Scenario: 11 Ensure the excluded education organization GET responds as an unknown route within a tenant
            Given valid credentials
              And token received
             When a POST request is made to "/v3/tenants" with
                  """
                    {
                        "name": "EdOrgGuardTenant_{scenarioRunId}"
                    }
                  """
             Then it should respond with 201
             When a GET request is made to "/v3/dataStores/edOrgs" with header "Tenant" value "EdOrgGuardTenant_{scenarioRunId}"
             Then it should respond with 404
              And the response body is
                  """
                  {
                      "detail": "The requested resource could not be found.",
                      "type": "urn:ed-fi:management-api:not-found",
                      "title": "Not Found",
                      "status": 404,
                      "validationErrors": {},
                      "errors": []
                  }
                  """
