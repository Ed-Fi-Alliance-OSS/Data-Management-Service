# Code Review: DMS-1491 — OAuth review finding 4 (Medium): Token endpoint error responses are not OAuth-conformant

**Branch:** DMS-1491
**Base:** main (isolated diff `b187ae855..4db9790cd`; the merged DMS-1625 #1412 and DMS-1606 #1385 commits are excluded)
**Date:** 2026-10-29
**Commits reviewed:** 9
**Round:** 2 (Round 1's six findings are all fixed and were re-checked in place)

---

| Category           | Resume                                                                             | High  | Medium | Low   |
|--------------------|------------------------------------------------------------------------------------|-------|--------|-------|
| Correctness        | An undecodable escape in a provider error body makes the token answer `500`        | 0     | 0      | 1     |
| Test coverage gaps | The service-owned failure fixture cannot tell the manager's text from the fixed text | 0     | 0      | 1     |
| Simplification     | Seven migrated legacy token fixtures repeat the new `ManagerOutcomeRequest` ones   | 0     | 0      | 1     |
| **Total**          |                                                                                    | **0** | **0**  | **3** |

Design-doc gate: 12 design docs changed, all justified.
- `CS-AUTH.md`, `PRD-CS-v8.1.md`, `CONFIGURATION.md`, `DOCKER.md` and `OWASP-AUTH-COVERAGE.md`: AC1–AC3 move `/connect/token`, and the `/connect/*` 429 on token, revoke and introspect, to RFC 6749 bodies. `PRD-CS-v8.1` also adds `/connect/introspect`, which was already OAuth.
- `PRD-CS-v8.2.md`: the `/connect/register` exclusion cites the PO decision (comment 100107).
- `DMS-1482-cms-connect-rate-limiting.md`: its D1 hands the throttle format to DMS-1491, and AC1 mandates the OAuth body.
- `DMS-1218`, `DMS-1327`, `DMS-1586` and the `DMS-1490-timing-evidence` README: superseded notes on artifacts this ticket removes (the token-only `FailureResults` helpers, `ForTooManyTokens`, the 401 problem-details answer).
- `adr-oauth-upstream-error-disclosure.md`: an additive update for AC4's new 429 envelope.

---

## Correctness risks / gaps

### 1. An undecodable escape in a provider error body makes the token answer `500` (Low)

**What's wrong:** `IdentityProviderTokenFailure` parses the provider body before it dispatches (`IdentityModule.cs:445`), through `TryReadProviderError` (`:490`) into `FailureResults.TryParseProviderError`. That parser catches only `JsonException` (`FailureResults.cs:138`), then calls `GetValue<string>()` on `error` and `error_description` (`:153-154`). When a JSON string holds an unpaired-surrogate escape (`"\uD800"`), `JsonNode.Parse` succeeds and `GetValue<string>()` throws `InvalidOperationException`. The exception escapes the handler, and `GlobalExceptionHandler` answers `500 server_error` ("The token request could not be processed.").

**Evidence:**
- **Probe:** a verbatim copy of `TryParseProviderError` at the branch head, run as a standalone program. It throws "Cannot read incomplete UTF-16 JSON text as string with missing low surrogate" for each of these bodies: `{"error":"invalid_scope","error_description":"\uD800"}`, `{"error":"access_denied","error_description":"\uD800"}` and `{"error":"\uD800","error_description":"x"}`.
- **The contract it breaks**, all written by this branch:
  - `CS-AUTH.md:116-118`: a Keycloak 401 is always `invalid_client`.
  - `:121-125`: an unusable `invalid_scope` description takes the fixed text.
  - `:127`: any other provider failure is `502`.
- **Unneeded parse:** the `InvalidClient or Unauthorized` arm (`IdentityModule.cs:453`) never reads the parse result, but it still runs the parse and gets the 500.
- **Not a regression:** before the branch the same parser ran on every provider-failure arm, through `GetIdentityErrorDetails` (`b187ae855` `FailureResults.cs:209`), and the route answered a 500 problem-details body for the same input. This is the same class as Round 1's `OAuthManager.cs:695` finding.

**Impact:**
- **Likelihood is low.** Keycloak's JSON writer does not emit lone-surrogate escapes, so only a misbehaving provider, or something rewriting its responses, triggers this.
- **When it does:** a rejected credential reads as a server fault (`500` instead of `invalid_client`), and a scope rejection or provider failure reads as a CMS fault instead of `invalid_scope` or `502`.

**Recommendation:**
- **The fix:** in the branch-owned `TryReadProviderError` (`IdentityModule.cs:490`), catch `InvalidOperationException` and return `false`. All three arms then fall back to their fixed answers. `FailureResults.TryParseProviderError`'s remaining caller, `BadGateway`, keeps its current behavior.
- **The test:** add `"\uD800"` description rows for `InvalidScope` (400 with the fixed `invalid_scope` text) and `Unauthorized` (`invalid_client`). They take the same shape as Round 1's `Given_A_Deviating_OAuth_Envelope_429` row.

---

## Test coverage gaps

### 2. `Given_a_service_owned_authentication_failure` cannot tell the manager's description from the fixed one (Low)

**What's wrong:** `CS-AUTH.md:119` says "The description a service-owned failure carries is used as it is", and the handler passes `authenticationFailure.ErrorDescription` (`IdentityModule.cs:395-399`). Only one test asserts that description: `IdentityModuleTests.cs:1986` (`It_answers_invalid_client_with_the_manager_description`, `:1999`). It feeds the manager `"Invalid client or Invalid client credentials"`, which is the handler's own `InvalidClientCredentialsDescription`. Replacing `authenticationFailure.ErrorDescription` with that constant would leave every test green:
- The two fixtures that return a different description (`ResponseCachePolicyTests.cs:85`, `ConnectRateLimitRefreshTests.cs:80`) assert only the status and the cache headers.
- The real-manager fixtures and the E2E scenarios all produce the same constant text.

**Impact:** the documented pass-through is unpinned, and the test name claims a property its data cannot prove. AGENTS.md asks of each test: "would this fail if the feature were reverted?"

**Recommendation:**
- **Minimal fix:** give the fixture a description of its own, such as `"Service-owned failure description"`, and assert that text.
- **If the fixed text is the intent:** use `InvalidClientCredentialsDescription` at `IdentityModule.cs:398`, as the provider arm does, rename the test and drop `CS-AUTH.md:119`. That variant also makes DMS-1490's single answer an endpoint guarantee rather than one that depends on every manager using the same wording.

---

## Simplification / dead-code opportunities

### 3. Seven migrated legacy token fixtures repeat the new `ManagerOutcomeRequest` fixtures (Low)

**What's wrong:** the branch rewrote the legacy token fixtures to the OAuth contract. It also added the `OAuthEndpointErrorTests` `ManagerOutcomeRequest` family, which sends the same `TokenResult` and `IdentityProviderError` inputs through the same form-credential path and asserts a superset. The token handler reads no identity-provider setting (the only `AppSettings:IdentityProvider` read in `IdentityModule.cs` is the introspection metadata at `:93`), so running the legacy fixtures in self-contained mode adds nothing. These seven no longer add coverage:

| Legacy fixture | Already covered by |
|---|---|
| `TokenEndpointTests.When_error_from_backend` (`:756`) | `Given_the_token_manager_reports_an_unknown_failure(false)` (`:2008`) |
| `TokenEndpointTests.When_provider_is_unreacheable` (`:801`) | `Given_the_identity_provider_fails_in_a_way_the_caller_cannot_fix`, `"Unreachable"` form row (`:2337`) |
| `TokenEndpointTests.When_provider_has_invalid_realm` (`:854`) | the same fixture's `"NotFound"` form row |
| `TokenEndpointTests.When_provider_has_not_realm_admin_role` (`:909`) | `Given_the_identity_provider_rejects_the_client_credentials_without_a_structured_body(false)` (`:2173`): same `"Insufficient Permissions"` input, same assertions |
| `TokenEndpointTests.When_provider_has_bad_credetials` (`:953`) | `Given_the_identity_provider_rejects_the_client_credentials(false, "Unauthorized")` (`:2092`) |
| `TokenEndpointInvalidScopeTests.Given_the_identity_provider_rejects_the_request_with_invalid_request` (`:6774`) | `Given_the_identity_provider_fails_in_a_way_the_caller_cannot_fix`, `"Generic"` form row |
| `TokenEndpointInvalidScopeTests.Given_the_identity_provider_is_unreachable` (`:6795`) | its `"Unreachable"` form row |

**Impact:** about 270 lines of a 7,140-line file, and every later change to a token error description has to be made twice.

**Recommendation:** delete the seven. Keep the fixtures that are unique:
- `Given_empty_client_credentials`.
- The token-limit and lock-timeout form fixtures.
- In `TokenEndpointInvalidScopeTests`:
  - the form-path `Given_the_identity_provider_rejects_the_scope`, the only form-path `invalid_scope` status assertion;
  - `…_without_a_description`;
  - `…_with_a_blank_description`.

---

## Checked, no finding

- **DMS proxy against every CMS answer:**
  - A CMS 401 now passes its `error_description` through.
  - The OAuth token-limit and rate-limit 429s are recognized, and every deviation falls back to the generic 429 with the warning.
  - 409 is mapped by status.
  - DMS only ever sends Basic plus `grant_type`, so the CMS's repeated-parameter, `invalid_scope` and form-only `invalid_client` answers cannot reach it, except a non-Basic header that merely contains "basic", which DMS-1606 already maps to 401.
- **Request order:** the check order matches `CS-AUTH.md:107`. `basicAttempted` is true whenever the lenient parser accepts a header.
- **Error format and rate limits:**
  - Every non-success path writes through `OAuthErrorResponseWriter`.
  - Escaped exceptions get the token route's `server_error` text.
  - Both rate-limit gates select the format from `OAuthErrorContractMetadata`.
  - `/connect/register` and the `/v3/*` endpoints are unchanged.
- **Removed helpers:** no remaining code, script, Pester or E2E consumer parses the old token body or references a removed helper. `ConfigurationServiceTokenHandler` and `Wait-CmsClientAvailable` treat every non-200 alike.
- **Enumeration:** no new client-existence oracle. Form and Basic failures each give one answer for unknown, wrong-secret and unapproved clients, the client gate spends budget regardless, and a Keycloak `invalid_scope` or `502` is reachable only after the secret verifies.
- **Docs:** the test names cited in `OWASP-AUTH-COVERAGE.md` and the anchors in the changelog and `CONFIGURATION.md` all resolve.
