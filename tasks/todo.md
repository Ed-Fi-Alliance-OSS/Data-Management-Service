# Todo: Authorize and Authenticate `/connect/revoke`

See `tasks/plan.md` for full context, architecture decisions, and the parking-lot rule. Read the
"Standing Rule: Parking Lot" section there before starting — do not fix unrelated bugs you find
along the way; log them to `docs/parking-lot.md` instead.

## Task 1: Require authentication on the `/connect/revoke` route

**Description:** Change the route mapping for `connect/revoke` in `IdentityModule.cs:36` from
anonymous to requiring an authenticated caller, using a bare authorization requirement (no named
policy — see "Bare `[Authorize]`, not a named policy" in `tasks/plan.md`). Before doing this,
confirm which authentication scheme(s) are registered as default for each identity-provider mode
(self-contained OpenIddict vs. Keycloak) in `Program.cs` / `WebApplicationBuilderExtensions.cs`, so
the `.RequireAuthorization()` call works correctly without needing to name a scheme explicitly (or
add the scheme name if it turns out to be required).

**Acceptance criteria:**
- [ ] `endpoints.MapPost("connect/revoke/{**contextPath}", RevokeToken)` now also requires
      authentication (e.g. `.RequireAuthorization()`), with no policy restriction beyond "must be
      authenticated."
- [ ] `connect/register`, `connect/token`, and `connect/introspect` are untouched — this task only
      changes `connect/revoke`.
- [ ] A `POST /connect/revoke` request with no `Authorization` header (or an invalid/expired
      bearer token) returns `401 Unauthorized`.

**Verification:**
- [ ] Build succeeds: `dotnet build`
- [ ] Manual check: `curl -X POST http://localhost:<port>/connect/revoke -d "token=anything"` (no
      auth header) returns `401`.
- [ ] Note: existing anonymous `RevokeToken` tests in `IdentityModuleTests.cs` (~lines 1200-1246)
      will now fail — that is expected here and fixed in Task 5, not in this task.

**Dependencies:** None

**Files likely touched:**
- `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Modules/IdentityModule.cs`

**Estimated scope:** Small (1 file)

---

## Task 2: Make the test auth harness support a configurable caller `client_id`

**Description:** `TestAuthHandler` (`.../Tests.Unit/TestAuthHandler.cs`) currently hardcodes the
authenticated principal's `client_id` claim to `identitySettings.Value.ClientId` for every test
request. Later tasks need to simulate two different callers (the token's owner vs. a different
client) in the same test run. Add a way to override the `client_id` claim per-request — e.g. a new
optional `X-Test-ClientId` header that, when present, is used as the `client_id` claim instead of
the hardcoded default. When the header is absent, behavior must be unchanged (default to the
existing hardcoded value), so no existing test is affected.

**Acceptance criteria:**
- [ ] `TestAuthHandler` reads an optional header (e.g. `X-Test-ClientId`) and uses its value as the
      `client_id` claim on the authenticated principal when present.
- [ ] When the header is absent, the existing hardcoded `client_id` behavior is unchanged.
- [ ] All existing tests that rely on `TestAuthHandler` still pass unmodified.

**Verification:**
- [ ] Build succeeds: `dotnet build`
- [ ] Tests pass: `dotnet test` (existing auth-dependent tests, e.g. `ClaimsManagementModuleTests.cs`)
- [ ] Manual check: a new throwaway test asserting the header overrides the claim, then removed
      once Task 5/6 exercise it for real (or folded directly into Task 5/6 — no need to keep a
      scratch test around).

**Dependencies:** None (can be done in parallel with Task 1)

**Files likely touched:**
- `.../Tests.Unit/TestAuthHandler.cs`

**Estimated scope:** Small (1 file)

---

## Checkpoint: Foundation (after Tasks 1-2)
- [ ] `dotnet build` succeeds
- [ ] `dotnet test` passes except the anonymous-`RevokeToken` tests noted in Task 1 (tracked, not ignored)
- [ ] Manual 401 check from Task 1 passes
- [ ] Review with human before proceeding if anything about the auth scheme investigation in
      Task 1 was surprising (e.g. both schemes registered simultaneously)

---

## Task 3: Verify the target token's signature and enforce `client_id` ownership in the token manager

**Description:** In `Services/OpenIddictTokenManager.cs`, change `RevokeTokenAsync` (lines
399-419) to:
1. Accept a new parameter for the caller's `client_id` (update `ITokenRevocationManager.RevokeTokenAsync`
   in `Token/ITokenRevocationManager.cs` accordingly).
