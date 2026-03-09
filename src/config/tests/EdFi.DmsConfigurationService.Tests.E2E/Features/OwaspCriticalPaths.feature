Feature: OWASP critical attack path protections

        Background:
            Given valid credentials
              And token received

        Scenario: 01 SQL injection payload in query string limit is rejected
             When a GET request is made to "/v2/vendors?limit='1;DROP TABLE dmscs.Vendor;--&offset=0"
             Then it should respond with 400

        Scenario: 02 SQL injection payload in query string offset is rejected
             When a GET request is made to "/v2/vendors?limit=10&offset='0 OR 1=1--"
             Then it should respond with 400

        Scenario: 02a SQL injection payload in query string offset is rejected
             When a GET request is made to "/v2/vendors?limit=10&offset='0+OR+1%3D1--"
             Then it should respond with 400

        Scenario: 03 SQL injection payload in route id is rejected
             When a GET request is made to "/v2/vendors/1;DROP TABLE dmscs.Vendor;--"
             Then it should respond with 400

        Scenario: 04 X-Forwarded-Host spoofing does not make an invalid token acceptable
            Given token signature manipulated
             When a security GET request is made to "/v2/vendors" with header "X-Forwarded-Host" value "trusted.local"
             Then it should respond with 401

        Scenario: 05 X-Forwarded-Proto spoofing does not make an invalid token acceptable
            Given token signature manipulated
             When a security GET request is made to "/v2/vendors" with header "X-Forwarded-Proto" value "https"
             Then it should respond with 401

        Scenario: 06 Origin spoofing does not make an invalid token acceptable
            Given token signature manipulated
             When a security GET request is made to "/v2/vendors" with header "Origin" value "https://malicious.attacker"
             Then it should respond with 401

        # CMSReadOnlyAccess must be registered in the Docker seed data with scope
        # "edfi_admin_api/readonly_access". If missing, token received returns an empty
        # token, the POST returns 401, and the test fails with a misleading status mismatch
        # rather than a clear setup error.
        Scenario: 07 Authenticated read-only token cannot perform admin write operations
            Given client "CMSReadOnlyAccess" credentials with "edfi_admin_api/readonly_access" scope
              And token received
             When a POST request is made to "/v2/vendors" with
                  """
                  {
                    "company": "Forbidden Vendor",
                    "contactName": "Security Test",
                    "contactEmailAddress": "security@test.example",
                    "namespacePrefixes": "uri://ed-fi.org"
                  }
                  """
             Then it should respond with 403

        Scenario: 08 Allowed CORS origin receives Access-Control-Allow-Origin
             When a security GET request is made to "/v2/vendors" with header "Origin" value "http://localhost:8082"
             Then it should respond with 200
              And the response headers include
                  """
                  {
                    "Access-Control-Allow-Origin": "http://localhost:8082"
                  }
                  """

        Scenario: 09 Disallowed CORS origin does not receive Access-Control-Allow-Origin
             When a security GET request is made to "/v2/vendors" with header "Origin" value "https://malicious.attacker"
             Then it should respond with 200
              And the response header "Access-Control-Allow-Origin" is not present

        Scenario: 10 Malformed JSON request body is rejected
             When a POST request is made to "/v2/vendors" with
                  """
                  {
                    "company": "Broken Json",
                    "contactName": "Security Test",
                  }
                  """
             Then it should respond with 400
              And the response body should not contain "System.Text"
              And the response body should not contain "System.Exception"
              And the response body should not contain " at EdFi."

        # Known gap — these headers are not set by the CMS application itself.
        # They must be added by a reverse proxy or application middleware.
        # Tracked for remediation: add response-header middleware to Program.cs.
        @KnownSecurityGap @ignore
        Scenario: 11 API responses include basic security headers
             When a GET request is made to "/v2/vendors"
             Then it should respond with 200
              And the response headers include
                  """
                  {
                    "X-Content-Type-Options": "nosniff",
                    "Referrer-Policy": "no-referrer"
                  }
                  """

        # Note: WhenAnRequestIsMadeToWithHeaders includes the bearer token from the test context.
        # Real browser CORS preflights are unauthenticated; the assertion (no allow-origin for
        # disallowed origins) is independent of auth and is still correct here.
        Scenario: 12 Disallowed CORS preflight does not return allow-origin header
             When an "OPTIONS" request is made to "/v2/vendors" with headers
                  | Key                           | Value                |
                  | Origin                        | https://evil.example |
                  | Access-Control-Request-Method | GET                  |
             Then it should respond with 200 or 204
              And the response header "Access-Control-Allow-Origin" is not present

        # Note: the bearer token is included by the step helper; TRACE rejection (404/405) is
        # enforced at the routing layer regardless of auth state.
        Scenario: 13 Unsupported TRACE method is not enabled
             When an "TRACE" request is made to "/v2/vendors" with headers
                  | Key    | Value |
                  | Accept | */*   |
             Then it should respond with 404 or 405

        Scenario: 14 SQL injection payload in JSON body id field is rejected
             When a POST request is made to "/v2/dmsInstances" with
                  """
                  {
                    "instanceType": "Test",
                    "instanceName": "SQLi JSON Validation",
                    "connectionString": "Server=sqlijson;Database=SqliJsonDb;"
                  }
                  """
             Then it should respond with 201

             When a PUT request is made to "/v2/dmsInstances/{dmsInstanceId}" with
                  """
                  {
                    "id": "'; DROP TABLE dmscs.DmsInstance; --",
                    "instanceType": "Test",
                    "instanceName": "SQLi JSON Validation Updated",
                    "connectionString": "Server=sqlijson;Database=SqliJsonDb;"
                  }
                  """
             Then it should respond with 400

        Scenario: 15 CSRF style forged browser cookie request without bearer token is rejected
             When an unauthenticated POST request is made to "/v2/vendors" with header "Cookie" value "sessionid=forged-session-id" and
                  """
                  {
                    "company": "CSRF Cookie Vendor",
                    "contactName": "Security Test",
                    "contactEmailAddress": "security@test.example",
                    "namespacePrefixes": "uri://ed-fi.org"
                  }
                  """
             Then it should respond with 401

        Scenario: 16 SQL injection payload in string JSON field is treated as data
             When a POST request is made to "/v2/vendors" with
                  """
                  {
                    "company": "' OR '1'='1",
                    "contactName": "SQLi String",
                    "contactEmailAddress": "sqli-string@test.example",
                    "namespacePrefixes": "uri://ed-fi.org"
                  }
                  """
             Then it should respond with 201

             When a GET request is made to "/v2/vendors/{vendorId}"
             Then it should respond with 200

        Scenario: 17 CSRF style browser form post without bearer token is rejected
             When an unauthenticated Form URL Encoded POST request is made to "/v2/vendors" with
                  | Key                 | Value                 |
                  | company             | CSRF Form Vendor      |
                  | contactName         | Security Test         |
                  | contactEmailAddress | security@test.example |
                  | namespacePrefixes   | uri://ed-fi.org       |
             Then it should respond with 401 or 415
