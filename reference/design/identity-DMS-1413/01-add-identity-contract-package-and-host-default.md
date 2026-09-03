---
jira: TBD
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
- `IdentityResultStatus` has only `Success`, `Incomplete`, `InvalidProperties`, and `NotFound`; provider integration failures are reported by throwing exceptions, not by an extra status value.
- `IdentityResult` has no request-token member.
- Only `FindAsync` and `SearchAsync` return `IdentityAsyncResult`.
- XML documentation states request body expectations, payload obligations, result invariants, async token rules, route context, and cancellation behavior.
- The package declares its own public DTOs and does not expose internal DMS Core types.
- The project declares its own `Version`, `AssemblyVersion`, and `FileVersion`, initially `1.0.0`, independent of the DMS release version.
- Package assertions prove the package version and contained assembly version match the contract's declared version.
- Runtime image assembly-version proof is intentionally deferred to the registry story that depends on DMS-1499's Docker stamping removal.
- `NoIdentityService` is registered as the DMS host default and declares no capabilities.
- The project is added to the DMS solution with a committed `packages.lock.json`.
- `src/dms/Dockerfile` has the new project in both explicit copy blocks needed for locked restore and source copy.
- No plugin registry entry or startup loading integration is claimed by this story.

## Tasks

1. Add the contract project and public types.
2. Add package metadata and packed README placeholder.
3. Add host default implementation.
4. Add solution, lock-file, and Docker build-lane entries.
5. Add contract package assertions for exported types, XML docs, package version, assembly version, and release-version independence.
