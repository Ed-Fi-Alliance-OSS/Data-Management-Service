Feature: Connect endpoints

        Scenario: 00 Verify register new client
             When a Form URL Encoded POST request is made to "/connect/register" with
                  | Key          | Value                                          |
                  | ClientId     | _scenarioRunId                                 |
                  | ClientSecret | S3cr3t!SuperLongSecretKeyWith$peci@lChar123456 |
                  | DisplayName  | E2E                                            |
             Then it should respond with 200
              And the response body is
                  """
                  {
                    "title": "Registered client {scenarioRunId} successfully.",
                    "status": 200
                  }
                  """

        Scenario: 01 Verify already registered clients return 400
             When a Form URL Encoded POST request is made to "/connect/register" with
                  | Key          | Value                                          |
                  | ClientId     | _scenarioRunId                                 |
                  | ClientSecret | S3cr3t!SuperLongSecretKeyWith$peci@lChar123456 |
                  | DisplayName  | E2E                                            |
             Then it should respond with 200
              And the response body is
                  """
                  {
                    "title": "Registered client {scenarioRunId} successfully.",
                    "status": 200
                  }
                  """
             When a Form URL Encoded POST request is made to "/connect/register" with
                  | Key          | Value                                          |
                  | ClientId     | _scenarioRunId                                 |
                  | ClientSecret | S3cr3t!SuperLongSecretKeyWith$peci@lChar123456 |
                  | DisplayName  | E2E                                            |
             Then it should respond with 400
              And the response body is
                  """
                  {
                    "detail": "Data validation failed. See 'validationErrors' for details.",
                    "type": "urn:ed-fi:management-api:bad-request:data",
                    "title": "Data Validation Failed",
                    "status": 400,
                    "validationErrors": {
                        "ClientId": [
                            "Client with the same Client Id already exists. Please provide different Client Id."
                            ]
                        },
                    "errors": []
                    }
                  """

        Scenario: 02 Verify password requirements
             When a Form URL Encoded POST request is made to "/connect/register" with
                  | Key          | Value          |
                  | ClientId     | _scenarioRunId |
                  | ClientSecret | weak           |
                  | DisplayName  | _scenarioRunId |
             Then it should respond with 400
              And the response body is
                  """
                  {
                    "detail": "Data validation failed. See 'validationErrors' for details.",
                    "type": "urn:ed-fi:management-api:bad-request:data",
                    "title": "Data Validation Failed",
                    "status": 400,
                    "validationErrors": {
                        "ClientSecret": [
                            "Client secret must contain at least one lowercase letter, one uppercase letter, one number, and one special character, and must be 32 to 128 characters long."
                        ]
                    },
                    "errors": []
                    }
                  """

        Scenario: 02a Verify client secret complexity
             When a Form URL Encoded POST request is made to "/connect/register" with
                  | Key          | Value                            |
                  | ClientId     | _scenarioRunId                   |
                  | ClientSecret | AbcdefghijklmnopqrstuvwxYZ123456 |
                  | DisplayName  | _scenarioRunId                   |
             Then it should respond with 400
              And the response body is
                  """
                  {
                    "detail": "Data validation failed. See 'validationErrors' for details.",
                    "type": "urn:ed-fi:management-api:bad-request:data",
                    "title": "Data Validation Failed",
                    "status": 400,
                    "validationErrors": {
                        "ClientSecret": [
                            "Client secret must contain at least one lowercase letter, one uppercase letter, one number, and one special character, and must be 32 to 128 characters long."
                        ]
                    },
                    "errors": []
                    }
                  """

        Scenario: 02b Verify client secret minimum length boundary
             When a Form URL Encoded POST request is made to "/connect/register" with
                  | Key          | Value                           |
                  | ClientId     | _scenarioRunId                  |
                  | ClientSecret | Aa1!aaaaaaaaaaaaaaaaaaaaaaaaaaa |
                  | DisplayName  | _scenarioRunId                  |
             Then it should respond with 400
              And the response body is
                  """
                  {
                    "detail": "Data validation failed. See 'validationErrors' for details.",
                    "type": "urn:ed-fi:management-api:bad-request:data",
                    "title": "Data Validation Failed",
                    "status": 400,
                    "validationErrors": {
                        "ClientSecret": [
                            "Client secret must contain at least one lowercase letter, one uppercase letter, one number, and one special character, and must be 32 to 128 characters long."
                        ]
                    },
                    "errors": []
                    }
                  """

        Scenario: 02c Verify client secret maximum length boundary
             When a Form URL Encoded POST request is made to "/connect/register" with
                  | Key          | Value                                                                                                                            |
                  | ClientId     | _scenarioRunId                                                                                                                   |
                  | ClientSecret | Aa1!aaaaaaaaaaaaaaaaaaaaaaaaaaaaAa1!aaaaaaaaaaaaaaaaaaaaaaaaaaaaAa1!aaaaaaaaaaaaaaaaaaaaaaaaaaaaAa1!aaaaaaaaaaaaaaaaaaaaaaaaaaaa |
                  | DisplayName  | _scenarioRunId                                                                                                                   |
             Then it should respond with 200
              And the response body is
                  """
                  {
                    "title": "Registered client {scenarioRunId} successfully.",
                    "status": 200
                  }
                  """

        Scenario: 03 Verify empty post failure
             When a Form URL Encoded POST request is made to "/connect/register" with
                  | Key | Value |
             Then it should respond with 400
              And the response body is
                  """
                  {
                    "detail": "Data validation failed. See 'validationErrors' for details.",
                    "type": "urn:ed-fi:management-api:bad-request:data",
                    "title": "Data Validation Failed",
                    "status": 400,
                    "validationErrors": {
                        "ClientId": [
                            "'Client Id' must not be empty."
                        ],
                        "ClientSecret": [
                            "'Client Secret' must not be empty."
                        ],
                        "DisplayName": [
                            "'Display Name' must not be empty."
                        ]
                    },
                    "errors": []
                    }
                  """
        @MssqlRepresentative
        Scenario: 04 Verify token creation with registered client
             When a Form URL Encoded POST request is made to "/connect/register" with
                  | Key          | Value                                          |
                  | ClientId     | _scenarioRunId                                 |
                  | ClientSecret | S3cr3t!SuperLongSecretKeyWith$peci@lChar123456 |
                  | DisplayName  | _scenarioRunId                                 |
             Then it should respond with 200
              And the response body is
                  """
                  {
                    "title": "Registered client {scenarioRunId} successfully.",
                    "status": 200
                  }
                  """
             When a Form URL Encoded POST request is made to "/connect/token" with
                  | Key           | Value                                          |
                  | client_id     | _scenarioRunId                                 |
                  | client_secret | S3cr3t!SuperLongSecretKeyWith$peci@lChar123456 |
                  | grant_type    | client_credentials                             |
                  | scope         | edfi_admin_api/full_access                     |
             Then it should respond with 200
              And the response headers include
                  """
                  {
                    "Cache-Control": "no-store",
                    "Pragma": "no-cache"
                  }
                  """
              And the token response body is valid
              And the token has an empty namespacePrefixes claim

        # A wrong secret is answered exactly as an unknown client id (scenario 06), so the response
        # does not reveal that the client exists.
        Scenario: 05 Verify token creation with invalid client_secret value
             When a Form URL Encoded POST request is made to "/connect/register" with
                  | Key          | Value                                          |
                  | ClientId     | _scenarioRunId                                 |
                  | ClientSecret | S3cr3t!SuperLongSecretKeyWith$peci@lChar123456 |
                  | DisplayName  | _scenarioRunId                                 |
             Then it should respond with 200
              And the response body is
                  """
                  {
                    "title": "Registered client {scenarioRunId} successfully.",
                    "status": 200
                  }
                  """
             When a Form URL Encoded POST request is made to "/connect/token" with
                  | Key           | Value                      |
                  | client_id     | _scenarioRunId             |
                  | client_secret | wrong                      |
                  | grant_type    | client_credentials         |
                  | scope         | edfi_admin_api/full_access |
             Then it should respond with 400
              And the response headers include
                  """
                  {
                    "Cache-Control": "no-store",
                    "Pragma": "no-cache"
                  }
                  """
              And the response is the OAuth error "invalid_client" with description "Invalid client or Invalid client credentials"
              And the response header "WWW-Authenticate" is not present

        Scenario: 06 Verify token creation with invalid client_id value
             When a Form URL Encoded POST request is made to "/connect/register" with
                  | Key          | Value                                          |
                  | ClientId     | _scenarioRunId                                 |
                  | ClientSecret | S3cr3t!SuperLongSecretKeyWith$peci@lChar123456 |
                  | DisplayName  | _scenarioRunId                                 |
             Then it should respond with 200
              And the response body is
                  """
                  {
                    "title": "Registered client {scenarioRunId} successfully.",
                    "status": 200
                  }
                  """
             When a Form URL Encoded POST request is made to "/connect/token" with
                  | Key           | Value                                          |
                  | client_id     | wrong                                          |
                  | client_secret | S3cr3t!SuperLongSecretKeyWith$peci@lChar123456 |
                  | grant_type    | client_credentials                             |
                  | scope         | edfi_admin_api/full_access                     |
             Then it should respond with 400
              And the response is the OAuth error "invalid_client" with description "Invalid client or Invalid client credentials"
              And the response header "WWW-Authenticate" is not present

        Scenario: 07 An unknown client attempting HTTP Basic receives a 401 with a challenge
             When a token is requested using HTTP Basic auth with client "unknown-{scenarioRunId}" and secret "wrong"
             Then it should respond with 401
              And the response is the OAuth error "invalid_client" with description "Invalid client or Invalid client credentials"
              And the response headers include
                  """
                  {
                    "WWW-Authenticate": "Basic realm=\"EdFi.DmsConfigurationService\""
                  }
                  """

        Scenario: 08 An unsupported grant_type is rejected as unsupported_grant_type
             When a token is requested with grant_type "password" using the system credentials
             Then it should respond with 400
              And the response is the OAuth error "unsupported_grant_type" with description "The specified grant type is not supported."
              And the response header "WWW-Authenticate" is not present

        Scenario: 09 A missing grant_type is rejected as invalid_request
             When a token is requested without a grant_type using the system credentials
             Then it should respond with 400
              And the response is the OAuth error "invalid_request" with description "The grant_type parameter is missing."
              And the response header "WWW-Authenticate" is not present
