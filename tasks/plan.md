# Implementation Plan: Authorize and Authenticate `/connect/revoke`

## Overview

`POST /connect/revoke` (`IdentityModule.RevokeToken`,
`src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Modules/IdentityModule.cs:322-361`)
currently allows anonymous access and revokes any syntactically-parseable token regardless of who
is asking. This plan adds two things, in order:

1. **Authentication requirement** — the caller must present a valid `Authorization: Bearer <token>`
   header (`[Authorize]` / `.RequireAuthorization()`, no policy restriction beyond "authenticated").
2. **Ownership check** — the token named in the `token` form field may only be revoked if its
   `client_id` claim matches the `client_id` claim of the caller's own (already-authenticated)
   bearer token. A mismatch is treated the same as "token not found" per RFC 7009 conventions
   already used by this endpoint: **return `200 OK` without revoking anything.** This was
   confirmed with the requester as the desired behavior (avoids leaking whether a token exists or
   who owns it).

This is the simplest responsible solution: it reuses the authentication scheme(s) already
registered for this service, reuses the existing signature-verifying JWT validator instead of
inventing a new one, and does not touch the self-contained-vs-Keycloak provider branching that
governs whether revocation is supported at all.

## Standing Rule: Parking Lot (read this before starting)

While implementing this plan, if you discover a bug in code **adjacent to** this work (i.e. not
something you introduced and not something this plan asks you to fix), **do not fix it**. Instead:

- Create `docs/parking-lot.md` the first time you find such an issue (if it doesn't already exist).
- Add a short entry: file/line, what's wrong, why it's out of scope for this change.
- Keep implementing the plan as scoped. Do not expand this PR to fix parking-lot items.

If `docs/parking-lot.md` ends up with any content, the PR description **must**:
- Tell the human reviewer to read `docs/parking-lot.md` as part of review.
- Tell the human reviewer to delete `docs/parking-lot.md` before final merge (it is not meant to
  ship to `main`).

If no adjacent bugs are found, do not create the file, and do not mention it in the PR.

## Architecture Decisions

- **Bare `[Authorize]`, not a named policy.** This service already defines
  `SecurityConstants.ServicePolicy` (`WebApplicationBuilderExtensions.cs:347-359` and `:422-434`),
  used via `MapSecuredGet/Post/Put/Delete` (`Infrastructure/Authorization/EndpointBuilderExtensions.cs`)
  for the admin/management API. That policy requires a specific service-role claim
  (`identitySettings.RoleClaimType == identitySettings.ConfigServiceRole`) that an ordinary
  client-credentials token minted by `/connect/token` does **not** carry. Applying `ServicePolicy`
  to `/connect/revoke` would block normal clients from revoking their own tokens. Use a plain
  `.RequireAuthorization()` (or `[Authorize]`) with no policy — "must be an authenticated caller,"
  nothing more.
- **Ownership check lives inside the token manager, not the HTTP handler.** The manager already
  owns JWT parsing for revocation (`OpenIddictTokenManager.RevokeTokenAsync`,
  `Services/OpenIddictTokenManager.cs:399-419`) and already has the signing keys / issuer / audience
  needed to verify a token's signature (used by `ValidateTokenAsync`, lines 354-394, via the shared
  `JwtTokenValidator.ValidateToken` helper in `Token/JwtTokenValidator.cs:31`). Extend
  `ITokenRevocationManager.RevokeTokenAsync` to accept the caller's `client_id` and do the
  comparison there, next to the code that already parses the token.
- **Use `JwtTokenValidator.ValidateToken`, not `ValidateTokenAsync`, to read the target token's
  claims.** `ValidateTokenAsync` (used by introspection) additionally checks the DB status equals
  `"valid"`, so it returns `false` for a token that is *already revoked* — but revoking an
  already-revoked token must still be treated as a harmless no-op (idempotent per RFC 7009), and we
  still need to know that token's `client_id` to decide whether to no-op. `JwtTokenValidator.ValidateToken`
  verifies signature/issuer/audience only, without the DB-status gate, so it is the correct helper to
  reuse here.
