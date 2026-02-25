# Postman vs Current DMS Profiles: Implementation-Relevant Differences

Date: 2026-02-24
Scope: Profile-related scenarios mapped in `ProfilesTestParityComparison.md` and validated through current E2E behavior.

## Purpose

This document captures behavior differences between legacy ODS Postman expectations and current DMS/API behavior, so product owners can decide whether to:

1. Keep current behavior and update parity expectations/docs, or
2. Change implementation to restore Postman-aligned behavior.

## Summary

- Scenario presence parity is complete (Postman scenarios are mapped in E2E).
- Remaining gaps are behavior/contract differences, not missing tests.
- Current profile E2E execution status: `Passed 139, Failed 0` for `FullyQualifiedName~Profile`.

## Differences That May Require Implementation Decisions

| Area | Postman-era expectation | Current DMS observed behavior | Impact | Recommended decision |
|---|---|---|---|---|
| Response content type for profile reads | Profile-specific vendor media type in response | `application/json; charset=utf-8` | Contract mismatch for clients expecting profile media type in response headers | Decide canonical response contract and enforce consistently |
| Media type parameter handling (`charset`, `q`) on profile requests | Historically permissive in some legacy expectations | Returns invalid profile usage (`400`) | Potential incompatibility with older clients/tests | Confirm strict parsing policy; document as breaking/intentional if kept |
| Embedded object validation (authors empty array) | Expected validation failure (`400`) | Request accepted (`200`) | Validation semantics drift; possible data quality concern | Decide whether to restore stricter validation or update contract/docs |
| Unsupported embedded/extension profile names | Legacy suite implied filtering behavior with named profiles | Returns `406/415 invalid-profile-usage` for unsupported names | Feature-support mismatch vs historical expectations | Decide support roadmap for these profile names or formally deprecate |
| Creatability validation scenarios (specific write cases) | Expected `400` for non-creatable/invalid payload paths | Returns `201` in current behavior (scenarios aligned in E2E) | May allow writes that legacy policy considered invalid | Confirm intended creatability enforcement and align API + docs |
| Assigned profile + standard content type behavior | Some Postman paths imply strict profile-only usage on covered resources | Covered resource reads/writes with standard `application/json` are currently allowed in tested scenarios | Authorization semantics differ from legacy assumption | Decide whether to enforce stricter profile-only behavior or document current behavior |
| Multi-assigned wrong profile selection | Legacy expectation often represented as authorization failure (`403`) | Returns `400` with `urn:ed-fi:api:profile:invalid-profile-usage` in tested scenario | Error classification/status mismatch for clients and tests | Decide canonical status/error contract and align tests/docs |

## Classification

### Product/API Policy Gaps

These require product decisions before code changes:

- Response `Content-Type` contract for profile requests.
- Validation strictness for embedded object edge payloads.
- Support stance for unsupported embedded/extension profile names.
- Expected status codes for creatability validation edge cases.

### Test Harness Gaps (Already Addressed)

These were test infrastructure issues, not product behavior differences:

- Missing seed prerequisites (for example `schoolYearTypes`) causing setup failures.
- Claimset permissions used in setup for ESC/LEA creation.
- JSON path step support for array-index navigation.

### Implementation Description

1. **Assigned profile enforcement (covered resources)**

- Locate the profile assignment enforcement path in DMS request handling where Content-Type/Accept is interpreted against assigned profiles.
- For resources covered by assigned profiles:
  - `GET` with `Accept: application/json` must fail.
  - `POST/PUT` with `Content-Type: application/json` must fail.
- Keep current behavior for not-covered resources (standard content type should remain allowed).

1. **Multi-assigned wrong profile selection contract**

- For requests using a profile-specific media type not valid for the covered resource under assigned profiles, return:
  - HTTP status: `403`
  - Error type: `urn:ed-fi:api:security:data-policy:incorrect-usage`
- Ensure this is consistent for read and write paths where applicable.

1. **Creatability enforcement**

- Reinstate validation so profile definitions that exclude required elements for creating:
  - child collection items, or
  - embedded objects
  cause create requests to fail with `400`.
- Ensure validation failure surfaces via the expected data-policy/validation problem details path used by existing creatability scenarios.

### Related Postman Tests

1. Multi-assigned wrong profile selection

- `Assigned profiles > Assigned profiles must be used for covered resources > Read Requests > Covered resource with different Profile's content type`
  Equivalent E2E: `ProfileAssignedProfiles.feature` Scenario `02 Covered resource with different profile content type fails` (single-assigned baseline).
- `Assigned profiles > Assigned profiles must be used for covered resources > Write Requests > Covered resource with different Profile's content type`
  Equivalent E2E: `ProfileAssignedProfiles.feature` Scenario `04 Covered resource write with different profile content type fails` (single-assigned baseline).
