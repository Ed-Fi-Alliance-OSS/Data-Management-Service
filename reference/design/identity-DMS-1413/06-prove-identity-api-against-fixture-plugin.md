---
jira: TBD
source_spike: DMS-1413
depends_on: 04, 05
---

# Story: Prove Identity End-to-End Against a Fixture Plugin

## Description

Use a fixture identity plugin to prove the DMS-owned HTTP surface and plugin-owned backend work together through the actual plugin loader.

## Acceptance Criteria

- A fixture plugin replaces `IIdentityService` through the DMS-1462 plugin path.
- Sync create/get-by-id/find/search/results flows succeed over HTTP.
- Async find/search return `202 Location`, and following the returned `Location` polls to incomplete and complete results.
- `Incomplete` from any operation except results returns provider-contract-violation `502`.
- Tokens that need escaping round-trip to the provider unchanged.
- Exact `.` and `..` tokens return `502` with no `Location`.
- Custom properties pass through request and response payloads.
- Standard identifying attributes and unsupported-as-null semantics appear in success responses.
- Search scores are present on returned search matches and are passed through without DMS inspection.
- Unsupported capability returns operation-unsupported `404`.
- Find/search no-match returns successful response groups with empty `Responses` arrays.
- Provider `NotFound` returns identity-not-found `404`, distinct from unsupported capability.
- Enabled with no plugin starts cleanly and answers operation-unsupported `404`.
- Duplicate-property, malformed-body, wrong-shape, invalid find/search array-entry, unsupported-media-type, provider `InvalidProperties`, missing-payload, provider-contract-violation, and provider-exception upstream-failure cases are covered.
- Two replacing plugins abort startup with both plugin names in the fatal diagnostic.

## Tasks

1. Build the fixture plugin against packed `EdFi.Api.Identity` and `EdFi.Api.Plugins`.
2. Add integration tests using test doubles where plugin loading is not required.
3. Add Docker-stack E2E tests once the plugin loader exists.
4. Assert served OpenAPI schemas validate fixture success payloads, including custom properties.
