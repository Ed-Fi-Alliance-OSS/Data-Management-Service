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
- Documentation requires complete synchronous find/search payloads with wire `Status: "Complete"`; pending work returns a usable token and no payload. Wire `Incomplete` is permitted only for pending results polls, as reflected in the operation-specific schemas.
- Documentation states find/search no-match uses empty `Responses` arrays in successful response groups, not provider `NotFound`.
- Documentation states accepted media types, request top-level shapes, duplicate-property rejection, and status-to-HTTP mapping.
- Documentation states operation-unsupported `404` is returned before POST body validation when a capability is absent.
- Documentation distinguishes identity-not-found `404` from operation-unsupported, tenant-not-found, and feature-off `404`.
- Documentation distinguishes provider-contract-violation `502` from identity-upstream-failure `502`.
- Documentation states find arrays may contain only JSON strings and search arrays may contain only JSON objects.
- Documentation states `IdentityError` is returned only for `InvalidProperties`, and upstream failure diagnostics are logged rather than returned to clients.
- Documentation states the `IdentityError.Path` convention with worked examples: JSONPath rooted at `$`, `$[n].property` for an item in a find or search array, a blank path routing to `errors`, and repeated messages grouping under one `validationErrors` key.
- Documentation states that a provider's exception message is recorded only at `Debug` and never returned, and that a provider should keep person data out of exception messages it raises.
- Documentation states the identity package has its own contract version independent of the DMS release version.
- Documentation states the async token rule, including the two excluded dot segments, the 1024-character escaped length ceiling, and the deployment-dependent composed-path limit.
- Documentation explains that the length rules exist because an over-long token yields a `202` whose `Location` the host rejects with `414`, and recommends keeping escaped tokens at or below 256 characters for interoperability with intermediate proxies that enforce total-URL limits DMS cannot observe.
- Documentation states the async job obligations an implementer owns: context binding to tenant, qualifiers, and client; a mismatched poll answered as `NotFound`; retention and post-expiry behavior; repeated polls of a complete job returning the same result; terminal failure represented as an answer rather than indefinite `Incomplete`; multi-replica retrievability or a single-replica statement; restart behavior; and that request cancellation does not cancel an accepted job.
- Documentation states that async results are scoped to the issuing client rather than shared across the tenant; this is mandatory for conforming providers and cannot be waived by documenting upstream sharing.
- Documentation states that DMS applies no timeout to and never retries a provider call, that a lost create response can therefore cause duplicate issuance on a client retry, that `TraceId` is not an idempotency key, and that each implementer must document its own repeated-create behavior and reconciliation.
- Documentation walks through story 03's lost-create-response example: unknown issuance outcome, provider-documented exact-key reconciliation within the same namespace, recovery of the original id without a second create, and stopping for operator/upstream reconciliation when lookup is unavailable or inconclusive. Neither a scored demographic match nor no-match without an authoritative absence guarantee makes retry safe. v1 offers no portable idempotency guarantee.
- Documentation states the UniqueId issuance constraints, including that the length limit is the person-resource `maxLength` in the deployment's ApiSchema and is 32 characters today, the guaranteed ASCII alphanumeric repertoire, and that case-variant ids within one natural-key domain collapse onto one person-resource key on SQL Server and remain distinct on PostgreSQL. DMS-1414 owns documentation/example guidance through custom validation, not a promised person-write implementation or cross-backend E2E story.
- Documentation defines provider-owned identity authority namespaces and their tenant/qualifier mapping. Independent registries may reuse values; combining their identities in the same resource table/datastore requires compatible namespaces or provider/upstream remapping. DMS neither mandates deployment-wide uniqueness nor enforces that deployment integration obligation.
- Documentation warns that a canonical 36-character GUID exceeds the person-resource `maxLength` while a 32-character hyphen-free GUID fits, since the canonical form is the likely default choice.
- Documentation states that ids outside the guaranteed repertoire are not rejected by DMS but shift the uniqueness obligation onto the provider under each store's actual equality, and that DMS pins no cross-engine equivalence for them, presented as a consequence rather than as styling advice.
- Documentation states the supported provider lifetimes, that DMS resolves the provider once per request from its own scope and does not dispose it directly, and that a provider must be safe for concurrent calls across requests.
- Documentation states the tenant/route-qualifier boundary: DMS validates tenant existence after authentication, binds the authenticated client to the URL tenant, passes route qualifiers through as context, and does not make datastore authorization part of identity.
- Documentation limits datastore independence to requests on a successfully initialized running host; fatal startup phases and readiness remain unchanged. The tenant check uses a full snapshot fresh for 60 seconds, one refresh per process, a 5-second failure cooldown, and no stale membership after expiry. Document the possible delay in observing tenant additions/deletions and `503` when a required refresh fails.
- Documentation includes the real CMS lifecycle enabled by story 00a: initial/additional clients with empty assignments, read/update, credential reset, token acquisition, and deletion; empty assignments do not grant resource access.
- Documentation distinguishes request cancellation from shared CMS cache-fill lifetime and accepted async jobs. The tenant refresh has a host-owned 30-second maximum duration; cancelling one caller ends its wait without cancelling work needed by another caller. This timeout is separate from the absence of a host timeout on `IIdentityService` calls.
- Documentation distinguishes the `401` for a client that does not belong to the URL tenant, the `404` for a tenant that does not exist, and the `503` for an unanswerable check.
- Documentation states that the identity claim's authorization strategies must be `NoFurtherAuthorizationRequired` and that any other configuration fails closed as invalid security configuration.
- The implementer chapter points to the served `/metadata/identity/v2/swagger.json` document, including its pinned `400` example bodies, as the artifact an implementer validates its own request and response payloads against; no separate conformance test package ships in this epic and no payload schemas are embedded in the contract package.
- Documentation states that clients reading the async flow from a browser must read `Location`, and that DMS exposes it via CORS for that reason.
- The packed package README points to the same implementer guidance.

