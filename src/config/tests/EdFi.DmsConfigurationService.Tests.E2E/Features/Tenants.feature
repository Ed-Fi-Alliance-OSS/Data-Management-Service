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

        # Exercises the full request pipeline (tenant resolution middleware, module
        # handlers, and tenant-scoped repositories) to prove one tenant's application
        # is invisible to another tenant, even when the request body references the
        # owning tenant's vendor and data store ids.
        @MssqlMultitenantRepresentative @MultitenantOnly
        Scenario: 02 Ensure applications are not accessible from another tenant
             When a POST request is made to "/v3/tenants" with
                  """
                    {
                        "name": "TenantA_{scenarioRunId}"
                    }
                  """
             Then it should respond with 201
             When a POST request is made to "/v3/tenants" with
                  """
                    {
                        "name": "TenantB_{scenarioRunId}"
                    }
                  """
             Then it should respond with 201
             When a POST request is made to "/v3/vendors" with header "Tenant" value "TenantA_{scenarioRunId}" and
                  """
                    {
                        "company": "Cross Tenant Vendor {scenarioRunId}",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "uri://ed-fi-e2e.org"
                    }
                  """
             Then it should respond with 201
             When a POST request is made to "/v3/dataStores" with header "Tenant" value "TenantA_{scenarioRunId}" and
                  """
                    {
                        "dataStoreType": "Test",
                        "name": "Cross Tenant Data Store {scenarioRunId}",
                        "connectionString": "Server=test;Database=TestDb;"
                    }
                  """
             Then it should respond with 201
             When a POST request is made to "/v3/applications" with header "Tenant" value "TenantA_{scenarioRunId}" and
                  """
                    {
                        "vendorId": {vendorId},
                        "applicationName": "CrossTenantApplication",
                        "claimSetName": "CrossTenantClaimSet",
                        "educationOrganizationIds": [],
                        "dataStoreIds": [{dataStoreId}]
                    }
                  """
             Then it should respond with 201
             When a GET request is made to "/v3/applications/{applicationId}" with header "Tenant" value "TenantB_{scenarioRunId}"
             Then it should respond with 404
             When a PUT request is made to "/v3/applications/{applicationId}" with header "Tenant" value "TenantB_{scenarioRunId}" and
                  """
                    {
                        "id": {applicationId},
                        "vendorId": {vendorId},
                        "applicationName": "CrossTenantApplication",
                        "claimSetName": "CrossTenantClaimSet",
                        "educationOrganizationIds": [],
                        "dataStoreIds": [{dataStoreId}]
                    }
                  """
             Then it should respond with 404
             When an "DELETE" request is made to "/v3/applications/{applicationId}" with headers
                  | Key    | Value                   |
                  | Tenant | TenantB_{scenarioRunId} |
             Then it should respond with 404
             When a GET request is made to "/v3/applications/{applicationId}" with header "Tenant" value "TenantA_{scenarioRunId}"
             Then it should respond with 200
