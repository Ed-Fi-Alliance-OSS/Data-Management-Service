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
