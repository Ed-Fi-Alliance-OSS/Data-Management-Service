---
jira: DMS-1514
jira_url: https://edfi.atlassian.net/browse/DMS-1514
epic: DMS-1412
source_spike: DMS-1413
---

# Story: Add the Identity Contract Package and Host Default

## Description

Create the public contract an implementer compiles against to provide Identity Management behavior.
The package is `EdFi.Api.Identity`, built from `src/dms/core/EdFi.DataManagementService.Identity/` under namespace `EdFi.DataManagementService.Identity`.

This story ships only the contract and the DMS host default.
It does not add HTTP endpoints, pipeline behavior, OpenAPI metadata, plugin-registry integration, or package publishing.

## Acceptance Criteria

- `IIdentityService`, `IdentityCapabilities`, `IdentityResultStatus`, `IdentityResult`, `IdentityAsyncResult`, `IdentityRequestContext`, and `IdentityError` exist as public types.
- `IdentityResultStatus` contains `Success`, `Incomplete`, `InvalidProperties`, `NotFound`, and `JobFailed`. `Incomplete` and `JobFailed` are results-only; `JobFailed` means an accepted job definitively failed, while exceptions from operation calls mean an answer could not be obtained.
- `IdentityResult` has no request-token member.
- Only `FindAsync` and `SearchAsync` return `IdentityAsyncResult`.
- `IdentityRequestContext` carries a required `ClientId` alongside `Tenant`, `RouteQualifiers`, and `TraceId`, so a provider can scope an async job to the client that created it.
- XML documentation states request body expectations, payload obligations, result invariants, async token rules, route context, and cancellation behavior.
- XML documentation requires wire `Status: "Complete"` for synchronous find/search payloads; pending find/search returns `Success` with a usable token and no payload. Wire `Incomplete` is reserved for results polling, without adding host runtime payload validation.
- XML documentation states the async job obligations: bind each job to the request's `Tenant`, `RouteQualifiers`, and `ClientId`; treat a mismatched poll as `NotFound`; document retention, repeated-poll, terminal-failure, multi-replica, and restart behavior; and that request cancellation does not cancel an accepted job.
- Client binding is mandatory for conforming providers, including those whose upstream shares results within a tenant; documenting a deviation cannot waive it.
- XML documentation requires provider-owned namespace authorization for `ClientId` on every operation before identity lookup/search/issuance or job acceptance. Missing/denied grants and unknown namespaces return `NotFound`; policy-source failure cannot grant access and follows the sanitized operation-failure path. Providers document the policy source, administration, permitted operations, and cache/revocation behavior. Tenant-wide access requires an explicit broad grant; it never waives async job ownership. No datastore entitlement or new DMS policy API is assumed.
- XML documentation pins context equality: tenant names and qualifier names/values use `OrdinalIgnoreCase`, qualifier maps compare as sets independent of order, null tenant equals only null, and `ClientId` uses case-sensitive `Ordinal`. Preserve passed spelling; no trimming, culture-dependent casing, or additional Unicode normalization. Equivalent tenant/qualifier contexts select the same namespace, while async ownership always checks the full issuing context even if other contexts share that namespace. Do not apply these rules to UniqueIds or job tokens.
- XML documentation requires an inexpensive, I/O-free `Capabilities` getter whose value is stable across requests/provider instances for the configured deployment; upstream availability does not change it. DMS captures its value once per request for both capability checks.
- XML documentation defines `JobFailed` as results-only, with no required payload and ignored supplied payload/errors. It maps to the sanitized terminal-job `502` problem, not a success-payload status. Repeat authorized polls return the same terminal classification while the record is retained and accessible; mismatched ownership and expiry return `NotFound`. A transient poll exception preserves job retrievability and does not establish terminal state or make resubmission safe.
- XML documentation distinguishes sanitized request-time provider activation/getter `500`s from operation-call upstream `502`s, preserves request cancellation, and restricts host logging of full exceptions/messages to `Debug`. The host cannot enforce a trusted plugin's own logging behavior.
- XML documentation states that `TraceId` is a correlation value and is not an ownership, cache, or idempotency key.
- XML documentation states that DMS applies no timeout to and never retries an identity provider call, and that a provider must document its repeated-create behavior and recovery workflow: treat lost responses as unknown issuance, reconcile through a documented authoritative lookup if available, otherwise stop for operator/upstream reconciliation. A demographic match or unqualified no-match is not proof that retry is safe.
- XML documentation states the UniqueId issuance constraints: fitting the `maxLength` the deployment's ApiSchema declares for person UniqueIds, which is 32 characters across the current core and shipped extension schemas; the guaranteed repertoire of ASCII letters and digits; uniqueness and `OrdinalIgnoreCase` distinctness within the provider-documented identity namespace; non-empty with no surrounding whitespace; a single URL path segment; and stability for the life of the identity.
- XML documentation requires the provider to document the tenant/qualifier-to-authority namespace mapping. Independent namespaces may reuse ids; namespaces combined into the same person-resource natural-key domain must be compatible. No deployment-wide namespace, host remapping, or DMS-1414 person-write implementation is implied.
- The length constraint is documented as a rule about the deployment's ApiSchema with 32 named as its current value, not as a contract constant, because the identity package versions independently of the DMS release and of the Data Standard.
- XML documentation calls out that a canonical 36-character GUID does not fit the current limit while a 32-character hyphen-free GUID does.
- XML documentation states that values outside the guaranteed repertoire are not rejected by DMS but move the uniqueness obligation to the provider under each backing store's actual equality, because `OrdinalIgnoreCase` is an approximation of the SQL Server identity collation rather than an emulation of it.
- XML documentation on `IdentityError.Path` states that the value is JSONPath rooted at `$`, that an item in a find or search array is addressed as `$[n].property`, and that a null or blank path routes the message to the response's `errors` collection.
- XML documentation states the async request-token limits: an escaped length ceiling of 1024 characters, and that DMS additionally refuses a token whose composed poll path does not fit the deployment's request-line budget.
- XML documentation states that all three service lifetimes are supported, that DMS resolves the provider once per request from its own scope and never disposes it directly, and that a provider must be safe for concurrent calls across requests.
- The package declares its own public DTOs and does not expose internal DMS Core types.
- The project declares its own `Version`, `AssemblyVersion`, and `FileVersion`, initially `1.0.0`, independent of the DMS release version.
- Package assertions prove the package version and contained assembly version match the contract's declared version.
- Runtime image assembly-version proof is intentionally deferred to the registry story that depends on DMS-1499's Docker stamping removal.
- `NoIdentityService` is registered as the DMS host default and declares no capabilities.
- The project is added to the DMS solution with a committed `packages.lock.json`.
- `src/dms/Dockerfile` has the new project in both explicit copy blocks needed for locked restore and source copy.
- `src/dms/core/EdFi.DataManagementService.Core/EdFi.DataManagementService.Core.csproj` takes the `ProjectReference` on the contract project, matching how it already references `EdFi.DataManagementService.CustomValidation`, so the contract assembly ships in the image. That reference and the `src/dms/Dockerfile` copy-list entries land together in this story.
- No plugin registry entry or startup loading integration is claimed by this story.

## Tasks

1. Add the contract project and public types.
2. Add package metadata and packed README placeholder.
3. Add host default implementation.
4. Add solution, lock-file, and Docker build-lane entries.
5. Add contract package assertions for exported types, XML docs, package version, assembly version, and release-version independence.
