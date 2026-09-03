---
jira: TBD
source_spike: DMS-1413
depends_on: 01-07, DMS-1501
---

# Story: Publish `EdFi.Api.Identity`

## Description

Publish the identity contract package only after the DMS-owned API, plugin registry integration, fixture proof, and documentation are complete.
Publishing burns the package id and makes the public contract additive-only.

## Acceptance Criteria

- The publish lane includes `EdFi.Api.Identity`.
- Publish behavior is publish-when-absent, skip-when-unchanged, and fail-when-changed.
- The comparison covers exported public types, XML documentation, and nuspec dependencies.
- SBOM and provenance artifacts are produced consistently with other DMS packages.
- Release promotion includes the identity package.
- A scratch consumer compiles against the published package and implements all interface members.
- The package README and XML docs are included in the artifact.

## Tasks

1. Extend the package verification scripts.
2. Extend prerelease and release workflows.
3. Add scratch consumer verification.
4. Document the publication order and compatibility policy.
