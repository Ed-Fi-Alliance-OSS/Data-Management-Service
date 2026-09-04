---
jira: TBD
source_spike: DMS-1413
depends_on: 02, DMS-1498, DMS-1499
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
- Async find/search return `202 Location`, and following the returned `Location` polls to incomplete and complete results.
- `Incomplete` from any operation except results returns provider-contract-violation `502`.
- Tokens that need escaping round-trip to the provider unchanged.
- Exact `.` and `..` tokens return `502` with no `Location`.
- A fixture-plugin token that exceeds the escaped length ceiling, and one that fits the ceiling but overflows the composed poll path under a tenant and route qualifiers, both return `502` with no `Location` rather than a `202`.
- Every `202 Location` the fixture plugin produces is followed and reaches the results route, proving the emitted URL is fetchable rather than rejected by the host before routing.
- The fixture plugin binds each async job to the issuing `Tenant`, `RouteQualifiers`, and `ClientId`, and a token redeemed under a different tenant, qualifier set, or client returns identity-not-found `404` rather than the original job.
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
- Two replacing plugins abort startup with both plugin names in the fatal diagnostic.

## Tasks

1. Add the registry entry after the plugin foundation exists.
2. Add cardinality, assembly-name, runtime image assembly-version, and duplicate-replacement startup tests.
3. Build the fixture plugin against packed `EdFi.Api.Identity` and `EdFi.Api.Plugins`.
4. Add integration tests using test doubles where plugin loading is not required.
5. Add Docker-stack E2E tests once the plugin loader exists.
6. Assert served OpenAPI schemas validate fixture success payloads, including custom properties.
7. Add implementer documentation hooks for `Add` versus `TryAdd`.
