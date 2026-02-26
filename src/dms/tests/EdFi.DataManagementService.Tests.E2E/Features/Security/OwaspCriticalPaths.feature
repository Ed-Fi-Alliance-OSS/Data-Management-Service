Feature: OWASP critical attack path protections

        Background:
            Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"
              And the system has these "schools"
                  | schoolId | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                   |
                  | 1001     | Security School   | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] |

        Scenario: 01 SQL injection payload in query string numeric field is rejected
             When a GET request is made to "/ed-fi/schools?schoolId=' OR 1=1--"
             Then it should respond with 400

        Scenario: 02 SQL injection payload in JSON body numeric field is rejected
             When a POST request is made to "/ed-fi/schools" with
                  """
                  {
                      "schoolId": "'; DROP TABLE dms.Document; --",
                      "nameOfInstitution": "Injected School",
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade"
                          }
                      ],
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ]
                  }
                  """
             Then it should respond with 400

        Scenario: 03 CSRF style forged browser request without bearer token is rejected
            Given there is no Authorization header
             When an unauthenticated POST request is made to "/ed-fi/schools" with header "Origin" value "https://malicious.attacker" and
                  """
                  {
                      "schoolId": 1002,
                      "nameOfInstitution": "CSRF Test School",
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade"
                          }
                      ],
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ]
                  }
                  """
             Then it should respond with 401

        Scenario: 04 Path traversal attempts are not resolved as files
             When a GET request is made to "/../appsettings.json"
             Then it should respond with 404

        Scenario: 05 X-Forwarded-Proto spoofing does not bypass authentication
            Given there is no Authorization header
             When an unauthenticated POST request is made to "/ed-fi/schools" with header "X-Forwarded-Proto" value "https" and
                  """
                  {
                      "schoolId": 1102,
                      "nameOfInstitution": "Forwarded Proto Attack",
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade"
                          }
                      ],
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ]
                  }
                  """
             Then it should respond with 401

        Scenario: 06 X-Forwarded-Host spoofing does not bypass authentication
            Given there is no Authorization header
             When an unauthenticated POST request is made to "/ed-fi/schools" with header "X-Forwarded-Host" value "trusted.local" and
                  """
                  {
                      "schoolId": 1103,
                      "nameOfInstitution": "Forwarded Host Attack",
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade"
                          }
                      ],
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ]
                  }
                  """
             Then it should respond with 401

           Scenario: 06a Expired JWT is rejected
              Given the SIS Vendor is authorized with namespacePrefixes "uri://ed-fi.org"
                And the token is expired
               When a GET request is made to "/ed-fi/schools"
               Then it should respond with 401

        # Host is a browser-forbidden header (https://fetch.spec.whatwg.org/#forbidden-header-name).
        # Playwright/Chromium silently drops it, so the spoofed value never reaches the server.
        # This scenario validates that authentication is enforced on all unauthenticated requests
        # regardless of any Host-like header value — it does NOT verify server-side Host validation.
        Scenario: 07 Unauthenticated request with Host header is rejected by authentication
            Given there is no Authorization header
             When an unauthenticated POST request is made to "/ed-fi/schools" with header "Host" value "trusted.local" and
                  """
                  {
                      "schoolId": 1104,
                      "nameOfInstitution": "Host Header Attack",
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade"
                          }
                      ],
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ]
                  }
                  """
             Then it should respond with 401

        Scenario: 08 Allowed CORS origin receives Access-Control-Allow-Origin
             When a security GET request is made to "/ed-fi/schools" with header "Origin" value "http://localhost:8082"
             Then it should respond with 200
              And the response headers include
                  """
                  {
                      "Access-Control-Allow-Origin": "http://localhost:8082"
                  }
                  """

        Scenario: 09 Disallowed CORS origin does not receive Access-Control-Allow-Origin
             When a security GET request is made to "/ed-fi/schools" with header "Origin" value "https://malicious.attacker"
             Then it should respond with 200
              And the response header "Access-Control-Allow-Origin" is not present

        Scenario: 10 Malformed JSON request body is rejected
             When a POST request is made to "/ed-fi/schools" with
                  """
                      "schoolId": 1301,
                      "nameOfInstitution": "Malformed Json School",
                  }
                  """
             Then it should respond with 400
              And the response body should not contain "System.Text"
              And the response body should not contain "System.Exception"
              And the response body should not contain " at EdFi."

        # Known gap — these headers are not set by the DMS application itself.
        # They must be added by a reverse proxy or application middleware.
        # Tracked for remediation: add response-header middleware to Program.cs.
        @KnownSecurityGap @ignore
        Scenario: 11 API responses include basic security headers
             When a GET request is made to "/ed-fi/schools"
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
             When an "OPTIONS" request is made to "/ed-fi/schools" with headers
                  | Key                           | Value                |
                  | Origin                        | https://evil.example |
                  | Access-Control-Request-Method | GET                  |
             Then it should respond with 200 or 204
              And the response header "Access-Control-Allow-Origin" is not present

        # Note: the bearer token is included by the step helper; TRACE rejection (404/405) is
        # enforced at the routing layer regardless of auth state.
        Scenario: 13 Unsupported TRACE method is not enabled
             When an "TRACE" request is made to "/ed-fi/schools" with headers
                  | Key    | Value |
                  | Accept | */*   |
             Then it should respond with 404 or 405

        @KnownSecurityGap @ignore
        Scenario: 14 Explicit non JSON content type is rejected
             When a POST request is made to "/ed-fi/schools" with header "Content-Type" value "text/plain"
                  """
                  {
                      "schoolId": 1302,
                      "nameOfInstitution": "Invalid Content Type",
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade"
                          }
                      ],
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ]
                  }
                  """
             Then it should respond with 415

        @KnownSecurityGap @ignore
        Scenario: 15 Oversized request body is rejected
             When a POST request larger than 11 MB is made to "/ed-fi/schools"
             Then the direct response should be 413

        Scenario: 16 BOLA cross-education-organization access to object id is denied
            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255901"
              And the system has these "localEducationAgencies"
                  | localEducationAgencyId | nameOfInstitution | categories                                                                                                          | localEducationAgencyCategoryDescriptor                       |
                  | 255901                 | Authorized LEA    | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#District" }] | "uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC" |
                  | 255902                 | Unauthorized LEA  | [{ "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#District" }] | "uri://ed-fi.org/localEducationAgencyCategoryDescriptor#ABC" |
              And the system has these "schools"
                  | schoolId  | nameOfInstitution | gradeLevels                                                                      | educationOrganizationCategories                                                                                   | localEducationAgencyReference       |
                  | 255901901 | BOLA School       | [ {"gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade"} ] | [ {"educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"} ] | { "localEducationAgencyId": 255901} |
              And the system has these "students"
                  | _storeResultingIdInVariable | studentUniqueId | firstName | lastSurname | birthDate  |
                  | BolaStudentId               | "BOLA-001"      | BOLA      | Student     | 2008-01-01 |
              And the system has these "studentSchoolAssociations"
                  | studentReference                  | schoolReference           | entryGradeLevelDescriptor                          | entryDate  |
                  | { "studentUniqueId": "BOLA-001" } | { "schoolId": 255901901 } | "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade" | 2023-08-01 |
             When a GET request is made to "/ed-fi/students/{BolaStudentId}"
             Then it should respond with 200

            Given the claimSet "EdFiSandbox" is authorized with educationOrganizationIds "255902"
             When a GET request is made to "/ed-fi/students/{BolaStudentId}"
             Then it should respond with 403

             When a PUT request is made to "/ed-fi/students/{BolaStudentId}" with
                  """
                  {
                      "id": "{BolaStudentId}",
                      "studentUniqueId": "BOLA-001",
                      "firstName": "BOLA Updated",
                      "lastSurname": "Student",
                      "birthDate": "2008-01-01"
                  }
                  """
             Then it should respond with 403

             When a DELETE request is made to "/ed-fi/students/{BolaStudentId}"
             Then it should respond with 403 or 409

        Scenario: 17 CSRF style forged browser cookie request without bearer token is rejected
            Given there is no Authorization header
             When an unauthenticated POST request is made to "/ed-fi/schools" with header "Cookie" value "sessionid=forged-session-id" and
                  """
                  {
                      "schoolId": 1003,
                      "nameOfInstitution": "CSRF Cookie Test School",
                      "gradeLevels": [
                          {
                              "gradeLevelDescriptor": "uri://ed-fi.org/GradeLevelDescriptor#Tenth grade"
                          }
                      ],
                      "educationOrganizationCategories": [
                          {
                              "educationOrganizationCategoryDescriptor": "uri://ed-fi.org/EducationOrganizationCategoryDescriptor#School"
                          }
                      ]
                  }
                  """
             Then it should respond with 401

        Scenario: 18 SQL injection payload in string query field is treated as data
             When a GET request is made to "/ed-fi/schools?nameOfInstitution=%27%20OR%20%271%27%3D%271"
             Then it should respond with 200
              And the response body is
                  """
                  []
                  """

        Scenario: 19 CSRF style browser form post without bearer token is rejected
            Given there is no Authorization header
             When an unauthenticated Form URL Encoded POST request is made to "/ed-fi/schools" with
                  | Key               | Value                |
                  | schoolId          | 1004                 |
                  | nameOfInstitution | CSRF Form Test       |
             Then it should respond with 401