2. Replace the unvalidated `new JwtSecurityTokenHandler().ReadJwtToken(token)` call with the
   signature-verifying `JwtTokenValidator.ValidateToken(...)` helper (`Token/JwtTokenValidator.cs:31`),
   using the same signing keys / issuer / audience already used by `ValidateTokenAsync` in this
   class (lines 354-394). Do **not** reuse `ValidateTokenAsync` itself or anything that checks DB
   status — an already-revoked token must still be treated as a harmless no-op, not a validation
   failure (see "Use `JwtTokenValidator.ValidateToken`, not `ValidateTokenAsync`" in `tasks/plan.md`).
3. If the token fails signature/issuer/audience verification, or has no `client_id` claim, or its
   `client_id` claim does not match the caller's `client_id` parameter: return `false` without
   calling `_tokenRepository.RevokeTokenAsync(...)`. Do not throw.
4. If the `client_id` matches: proceed exactly as before (extract `jti`, call
   `_tokenRepository.RevokeTokenAsync(Guid.Parse(jti))`).

Note: the Keycloak `client_id`-claim-name risk originally flagged in `tasks/plan.md` is resolved —
a real Keycloak-issued JWT was verified to include `"client_id": "<actual client id>"`, the same
claim name used for self-contained tokens. No per-mode branching is needed to read this claim.

**Acceptance criteria:**
- [ ] `ITokenRevocationManager.RevokeTokenAsync` signature includes the caller's `client_id`.
- [ ] Target token's signature/issuer/audience is verified via `JwtTokenValidator.ValidateToken`
      before any claim on it is trusted.
- [ ] A `client_id` mismatch (or unverifiable token) results in `false` returned, no repository
      call made, no exception thrown.
- [ ] A `client_id` match with a valid `jti` behaves exactly as before (repository revocation
      called, returns its result).
- [ ] The confirmed Keycloak claim-name finding (see note above) is recorded in the PR description.

**Verification:**
- [ ] Build succeeds: `dotnet build`
- [ ] Unit tests pass: `dotnet test` (Task 6 below adds direct coverage; this task's own
      correctness is provisionally checked by that)

**Dependencies:** None (independent of Tasks 1-2, but Task 4 depends on this)

**Files likely touched:**
- `src/config/backend/EdFi.DmsConfigurationService.Backend.OpenIddict/Services/OpenIddictTokenManager.cs`
- `src/config/backend/EdFi.DmsConfigurationService.Backend.OpenIddict/Token/ITokenRevocationManager.cs`

**Estimated scope:** Small–Medium (2 files)

---

## Task 4: Wire the caller's `client_id` into the `/connect/revoke` handler

**Description:** In `IdentityModule.RevokeToken` (`IdentityModule.cs:322-361`), after confirming
`model.Token` is present, read the authenticated caller's `client_id` from
`httpContext.User.FindFirst("client_id")?.Value` (same claim name used by `IntrospectToken` at
line 309) and pass it to `revocationManager.RevokeTokenAsync(model.Token, callerClientId)`. Keep
the existing behavior of always returning `200 OK` from this handler for every outcome except the
missing-`token`-field case (still `400`) — the mismatch/no-op decision now happens inside the
manager (Task 3), not in this handler.

**Acceptance criteria:**
- [ ] Handler extracts the caller's `client_id` from the authenticated principal.
- [ ] Handler passes it into the updated `RevokeTokenAsync` call.
- [ ] Missing `token` form field still returns `400` with the existing error message, unchanged.
- [ ] Every other outcome (match, mismatch, exception) still returns `200 OK`, unchanged from
      current behavior at the HTTP layer.

**Verification:**
- [ ] Build succeeds: `dotnet build`
- [ ] Manual/curl checks from `tasks/plan.md`'s Phase 2 checkpoint pass

**Dependencies:** Task 1 (needs `httpContext.User` populated), Task 3 (needs updated interface)

**Files likely touched:**
- `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Modules/IdentityModule.cs`

**Estimated scope:** Small (1 file)

---