- **Replacing the unvalidated `ReadJwtToken` call is in-scope, not a parking-lot item.** The current
  implementation reads the target token's claims with a bare `JwtSecurityTokenHandler().ReadJwtToken(...)`
  — no signature verification. If the ownership check trusted an unverified `client_id` claim, a
  caller could forge a token asserting their own `client_id` while embedding a victim's real `jti`,
  which defeats the entire point of this change. This plan's feature is meaningless without fixing
  this, so it is required work, not an adjacent bug to park.

## Risks and Open Questions to Verify During Implementation

**Resolved:** the Keycloak `client_id`-claim-name risk was manually verified against a real
Keycloak-issued JWT, which includes `"client_id": "<actual client id>"` — the same claim name
already used for self-contained OpenIddict tokens (`IdentityModule.cs:309`). The ownership check
can therefore read the `client_id` claim by the same name regardless of active provider mode; no
per-mode branching is needed for this.

| Risk / Question | Why it matters | What to do |
|---|---|---|
| Is exactly one authentication scheme active at a time (self-contained XOR Keycloak), or could both be registered simultaneously, requiring an explicit scheme name on `.RequireAuthorization()`? | A bare `[Authorize]` relies on there being an unambiguous default authentication scheme. | Confirm in `Program.cs` / `WebApplicationBuilderExtensions.cs` during Task 1 which `AddAuthentication(...)` default scheme is configured for each mode, and whether both modes are ever registered in the same running instance. |
| Existing anonymous `RevokeToken` tests (`IdentityModuleTests.cs:1200-1246`) will start failing (401) once `[Authorize]` is added. | These are not "adjacent" bugs — they are existing tests whose behavior this plan intentionally changes. | Update these tests as part of Task 5, not the parking lot. |

## Task List

### Phase 1: Foundation — require authentication

- [ ] Task 1: Add authorization requirement to the route
- [ ] Task 2: Make the test auth harness support a configurable caller `client_id`

**Checkpoint: Foundation**
- [ ] Build succeeds
- [ ] Full existing test suite still passes except the anonymous-`RevokeToken` tests, which are
      expected to now fail with 401 (to be fixed in Phase 3, not silently skipped)
- [ ] Manual/curl check: `POST /connect/revoke` with no `Authorization` header returns `401`

### Phase 2: Core feature — client_id ownership validation

- [ ] Task 3: Verify the target token's signature and enforce `client_id` ownership in the token manager
- [ ] Task 4: Wire the caller's `client_id` from the authenticated principal into the handler

**Checkpoint: Core feature**
- [ ] Build succeeds
- [ ] Manual/curl check, self-contained mode: token issued to client A, revoke call authenticated as
      client A → `200 OK`, token subsequently rejected as revoked
- [ ] Manual/curl check, self-contained mode: token issued to client A, revoke call authenticated as
      client B → `200 OK`, but token is still valid afterward (not revoked)

### Phase 3: Tests

- [ ] Task 5: Update and extend `IdentityModuleTests.cs` for `/connect/revoke`
- [ ] Task 6: Add/extend unit tests for `OpenIddictTokenManager.RevokeTokenAsync`

**Checkpoint: Tests**
- [ ] `dotnet test` passes for the full solution (or the Configuration Service test projects at
      minimum, per repo convention)
- [ ] No test was deleted or weakened to make it pass — mismatches were fixed by updating expected
      behavior, not by removing coverage

### Phase 4: Documentation

- [ ] Task 7: Document the new auth model for `/connect/revoke`

**Checkpoint: Complete**
- [ ] All acceptance criteria above met
- [ ] `docs/parking-lot.md` exists and is non-empty only if adjacent issues were actually found;
      otherwise it does not exist
- [ ] PR description includes the parking-lot reminder (see below) only if the file exists
- [ ] Ready for human review

## PR Description Requirements

The PR opened for this change must:
- Summarize the two behavior changes (auth required; ownership-checked revocation) and the chosen
  mismatch behavior (`200 OK`, silent no-op).
- Note that the `client_id` claim name was confirmed identical across both self-contained and
  Keycloak-issued tokens, so the ownership check needs no per-mode branching.
- If `docs/parking-lot.md` was created: explicitly ask the human reviewer to (1) read it as part of
  review, and (2) delete it before merging to `main`. Do not merge it into `main` yourself.

## Definition of Done

This plan defers to the project's standing Definition of Done in addition to the acceptance
criteria above (build clean, tests pass, no unrelated scope creep, no weakened checks or skipped
tests to force green).
