---
jira: TBD
source_spike: DMS-1413
depends_on: 04, 05, DMS-1500
---

# Story: Document the Identity API and Plugin Contract

## Description

Write the operator and implementer documentation for Identity Management.
This story depends on the plugin documentation foundation so identity docs can link to the shared packaging, delivery, trust, and allowlist guidance.

## Acceptance Criteria

- `docs/CONFIGURATION.md` documents `AppSettings:EnableIdentityManagement`, default `false`, and what it gates.
- The plugin documentation has an identity chapter explaining how to implement and register `IIdentityService`.
- Documentation states that DMS owns routes and plugins map no identity endpoints.
- Documentation states that plugins register the replacement with `Add`, not `TryAdd`.
- Documentation states capabilities are deployment-wide in v1.
- Documentation states request and response payload obligations per operation, including standard identifying attributes, unsupported-as-null semantics, ordered search-response groups, `BirthDate` as `date-time`, `Score` as `number`/`double`, and that DMS does not runtime-validate response schemas beyond presence.
- Documentation states find/search no-match uses empty `Responses` arrays in successful response groups, not provider `NotFound`.
- Documentation states accepted media types, request top-level shapes, duplicate-property rejection, and status-to-HTTP mapping.
- Documentation states operation-unsupported `404` is returned before POST body validation when a capability is absent.
- Documentation distinguishes identity-not-found `404` from operation-unsupported, tenant-not-found, and feature-off `404`.
- Documentation distinguishes provider-contract-violation `502` from identity-upstream-failure `502`.
- Documentation states find arrays may contain only JSON strings and search arrays may contain only JSON objects.
- Documentation states `IdentityError` is returned only for `InvalidProperties`, and upstream failure diagnostics are logged rather than returned to clients.
- Documentation states the identity package has its own contract version independent of the DMS release version.
- Documentation states the async token rule, including the two excluded dot segments.
- Documentation states the tenant/route-qualifier boundary: DMS validates tenant existence after authentication, route qualifiers pass through as context, and datastore authorization is not part of identity.
- The packed package README points to the same implementer guidance.

## Tasks

1. Add configuration documentation.
2. Add identity plugin implementer documentation.
3. Add contract README content.
4. Add doc tests or assertions used elsewhere in the repository to keep examples in sync.