### Package Publication

- The publish lane includes `EdFi.Api.Identity`.
- Publish behavior is publish-when-absent, skip-when-unchanged, and fail-when-changed.
- The comparison covers exported public types, XML documentation, and nuspec dependencies.
- Both package publication and DMS release gate the host-owned wire contract against the immutable baseline for the last published identity contract version, even if the package comparison would skip publication as unchanged. Story 02 supplies the deterministic baseline and `x-edfi-identity-contract-version` stamp; the stamp must match the package version.
- The first publication establishes the reviewed initial baseline. Once any identity contract is published, a missing/unreadable published baseline fails verification rather than silently establishing a replacement baseline; gate tests cover both cases.
- An unchanged contract version requires an unchanged wire baseline. A version increment requires an explicit reviewed compatibility diff and validation of prior request/response examples; the additive-only v1 policy rejects removing operations/fields, narrowing accepted values, or adding required fields. Breaking changes need a separately designed contract/API version and cannot pass merely by bumping the package version.
- Retain the version-to-baseline association with release verification artifacts so a host-only schema change is compared against published history, not just a baseline edited in the same PR. Cover paths, referenced schemas, status codes, media types, headers, security, and examples; normalize ordering and replace only runtime `servers` values and the injected OAuth `tokenUrl` with fixed placeholders, excluding the separately asserted version stamp. Security scheme/flow names, scopes, and requirements remain covered. No separately published conformance package or embedded duplicate schema is required.
- Gate tests mutate a schema constraint without changing public C# types and prove failure at the same version, prove an incompatible change still fails after a v1 version increment, and prove runtime server and token URL substitutions do not fail the comparison.
- SBOM and provenance artifacts are produced consistently with other DMS packages.
- Release promotion includes the identity package.
- A scratch consumer compiles against the published package and implements all interface members.
- The package README and XML docs are included in the artifact.

## Tasks

1. Add configuration documentation.
2. Add identity plugin implementer documentation.
3. Add contract README content.
4. Add doc tests or assertions used elsewhere in the repository to keep examples in sync.
5. Extend package verification scripts and wire-contract compatibility checks against published baselines.
6. Extend prerelease and release workflows, including DMS host releases when the identity package itself is unchanged.
7. Add scratch consumer verification.
8. Document the publication order and compatibility policy.
