# OWASP Analysis Summary — DMS-414

## Overview

This summary documents the OWASP attack path coverage achieved in branch `DMS-414` for Ed-Fi Data Management Service (DMS) and Configuration Service (CMS). It identifies which scenarios are covered by tests, which remain uncovered, and whether gaps are due to missing tests or require code changes.

---

## ZAP Analysis Summary

- **Report:** ZAP 2.17.0 scan (generated 2026-02-27).
- **Top findings:**
  - Multiple SQL Injection alerts across many endpoints (automated boolean/tautology payloads observed against GET query parameters and POST fields). Example targets include `descriptorMappings`, `educationContents`, `people`, `students` and various collection endpoints.
  - These findings were detected via response manipulation checks (boolean conditions, payload comparisons) and require manual triage to confirm true positives versus scanner false positives.
    - Added E2E confirmation: targeted E2E checks show these alerts are false positives for the tested endpoints — the APIs reject injected payloads and do not permit SQL injection.
- **Other scan insights:**
  - High percentage of 4xx responses during crawling and widespread use of `application/problem+json` for error payloads.
  - ZAP reported operational warnings (memory/log warnings) during the run; these are environment/scan notes rather than direct app vulnerabilities.

## Covered OWASP Scenarios (Validated by E2E Tests)

| OWASP Category | Scenario | Service | Status | Notes | Official Documentation |
|---|---|---|---|---|---|
| [API1/BOLA/IDOR](https://owasp.org/API-Security/editions/2023/en/0xa1-broken-object-level-authorization/) | Cross-org GET/PUT/DELETE | DMS | Covered | GET/PUT/DELETE denied for unauthorized org; CMS scope-based only | [API1: Broken Object Level Authorization](https://owasp.org/API-Security/editions/2023/en/0xa1-broken-object-level-authorization/) |
| [API1/BOLA](https://owasp.org/API-Security/editions/2023/en/0xa1-broken-object-level-authorization/) | Cross-tenant read/write denied | DMS | Covered | Instance routes block cross-tenant GET/POST (404) | [API1: Broken Object Level Authorization](https://owasp.org/API-Security/editions/2023/en/0xa1-broken-object-level-authorization/) |
| [API2/Broken Authentication](https://owasp.org/API-Security/editions/2023/en/0xa2-broken-authentication/) | Missing/invalid JWT rejected | DMS | Covered | Missing token returns 401; manipulated signature returns 401 | [API2: Broken Authentication](https://owasp.org/API-Security/editions/2023/en/0xa2-broken-authentication/) |
| [API2/Broken Authentication](https://owasp.org/API-Security/editions/2023/en/0xa2-broken-authentication/) | Expired JWT rejected | DMS | Covered | Expired token returns 401 (`06a Expired JWT is rejected`) | [API2: Broken Authentication](https://owasp.org/API-Security/editions/2023/en/0xa2-broken-authentication/) |
| [API5/Broken Function Level Authorization](https://owasp.org/API-Security/editions/2023/en/0xa5-broken-function-level-authorization/) | Read-only token cannot write admin endpoint | CMS | Covered | Read-only scope cannot POST admin resources (403) | [API5: Broken Function Level Authorization](https://owasp.org/API-Security/editions/2023/en/0xa5-broken-function-level-authorization/) |
| [API8/Security Misconfiguration](https://owasp.org/API-Security/editions/2023/en/0xa8-security-misconfiguration/) | SQLi in query string | DMS/CMS | Covered | Numeric and string fields, query and JSON body | [API8: Security Misconfiguration](https://owasp.org/API-Security/editions/2023/en/0xa8-security-misconfiguration/) |
| [API8/Security Misconfiguration](https://owasp.org/API-Security/editions/2023/en/0xa8-security-misconfiguration/) | SQLi in JSON body | DMS/CMS | Covered | Numeric and string fields, JSON body validation | [API8: Security Misconfiguration](https://owasp.org/API-Security/editions/2023/en/0xa8-security-misconfiguration/) |
| [OWASP CSRF](https://owasp.org/www-community/attacks/csrf) | Forged browser/cookie POST | DMS/CMS | Covered | Unauthenticated POST with Origin/Cookie/Form fails with 401 | [Cross-Site Request Forgery (CSRF)](https://owasp.org/www-community/attacks/csrf) |
| [API8/Security Misconfiguration](https://owasp.org/API-Security/editions/2023/en/0xa8-security-misconfiguration/) | Path traversal | DMS | Covered | `/../appsettings.json` returns 404, not file | [API8: Security Misconfiguration](https://owasp.org/API-Security/editions/2023/en/0xa8-security-misconfiguration/) |
| [API8/Security Misconfiguration](https://owasp.org/API-Security/editions/2023/en/0xa8-security-misconfiguration/) | Forwarded header spoofing | DMS/CMS | Covered | X-Forwarded-Host/Proto/Origin spoofing fails without valid token | [API8: Security Misconfiguration](https://owasp.org/API-Security/editions/2023/en/0xa8-security-misconfiguration/) |
| [API8/Security Misconfiguration](https://owasp.org/API-Security/editions/2023/en/0xa8-security-misconfiguration/) | CORS policy | DMS/CMS | Covered | Allowed/disallowed origins tested; preflight/TRACE tested | [API8: Security Misconfiguration](https://owasp.org/API-Security/editions/2023/en/0xa8-security-misconfiguration/) |
| [API8/Security Misconfiguration](https://owasp.org/API-Security/editions/2023/en/0xa8-security-misconfiguration/) | Malformed JSON error leak | DMS/CMS | Covered | No stack trace in error body; specific fragments checked | [API8: Security Misconfiguration](https://owasp.org/API-Security/editions/2023/en/0xa8-security-misconfiguration/) |
| [API1/BOLA](https://owasp.org/API-Security/editions/2023/en/0xa1-broken-object-level-authorization/) | Object-level auth denial | DMS | Covered | Explicit scenario for cross-org access denial | [API1: Broken Object Level Authorization](https://owasp.org/API-Security/editions/2023/en/0xa1-broken-object-level-authorization/) |
| [API4/Resource consumption](https://owasp.org/API-Security/editions/2023/en/0xa4-unrestricted-resource-consumption/) | Rate limiting | DMS | Partially covered | DMS has conditional rate limit; no E2E 429 test | [API4: Unrestricted Resource Consumption](https://owasp.org/API-Security/editions/2023/en/0xa4-unrestricted-resource-consumption/) |

---

## Uncovered or Partially Covered Scenarios

| Scenario | OWASP | Reason | Gap Type | Recommendation | Official Documentation |
|---|---|---|---|---|---|
| Security headers (nosniff, Referrer-Policy, X-Frame-Options, HSTS, CSP) | [API8](https://owasp.org/API-Security/editions/2023/en/0xa8-security-misconfiguration/) | Not set in app code; E2E scenario is ignored as a known gap | Code gap | Add response-header middleware or document proxy requirement | [API8: Security Misconfiguration](https://owasp.org/API-Security/editions/2023/en/0xa8-security-misconfiguration/) |
| DMS rate limiting | [API4](https://owasp.org/API-Security/editions/2023/en/0xa4-unrestricted-resource-consumption/) | No E2E test for 429 | Test gap | Add E2E test in low-limit environment | [API4: Unrestricted Resource Consumption](https://owasp.org/API-Security/editions/2023/en/0xa4-unrestricted-resource-consumption/) |
| Forwarded headers trust | [API8](https://owasp.org/API-Security/editions/2023/en/0xa8-security-misconfiguration/) | Accepts forwarded headers from any network/proxy when enabled | Code gap | Restrict to known proxy CIDR ranges | [API8: Security Misconfiguration](https://owasp.org/API-Security/editions/2023/en/0xa8-security-misconfiguration/) |
| Content-Type enforcement (DMS) | [API8](https://owasp.org/API-Security/editions/2023/en/0xa8-security-misconfiguration/) | Explicit non-JSON content type is a known gap (ignored E2E scenario expects 415) | Code gap | Enforce media types; ensure 415 response | [API8: Security Misconfiguration](https://owasp.org/API-Security/editions/2023/en/0xa8-security-misconfiguration/) |
| Oversized body handling (DMS) | [API4](https://owasp.org/API-Security/editions/2023/en/0xa4-unrestricted-resource-consumption/) | Known gap is tracked via ignored E2E scenario (expects 413 for >11MB) | Test gap | Stabilize behavior, then un-ignore scenario | [API4: Unrestricted Resource Consumption](https://owasp.org/API-Security/editions/2023/en/0xa4-unrestricted-resource-consumption/) |
| Property-level auth | [API3](https://owasp.org/API-Security/editions/2023/en/0xa3-broken-object-property-level-authorization/) | No explicit enforcement mechanism or tests | Code + test gap | Add enforcement and tests (redaction/deny writes) | [API3: Broken Object Property Level Authorization](https://owasp.org/API-Security/editions/2023/en/0xa3-broken-object-property-level-authorization/) |
| Replayed JWTs | [API2](https://owasp.org/API-Security/editions/2023/en/0xa2-broken-authentication/) | No E2E coverage for token replay; depends on IdP/session design in the test stack | Test gap | Add explicit replay scenarios if the IdP supports revocation/one-time tokens | [API2: Broken Authentication](https://owasp.org/API-Security/editions/2023/en/0xa2-broken-authentication/) |
| RequireHttpsMetadata | [API2](https://owasp.org/API-Security/editions/2023/en/0xa2-broken-authentication/) | Configurable; must be true in prod | Code gap | Document requirement; enforce in prod | [API2: Broken Authentication](https://owasp.org/API-Security/editions/2023/en/0xa2-broken-authentication/) |

---

## Ignored Scenarios (Documented Known Gaps)

- DMS Scenario 11: API responses include basic security headers (`@KnownSecurityGap @ignore`) - requires code or proxy config. [API8: Security Misconfiguration](https://owasp.org/API-Security/editions/2023/en/0xa8-security-misconfiguration/)
- CMS Scenario 11: API responses include basic security headers (`@KnownSecurityGap @ignore`) - requires code or proxy config. [API8: Security Misconfiguration](https://owasp.org/API-Security/editions/2023/en/0xa8-security-misconfiguration/)
- DMS Scenario 14: Explicit non-JSON content type should return 415 (`@KnownSecurityGap @ignore`) - requires code change. [API8: Security Misconfiguration](https://owasp.org/API-Security/editions/2023/en/0xa8-security-misconfiguration/)
- DMS Scenario 15: Oversized request body should return 413 (`@KnownSecurityGap @ignore`) - tracked for stabilization/verification. [API4: Unrestricted Resource Consumption](https://owasp.org/API-Security/editions/2023/en/0xa4-unrestricted-resource-consumption/)

---

## Test Additions and Improvements

- Deterministic header merge in custom-header steps (case-insensitive, scenario-specified header wins)
- Async/await in response body negative assertion steps (no sync-over-async)
- Hardened error-leak assertions (specific fragments)
- CSRF-style browser/cookie/form POST scenarios (DMS/CMS)
- SQLi literal-data scenarios (string fields)
- BOLA DELETE denial assertion (DMS)

---

## Build and Test Results

- DMS and CMS solutions build successfully
- All non-ignored OWASP scenarios pass in E2E tests
- Ignored scenarios are documented with rationale and tracking comments

---

## OWASP API Security Top 10 (2023) Summary

| OWASP Risk | Coverage | Notes |
|---|---|---|
| [API1: BOLA](https://owasp.org/API-Security/editions/2023/en/0xa1-broken-object-level-authorization/) | Covered | Cross-education-organization and cross-tenant access is denied in DMS E2E tests |
| [API2: Broken Authentication](https://owasp.org/API-Security/editions/2023/en/0xa2-broken-authentication/) | Partial | Missing/invalid and expiration covered; replay and anti-bruteforce not covered in E2E |
| [API3: Broken Object Property Level Authorization](https://owasp.org/API-Security/editions/2023/en/0xa3-broken-object-property-level-authorization/) | Uncovered | No explicit property-level enforcement or tests |
| [API4: Unrestricted Resource Consumption](https://owasp.org/API-Security/editions/2023/en/0xa4-unrestricted-resource-consumption/) | Partial | Rate limiter is conditional; no E2E 429 test; oversized body scenario is ignored |
| [API5: Broken Function Level Authorization](https://owasp.org/API-Security/editions/2023/en/0xa5-broken-function-level-authorization/) | Partial | CMS has a read-only scope negative test; DMS function-level matrix not fully exercised |
| [API6: Unrestricted Access to Sensitive Business Flows](https://owasp.org/API-Security/editions/2023/en/0xa6-unrestricted-access-to-sensitive-business-flows/) | Not targeted | No dedicated anti-automation controls; rate limiting is the closest mitigation |
| [API7: SSRF](https://owasp.org/API-Security/editions/2023/en/0xa7-server-side-request-forgery/) | Likely not applicable | No clear user-supplied URL fetch surface observed in DMS/CMS |
| [API8: Security Misconfiguration](https://owasp.org/API-Security/editions/2023/en/0xa8-security-misconfiguration/) | Partial | Strong coverage for CORS/error leaks/forwarded spoofing; security headers and content-type remain gaps |
| [API9: Improper Inventory Management](https://owasp.org/API-Security/editions/2023/en/0xa9-improper-inventory-management/) | Uncovered | No explicit tests for deprecated/undocumented endpoints or version exposure |
| [API10: Unsafe Consumption of APIs](https://owasp.org/API-Security/editions/2023/en/0xaa-unsafe-consumption-of-apis/) | Uncovered | Limited E2E coverage of upstream failure modes and timeouts/validation |

---

## Summary Table

| Scenario | Covered | Gap Type | Action Needed |
|---|---|---|---|
| SQLi (query/body) | Yes | — | — |
| CSRF (Origin/Cookie/Form) | Yes | — | — |
| Path traversal | Yes | — | — |
| Forwarded header spoofing | Yes | — | — |
| CORS policy | Yes | - | - |
| Malformed JSON error leak | Yes | - | - |
| Broken authentication (missing/tampered JWT) | Yes | - | - |
| Function-level auth (CMS read-only) | Yes | - | - |
| Security headers | No | Code | Add middleware or proxy config |
| Rate limiting | Partial | Test | Add E2E 429 test |
| Content-Type enforcement | No | Code | Add media-type constraint |
| Oversized body handling | Partial | Test | Stabilize/enable 413 E2E scenario |
| Tenant isolation | Yes | - | - |
| Property-level auth | No | Code/Test | Add enforcement + tests |
| RequireHttpsMetadata | Partial | Code | Enforce in prod |

---

## Conclusion

OWASP attack path coverage in DMS-414 is strong for BOLA denial, missing/tampered token rejection, SQL injection payload handling, CSRF-style unauthenticated browser requests, forwarded header spoofing, CORS behavior, and error-leak prevention. Remaining gaps are either documented as known issues (ignored scenarios) or require additional tests and/or code changes (notably security headers, content-type enforcement, rate limiting and payload size tests, tenant/property-level authorization, and deeper OWASP API Security Top 10 areas).
