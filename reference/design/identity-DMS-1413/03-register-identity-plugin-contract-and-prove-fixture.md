---
jira: DMS-1516
jira_url: https://edfi.atlassian.net/browse/DMS-1516
epic: DMS-1412
source_spike: DMS-1413
depends_on: DMS-1515, DMS-1498, DMS-1499
---

# Story: Register the Identity Plugin Contract and Prove a Fixture Plugin

## Description

Register `IIdentityService` in the DMS plugin contract registry as a replace-cardinality contract and prove the DMS-owned HTTP surface against a fixture identity plugin loaded through the actual DMS-1462 plugin path.

This story depends on the plugin recording wrapper and startup loading work from DMS-1498 and DMS-1499.
Before those foundations exist, the Identity API can be implemented and tested only with the DMS host default or in-repo test doubles.

## Acceptance Criteria

### Plugin Contract Registry

- `DmsPluginContracts.Registry` declares `IIdentityService` as `Replace`.
- `ContractAssemblyNames` includes the assembly name `EdFi.DataManagementService.Identity`, not the package id `EdFi.Api.Identity`.
- A DMS image build after DMS-1499 carries `EdFi.DataManagementService.Identity.dll` with `AssemblyVersion` equal to the identity contract package version.
- Host default plus no plugin is valid.
- Host default plus one plugin replacement is valid.
- Two plugin replacements are fatal and the error names both plugins.
- A plugin registering `IIdentityService` is admitted by the declared-contract exemption.
- A plugin that uses `TryAdd` and silently keeps the host default is documented as an implementer error; the registry does not pretend to observe a descriptor the wrapper cannot see.

### Fixture Plugin Proof

