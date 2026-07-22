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
             Then it should respond with 409
              And the response body is
                  """
                  {
                    "detail": "The identifying value(s) of the item are the same as another item that already exists.",
                    "type": "urn:ed-fi:api:conflict:non-unique-identity",
                    "title": "Identifying Values Are Not Unique",
                    "status": 409,
                    "validationErrors": {},
                    "errors": [
                            "Client with the same Client Id already exists. Please provide different Client Id."
                            ]
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
                    "type": "urn:ed-fi:api:bad-request:data",
                    "title": "Data Validation Failed",
                    "status": 400,
                    "validationErrors": {
                        "$.clientSecret": [
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
                    "type": "urn:ed-fi:api:bad-request:data",
                    "title": "Data Validation Failed",
                    "status": 400,
                    "validationErrors": {
                        "$.clientSecret": [
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
                    "type": "urn:ed-fi:api:bad-request:data",
                    "title": "Data Validation Failed",
                    "status": 400,
                    "validationErrors": {
                        "$.clientSecret": [
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
                    "type": "urn:ed-fi:api:bad-request:data",
                    "title": "Data Validation Failed",
                    "status": 400,
                    "validationErrors": {
                        "$.clientId": [
                            "'Client Id' must not be empty."
                        ],
                        "$.clientSecret": [
                            "'Client Secret' must not be empty."
                        ],
                        "$.displayName": [
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
              And the token response body is valid

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
             Then it should respond with 401
              And the response body is
                  """
                  {
                    "error": "invalid_client",
                    "error_description": "Client authentication failed."
                  }
                  """

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
             Then it should respond with 401
              And the response body is
                  """
                  {
                    "error": "invalid_client",
                    "error_description": "Client authentication failed."
                  }
                  """

        Scenario: 07 Verify unsupported grant type returns the OAuth error
             When a Form URL Encoded POST request is made to "/connect/token" with
                  | Key           | Value                                          |
                  | client_id     | _scenarioRunId                                 |
                  | client_secret | S3cr3t!SuperLongSecretKeyWith$peci@lChar123456 |
                  | grant_type    | authorization_code                             |
                  | scope         | edfi_admin_api/full_access                     |
             Then it should respond with 400
              And the response body is
                  """
                  {
                    "error": "unsupported_grant_type",
                    "error_description": "The specified grant type is not supported."
                  }
                  """

        Scenario: 08 Verify missing client authentication returns invalid_client
             When a Form URL Encoded POST request is made to "/connect/token" with
                  | Key        | Value              |
                  | client_id  | _scenarioRunId     |
                  | grant_type | client_credentials |
             Then it should respond with 401
              And the response body is
                  """
                  {
                    "error": "invalid_client",
                    "error_description": "Client authentication failed."
                  }
                  """

        Scenario: 09 Verify a missing required parameter returns the OAuth error
             When a Form URL Encoded POST request is made to "/connect/token" with
                  | Key           | Value          |
                  | client_id     | _scenarioRunId |
                  | client_secret | wrongsecret    |
             Then it should respond with 400
              And the response body is
                  """
                  {
                    "error": "invalid_request",
                    "error_description": "The request is missing a required parameter or is otherwise malformed."
                  }
                  """