## Checkpoint: Core feature (after Tasks 3-4)
- [ ] `dotnet build` succeeds
- [ ] Manual curl checks from `tasks/plan.md` Phase 2 checkpoint both pass (own-token revoked;
      other-client's-token left valid)
- [ ] Review with human before proceeding, especially the Keycloak claim-name finding from Task 3

---

## Task 5: Update and extend `IdentityModuleTests.cs` for `/connect/revoke`

**Description:** Update the existing `RevokeToken` tests (~lines 1200-1246) and add new cases so
the full set covers:
- Unauthenticated request → `401` (replaces the old anonymous-success expectation).
- Authenticated, token's `client_id` matches caller's `client_id` → `200 OK`, and the fake
  `ITokenManager`/`ITokenRevocationManager` is verified to have been called such that revocation
  occurs.
- Authenticated, token's `client_id` does **not** match caller's `client_id` (use the new
  `X-Test-ClientId` header from Task 2 to simulate a different caller) → `200 OK`, but revocation
  is verified **not** to have occurred (repository-level call not made / fake shows no state change).
- Malformed or unsigned/invalid-signature token in the `token` field, authenticated caller → `200 OK`,
  no revocation.
- Missing `token` form field, authenticated caller → `400` (regression check, should be unchanged).

**Acceptance criteria:**
- [ ] All five scenarios above are covered by tests.
- [ ] No previously-passing test was deleted to make this pass; any that needed behavior changes
      were updated in place with a clear reason (auth now required).

**Verification:**
- [ ] Tests pass: `dotnet test` (Configuration Service frontend unit test project)
- [ ] Build succeeds: `dotnet build`

**Dependencies:** Tasks 1-4

**Files likely touched:**
- `.../Tests.Unit/.../IdentityModuleTests.cs`

**Estimated scope:** Medium (1 file, several new test cases)

---

## Task 6: Add/extend unit tests for `OpenIddictTokenManager.RevokeTokenAsync`

**Description:** Directly test the manager-level logic added in Task 3, independent of the HTTP
layer: matching `client_id` succeeds and calls the repository; mismatched `client_id` returns
`false` without calling the repository; a token that fails signature verification returns `false`
without calling the repository; a token missing the `jti` claim (existing behavior) still returns
`false`.

**Acceptance criteria:**
- [ ] Each scenario above has a corresponding test.
- [ ] Repository mock/fake is asserted as "not called" for the negative cases, not just checked by
      return value.

**Verification:**
- [ ] Tests pass: `dotnet test`
- [ ] Build succeeds: `dotnet build`

**Dependencies:** Task 3

**Files likely touched:**
- Unit test project covering `EdFi.DmsConfigurationService.Backend.OpenIddict` (find existing
  `OpenIddictTokenManager` test file, if any, and extend it; create one following existing
  conventions if none exists)

**Estimated scope:** Small–Medium (1 file)

---

## Checkpoint: Tests (after Tasks 5-6)
- [ ] `dotnet test` passes for the full solution (or at minimum the Configuration Service test
      projects, per repo convention)
- [ ] No test was skipped, deleted, or weakened to reach green

---

## Task 7: Document the new auth model for `/connect/revoke`

**Description:** Update `reference/design/configuration-service/CS-AUTH.md` (the token-endpoints
table, ~line 29) and `docs/OWASP-AUTH-COVERAGE.md` (the revocation sections, ~lines 72, 76-79,
137-139, 166-168, 183) to state that `/connect/revoke` now requires an authenticated caller and
only revokes tokens whose `client_id` matches the caller's own, with a mismatch treated as a
silent no-op returning `200 OK`. Note that this `client_id` check applies uniformly to both
self-contained and Keycloak-issued tokens, since both use the same claim name.

**Acceptance criteria:**
- [ ] `CS-AUTH.md`'s revoke row/description reflects the auth requirement.
- [ ] `OWASP-AUTH-COVERAGE.md`'s revocation sections reflect both the auth requirement and the
      ownership-check/no-op behavior.

**Verification:**
- [ ] Manual check: re-read both files after editing to confirm they no longer contradict the
      implemented behavior.

**Dependencies:** Tasks 1-4

**Files likely touched:**
- `reference/design/configuration-service/CS-AUTH.md`
- `docs/OWASP-AUTH-COVERAGE.md`

**Estimated scope:** Small (2 files, text only)

---

## Checkpoint: Complete (after Task 7)
- [ ] All acceptance criteria across Tasks 1-7 met
- [ ] `docs/parking-lot.md` exists only if adjacent bugs were actually found during this work
- [ ] If it exists, PR description reminds the reviewer to read it and to delete it before merge
- [ ] Ready for human review
