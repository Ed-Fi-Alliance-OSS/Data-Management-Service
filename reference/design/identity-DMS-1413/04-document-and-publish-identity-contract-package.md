---
jira: TBD
source_spike: DMS-1413
depends_on: 03, DMS-1500, DMS-1501
---

# Story: Document and Publish `EdFi.Api.Identity`

## Description

Write the operator and implementer documentation for Identity Management and add `EdFi.Api.Identity` to the package publication lane.

This story depends on the plugin documentation foundation so identity docs can link to the shared packaging, delivery, trust, and allowlist guidance.
It also depends on the package publication foundation because publishing burns the package id and makes the public contract additive-only.
Documentation can be drafted earlier, but this Jira is not complete until the package publication path is wired and verified.

## Acceptance Criteria

### Documentation

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
- Documentation states the async job obligations an implementer owns: context binding to tenant, qualifiers, and client; a mismatched poll answered as `NotFound`; retention and post-expiry behavior; repeated polls of a complete job returning the same result; terminal failure represented as an answer rather than indefinite `Incomplete`; multi-replica retrievability or a single-replica statement; restart behavior; and that request cancellation does not cancel an accepted job.
- Documentation states that async results are scoped to the issuing client rather than shared across the tenant.
- Documentation states that DMS applies no timeout to and never retries a provider call, that a lost create response can therefore cause duplicate issuance on a client retry, that `TraceId` is not an idempotency key, and that each implementer must document its own repeated-create behavior and reconciliation.
- Documentation states the UniqueId issuance constraints, including that case-variant ids collapse onto one person-resource natural key on SQL Server and remain distinct on PostgreSQL, and that person-write validation itself is DMS-1414's scope.
- Documentation states the supported provider lifetimes, that DMS resolves the provider once per request from its own scope and does not dispose it directly, and that a provider must be safe for concurrent calls across requests.
- Documentation states the tenant/route-qualifier boundary: DMS validates tenant existence after authentication, binds the authenticated client to the URL tenant, passes route qualifiers through as context, and does not make datastore authorization part of identity.
- Documentation distinguishes the `401` for a client that does not belong to the URL tenant, the `404` for a tenant that does not exist, and the `503` for an unanswerable check.
- Documentation states that the identity claim's authorization strategies must be `NoFurtherAuthorizationRequired` and that any other configuration fails closed as invalid security configuration.
- The implementer chapter points to the served `/metadata/identity/v2/swagger.json` document as the artifact an implementer validates its own request and response payloads against; no separate conformance test package ships in this epic.
- The packed package README points to the same implementer guidance.

### Package Publication

- The publish lane includes `EdFi.Api.Identity`.
- Publish behavior is publish-when-absent, skip-when-unchanged, and fail-when-changed.
- The comparison covers exported public types, XML documentation, and nuspec dependencies.
- SBOM and provenance artifacts are produced consistently with other DMS packages.
- Release promotion includes the identity package.
- A scratch consumer compiles against the published package and implements all interface members.
- The package README and XML docs are included in the artifact.

## Tasks

1. Add configuration documentation.
2. Add identity plugin implementer documentation.
3. Add contract README content.
4. Add doc tests or assertions used elsewhere in the repository to keep examples in sync.
5. Extend package verification scripts.
6. Extend prerelease and release workflows.
7. Add scratch consumer verification.
8. Document the publication order and compatibility policy.
