# Ed-Fi Configuration Service Features and Design

The Configuration Service implements the Ed-Fi Admin API specification (version
3), providing centralized management of claimsets, authorization strategies, and
API client credentials for the Data Management Service.

Detailed design notes:

* [API Client GET Identifier Semantics (DMS-1343)](./DMS-1343-apiclient-get-identifier-semantics.md)
* [Authorization in the Configuration Service](./CS-AUTH.md)
* [Claimset Management](./CLAIMSET-MGMT.md)
* [Expired Access Token Cleanup](./TOKEN-CLEANUP.md)
* [Keycloak Client Provisioning Compensation (DMS-1365)](./DMS-1365-keycloak-client-provisioning-compensation.md)
* [Secret Management](./SECRET-MANAGEMENT.md)
* [Signing-Key Resolution Under Load (DMS-1556)](./DMS-1556-cms-signing-key-resolution-under-load.md),
  with its [investigation and test evidence](./DMS-1556-investigation.md); operator-facing
  behavior is in [Authorization: Signing keys in self-contained mode](./CS-AUTH.md#signing-keys-in-self-contained-mode),
  and the PR #1317 review follow-up is in its [review remediation addendum](./DMS-1556-review-remediation.md)
* [Vendor Namespace-Prefix Update Consistency (DMS-1356)](./DMS-1356-vendor-namespace-update-consistency.md)