- `Assigned profiles > Assigned profiles must be used for covered resources > Read Requests > Covered resource with different Profile's content type for API client with several assigned profiles`
  Equivalent E2E: `ProfileAssignedProfiles.feature` Scenario `07 Covered resource with different profile content type for API client with several assigned profiles fails with invalid-profile-usage` (multi-assigned).
- `Assigned profiles > Assigned profiles must be used for covered resources > Write Requests > Covered resource with different Profile's content type for API client with several assigned profiles`
  Equivalent E2E: No dedicated multi-assigned write-wrong-profile scenario yet (candidate to add; currently inferred from Scenario 04 + multi-assigned read Scenario 07).
- Source file anchor: [Ed-Fi ODS-API Profile Test Suite.postman_collection.json](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/main/Postman%20Test%20Suite/Ed-Fi%20ODS-API%20Profile%20Test%20Suite.postman_collection.json) (search around lines ~`8234`, `9051`, `9576`, `10213`).

1. Assigned profile + standard content type behavior (covered resources)

- `Assigned profiles > Assigned profiles must be used for covered resources > Read Requests > Covered resource with standard content type specified for API client with one assigned profile > Get School`
  Equivalent E2E: `ProfileAssignedProfiles.feature` Scenario `05 Covered resource with standard content type and one assigned profile is currently allowed`.
- `Assigned profiles > Assigned profiles must be used for covered resources > Read Requests > Covered resource with standard content type specified for API client with multiple assigned profiles > Get School`
  Equivalent E2E: `ProfileAssignedProfiles.feature` Scenario `11 Covered resource with standard content type and several assigned profiles is currently allowed`.
- `Assigned profiles > Assigned profiles must be used for covered resources > Write Requests > Covered resource with standard content type specified for API client with one assigned profile`
  Equivalent E2E: `ProfileAssignedProfiles.feature` Scenario `09 Covered resource write with standard content type and one assigned profile is currently allowed`.
- `Assigned profiles > Assigned profiles must be used for covered resources > Write Requests > Covered resource with standard content type specified for API client with several assigned profiles`
  Equivalent E2E: `ProfileAssignedProfiles.feature` Scenario `10 Covered resource write with standard content type and several assigned profiles is currently allowed`.
- Source file anchor: [Ed-Fi ODS-API Profile Test Suite.postman_collection.json](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/main/Postman%20Test%20Suite/Ed-Fi%20ODS-API%20Profile%20Test%20Suite.postman_collection.json) (search around lines ~`8485`, `8580`, `9079`, `9167`).

1. Creatability validation scenarios (specific write cases)

- `Data Policy Enforcement > Detecting Profile usage where resource items cannot be created > Create School (Profile prevents creation)`
  Equivalent E2E: `ProfileCreatabilityValidation.feature` Scenario `01 POST with profile excluding required scalar field returns 400 with data-policy-enforced error` (resource-level create prevention contract).
- `Data Policy Enforcement > Detecting Profile usage when creating collection items is prevented > Create School (Profile prevents creation of child collection item)`
  Equivalent E2E: `ProfileCreatabilityValidation.feature` Scenario `07 Profile with non-creatable child collection rule still allows creation` (current behavior divergence target).
- `Data Policy Enforcement > Detecting Profile usage when creating child embedded object is prevented > Create Assessment (Profile prevents creation of embedded object)`
  Equivalent E2E: `ProfileCreatabilityValidation.feature` Scenario `09 Profile with non-creatable embedded object rule still allows creation` (current behavior divergence target).
- Source file anchor: [Ed-Fi ODS-API Profile Test Suite.postman_collection.json](https://github.com/Ed-Fi-Alliance-OSS/Ed-Fi-ODS/blob/main/Postman%20Test%20Suite/Ed-Fi%20ODS-API%20Profile%20Test%20Suite.postman_collection.json) (search around lines ~`14602`, `14651`, `14967`).

## Ticket Backlog Candidates

1. `Profiles API Contract`: Decide and codify response `Content-Type` behavior for profile reads.
2. `Profiles Validation`: Resolve `contentStandard.authors: []` expected behavior (`400` vs `200`).
3. `Profiles Support Matrix`: Define supported vs unsupported embedded/extension profile names and expected status codes.
4. `Profiles Creatability`: Reconcile creatability validation outcomes with intended policy for currently-accepted writes.
5. `Docs/Compatibility Note`: Publish migration note for any intentional deviations from legacy Postman expectations.

## Suggested Exit Criteria Per Decision

- Contract decision documented in API docs and enforced by E2E assertions.
- Status code expectations fixed and validated in both feature files and CI runs.
- Unsupported profile handling explicitly documented with examples.
- No ambiguous parity failures: each divergence labeled as either `intentional` or `defect`.
