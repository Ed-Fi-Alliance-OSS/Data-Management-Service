@SelfContainedOnly
Feature: Client lifecycle token revocation
    Resetting a client's credentials, deleting the client, or un-approving it revokes every
    outstanding token the self-contained provider issued to it (DMS-1493); the application-level
    reset and delete do the same for their clients. A token's state is read through the
    Configuration Service's own bearer validation, which checks the token's stored status. GET
    /v3/vendors with a client's token answers 403 while the token is valid: the token authenticates,
    but its client role is not authorised for /v3/vendors (authenticated but unauthorised). Once
    the token is revoked it no longer authenticates and the same request answers 401. A 403 after
    a lifecycle operation is therefore never evidence of rejection. The administrator's session
    performs every lifecycle operation and is never replaced by a client's token.

        Background:
            Given valid credentials
              And token received
              And a POST request is made to "/v3/vendors" with
                  """
                    {
                        "company": "Lifecycle Vendor {scenarioRunId}",
                        "contactName": "Lifecycle",
                        "contactEmailAddress": "lifecycle@example.com",
                        "namespacePrefixes": "uri://ed-fi-e2e.org"
                    }
                  """
              And a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Lifecycle Application",
                   "claimSetName": "TestClaim01",
                   "dataStoreIds": []
                  }
                  """
              And the response location id is captured as "lifecycleApplicationId"
              And a POST request is made to "/v3/apiClients" with
                  """
                  {
                   "applicationId": {lifecycleApplicationId},
                   "name": "Victim Client",
                   "isApproved": true,
                   "dataStoreIds": []
                  }
                  """
              And the response body id is captured as "victimApiClientId"
              And the response body credentials are captured as "victim"
              And a POST request is made to "/v3/apiClients" with
                  """
                  {
                   "applicationId": {lifecycleApplicationId},
                   "name": "Bystander Client",
                   "isApproved": true,
                   "dataStoreIds": []
                  }
                  """
              And the response body credentials are captured as "bystander"
              And a token is requested with the credentials captured as "victim" and scope "TestClaim01"
              And the access token in the response is captured as "victim1"
              And a token is requested with the credentials captured as "victim" and scope "TestClaim01"
              And the access token in the response is captured as "victim2"
              And a token is requested with the credentials captured as "bystander" and scope "TestClaim01"
              And the access token in the response is captured as "bystander"

        @MssqlRepresentative
        Scenario: 01 Resetting a client's credentials revokes every token it holds
            # Authenticated but unauthorised: the victim's token is valid before the reset.
             When a GET request is made to "/v3/vendors" with the token captured as "victim1"
             Then it should respond with 403
            # Positive control: the victim introspecting its own token with its current credentials
            # sees it active, so the inactive answer after the reset is about this token.
             When an introspection of the token captured as "victim1" is attempted with the credentials captured as "victim"
             Then it should respond with 200
              And the response body is an object where
                  | property | value / condition |
                  | active   | "true"            |
              And the response body property "client_id" equals the value captured as "victimKey"
             When a PUT request is made to "/v3/apiClients/{victimApiClientId}/reset-credential" with
                  """
                  {}
                  """
             Then it should respond with 200
              And the response body credentials are captured as "victim"
            # Rejected: neither of the victim's earlier tokens authenticates any more.
             When a GET request is made to "/v3/vendors" with the token captured as "victim1"
             Then it should respond with 401
             When a GET request is made to "/v3/vendors" with the token captured as "victim2"
             Then it should respond with 401
            # Authenticated but unauthorised: the bystander's token is untouched.
             When a GET request is made to "/v3/vendors" with the token captured as "bystander"
             Then it should respond with 403
             When a token is requested with the credentials captured as "victim" and scope "TestClaim01"
             Then it should respond with 200
              And the access token in the response is captured as "victim3"
            # Authenticated but unauthorised: a token issued with the new secret is valid.
             When a GET request is made to "/v3/vendors" with the token captured as "victim3"
             Then it should respond with 403
             When an introspection of the token captured as "victim1" is attempted with the credentials captured as "victim"
             Then it should respond with 200
              And the response body is
                  """
                  {
                    "active": false
                  }
                  """

        @MssqlRepresentative
        Scenario: 02 Deleting a client revokes every token it holds and refuses its credentials
            # Authenticated but unauthorised: the victim's token is valid before the deletion.
             When a GET request is made to "/v3/vendors" with the token captured as "victim1"
             Then it should respond with 403
             When a DELETE request is made to "/v3/apiClients/{victimApiClientId}"
             Then it should respond with 204
            # Rejected: neither of the victim's earlier tokens authenticates any more.
             When a GET request is made to "/v3/vendors" with the token captured as "victim1"
             Then it should respond with 401
             When a GET request is made to "/v3/vendors" with the token captured as "victim2"
             Then it should respond with 401
            # Authenticated but unauthorised: the bystander's token is untouched.
             When a GET request is made to "/v3/vendors" with the token captured as "bystander"
             Then it should respond with 403
             When a token is requested with the credentials captured as "victim" and scope "TestClaim01"
             Then it should respond with 400
              And the response is the OAuth error "invalid_client" with description "Invalid client or Invalid client credentials"
              And the response header "WWW-Authenticate" is not present

        @MssqlRepresentative
        Scenario: 03 Un-approving a client revokes its tokens and re-approving it does not revive them
            # Authenticated but unauthorised: the victim's token is valid before the un-approval.
             When a GET request is made to "/v3/vendors" with the token captured as "victim1"
             Then it should respond with 403
             When a PUT request is made to "/v3/apiClients/{victimApiClientId}" with
                  """
                  {
                   "id": {victimApiClientId},
                   "applicationId": {lifecycleApplicationId},
                   "name": "Victim Client",
                   "isApproved": false,
                   "dataStoreIds": []
                  }
                  """
             Then it should respond with 204
            # Rejected: neither of the victim's earlier tokens authenticates any more.
             When a GET request is made to "/v3/vendors" with the token captured as "victim1"
             Then it should respond with 401
             When a GET request is made to "/v3/vendors" with the token captured as "victim2"
             Then it should respond with 401
            # Authenticated but unauthorised: the bystander's token is untouched.
             When a GET request is made to "/v3/vendors" with the token captured as "bystander"
             Then it should respond with 403
             When a token is requested with the credentials captured as "victim" and scope "TestClaim01"
             Then it should respond with 400
              And the response is the OAuth error "invalid_client" with description "Invalid client or Invalid client credentials"
              And the response header "WWW-Authenticate" is not present
             When a PUT request is made to "/v3/apiClients/{victimApiClientId}" with
                  """
                  {
                   "id": {victimApiClientId},
                   "applicationId": {lifecycleApplicationId},
                   "name": "Victim Client",
                   "isApproved": false,
                   "dataStoreIds": []
                  }
                  """
             Then it should respond with 204
             When a PUT request is made to "/v3/apiClients/{victimApiClientId}" with
                  """
                  {
                   "id": {victimApiClientId},
                   "applicationId": {lifecycleApplicationId},
                   "name": "Victim Client",
                   "isApproved": true,
                   "dataStoreIds": []
                  }
                  """
             Then it should respond with 204
            # Rejected: re-approval does not revive the revoked tokens.
             When a GET request is made to "/v3/vendors" with the token captured as "victim1"
             Then it should respond with 401
             When a GET request is made to "/v3/vendors" with the token captured as "victim2"
             Then it should respond with 401
             When a token is requested with the credentials captured as "victim" and scope "TestClaim01"
             Then it should respond with 200
              And the access token in the response is captured as "victim3"
            # Authenticated but unauthorised: a token issued after re-approval is valid.
             When a GET request is made to "/v3/vendors" with the token captured as "victim3"
             Then it should respond with 403

        Scenario: 04 Resetting an application's credentials revokes its client's tokens
             When a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Single Client Application",
                   "claimSetName": "TestClaim01",
                   "dataStoreIds": []
                  }
                  """
             Then it should respond with 201
              And the response location id is captured as "singleClientApplicationId"
              And the response body credentials are captured as "singleClient"
             When a token is requested with the credentials captured as "singleClient" and scope "TestClaim01"
             Then it should respond with 200
              And the access token in the response is captured as "singleClient1"
            # Authenticated but unauthorised: the application's token is valid before the reset.
             When a GET request is made to "/v3/vendors" with the token captured as "singleClient1"
             Then it should respond with 403
             When a PUT request is made to "/v3/applications/{singleClientApplicationId}/reset-credential" with
                  """
                  {}
                  """
             Then it should respond with 200
            # Rejected: the application's earlier token no longer authenticates.
             When a GET request is made to "/v3/vendors" with the token captured as "singleClient1"
             Then it should respond with 401
            # Authenticated but unauthorised: another application's client keeps its token.
             When a GET request is made to "/v3/vendors" with the token captured as "victim1"
             Then it should respond with 403

        Scenario: 05 Deleting an application revokes the tokens of every client it has
             When a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "Survivor Application",
                   "claimSetName": "TestClaim01",
                   "dataStoreIds": []
                  }
                  """
             Then it should respond with 201
              And the response body credentials are captured as "survivor"
             When a token is requested with the credentials captured as "survivor" and scope "TestClaim01"
             Then it should respond with 200
              And the access token in the response is captured as "survivor1"
            # Authenticated but unauthorised: every token is valid before the deletion.
             When a GET request is made to "/v3/vendors" with the token captured as "victim1"
             Then it should respond with 403
             When a GET request is made to "/v3/vendors" with the token captured as "bystander"
             Then it should respond with 403
             When a DELETE request is made to "/v3/applications/{lifecycleApplicationId}"
             Then it should respond with 204
            # Rejected: no token of any of the deleted application's clients authenticates any more.
             When a GET request is made to "/v3/vendors" with the token captured as "victim1"
             Then it should respond with 401
             When a GET request is made to "/v3/vendors" with the token captured as "victim2"
             Then it should respond with 401
             When a GET request is made to "/v3/vendors" with the token captured as "bystander"
             Then it should respond with 401
            # Authenticated but unauthorised: another application's client keeps its token.
             When a GET request is made to "/v3/vendors" with the token captured as "survivor1"
             Then it should respond with 403