- A fixture plugin replaces `IIdentityService` through the DMS-1462 plugin path.
- Sync create/get-by-id/find/search/results flows succeed over HTTP.
- Synchronous find/search fixture payloads have wire `Status: "Complete"` and required `SearchResponses`; incomplete payloads fail conformance against those operations' served schemas. Pending work returns a token; results polling permits incomplete and complete payloads. This adds no runtime host payload validation.
- The fixture documents its authority/namespace mapping. Independent tenant/qualifier namespaces can reuse an id without returning the other namespace's person; a shared namespace resolves an id consistently across the contexts mapped to it. No person-write integration or deployment-wide uniqueness is assumed.
- The fixture documents and enforces client/namespace grants before every operation's identity work. Within one tenant, A is granted district A and B district B, with both clients host-authorized for Create/Read. A's new get/find/search/create requests to valid district B return identity-not-found `404` with no lookup, issuance, or job creation; own-namespace requests succeed. Also test missing grants, policy-source failure returning `502`, and an explicit tenant-wide grant. Namespace grants do not waive job ownership, and revoking a grant blocks polls of previously owned jobs once the documented policy cache observes it.
- The fixture uses the contract's case-insensitive tenant/qualifier name/value equality and order-independent qualifier sets for both namespace selection and job binding, with case-sensitive client IDs and null tenant distinct from named tenants. Test equivalent case/order variants and denial for missing/extra qualifiers or genuinely different values/clients. Where case-distinct client IDs or dictionary-order variants cannot be produced by HTTP provisioning/routing, test them directly at the fixture provider boundary as well as exercising valid route-case variants over HTTP. A shared namespace never waives job ownership.
- Async find/search return `202 Location`, and following the returned `Location` polls to incomplete and complete results.
- A temporary poll retrieval failure returns ordinary upstream-failure `502`; after recovery, the same unexpired token returns its pending/complete answer. A definitively failed job returns `JobFailed`, mapped to terminal-job-failure `502` with `urn:ed-fi:api:identities:job-failed` and no `Location`; repeat authorized polls return the stable type, while mismatched ownership and expiry return `404`. Different trace IDs do not violate terminal stability. If the state is inaccessible, the poll may return upstream-failure rather than asserting terminal state.
- `JobFailed` from create/get/find/search is contract misuse. A results `JobFailed` with no payload is valid; supplied payload/errors are not exposed. The fixture's client stops polling on the terminal type and never automatically resubmits the original request.
- All host-generated responses on mapped identity operations carry `Cache-Control: no-store`, including success, pending, complete, terminal, missing/expired token, capability rejection, and early authentication failure. Assert the header over HTTP with the real provider path; metadata caching behavior is unchanged.
- A request rejected by the real global limiter also returns `429` with `no-store` and the existing rate-limit problem/optional `Retry-After`, without invoking the fixture provider; endpoint-level enforcement alone cannot satisfy this test.
- `Incomplete` from any operation except results returns provider-contract-violation `502`.
- Tokens that need escaping round-trip to the provider unchanged.
- Exact `.` and `..` tokens return `502` with no `Location`.
- A fixture-plugin token that exceeds the escaped length ceiling, and one that fits the ceiling but overflows the composed poll path under a tenant and route qualifiers, both return `502` with no `Location` rather than a `202`.
- Every `202 Location` the fixture plugin produces is followed and reaches the results route, proving the emitted URL is fetchable rather than rejected by the host before routing.
- The fixture plugin binds each async job to the issuing `Tenant`, `RouteQualifiers`, and `ClientId`, and a token redeemed under a different tenant, qualifier set, or client returns identity-not-found `404` rather than the original job.
- The different-client case uses another client authorized for identity in the same tenant and qualifiers, and the different-tenant case uses a client authorized in that target tenant, so provider ownership is tested after host authorization succeeds. No documentation-based sharing exception is permitted, including when the upstream shares results.
- A fixture-plugin async job either remains pollable after the DMS container restarts, or the fixture documents itself as in-memory only and the test asserts the documented `404`.
- The fixture plugin is registered with a scoped lifetime and a scoped dependency of its own, proving the host resolves it per request rather than capturing it in the singleton `ApiService`.
- Custom properties pass through request and response payloads.
- Standard identifying attributes and unsupported-as-null semantics appear in success responses.
- Search scores are present on returned search matches and are passed through without DMS inspection.
- Unsupported capability returns operation-unsupported `404`.
- Find/search no-match returns successful response groups with empty `Responses` arrays.
- Provider `NotFound` returns identity-not-found `404`, distinct from unsupported capability.
- Enabled with no plugin starts cleanly and answers operation-unsupported `404`.
- Duplicate-property, malformed-body, wrong-shape, invalid find/search array-entry, unsupported-media-type, provider `InvalidProperties`, missing-payload, provider-contract-violation, and provider-exception upstream-failure cases are covered.
- The fixture plugin returns `InvalidProperties` in each projection shape - a path on a single-object body, a path on an indexed search item, a blank path, and two messages at one path - and the resulting `400` bodies match the pinned examples in the served OpenAPI document.
- A get-by-id and a results poll over the fixture plugin leave no UniqueId and no token in the captured logs, in either the structured `Path` property or the rendered message, at both logging layers.
- A fixture-plugin exception whose message contains person-shaped text does not surface that text in the client response or in the failure-level log entry.
- Fixture variants throw from request-time activation (factory/constructor), capability getter, and operation invocation, with nested person-data sentinel messages. Activation/getter failures return sanitized provider-configuration `500` and never invoke an operation; operation failures return sanitized upstream-failure `502`. Sentinels are absent from client responses and all non-Debug host logs, including outer exception logging and cancellation handling. Capability access occurs once per request; use scoped activation so startup loading succeeds and the request-time boundary is exercised.
- Two replacing plugins abort startup with both plugin names in the fatal diagnostic.
- A fixture create records an issuance then throws to simulate a lost upstream response; DMS returns `502`. The example client treats the outcome as unknown, uses the fixture's documented exact upstream-key search in the same namespace, and recovers the original id without a second create. The key is a fixture custom property, not a new host idempotency contract.
- A second recovery case with no reliable reconciliation lookup stops for documented operator/upstream reconciliation and performs no automatic create retry. A scored match or a no-match without authoritative absence is not considered safe recovery. Document the fixture's limitation rather than claiming portable retry safety.

## Tasks

1. Add the registry entry after the plugin foundation exists.
2. Add cardinality, assembly-name, runtime image assembly-version, and duplicate-replacement startup tests.
3. Build the fixture plugin against packed `EdFi.Api.Identity` and `EdFi.Api.Plugins`.
4. Add integration tests using test doubles where plugin loading is not required.
5. Add Docker-stack E2E tests once the plugin loader exists.
6. Assert operation-specific served OpenAPI schemas validate fixture success payloads, including custom properties and complete synchronous response states; prove namespace isolation and the documented lost-create recovery workflow.
7. Add implementer documentation hooks for `Add` versus `TryAdd`.
