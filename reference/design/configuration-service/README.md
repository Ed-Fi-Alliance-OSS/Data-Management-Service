# Ed-Fi Configuration Service Features and Design

The Configuration Service implements the Ed-Fi Admin API specification (version
3), providing centralized management of claimsets, authorization strategies, and
API client credentials for the Data Management Service.

Detailed design notes:

* [API Client GET Identifier Semantics (DMS-1343)](./DMS-1343-apiclient-get-identifier-semantics.md)
* [API Client Identifier Uniqueness (DMS-1529)](./DMS-1529-apiclient-identifier-uniqueness.md)
* [API Client Education Organization Scope (DMS-1625)](./DMS-1625-apiclient-education-organization-scope.md)
* [Application Update Fan-Out Measurement (DMS-1642)](./DMS-1642-application-update-fanout-measurement.md):
  timings of a claim set change applied to 200 clients on Keycloak and OpenIddict
* [Authorization in the Configuration Service](./CS-AUTH.md)
* [Claimset Management](./CLAIMSET-MGMT.md)
* [CMS Token Revocation: Keycloak Support and OAuth Failure Handling (DMS-1327)](./DMS-1327-cms-token-revocation.md)
* [Expired Access Token Cleanup](./TOKEN-CLEANUP.md)
* [Keycloak Client Provisioning Compensation (DMS-1365)](./DMS-1365-keycloak-client-provisioning-compensation.md)
* [Secret Management](./SECRET-MANAGEMENT.md)
* [Signing-Key Resolution Under Load (DMS-1556)](./DMS-1556-cms-signing-key-resolution-under-load.md),
  with its [investigation and test evidence](./DMS-1556-investigation.md); operator-facing
  behavior is in [Authorization: Signing keys in self-contained mode](./CS-AUTH.md#signing-keys-in-self-contained-mode),
  and the PR #1317 review follow-up is in its [review remediation addendum](./DMS-1556-review-remediation.md)
* [Vendor Namespace-Prefix Update Consistency (DMS-1356)](./DMS-1356-vendor-namespace-update-consistency.md)
