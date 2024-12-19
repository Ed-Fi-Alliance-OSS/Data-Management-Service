Feature: Registration endpoints

        Scenario: 00 Verify register new client
             When a Form URL Encoded POST request is made to "/connect/register" with
                  | Key          | Value          |
                  | ClientId     | _scenarioRunId |
                  | ClientSecret | Secr3t:)       |
                  | DisplayName  | E2E            |
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
                  | Key          | Value          |
                  | ClientId     | _scenarioRunId |
                  | ClientSecret | Secr3t:)       |
                  | DisplayName  | E2E            |
             Then it should respond with 200
              And the response body is
                  """
                  {
                    "title": "Registered client {scenarioRunId} successfully.",
                    "status": 200
                  }
                  """
             When a Form URL Encoded POST request is made to "/connect/register" with
                  | Key          | Value          |
                  | ClientId     | _scenarioRunId |
                  | ClientSecret | Secr3t:)       |
                  | DisplayName  | E2E            |
             Then it should respond with 400
              And the response body is
                  """
                  {
                    "detail": "Data validation failed. See 'validationErrors' for details.",
                    "type": "urn:ed-fi:api:bad-request:data-validation-failed",
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
                    "type": "urn:ed-fi:api:bad-request:data-validation-failed",
                    "title": "Data Validation Failed",
                    "status": 400,
                    "validationErrors": {
                        "ClientSecret": [
                            "Client secret must contain at least one lowercase letter, one uppercase letter, one number, and one special character, and must be 8 to 12 characters long."
                        ]
                    },
                    "errors": []
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
                    "type": "urn:ed-fi:api:bad-request:data-validation-failed",
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
        Scenario: 04 Verify token creation with registered client
             When a Form URL Encoded POST request is made to "/connect/register" with
                  | Key          | Value          |
                  | ClientId     | _scenarioRunId |
                  | ClientSecret | Secr3t:)       |
                  | DisplayName  | _scenarioRunId |
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
                  | client_secret | Secr3t:)                   |
                  | grant_type    | client_credentials         |
                  | scope         | edfi_admin_api/full_access |
             Then it should respond with 200
              And the response body is
                  """
                  {
                    "access_token": "{access_token}",
                    "expires_in": 1800,
                    "token_type": "Bearer"
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
                    "detail":"{\"error\":\"unauthorized_client\",\"error_description\":\"Invalid client or Invalid client credentials\"}",
                    "type":"urn:ed-fi:api:security:authentication",
                    "title":"Authentication Failed",
                    "status":401,
                    "validationErrors":{},
                    "errors":[]
                  }
                  """

