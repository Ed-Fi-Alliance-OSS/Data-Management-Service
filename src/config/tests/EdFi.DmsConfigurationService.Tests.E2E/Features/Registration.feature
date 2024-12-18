Feature: Registration endpoints
        Scenario: 00 Verify register new client
        # This scenario can only be run once. If it is run again, it will fail because the client is already registered in keycloak.
             When a Form URL Encoded POST request is made to "/connect/register" with
                  | key          | value    |
                  | ClientId     | E2E      |
                  | ClientSecret | Secr3t:) |
                  | DisplayName  | E2E      |
             Then it should respond with 200
              And the response body is
                  """
                  {
                    "title": "Registered client E2E successfully.",
                    "status": 200
                  }
                  """

        Scenario: 01 Verify already registered clients return 400
             When a Form URL Encoded POST request is made to "/connect/register" with
                  | key          | value                     |
                  | ClientId     | DmsConfigurationService   |
                  | ClientSecret | Secr3t:)                  |
                  | DisplayName  | DMS Configuration Service |
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
                  | key          | value                     |
                  | ClientId     | DmsConfigurationService   |
                  | ClientSecret | weak                      |
                  | DisplayName  | DMS Configuration Service |
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
                  | key | value |
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
