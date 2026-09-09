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

        # Removing the api-client minimum-count rule must not weaken the checks that run for
        # supplied datastore ids. Exercised over HTTP so tenant resolution, the module handlers and
        # the tenant-scoped repositories all participate, and every rejection is followed by a read
        # proving the target's assignments were left alone.
        @MssqlMultitenantRepresentative @MultitenantOnly
        Scenario: 03 Ensure supplied datastore ids from another tenant are rejected for api clients
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
                        "company": "Tenant A Vendor {scenarioRunId}",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "uri://ed-fi-e2e.org"
                    }
                  """
             Then it should respond with 201
              And the response location id is captured as "vendorIdA"
             When a POST request is made to "/v3/dataStores" with header "Tenant" value "TenantA_{scenarioRunId}" and
                  """
                    {
                        "dataStoreType": "Test",
                        "name": "Tenant A Data Store {scenarioRunId}",
                        "connectionString": "Server=testA;Database=TestDbA;"
                    }
                  """
             Then it should respond with 201
              And the response location id is captured as "dataStoreIdA"
             When a POST request is made to "/v3/applications" with header "Tenant" value "TenantA_{scenarioRunId}" and
                  """
                    {
                        "vendorId": {vendorIdA},
                        "applicationName": "Tenant A Application",
                        "claimSetName": "TenantAClaimSet",
                        "educationOrganizationIds": [],
                        "dataStoreIds": [{dataStoreIdA}]
                    }
                  """
             Then it should respond with 201
              And the response location id is captured as "applicationIdA"
             When a POST request is made to "/v3/vendors" with header "Tenant" value "TenantB_{scenarioRunId}" and
                  """
                    {
                        "company": "Tenant B Vendor {scenarioRunId}",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "uri://ed-fi-e2e.org"
                    }
                  """
             Then it should respond with 201
              And the response location id is captured as "vendorIdB"
             When a POST request is made to "/v3/dataStores" with header "Tenant" value "TenantB_{scenarioRunId}" and
                  """
                    {
                        "dataStoreType": "Test",
                        "name": "Tenant B Data Store {scenarioRunId}",
                        "connectionString": "Server=testB;Database=TestDbB;"
                    }
                  """
             Then it should respond with 201
              And the response location id is captured as "dataStoreIdB"
             When a POST request is made to "/v3/applications" with header "Tenant" value "TenantB_{scenarioRunId}" and
                  """
                    {
                        "vendorId": {vendorIdB},
                        "applicationName": "Tenant B Application",
                        "claimSetName": "TenantBClaimSet",
                        "educationOrganizationIds": [],
                        "dataStoreIds": [{dataStoreIdB}]
                    }
                  """
             Then it should respond with 201
              And the response location id is captured as "applicationIdB"
              # Tenant B cannot attach tenant A's data store to a new client.
             When a POST request is made to "/v3/apiClients" with header "Tenant" value "TenantB_{scenarioRunId}" and
                  """
                    {
                        "applicationId": {applicationIdB},
                        "name": "Foreign Data Store Client",
                        "isApproved": true,
                        "dataStoreIds": [{dataStoreIdA}]
                    }
                  """
             Then it should respond with 409
              # Nor as part of a list that also contains its own valid data store.
             When a POST request is made to "/v3/apiClients" with header "Tenant" value "TenantB_{scenarioRunId}" and
                  """
                    {
                        "applicationId": {applicationIdB},
                        "name": "Mixed Data Store Client",
                        "isApproved": true,
                        "dataStoreIds": [{dataStoreIdB}, {dataStoreIdA}]
                    }
                  """
             Then it should respond with 409
              # Nor create a client, even with no assignment at all, under tenant A's application.
             When a POST request is made to "/v3/apiClients" with header "Tenant" value "TenantB_{scenarioRunId}" and
                  """
                    {
                        "applicationId": {applicationIdA},
                        "name": "Foreign Application Client",
                        "isApproved": true,
                        "dataStoreIds": []
                    }
                  """
             Then it should respond with 409
             When a POST request is made to "/v3/apiClients" with header "Tenant" value "TenantB_{scenarioRunId}" and
                  """
                    {
                        "applicationId": {applicationIdB},
                        "name": "Tenant B Client",
                        "isApproved": true,
                        "dataStoreIds": [{dataStoreIdB}]
                    }
                  """
             Then it should respond with 201
              And the response body credentials are captured as "tenantBClient"
              And the response body id is captured as "tenantBClientId"
              # An update may not swap in another tenant's data store, and must leave the stored
              # assignment exactly as it was.
             When a PUT request is made to "/v3/apiClients/{tenantBClientId}" with header "Tenant" value "TenantB_{scenarioRunId}" and
                  """
                    {
                        "id": {tenantBClientId},
                        "applicationId": {applicationIdB},
                        "name": "Tenant B Client",
                        "isApproved": true,
                        "dataStoreIds": [{dataStoreIdA}]
                    }
                  """
             Then it should respond with 409
             When a GET request is made to "/v3/apiClients/{tenantBClientKey}" with header "Tenant" value "TenantB_{scenarioRunId}"
             Then it should respond with 200
              And the response body is
                  """
                    {
                        "id": {tenantBClientId},
                        "applicationId": {applicationIdB},
                        "clientId": "{tenantBClientKey}",
                        "clientUuid": "{clientUuid}",
                        "name": "Tenant B Client",
                        "isApproved": true,
                        "creatorOwnershipTokenId": null,
                        "ownershipTokenIds": [],
                        "dataStoreIds": [{dataStoreIdB}]
                    }
                  """
             When a PUT request is made to "/v3/apiClients/{tenantBClientId}" with header "Tenant" value "TenantB_{scenarioRunId}" and
                  """
                    {
                        "id": {tenantBClientId},
                        "applicationId": {applicationIdB},
                        "name": "Tenant B Client",
                        "isApproved": true,
                        "dataStoreIds": [{dataStoreIdB}, {dataStoreIdA}]
                    }
                  """
             Then it should respond with 409
             When a GET request is made to "/v3/apiClients/{tenantBClientKey}" with header "Tenant" value "TenantB_{scenarioRunId}"
             Then it should respond with 200
              And the response body is
                  """
                    {
                        "id": {tenantBClientId},
                        "applicationId": {applicationIdB},
                        "clientId": "{tenantBClientKey}",
                        "clientUuid": "{clientUuid}",
                        "name": "Tenant B Client",
                        "isApproved": true,
                        "creatorOwnershipTokenId": null,
                        "ownershipTokenIds": [],
                        "dataStoreIds": [{dataStoreIdB}]
                    }
                  """

        # A client with no datastore assignment must be no more visible across tenants than one
        # with an assignment: an empty list is not a wildcard.
        @MssqlMultitenantRepresentative @MultitenantOnly
        Scenario: 04 Ensure an api client with no datastore assignment is isolated from another tenant
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
                        "company": "Tenant A Vendor {scenarioRunId}",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "uri://ed-fi-e2e.org"
                    }
                  """
             Then it should respond with 201
              And the response location id is captured as "vendorIdA"
             When a POST request is made to "/v3/applications" with header "Tenant" value "TenantA_{scenarioRunId}" and
                  """
                    {
                        "vendorId": {vendorIdA},
                        "applicationName": "Tenant A Identity Only Application",
                        "claimSetName": "TenantAClaimSet",
                        "educationOrganizationIds": [],
                        "dataStoreIds": []
                    }
                  """
             Then it should respond with 201
              And the response body credentials are captured as "tenantAEmpty"
              And the response location id is captured as "applicationIdA"
             When a GET request is made to "/v3/apiClients/{tenantAEmptyKey}" with header "Tenant" value "TenantA_{scenarioRunId}"
             Then it should respond with 200
              And the response body id is captured as "tenantAClientId"
              And the response body is
                  """
                    {
                        "id": {tenantAClientId},
                        "applicationId": {applicationIdA},
                        "clientId": "{tenantAEmptyKey}",
                        "clientUuid": "{clientUuid}",
                        "name": "Tenant A Identity Only Application",
                        "isApproved": true,
                        "creatorOwnershipTokenId": null,
                        "ownershipTokenIds": [],
                        "dataStoreIds": []
                    }
                  """
              # Tenant B can neither read, update, reset nor delete tenant A's unassigned client.
             When a GET request is made to "/v3/apiClients/{tenantAEmptyKey}" with header "Tenant" value "TenantB_{scenarioRunId}"
             Then it should respond with 404
             When a PUT request is made to "/v3/apiClients/{tenantAClientId}" with header "Tenant" value "TenantB_{scenarioRunId}" and
                  """
                    {
                        "id": {tenantAClientId},
                        "applicationId": {applicationIdA},
                        "name": "Hijacked Client",
                        "isApproved": true,
                        "dataStoreIds": []
                    }
                  """
             Then it should respond with 404
             When a PUT request is made to "/v3/apiClients/{tenantAClientId}/reset-credential" with header "Tenant" value "TenantB_{scenarioRunId}" and
                  """
                    {}
                  """
             Then it should respond with 404
             When an "DELETE" request is made to "/v3/apiClients/{tenantAClientId}" with headers
                  | Key    | Value                   |
                  | Tenant | TenantB_{scenarioRunId} |
             Then it should respond with 404
              # Tenant A's client and its empty assignment survived every rejected operation.
             When a GET request is made to "/v3/apiClients/{tenantAEmptyKey}" with header "Tenant" value "TenantA_{scenarioRunId}"
             Then it should respond with 200
              And the response body is
                  """
                    {
                        "id": {tenantAClientId},
                        "applicationId": {applicationIdA},
                        "clientId": "{tenantAEmptyKey}",
                        "clientUuid": "{clientUuid}",
                        "name": "Tenant A Identity Only Application",
                        "isApproved": true,
                        "creatorOwnershipTokenId": null,
                        "ownershipTokenIds": [],
                        "dataStoreIds": []
                    }
                  """
             When a token is requested with the credentials captured as "tenantAEmpty" and scope "TenantAClaimSet"
             Then it should respond with 200
              And the response body has a non-empty access_token
