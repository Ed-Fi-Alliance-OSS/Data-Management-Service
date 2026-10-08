Feature: Jobs endpoint

        # GET /v3/jobs/{jobId} through the deployed stack. No job is enqueued over HTTP in this
        # release, so these scenarios cover the security, not-found, and tenant-header contracts;
        # job states are covered by the API-level integration tests.

        Scenario: 01 Ensure anonymous clients cannot GET a job
             When an unauthenticated GET request is made to "/v3/jobs/Unknown-Job-{scenarioRunId}"
             Then it should respond with 401

        Scenario: 02 Ensure clients with only the authorization metadata scope cannot GET a job
            Given client "CMSAuthMetadataReadOnlyAccess" credentials with "edfi_admin_api/authMetadata_readonly_access" scope
              And token received
             When a GET request is made to "/v3/jobs/Unknown-Job-{scenarioRunId}"
             Then it should respond with 403

        @MssqlRepresentative
        Scenario: 03 Ensure an unknown job responds with the not found problem details
            Given valid credentials
              And token received
             When a GET request is made to "/v3/jobs/Unknown-Job-{scenarioRunId}"
             Then it should respond with 404
              And the response body is
                  """
                  {
                      "detail": "Job not found.",
                      "type": "urn:ed-fi:api:not-found",
                      "title": "Not Found",
                      "status": 404,
                      "validationErrors": {},
                      "errors": []
                  }
                  """

        Scenario: 04 Ensure read only clients reach the job lookup
            Given client "CMSReadOnlyAccess" credentials with "edfi_admin_api/readonly_access" scope
              And token received
             When a GET request is made to "/v3/jobs/Unknown-Job-{scenarioRunId}"
             Then it should respond with 404

        # The multi-tenant scenarios run only in the SQL Server multi-tenant lane; every other lane
        # is single-tenant and skips them.
        @MssqlMultitenantRepresentative @MultitenantOnly
        Scenario: 05 Ensure a job request without a tenant header is rejected when multi-tenancy is enabled
            Given valid credentials
              And token received
             When a GET request is made to "/v3/jobs/Unknown-Job-{scenarioRunId}"
             Then it should respond with 400
              And the response body is
                  """
                  {
                      "detail": "The 'Tenant' header is required when multi-tenancy is enabled",
                      "type": "urn:ed-fi:api:bad-request",
                      "title": "Bad Request",
                      "status": 400,
                      "validationErrors": {},
                      "errors": []
                  }
                  """

        @MssqlMultitenantRepresentative @MultitenantOnly
        Scenario: 06 Ensure an unknown job responds 404 within a valid tenant
            Given valid credentials
              And token received
             When a POST request is made to "/v3/tenants" with
                  """
                    {
                        "name": "JobTenant_{scenarioRunId}"
                    }
                  """
             Then it should respond with 201
             When a GET request is made to "/v3/jobs/Unknown-Job-{scenarioRunId}" with header "Tenant" value "JobTenant_{scenarioRunId}"
             Then it should respond with 404
