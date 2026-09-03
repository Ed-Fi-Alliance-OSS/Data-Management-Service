---
jira: TBD
source_spike: DMS-1413
depends_on: 01
---

# Story: Add the Identity Core Pipeline and Response Mapping

## Description

Add the Core fixed-service pipeline that brokers identity calls to `IIdentityService`.
The pipeline is DMS-owned and does not resolve datastore, ApiSchema, backend mappings, profile resolution, or fingerprints.

## Acceptance Criteria

- Core exposes five identity facade methods with trailing `CancellationToken`.
- JSON-body operations compose request logging, exception logging, tenant syntax validation, JWT authentication, tenant existence validation, service-claim authorization, capability validation, baseline-JSON content-type validation, body parsing, duplicate-property rejection, and the identity handler.
- GET operations compose the same initial authorization, tenant, and capability steps, then the identity handler.
- Tenant existence runs after JWT authentication and before service-claim authorization.
- A valid token plus nonexistent tenant returns `404` without calling `IClaimSetProvider` or `IIdentityService`.
- Service-claim authorization uses the shipped identity service claim and requires `Create` for create and `Read` for all other operations.
- Capability validation runs after service-claim authorization and before POST content-type or body validation.
- Enabled with no plugin returns operation-unsupported `404` for all five operations, even when a POST body would otherwise fail request validation.
- `ResolveDataStoreMiddleware` is not in the identity pipeline; clients with no authorized datastore can still call identity endpoints when the identity service claim permits it.
- Route qualifiers are passed to `IdentityRequestContext` and are not matched against datastore authorization.
- DMS rejects unsupported media type, malformed JSON, empty body, duplicate property names, wrong top-level body shapes, non-string find array entries, and non-object search array entries before provider invocation.
- Result status and invariant mapping follows `design.md`.
- Provider `NotFound` for get-by-id subject miss, results token miss, or provider-owned context refusal maps to identity-not-found `404`, distinct from operation-unsupported, tenant-not-found, and route-miss `404`.
- Find/search no-match remains a successful `IdentitySearchResponse` with an empty `Responses` array, not provider `NotFound`.
- `Incomplete` from any operation except `ResultsAsync` is provider contract misuse.
- Provider calls are wrapped by one provider-only exception boundary; cancelled `OperationCanceledException` is rethrown and un-cancelled provider exceptions map to identity-upstream-failure `502`.
- Provider contract misuse cases map to provider-contract-violation `502`, distinct from identity-upstream-failure `502`.
- Provider `IdentityError` values are projected only for `InvalidProperties`; upstream failure diagnostics are logged and not returned to clients.
- Async token usability includes the `/`, `\`, control-character, exact `.`/`..`, blank, and escape/unescape round-trip checks.

## Tasks

1. Add the tenant-existence middleware using the existing `IDataStoreProvider` cache/reload behavior.
2. Add service-claim authorization for fixed service claims.
3. Parameterize content-type validation without changing resource write behavior.
4. Add the identity handler and response mapping.
5. Add unit tests for ordering, capability gating, body handling, token handling, cancellation, and status mapping.
