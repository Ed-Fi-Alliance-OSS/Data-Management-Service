Feature: API client education organizations
    An API client inherits its Application's education organizations or is narrowed to its own
    list, and the identity provider issues its tokens with exactly its effective list. Every token
    here is a real token from the running identity provider, requested with the client's own
    credentials. Application A holds 255901, 255902 and 255903 and has no default API client.

        Background:
            Given valid credentials
              And token received
              And a POST request is made to "/v3/vendors" with
                  """
                    {
                        "company": "EdOrg Vendor {scenarioRunId}",
                        "contactName": "Test",
                        "contactEmailAddress": "test@gmail.com",
                        "namespacePrefixes": "uri://edorg-{scenarioRunId}.org"
                    }
                  """
              And a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "EdOrg Application A {scenarioRunId}",
                   "claimSetName": "EdOrgScopeClaimSet",
                   "educationOrganizationIds": [255901, 255902, 255903],
                   "createDefaultCredentials": false
                  }
                  """
              And the response location id is captured as "appA"

        @MssqlRepresentative
        Scenario: 01 Each create request gives the client's token exactly its effective list
             When a POST request is made to "/v3/apiClients" with
                  """
                  {
                   "applicationId": {appA},
                   "name": "List {scenarioRunId}",
                   "isApproved": true,
                   "educationOrganizationIds": [255902]
                  }
                  """
             Then it should respond with 201
              And the response body credentials are captured as "listClient"
             When a POST request is made to "/v3/apiClients" with
                  """
                  {
                   "applicationId": {appA},
                   "name": "False List {scenarioRunId}",
                   "isApproved": true,
                   "inheritsApplicationEducationOrganizations": false,
                   "educationOrganizationIds": [255903, 255901]
                  }
                  """
             Then it should respond with 201
              And the response body credentials are captured as "falseListClient"
             When a POST request is made to "/v3/apiClients" with
                  """
                  {
                   "applicationId": {appA},
                   "name": "True {scenarioRunId}",
                   "isApproved": true,
                   "inheritsApplicationEducationOrganizations": true
                  }
                  """
             Then it should respond with 201
              And the response body credentials are captured as "trueClient"
             When a POST request is made to "/v3/apiClients" with
                  """
                  {
                   "applicationId": {appA},
                   "name": "Equal {scenarioRunId}",
                   "isApproved": true,
                   "inheritsApplicationEducationOrganizations": true,
                   "educationOrganizationIds": [255903, 255901, 255902, 255903]
                  }
                  """
             Then it should respond with 201
              And the response body credentials are captured as "trueEqualListClient"
             When a POST request is made to "/v3/apiClients" with
                  """
                  {
                   "applicationId": {appA},
                   "name": "Neither {scenarioRunId}",
                   "isApproved": true
                  }
                  """
             Then it should respond with 201
              And the response body credentials are captured as "neitherClient"
             When a token is requested with the credentials captured as "listClient" and scope "EdOrgScopeClaimSet"
             Then it should respond with 200
              And the token carries exactly the education organizations "255902"
             When a token is requested with the credentials captured as "falseListClient" and scope "EdOrgScopeClaimSet"
             Then it should respond with 200
              And the token carries exactly the education organizations "255901,255903"
             When a token is requested with the credentials captured as "trueClient" and scope "EdOrgScopeClaimSet"
             Then it should respond with 200
              And the token carries exactly the education organizations "255901,255902,255903"
             When a token is requested with the credentials captured as "trueEqualListClient" and scope "EdOrgScopeClaimSet"
             Then it should respond with 200
              And the token carries exactly the education organizations "255901,255902,255903"
             When a token is requested with the credentials captured as "neitherClient" and scope "EdOrgScopeClaimSet"
             Then it should respond with 200
              And the token carries exactly the education organizations "255901,255902,255903"

        # The Application update refreshes the Application's first client in the identity provider.
        # A client narrowed to the whole list keeps that list, so its token never gains the added id.
        @MssqlRepresentative
        Scenario: 02 A client narrowed to its whole Application never gains an id added later
            Given a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "EdOrg Application B {scenarioRunId}",
                   "claimSetName": "EdOrgScopeClaimSet",
                   "educationOrganizationIds": [255901, 255902],
                   "createDefaultCredentials": false
                  }
                  """
              And the response location id is captured as "appB"
             When a POST request is made to "/v3/apiClients" with
                  """
                  {
                   "applicationId": {appB},
                   "name": "Full List {scenarioRunId}",
                   "isApproved": true,
                   "educationOrganizationIds": [255901, 255902]
                  }
                  """
             Then it should respond with 201
              And the response body id is captured as "fullListClientId"
              And the response body credentials are captured as "fullListClient"
             When a GET request is made to "/v3/apiClients/{fullListClientId}"
             Then it should respond with 200
              And the response body has inheritance "false" and education organizations "255901,255902"
             When a token is requested with the credentials captured as "fullListClient" and scope "EdOrgScopeClaimSet"
             Then it should respond with 200
              And the token carries exactly the education organizations "255901,255902"
             When a PUT request is made to "/v3/applications/{appB}" with
                  """
                  {
                   "id": {appB},
                   "vendorId": {vendorId},
                   "applicationName": "EdOrg Application B {scenarioRunId}",
                   "claimSetName": "EdOrgScopeClaimSet",
                   "educationOrganizationIds": [255901, 255902, 255904]
                  }
                  """
             Then it should respond with 204
             When a token is requested with the credentials captured as "fullListClient" and scope "EdOrgScopeClaimSet"
             Then it should respond with 200
              And the token carries exactly the education organizations "255901,255902"
             When a GET request is made to "/v3/apiClients/{fullListClientId}"
             Then it should respond with 200
              And the response body has inheritance "false" and education organizations "255901,255902"

        @MssqlRepresentative
        Scenario: 03 Updates and moves give the client's next token its new effective list
            Given a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "EdOrg Application C {scenarioRunId}",
                   "claimSetName": "EdOrgScopeClaimSet",
                   "educationOrganizationIds": [255901, 255904],
                   "createDefaultCredentials": false
                  }
                  """
              And the response location id is captured as "appC"
             When a POST request is made to "/v3/apiClients" with
                  """
                  {
                   "applicationId": {appA},
                   "name": "Mover {scenarioRunId}",
                   "isApproved": true
                  }
                  """
             Then it should respond with 201
              And the response body id is captured as "moverId"
              And the response body credentials are captured as "mover"
             # Narrow the inheriting client
             When a PUT request is made to "/v3/apiClients/{moverId}" with
                  """
                  { "id": {moverId}, "applicationId": {appA}, "name": "Mover {scenarioRunId}", "isApproved": true, "educationOrganizationIds": [255902] }
                  """
             Then it should respond with 204
             When a token is requested with the credentials captured as "mover" and scope "EdOrgScopeClaimSet"
             Then it should respond with 200
              And the token carries exactly the education organizations "255902"
             # Neither field keeps the narrowing
             When a PUT request is made to "/v3/apiClients/{moverId}" with
                  """
                  { "id": {moverId}, "applicationId": {appA}, "name": "Mover Renamed {scenarioRunId}", "isApproved": true }
                  """
             Then it should respond with 204
             When a token is requested with the credentials captured as "mover" and scope "EdOrgScopeClaimSet"
             Then it should respond with 200
              And the token carries exactly the education organizations "255902"
             # Replace the list
             When a PUT request is made to "/v3/apiClients/{moverId}" with
                  """
                  { "id": {moverId}, "applicationId": {appA}, "name": "Mover {scenarioRunId}", "isApproved": true, "educationOrganizationIds": [255903, 255901] }
                  """
             Then it should respond with 204
             When a token is requested with the credentials captured as "mover" and scope "EdOrgScopeClaimSet"
             Then it should respond with 200
              And the token carries exactly the education organizations "255901,255903"
             # true with the Application's list switches back to inheriting
             When a PUT request is made to "/v3/apiClients/{moverId}" with
                  """
                  { "id": {moverId}, "applicationId": {appA}, "name": "Mover {scenarioRunId}", "isApproved": true, "inheritsApplicationEducationOrganizations": true, "educationOrganizationIds": [255902, 255901, 255903] }
                  """
             Then it should respond with 204
             When a GET request is made to "/v3/apiClients/{moverId}"
             Then it should respond with 200
              And the response body has inheritance "true" and education organizations "255901,255902,255903"
             When a token is requested with the credentials captured as "mover" and scope "EdOrgScopeClaimSet"
             Then it should respond with 200
              And the token carries exactly the education organizations "255901,255902,255903"
             # Neither field keeps inheriting
             When a PUT request is made to "/v3/apiClients/{moverId}" with
                  """
                  { "id": {moverId}, "applicationId": {appA}, "name": "Mover Renamed {scenarioRunId}", "isApproved": true }
                  """
             Then it should respond with 204
             When a token is requested with the credentials captured as "mover" and scope "EdOrgScopeClaimSet"
             Then it should respond with 200
              And the token carries exactly the education organizations "255901,255902,255903"
             # An inheriting client moved with neither field takes the new Application's list
             When a PUT request is made to "/v3/apiClients/{moverId}" with
                  """
                  { "id": {moverId}, "applicationId": {appC}, "name": "Mover {scenarioRunId}", "isApproved": true }
                  """
             Then it should respond with 204
             When a GET request is made to "/v3/apiClients/{moverId}"
             Then it should respond with 200
              And the response body has inheritance "true" and education organizations "255901,255904"
             When a token is requested with the credentials captured as "mover" and scope "EdOrgScopeClaimSet"
             Then it should respond with 200
              And the token carries exactly the education organizations "255901,255904"
             # A narrowed client that does not fit A cannot move there with neither field; nothing changes
             When a PUT request is made to "/v3/apiClients/{moverId}" with
                  """
                  { "id": {moverId}, "applicationId": {appC}, "name": "Mover {scenarioRunId}", "isApproved": true, "educationOrganizationIds": [255904] }
                  """
             Then it should respond with 204
             When a PUT request is made to "/v3/apiClients/{moverId}" with
                  """
                  { "id": {moverId}, "applicationId": {appA}, "name": "Mover {scenarioRunId}", "isApproved": true }
                  """
             Then it should respond with 409
             When a token is requested with the credentials captured as "mover" and scope "EdOrgScopeClaimSet"
             Then it should respond with 200
              And the token carries exactly the education organizations "255904"
             # An explicit replacement list moves it
             When a PUT request is made to "/v3/apiClients/{moverId}" with
                  """
                  { "id": {moverId}, "applicationId": {appA}, "name": "Mover {scenarioRunId}", "isApproved": true, "educationOrganizationIds": [255902] }
                  """
             Then it should respond with 204
             When a token is requested with the credentials captured as "mover" and scope "EdOrgScopeClaimSet"
             Then it should respond with 200
              And the token carries exactly the education organizations "255902"
             # An explicit true moves it as an inheriting client
             When a PUT request is made to "/v3/apiClients/{moverId}" with
                  """
                  { "id": {moverId}, "applicationId": {appC}, "name": "Mover {scenarioRunId}", "isApproved": true, "inheritsApplicationEducationOrganizations": true }
                  """
             Then it should respond with 204
             When a token is requested with the credentials captured as "mover" and scope "EdOrgScopeClaimSet"
             Then it should respond with 200
              And the token carries exactly the education organizations "255901,255904"
             # A narrowed client that fits moves with neither field and keeps its list
             When a PUT request is made to "/v3/apiClients/{moverId}" with
                  """
                  { "id": {moverId}, "applicationId": {appC}, "name": "Mover {scenarioRunId}", "isApproved": true, "educationOrganizationIds": [255901] }
                  """
             Then it should respond with 204
             When a PUT request is made to "/v3/apiClients/{moverId}" with
                  """
                  { "id": {moverId}, "applicationId": {appA}, "name": "Mover {scenarioRunId}", "isApproved": true }
                  """
             Then it should respond with 204
             When a GET request is made to "/v3/apiClients/{moverId}"
             Then it should respond with 200
              And the response body property "applicationId" equals the value captured as "appA"
              And the response body has inheritance "false" and education organizations "255901"
             When a token is requested with the credentials captured as "mover" and scope "EdOrgScopeClaimSet"
             Then it should respond with 200
              And the token carries exactly the education organizations "255901"
             # An invalid request changes nothing
             When a PUT request is made to "/v3/apiClients/{moverId}" with
                  """
                  { "id": {moverId}, "applicationId": {appA}, "name": "Mover {scenarioRunId}", "isApproved": true, "educationOrganizationIds": [] }
                  """
             Then it should respond with 400
             When a token is requested with the credentials captured as "mover" and scope "EdOrgScopeClaimSet"
             Then it should respond with 200
              And the token carries exactly the education organizations "255901"

        @MssqlRepresentative
        Scenario: 04 A narrowed client's credential reset changes only its secret
             When a POST request is made to "/v3/apiClients" with
                  """
                  {
                   "applicationId": {appA},
                   "name": "Reset {scenarioRunId}",
                   "isApproved": true,
                   "educationOrganizationIds": [255902]
                  }
                  """
             Then it should respond with 201
              And the response body id is captured as "resetClientId"
              And the response body property "key" is captured as "resetKey"
              And the response body credentials are captured as "resetClient"
             When a token is requested with the credentials captured as "resetClient" and scope "EdOrgScopeClaimSet"
             Then it should respond with 200
              And the token carries exactly the education organizations "255902"
             When a PUT request is made to "/v3/apiClients/{resetClientId}/reset-credential" with
                  """
                  {}
                  """
             Then it should respond with 200
              And the response body property "key" equals the value captured as "resetKey"
              And the response body credentials are captured as "resetClientAfterReset"
             When a token is requested with the credentials captured as "resetClientAfterReset" and scope "EdOrgScopeClaimSet"
             Then it should respond with 200
              And the token carries exactly the education organizations "255902"
             When a token is requested with the credentials captured as "resetClient" and scope "EdOrgScopeClaimSet"
             Then it should respond with 400 or 401
             When a GET request is made to "/v3/apiClients/{resetClientId}"
             Then it should respond with 200
              And the response body property "clientId" equals the value captured as "resetKey"
              And the response body has inheritance "false" and education organizations "255902"

        # Removing a narrowed first client's last education organization from its Application leaves it
        # narrowed to nothing and still approved; its next token states no education organization.
        @MssqlRepresentative
        Scenario: 05 A narrowed first client whose education organizations are all removed keeps no scope
            Given a POST request is made to "/v3/applications" with
                  """
                  {
                   "vendorId": {vendorId},
                   "applicationName": "EdOrg Application D {scenarioRunId}",
                   "claimSetName": "EdOrgScopeClaimSet",
                   "educationOrganizationIds": [255905, 255906],
                   "createDefaultCredentials": false
                  }
                  """
              And the response location id is captured as "appD"
             When a POST request is made to "/v3/apiClients" with
                  """
                  {
                   "applicationId": {appD},
                   "name": "Last {scenarioRunId}",
                   "isApproved": true,
                   "educationOrganizationIds": [255905]
                  }
                  """
             Then it should respond with 201
              And the response body id is captured as "lastClientId"
              And the response body credentials are captured as "lastClient"
             When a GET request is made to "/v3/apiClients/{lastClientId}"
             Then it should respond with 200
              And the response body property "isApproved" is captured as "lastClientApproved"
             When a PUT request is made to "/v3/applications/{appD}" with
                  """
                  {
                   "id": {appD},
                   "vendorId": {vendorId},
                   "applicationName": "EdOrg Application D {scenarioRunId}",
                   "claimSetName": "EdOrgScopeClaimSet",
                   "educationOrganizationIds": [255906]
                  }
                  """
             Then it should respond with 204
             When a GET request is made to "/v3/apiClients/{lastClientId}"
             Then it should respond with 200
              And the response body has inheritance "false" and education organizations ""
              And the response body property "isApproved" equals the value captured as "lastClientApproved"
             When a token is requested with the credentials captured as "lastClient" and scope "EdOrgScopeClaimSet"
             Then it should respond with 200
              And the token carries no education organizations
